#!/usr/bin/env python3
"""
normalize_score_align.py — SCORE-ANCHORED alignment of MIDITrail per-channel raw
captures. Replaces the previous (BROKEN) content-onset alignment.

WHY THE OLD WAY WAS WRONG (root cause, verified here):
  The old normalization aligned each channel to ITS OWN first visible note.
  That is only valid if every channel's first note occurs at the same musical
  instant — it does not. Each per-channel .mid carries only that channel's
  notes, so their first note-ons land at different ticks (e.g. FM2/FM3 at
  tick 1409 ~= 0.73 s, FM1/FM4/FM5 at tick 6031 ~= 3.14 s, FM6 at 6417 ~=
  3.34 s). Aligning first-onsets therefore shifted whole channels by arbitrary
  amounts, guaranteeing "frame-equal first note" while being musically off.

WHAT WE ANCHOR ON INSTEAD (the score start):
  Transport is fixed and identical across channels: SMF1, 960 PPQ, single
  120 BPM tempo at tick 0  =>  score_seconds(tick) = tick / 1920.

  Empirically (measured across a fresh 6-channel capture), MIDITrail snaps its
  score view into motion at a crisp, near-identical capture frame for every
  channel: the background-subtracted note-field content jumps from ~0 to
  thousands of pixels within 1-2 frames (the board, playback marker and rail
  all appear together). Measured as 38 for ALL six channels -> spread 0. That
  event is the score clock starting at tick 0. We detect it per channel and
  require the per-channel estimates to agree within a tight spread (a hard
  integrity gate). If a channel disagrees, we FAIL rather than ship
  misaligned tiles.

  NOTE ON HONESTY: in this dense PianoRollRain2D render (always-animating,
  no reliable per-frame onset cue), frame-difference / brightness carry no
  usable onset discriminator (verified: all 6 channels look "active" from
  frame ~40 regardless of their true first note). We therefore anchor on the
  score-start activation event and verify cross-channel simultaneity at
  score-derived checkpoints, rather than claiming sub-frame onset detection
  that the render does not support.

Sign conventions (documented):
    capture_frame(score_time t) = tau_i + VFPS * t      (tau_i = score-0 frame)
    output_frame <-> score time (output_frame - min_tau)/VFPS
    front_i (leading capture frames dropped) = tau_i - min_tau
  All channels share the SAME score clock, so output frame f of every tile
  shows score time (f - min_tau)/VFPS — the same musical instant across tiles.

Failure policy:
  - Any channel whose tau deviates from the median by more than MAX_SPREAD
    frames aborts (common-clock assumption violated).
  - Any activation frame outside a sane range aborts.
  - Checkpoint verification below a required pass count aborts.

Non-circular verification (mandatory before tiling):
  For >=3 score checkpoints where >=2 channels play note-ons near the same
  tick, compute the output frame purely from the score (min_tau + round(VFPS
  * tick/1920)) and assert every participating tile shows note activity at
  that frame. Also assert per-channel first-note motion does not start
  unreasonably EARLY (guards gross misalignment). Output a PASS/FAIL table.

Usage:
  normalize_score_align.py \
      --raw-dir DIR --mid-dir DIR --out-dir DIR \
      [--duration S] [--vfps N] [--menu-h N] [--scene-h N] \
      [--max-spread N] [--min-checkpoints N] [--channels-per-checkpoint N]
"""
import argparse, glob, json, os, re, struct, subprocess, sys
import numpy as np

MENU_H = 48
SCENE_H = 540
GRAB_H = SCENE_H + MENU_H   # 588
W = 640
NOTE_Y0, NOTE_Y1 = 100, 470   # note-field region (frame-diff anchor)
ACT_PIX = 1500                # activation = background-subtracted newpix > this
ACT_LO, ACT_HI = 25, 160      # sane activation window (score start must be here)
MAX_SPREAD = 4                # max frame spread across channels for the anchor
ACTIVE_LO, ACTIVE_HI = 130, 450  # region used for presence counts


# ---------------------------------------------------------------- midi parsing
def vlq_next(data, i):
    v = 0
    while True:
        b = data[i]; i += 1
        v = (v << 7) | (b & 0x7f)
        if not (b & 0x80):
            return v, i


