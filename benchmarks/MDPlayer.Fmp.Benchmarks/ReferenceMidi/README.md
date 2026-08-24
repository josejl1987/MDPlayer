# ReferenceMidi — Reference-Driven MIDI Export Recovery

This directory (under `benchmarks/MDPlayer.Fmp.Benchmarks/ReferenceMidi/`) is the
governance surface for the reference-driven recovery of MDPlayer's MIDI exporter.

> Master rule: **the MIDI exporter behavior is frozen** until the reference corpus
> and the `reports/reference-baseline.md` report exist and answer the open
> questions. This directory and the `scripts/reference-midi.sh` harness only build
> toolchain, corpus, analysis, comparison and reporting. They never touch
> `MusicalMidiExporter` heuristics.

## Hierarchy of truth (spec §89)

| Level | Evidence |
| ----- | -------- |
| 0 | serialized MIDI bytes actually produced (hash + independent decode) |
| 1 | same-input comparison: `VGM → vgm2mid` vs `VGM → MDPlayer` |
| 2 | driver-aware MIDI: `SMPS → smps2mid`, driver sequence → ValleyBell converter |
| 3 | verified same-song triad (sequence MIDI / vgm2mid MIDI / MDPlayer MIDI) |
| 4 | human listening against source playback |

## Versioned files (only these)

- `README.md` — this file
- `tools.lock.json` — pinned tool identities; the only thing `bootstrap --update-lock` writes
- `corpus.manifest.json` — single source of truth for songs, tiers, tool runs, hashes
- `reference-exceptions.json` — documented, evidenced bugs in references
- `track-map.json` — explicit reference→candidate track mappings
- `converter-behavior.md` — external converter source study + behavior table
- `derived-export-rules.md` — rules derived from baseline evidence (phase 2)

Everything generated lives under `artifacts/reference-midi/` (gitignored):
`tools/`, `inputs/`, `canonical/`, `midi/`, `analysis/`, `compare/`, `audio/`,
`listening/`, `logs/`.

## Tool sources (canonical)

- `ValleyBell/MidiConverters` — https://github.com/ValleyBell/MidiConverters
- `vgm2mid 0.5` (Paul Jensen, updated by Valley Bell) — original distribution
  `http://vgmrips.net/programs/tools/...` (now 410); recovered from Internet
  Archive Wayback Machine `id_` captures whose digests are stable across
  snapshots (recorded in `tools.lock.json`).
- `smps2mid 0.4.3` (ValleyBell) — `http://vgmrips.net/programs/non-vgm/...`
  (same recovery).
- `sonicretro/smps-rips` — https://github.com/sonicretro/smps-rips
- `vgmtrans` (Tier 2) — https://github.com/vgmtrans/vgmtrans (optional)
- `libvgm` — https://github.com/ValleyBell/libvgm (source playback render, optional)

## Commands

```bash
./scripts/reference-midi.sh bootstrap            # verify tools against lock
./scripts/reference-midi.sh bootstrap --update-lock  # deliberate re-resolve
./scripts/reference-midi.sh discover             # discover local VGM corpus
./scripts/reference-midi.sh generate             # vgm2mid refs + MDPlayer candidates + smps2mid semantic refs
./scripts/reference-midi.sh analyze              # run ReferenceMidiAnalyzer
./scripts/reference-midi.sh compare              # run ReferenceMidiComparator
./scripts/reference-midi.sh render               # optional FluidSynth / libvgm audio
./scripts/reference-midi.sh verify               # reopen + hash every output
./scripts/reference-midi.sh all
```

## Rules that are not optional

1. Do not modify `MusicalMidiExporter` / pitch / tempo / percussion heuristics
   before Gate C (baseline report) passes.
2. Every input and every output gets a SHA-256; no timestamp inside reproducible identity.
3. After writing a MIDI: close, reopen from disk, hash, decode, then write the receipt.
4. `vgm2mid` only runs with a demonstrated Windows ANSI codepage 1252; otherwise
   status `unavailable` and the same-input corpus is blocked.
5. `smps2mid` interactive dialog answers are recorded per track in the manifest and
   must be reproducible; unknown prompts are a FAIL.
6. Reference MIDIs never get patched; reference bugs are recorded as exceptions.
7. A derived VGM (OVI → capture → VGM) is never presented as VGM ground truth.
