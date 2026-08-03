# YM2608-LLE WIP Preservation

This file records the preservation of uncommitted OPNA / YM2608-LLE work
performed before starting the Furnace-native implementation sequence. The
preserved work is **not** part of the Furnace implementation.

## Preservation record

- Date: 2026-08-03
- Branch: `feature/linux-fmp-renderer`
- HEAD: `98fefea0e6232b8a65008ecf6087a454877431ed`
- Patch path: `../ym2608-lle-pre-furnace-wip.patch` (git diff --binary of the
  tracked working tree)
- Untracked archive path: `../ym2608-lle-pre-furnace-untracked/` (relative
  paths preserved)

## Original `git status --short`

```
 M .gitignore
 M MDPlayer/.gitignore
?? .claude/
?? .mcp.json
?? .repowise/
?? 12 - Ken's Theme.analysis/
?? 12 - Ken's Theme.vgz
?? MDPlayer/native/MDPlayer.OpnaNative/src/generated/
?? MDPlayer/native/MDPlayer.OpnaNative/tools/
?? docs/opna-lle-implementation-notes.md
```

The tracked `.gitignore` edit (repo-root OPNA runtime ignore rules and the
`MDPlayer/.gitignore` switch from `runtimes/*/native/*.{so,dll}` to
`native/MDPlayer.OpnaNative/build*/` ignores) is captured in the patch. The
untracked `generated/` resampler `.inc` files and the
`tools/generate_resampler_coefficients.py` tool are archived at their relative
paths; the pre-existing `docs/opna-lle-implementation-notes.md` (which documents
an earlier `/home/jose/opna-wip-archive/` preservation and the superseded
`nopna.c` worktree) is archived as well.

## Statement

The preserved work above is recoverable original OPNA/YM2608-LLE WIP and is
**not** part of the Furnace implementation. It is preserved outside the
repository and is not committed by Prompt 1.

## Legacy FMP baselines (Prompt 1)

Determinism was confirmed with two independent renders producing identical
output before recording:

- Real track: local OVI fixture `Field.OVI` (user-provided; not tracked).
- Render engine: MDSound path via the CLI `mdplayer-render batch` command.
- Command (public CLI):
  `mdplayer-render batch <dir> --output-dir <dir> --overwrite`
- Baseline SHA-256 of rendered WAV (stereo, 44.1 kHz):
  `afab4bddc0debda3f01df64857545d6796cea2b37cc16ec87fdfa6975ce511f5`
- Two runs produced byte-identical output; the hash is the same across both.

Baseline tests added in `MDPlayer.Fmp.Tests/FmpLegacyBaselineTests.cs`:
1. `DefaultBackend_SelectsFmpForRealOvi` — real .ovi selects the FMP backend.
2. `RealFmpFile_RenderIsDeterministicAcrossTwoRuns` — two renders identical.
3. `LoopBoundary_AdvancesCurrentLoop` — loop machinery + YM2608 writes.
4. `Ppz8Sink_BankLoadAndRender_IsWired` — MDSound PPZ8 bank path.
5. `SsgGain_ConversionAndRouting_ArePinned` — SSG gain db→unit mapping + routing.
6. `Rhythm_SinkHandlesKeyOnAndVolume` — rhythm key-on + volume routing.

All six pass. MDSound remains the default backend; no native YM2608
implementation is added in Prompt 1.