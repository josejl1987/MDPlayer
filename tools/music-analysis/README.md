# MDPlayer symbolic analysis

The worker is deterministic and conservative. `score` is an evidence index in
the closed interval `[0, 1]`; it is not a calibrated probability that a label
is correct.

The public detail levels are trust boundaries:

- `minimal` exposes pitch statistics, timing limitations, and only a strong
  global key.
- `standard` has the same public theory surface until a category passes its
  annotated precision gate.
- `full` may contain candidates for development, but every non-key candidate
  is marked `status: experimental` and is rejected by normal overlays.

Confidence meanings are `observed`, `strong`, `tentative`, and `withheld`.
The display policy is implemented once in the C# analysis core and is used by
overlay construction and result validation.

Run the dedicated environment with:

```bash
./scripts/test-analysis.sh
```

The cache consists of `input.json`, `analysis.json`, and
`cache-metadata.json` (metadata schema v2). A valid cache is checked before
Python is resolved.
Partial files are temporary and are atomically renamed only after validation.

The checked-in evaluation annotations are synthetic and public-domain-safe.
Put user-local material under `evaluation/local/`; it is intentionally not a
Git fixture.

The worker compatibility version is `1.1.0` while analysis output remains
schema version 1. `--probe --json` prints the worker and dependency metadata
as a single JSON object. `--analysis-profile` emits newline-delimited,
monotonic stage timing records on stderr; it does not affect analysis output.