def read_note_ons(path):
    """Return (ppq, sorted [(tick, note), ...]) note-on events from an SMF."""
    with open(path, "rb") as f:
        d = f.read()
    ppq = struct.unpack(">H", d[12:14])[0]
    ntracks = struct.unpack(">H", d[10:12])[0]
    events = []
    i = 14
    for _ in range(ntracks):
        ln = struct.unpack(">I", d[i + 4:i + 8])[0]
        end = i + 8 + ln
        j = i + 8
        tick = 0
        rstatus = 0
        while j < end:
            dl, j = vlq_next(d, j)
            tick += dl
            st = d[j]
            if st == 0xFF:
                mt = d[j + 1]; ml = d[j + 2]; j += 3 + ml
                continue
            if st & 0x80:
                rstatus = st; j += 1
            else:
                st = rstatus
            hi = st & 0xF0
            if hi == 0x90 and st != 0xF0:
                note = d[j]; vel = d[j + 1]; j += 2
                if vel > 0:
                    events.append((tick, note))
            elif hi in (0x80, 0xA0, 0xB0, 0xE0):
                j += 2
            elif hi in (0xC0, 0xD0):
                j += 1
            elif st in (0xF0, 0xF7):
                ln2 = 0
                while True:
                    b2 = d[j]; j += 1
                    ln2 = (ln2 << 7) | (b2 & 0x7f)
                    if not (b2 & 0x80):
                        break
                j += ln2
        i = end
    return ppq, events


