#!/usr/bin/env python3
"""
Reference corpus validation tests.
Verifies that captured reference files have correct structure and format.
Run: python3 verify-reference.py /path/to/ref-corpus
"""

import json, os, sys, struct, hashlib, glob

def check(path, ok, msg):
    if ok:
        print(f"  ✓ {msg}")
    else:
        print(f"  ✗ FAIL: {msg}")
    return ok

def verify_track(track_dir):
    name = os.path.basename(track_dir.rstrip('/'))
    print(f"\n--- {name} ---")
    all_ok = True

    # metadata.json
    meta_path = os.path.join(track_dir, "metadata.json")
    if check(track_dir, os.path.exists(meta_path), "metadata.json exists"):
        with open(meta_path, "rb") as f:
            try: meta = json.load(f)
            except: meta = {}
        all_ok &= check(track_dir, "title" in meta, "metadata has title")
        all_ok &= check(track_dir, isinstance(meta.get("title"), str) and len(meta["title"]) > 0, f"title non-empty: {meta.get('title','?')[:40]}")
        all_ok &= check(track_dir, "comment" in meta, "metadata has comment")

    # register-trace.jsonl
    trace_paths = glob.glob(os.path.join(track_dir, "*.register-trace.jsonl")) + \
                  [p for p in os.listdir(track_dir) if p.endswith(".register-trace.jsonl")]
    trace_file = None
    for p in trace_paths:
        full = os.path.join(track_dir, p) if not os.path.isabs(p) else p
        if os.path.isfile(full):
            trace_file = full
            break
    if check(track_dir, trace_file is not None, "register-trace.jsonl exists"):
        with open(trace_file, "r") as f:
            lines = f.readlines()
        all_ok &= check(track_dir, len(lines) > 0, f"trace has {len(lines)} lines")
        if lines:
            try:
                first = json.loads(lines[0])
                all_ok &= check(track_dir, "event" in first, f"first trace line has event field: {first.get('event','?')}")
                if first.get("event") == "opna":
                    all_ok &= check(track_dir, all(k in first for k in ("port","address","data","sample")),
                        "opna trace has port/address/data/sample")
            except:
                all_ok &= check(track_dir, False, "first trace line is valid JSON")

    # WAV
    wav_paths = glob.glob(os.path.join(track_dir, "*.wav"))
    wav_file = wav_paths[0] if wav_paths else None
    if check(track_dir, wav_file is not None, "WAV file exists"):
        with open(wav_file, "rb") as f:
            header = f.read(44)
        all_ok &= check(track_dir, header[:4] == b"RIFF", "WAV has RIFF header")
        all_ok &= check(track_dir, header[8:12] == b"WAVE", "WAV has WAVE format")
        fmt = struct.unpack_from("<HH", header, 20)
        all_ok &= check(track_dir, fmt[0] == 1, f"WAV PCM format tag={fmt[0]}")
        all_ok &= check(track_dir, fmt[1] == 2, f"WAV stereo channels={fmt[1]}")
        sr = struct.unpack_from("<I", header, 24)[0]
        all_ok &= check(track_dir, sr == 44100, f"WAV sample rate={sr}")
        bits = struct.unpack_from("<H", header, 34)[0]
        all_ok &= check(track_dir, bits == 16, f"WAV bit depth={bits}")

        # SHA-256
        sha_path = wav_file + ".sha256"
        if check(track_dir, os.path.exists(sha_path), "WAV SHA-256 file exists"):
            with open(sha_path, "r") as f:
                stored_sha = f.read().strip()
            with open(wav_file, "rb") as f:
                computed_sha = hashlib.sha256(f.read()).hexdigest().upper()
            all_ok &= check(track_dir, stored_sha.upper() == computed_sha, "SHA-256 matches")

    # render-result.json
    result_paths = glob.glob(os.path.join(track_dir, "*.render-result.json")) + \
                   [p for p in os.listdir(track_dir) if p.endswith(".render-result.json")]
    result_file = None
    for p in result_paths:
        full = os.path.join(track_dir, p) if not os.path.isabs(p) else p
        if os.path.isfile(full):
            result_file = full
            break
    if check(track_dir, result_file is not None, "render-result.json exists"):
        r = json.load(open(result_file))
        all_ok &= check(track_dir, r.get("succeeded") == True, f"render succeeded={r.get('succeeded')}")
        all_ok &= check(track_dir, "inputSha256" in r, "has inputSha256")
        all_ok &= check(track_dir, "fmpComSha256" in r, "has fmpComSha256")
        all_ok &= check(track_dir, "format" in r, "has format")
        all_ok &= check(track_dir, "traceCount" in r, "has traceCount")
        all_ok &= check(track_dir, "succeeded" in r, "has succeeded")
        all_ok &= check(track_dir, r.get("traceCount", 0) > 0, f"traceCount={r.get('traceCount',0)} > 0")
        # WAV size matches renderedSamples
        if wav_file and r.get("renderedSamples"):
            filesize = os.path.getsize(wav_file)
            expected_data = r["renderedSamples"] * 4  # 2 channels * 2 bytes
            actual_data = max(0, filesize - 44)
            all_ok &= check(track_dir, abs(actual_data - expected_data) < 4,
                f"WAV data size match: expected={expected_data} actual={actual_data}")

    return all_ok

def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "/home/jose/MDPlayer/mdpc-ref"
    print(f"Verifying reference corpus at: {root}")
    print(f"Directory exists: {os.path.isdir(root)}")

    track_dirs = sorted([os.path.join(root, d) for d in os.listdir(root)
                         if os.path.isdir(os.path.join(root, d))])
    print(f"Found {len(track_dirs)} track directories\n")

    passed = 0
    failed = 0
    for td in track_dirs:
        if verify_track(td):
            passed += 1
        else:
            failed += 1

    print(f"\n{'='*40}")
    print(f"Results: {passed} passed, {failed} failed out of {len(track_dirs)} tracks")
    return 0 if failed == 0 else 1

if __name__ == "__main__":
    sys.exit(main())
