# Visualizer compositions — Performance / Scope Stage / Diagnostic

The publishing renderer now exposes **three intentional compositions** instead of
a generic grid with many combinations. All stills below were generated from the
same track (`120 Smash Up.spc`, S-DSP 8-voice) at the same playback instant
(t = 8 s) with `mdplayer-render preview --request-json <request> --time 8`.

## The three compositions

| Composition | CLI / GUI name | Geometry | Intent |
|---|---|---|---|
| **Performance** (default) | `performance` | One dominant unified roll (~2/3 of the grid), compact unpitched lanes, optional compact scope strip | Audience-facing music video |
| **Scope Stage** | `scope-stage` | Large scope mosaic + compact synchronized activity strip | Corrscope-style waveform focus |
| **Diagnostic** | `diagnostic` | Full per-channel grid with headers, scopes, pitch cameras | Inspection / export |

`Auto` now resolves deterministically to exactly one of the three:

1. compatible pitched voices present → **Performance**
2. only scope content reliable → **Scope Stage**
3. otherwise → **Diagnostic**

Legacy names (`unified`, `hybrid`, `split`, `scope`, `diagnostic-v2`, `focus`)
remain accepted CLI/GUI values and map onto the same rendering paths as the
three canonical compositions.

## Stills (720p / 1080p)

`contact-sheet.png` shows all six stills side by side.

| Composition | 720p painted | 1080p painted | Structure check |
|---|---|---|---|
| Performance | 22.6 % | 21.2 % | Top metadata bar, dominant roll, compact lanes, scope strip and footer fill the frame; no dead bands |
| Scope Stage | 14.7 % | 14.5 % | Scope cells stay transparent (Corrscope fills them in the final pass); the synchronized activity strip + footer are painted at the bottom |
| Diagnostic | 41.2 % | 39.8 % | Dense per-channel grid across the whole frame |

Band analysis confirms the intended hierarchy: Performance paints the entire
frame with the roll as the primary region; Scope Stage keeps the mosaic region
reserved for waveforms and paints only the strip/footer; Diagnostic fills every
panel.

## Geometry invariants (unit-tested)

- Performance: shared semantic region ≥ 60 % of the grid; scope strip ≈ 18 %
  of the grid (≈170 px at 1080p); compact lanes ≤ ~15 px each.
- ScopeStage: mosaic height is exactly `ScopeHeight × RowCount` (Corrscope
  agreement), the strip sits between the mosaic and the footer, and scope cells
  have no headers.
- Diagnostic: unchanged 12-panel grid; `ScopeStageStripHeight == 0`.

## Regenerating

```bash
mdplayer-render plan   --request-json request-performance.json
mdplayer-render preview --request-json request-performance.json --time 8 --output perf-720.png
```

Request JSON uses camelCase enum names, e.g. `"layout": "performance"`,
`"layout": "scopeStage"`, `"layout": "diagnostic"`.
