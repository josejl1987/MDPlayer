# FMP Renderer — Characterization Corpus

This directory holds the regression, determinism and extraction baseline corpus for the FMP renderer.

## Layout

```
tests/Corpus/
├── manifest.json          — Machine-readable corpus index
├── synthetic/             — Distributable synthetic test fixtures
│   └── .gitkeep
├── local/                 — User-provided tracks (not committed)
│   ├── local-config.example.json
│   └── local-config.json  (gitignored)
└── README.md

artifacts/linux-baseline/   — Regression golden artifacts (gitignored)
    └── .gitkeep
```

## Track categories

| Category | Source | Committed? | Purpose |
|----------|--------|-----------|---------|
| `synthetic/` | Hand-crafted byte arrays | Yes | Nise98 CPU/memory/DOS unit tests |
| `local/` | User-provided OVI files | No (gitignored) | Render integration tests, determinism, golden traces |

## Using the corpus

### Local tracks

1. Copy `local/local-config.example.json` to `local/local-config.json`
2. Set `fmpComPath` to your FMP.COM location
3. Add track entries with absolute paths to verified `.OVI` files
4. Tag each track with its features (fm-only, ppz8, ssg, rhythm, adpcm, looping, etc.)

### Golden artifacts

After rendering a track, save its output artifacts:

```
artifacts/linux-baseline/
├── <track-name>.events.jsonl   — Event trace
├── <track-name>.result.json    — Result manifest
└── <track-name>.wav            — Rendered audio (if needed)
```

These serve as regression baselines — the same input must produce
byte-identical output on repeated runs.

## Important restriction

Linux-generated traces are NOT proof of Windows parity.
They are:

- Regression baselines
- Determinism baselines
- Extraction baselines
- Future comparison inputs