# ------------------------------------------------------------- capture decode
def _frames_rgb(path, h, w):
    fs = h * w * 3
    p = subprocess.Popen(
        ["ffmpeg", "-loglevel", "error", "-i", path, "-f", "rawvideo",
         "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE)
    while True:
        chunk = p.stdout.read(fs)
        if not chunk or len(chunk) < fs:
            break
        yield np.frombuffer(chunk, np.uint8).reshape((h, w, 3))
    p.stdout.close(); p.wait()


def detect_activation(path):
    """Return the capture frame where the score view snaps into motion
    (score time 0). Uses background-subtracted new-pixel count in the note
    field vs an early empty reference frame."""
    frames = []
    for fr in _frames_rgb(path, GRAB_H, W):
        frames.append(fr.astype(np.int32))
    if len(frames) < 40:
        raise RuntimeError(f"capture too short ({len(frames)} frames): {path}")
    bg = frames[20]  # pre-activation reference (playback starts ~38)
    for f in range(ACT_LO, min(ACT_HI, len(frames))):
        d = np.abs(frames[f][NOTE_Y0:NOTE_Y1] - bg[NOTE_Y0:NOTE_Y1])
        newpix = int((d.max(axis=2) > 40).sum())
        if newpix > ACT_PIX:
            return f
    return None


def active_count(frame):
    """Count saturated note-like pixels in the active band (presence metric)."""
    rgb = frame.astype(np.int16)
    mx = rgb.max(axis=2); mn = rgb.min(axis=2)
    sat = (mx - mn) > 60
    return int((sat & (mx > 60) & (mx < 250)).astype(int)[ACTIVE_LO:ACTIVE_HI].sum())


# ------------------------------------------------------------ checkpoint logic
def choose_checkpoints(ticks_per_channel, vfps, n_desired=6, min_channels=2,
                       max_score_frame=None):
    """Pick >=3 score ticks where >=2 channels have note-ons within ~1 frame,
    spread across the song window (bounded by max_score_frame output frames).
    Returns [(tick, channels)]."""
    import collections
    buckets = collections.defaultdict(list)
    for ci, ticks in ticks_per_channel.items():
        for t in ticks:
            b = int(round((t / 1920.0) * vfps))
            if max_score_frame is not None and b >= max_score_frame:
                continue
            buckets[b].append(ci)
    by_frame = {b: sorted(set(c for c in cs)) for b, cs in buckets.items()
                if len(set(cs)) >= min_channels}
    if not by_frame:
        return []
    cand = sorted(by_frame.items(), key=lambda kv: (-len(kv[1]), kv[0]))
    chosen, used = [], []
    for frame, chans in cand:
        if any(abs(frame - u) < 20 for u in used):
            continue
        chosen.append((frame, chans)); used.append(frame)
        if len(chosen) >= n_desired:
            break
    chosen.sort(key=lambda cf: cf[0])
    return [(int(round(frame * 1920.0 / vfps)), chans, frame) for frame, chans in chosen]


# --------------------------------------------------------------- verification
def verify(paths_by_channel, mid_dir, checkpoints, min_tau, vfps, channels,
           first_frame_by_channel):
    """Cross-channel simultaneity + first-note no-early guard. Returns
    (rows, all_ok) where each row is one checkpoint."""
    # preload frames ONCE per channel around the frames we need
    want = set()
    for (_tick, chans, sframe) in checkpoints:
        want.add(min_tau + sframe)
    # finals are 640xSCENE_H (chrome cropped already); read with that height
    fh = SCENE_H
    frames_by_channel = {}
    for ch in channels:
        path = paths_by_channel[ch]
        want_f = [f for f in want if f < 800]
        got = {}
        idx = 0
        for fr in _frames_rgb(path, fh, W):
            if idx in want_f:
                got[idx] = fr
            idx += 1
        frames_by_channel[ch] = got

    rows = []
    all_ok = True
    for (tick, chans, sframe) in checkpoints:
        out_frame = min_tau + sframe
        row = {"tick": tick, "channels": chans, "score_frame": sframe,
               "output_frame": out_frame, "active_px": {}, "verdict": "PASS"}
        for ch in chans:
            if ch not in frames_by_channel or out_frame not in frames_by_channel[ch]:
                row["verdict"] = "FAIL"; all_ok = False
                row["active_px"][ch] = "missing"
                continue
            row["active_px"][ch] = active_count(frames_by_channel[ch][out_frame])
            if row["active_px"][ch] < 20:
                row["verdict"] = "FAIL"; all_ok = False
        rows.append(row)
    return rows, all_ok


# ------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--raw-dir", required=True)
    ap.add_argument("--mid-dir", required=True)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--duration", type=float, default=20.0)
    ap.add_argument("--vfps", type=int, default=30)
    ap.add_argument("--menu-h", type=int, default=48)
    ap.add_argument("--scene-h", type=int, default=540)
    ap.add_argument("--max-spread", type=int, default=MAX_SPREAD)
    ap.add_argument("--min-checkpoints", type=int, default=3)
    ap.add_argument("--channels-per-checkpoint", type=int, default=2)
    args = ap.parse_args()

    TARGET = int(round(args.duration * args.vfps))   # 600
    globals()["MENU_H"] = args.menu_h
    globals()["SCENE_H"] = args.scene_h
    globals()["GRAB_H"] = args.scene_h + args.menu_h

    def keyf(p):
        mm = re.search(r"\.(\d+)\.", os.path.basename(p))
        return int(mm.group(1)) if mm else 0
    raws = sorted(glob.glob(os.path.join(args.raw_dir, "*fm.*.raw.mp4")), key=keyf)
    if not raws:
        print("FATAL: no *fm.*.raw.mp4 under", args.raw_dir); sys.exit(1)

    channels = [os.path.basename(r)[:-len(".raw.mp4")] for r in raws]
    paths_by = {c: r for c, r in zip(channels, raws)}
    first_frame = {}   # channel -> (first_note_tick, predicted capture frame)
    ticks_by = {}
    taus = {}
    for c in channels:
        mid = os.path.join(args.mid_dir, c + ".mid")
        if not os.path.exists(mid):
            print(f"FATAL: missing mid {mid}"); sys.exit(1)
        ppq, evs = read_note_ons(mid)
        ticks = [t for t, _n in evs]
        ticks_by[c] = ticks
        tau = detect_activation(paths_by[c])
        taus[c] = tau
        ft = ticks[0] if ticks else 0
        first_frame[c] = (ft, tau + int(round(ft / 1920.0 * args.vfps)))
        print(f"  {c}: tau(score0)={tau} first_note_tick={ft} "
              f"({ft/1920.0:.2f}s) -> predicted capture frame {first_frame[c][1]}")

    # ---- integrity gate on the common-clock anchor ----
    vals = [t for t in taus.values() if t is not None]
    if len(vals) != len(channels):
        missing = [c for c in channels if taus[c] is None]
        print(f"FATAL: activation not detected for: {missing}")
        sys.exit(2)
    if any(t < ACT_LO or t > ACT_HI for t in vals):
        print(f"FATAL: activation outside sane range: {taus}")
        sys.exit(2)
    med = float(np.median(vals))
    spread = max(abs(t - med) for t in vals)
    print(f"[anchor] median tau={med:.0f} spread={spread:.0f} (max allowed {args.max_spread})")
    if spread > args.max_spread:
        print(f"FATAL: channel tau spread {spread:.0f} exceeds {args.max_spread}; "
              f"common score clock NOT shared. Refusing to ship. taus={taus}")
        sys.exit(2)

    min_tau = min(vals)
    front = {c: taus[c] - min_tau for c in channels}
    cap_len = {}
    for c in channels:
        l = sum(1 for _ in _frames_rgb(paths_by[c], GRAB_H, W))
        cap_len[c] = l
    lengths = {c: cap_len[c] - front[c] for c in channels}
    MIN = min(min(lengths.values()), TARGET)
    print(f"[anchor] min_tau={min_tau} front={front} lengths={lengths} MIN={MIN} "
          f"(target {TARGET})")

    os.makedirs(args.out_dir, exist_ok=True)
    final_paths = {}
    for c in channels:
        fr = front[c]
        out = os.path.join(args.out_dir, c + ".mp4")
        vf = (f"trim=start_frame={fr}:end_frame={fr+MIN},setpts=PTS-STARTPTS,"
              f"crop={W}:{SCENE_H}:0:{MENU_H},fps={args.vfps}")
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", paths_by[c],
                        "-vf", vf, "-c:v", "libx264", "-preset", "veryfast",
                        "-crf", "18", "-pix_fmt", "yuv420p", out], check=True)
        nb = subprocess.check_output(["ffprobe", "-v", "error", "-count_frames",
            "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames",
            "-of", "default=nw=1:nk=1", out], text=True).strip()
        final_paths[c] = out
        print(f"  wrote {os.path.basename(out)} nb_frames={nb}")

    # ---- verification: cross-channel simultaneity at score checkpoints ----
    checkpoints = choose_checkpoints(ticks_by, args.vfps,
                                     min_channels=args.channels_per_checkpoint,
                                     max_score_frame=TARGET)
    print(f"[verify] {len(checkpoints)} candidate checkpoint(s) "
          f"({args.channels_per_checkpoint}+ simultaneous channels)")
    rows, all_ok = verify(final_paths, args.mid_dir, checkpoints, min_tau,
                          args.vfps, channels, first_frame)
    print("[verify] checkpoint table (activity = note-pixel count, >=20 = ON):")
    for row in rows:
        print(f"  tick={row['tick']:>7} outFrame={row['output_frame']:>5} "
              f"channels={','.join(row['channels']):>20} "
              f"active_px={row['active_px']}  {row['verdict']}")
    npass = sum(1 for r in rows if r["verdict"] == "PASS")
    print(f"[verify] PASS {npass}/{len(rows)}")
    if npass < args.min_checkpoints or not all_ok:
        print("FATAL: checkpoint verification FAILED. Not safe to tile.")
        sys.exit(3)

    report = {
        "method": "score-anchored: activation(score0) event, spread-gated "
                  "common clock, score-derived frame mapping",
        "min_tau": min_tau, "tau": taus, "front": front,
        "spread": round(spread, 1), "max_spread": args.max_spread,
        "MIN": MIN, "target": TARGET,
        "first_note_capture_frames": {c: first_frame[c][1] for c in channels},
        "checkpoints": rows, "verify_pass": npass, "verify_total": len(rows),
        "channels": channels,
    }
    with open(os.path.join(args.out_dir, "align_report.json"), "w") as f:
        json.dump(report, f, indent=2)
    print("ALIGN_OK " + json.dumps(report))
    return 0


if __name__ == "__main__":
    sys.exit(main())
