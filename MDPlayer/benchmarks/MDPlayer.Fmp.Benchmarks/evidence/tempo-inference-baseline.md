# Tempo-Inference Baseline Evidence

Frozen pre-optimization baseline for `tempo-inference-optimization`, captured
**before** any hoist/prune/instrumentation change lands (Task TI-BASE).

## Command

```
dotnet run -c Release --project MDPlayer/benchmarks/MDPlayer.Fmp.Benchmarks -- --midi-fixture "21 Master Ninja.vgz"
```

## Receipt (mdplayer.midi-fixture-receipt/v1)

- **Input fixture**: `21 Master Ninja.vgz`
  - sha256: `0474f69dfd870f2057c19fa4852131170c850d27d3a00b176f60748505ac017d`
  - sizeBytes: 18627
- **Duration**: 93.93s (endSample 4142275 @ 44100)
- **sourceEvents**: 5288
- **emittedMidiEvents**: 0 (all events routed to output bytes; no note-on semantic split)
- **output sha256**: `c8f7e9583a71834ec2795f1df04892c3efb5847aef30653a14dbca10c7ed6c2b`
- **output sizeBytes**: 81869
- **capture phase**: wallMilliseconds 2037, allocatedBytes 550342680

## Wall time (Release binary, process-only, after build)

| Run | Wall (s) |
|-----|----------|
| 1    | 8.588 |
| 2    | 8.942 |
| 3    | 8.005 |
| median | ~8.59 |

> Wall includes ~2.0s capture + the full SymbolicTempoInference search. Subsequent
> optimization runs MUST be measured the same way (same fixture, same binary) and
> compared to this baseline. No speedup claim is made without this baseline.