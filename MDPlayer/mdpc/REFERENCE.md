# mdpc FMP reference harness

This document describes the temporary Windows-only reference harness added to `mdpc` for PR 1 of the native Linux FMP renderer project. The goal is to capture an immutable behavioral baseline from the existing MDPlayer FMP implementation before extracting the portable core.

## Scope

The reference harness supports only compiled FMP formats:

- `.opi`
- `.ovi`
- `.ozi`

Source formats (`.mpi`, `.mvi`, `.mzi`) require `FMC.EXE` and are out of scope for PR 1.

## Requirements

- Windows x64
- .NET 8
- A legally obtained copy of `FMP.COM` (never commit this binary to the repository)
- One or more OVI/OPI/OZI test fixtures

## Usage

```powershell
mdpc --reference --fmp-com=C:\path\to\FMP.COM C:\path\to\track.ovi
```

Optional arguments:

```text
--reference-dir=PATH    Directory for reference output (default: track-directory\reference)
--search-path=PATH      Semicolon-separated supplemental search path (e.g. for PVI banks)
--trace=PATH            Explicit register-trace JSONL path
-w                      Enable WAV output (legacy; reference mode enables it automatically)
-e                      Force emulation-only (reference mode enables it automatically)
```

## Reference output

For a track named `track.ovi`, the harness produces:

```text
reference/
├── track.wav
├── track.register-trace.jsonl
└── track.render-result.json
```

### `track.register-trace.jsonl`

One JSON object per chip event:

```json
{"event":"opna","port":0,"address":40,"data":127,"sample":12345}
{"event":"ppz8-load","bank":0,"mode":1,"sampleCount":256,"sample":67890}
{"event":"ppz8-write","port":0,"address":1,"data":0}
```

### `track.render-result.json`

```json
{
  "input": "C:\\path\\to\\track.ovi",
  "inputSha256": "...",
  "format": "FMP",
  "fmpComPath": "C:\\path\\to\\FMP.COM",
  "fmpComSha256": "...",
  "referenceDir": "C:\\path\\to\\reference",
  "traceCount": 123456,
  "renderedSamples": 0,
  "peakLevel": 0,
  "succeeded": true,
  "exitCode": 0
}
```

## Exit codes

| Code | Meaning                              |
|------|--------------------------------------|
| 0    | Success                              |
| 2    | Invalid command-line arguments       |
| 3    | Unsupported input format             |
| 4    | Missing runtime asset (FMP.COM)        |
| 7    | Emulation/rendering failure            |

## Important notes

- `FMP.COM` is a user-supplied runtime asset. Do not add it to version control.
- The harness forces `EmuOnly = true` and output device `DEV_Null` for FMP inputs, so no physical audio device is opened.
- This is a reference harness, not the final Linux CLI. The final renderer will be in `MDPlayer.Fmp.Cli` and will run natively on Linux.
