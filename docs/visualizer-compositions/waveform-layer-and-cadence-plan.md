# Waveform layer transparency and scope cadence — implementation plan

> **Status — implemented (2026-08-13, branch `feature/linux-fmp-renderer`, after `a5deb9fe`).**
> All tasks are implemented and tested; see §8 for the work-unit commits.
>
> - **Spike (Q1) — PASS.** `bg_color: "#080a0f00"` produces a true alpha-0
>   background (97.7 % of pixels) via `buffer_rgba()` in corrscope v0.11.0;
>   waveform line centers are alpha 255 with exact line colors; antialiased
>   edges carry real mid-alpha masks; the parallel renderer path
>   (`Parallelism(parallel=True)`) and the serial path emit byte-identical
>   frames. Plan §4.2 is in force; the §4.8 fallback was not needed. One
>   nuance recorded: alpha-0 background pixels carry white RGB (matplotlib
>   leaves the buffer white behind the transparent facecolor), which is
>   irrelevant to the alpha-driven blend.
> - **Q2 — grid fully removed** (`#10141c00`, alpha 00).
> - **Q3 — auto default** `min(outputFps, 30)` (explicit `--scope-fps`
>   overrides; clamped to 1:1 above output fps).
> - **Q4 — legacy `Compose(corrProcess, …)` kept** with the same mapping and a
>   last-grid reuse buffer (test-pinned).
> - **Q5 — playhead stays under the waveform** (visible through it). The
>   sequential session path now draws playheads before the scope rows, matching
>   `RenderCompositeFrameSingle` (at `a5deb9fe` the session path drew no
>   playheads at all — a deliberate consistency fix, flagged in the commit).
> - **Q6 — CLI-only milestone**; GUI surface deferred.
> - **Q7 — `ScopeOpacity` default 1.0** (transparent background alone
>   subordinates the layer).
> - **Benchmark (Change A, 1280×720 synthetic):** fast path (opacity 1.0,
>   opaque source) 1.13 s / 600 frames; blend path (opacity 0.5) 2.10 s — the
>   blend path restores the scope body from the static layer every frame (the
>   alpha blend leaves transparent pixels' RGB untouched, so a sequential
>   session must not retain previous-frame pixels), which dominates AC-A4's
>   < 5 % estimate. See the report/commit for the full breakdown.
> - **Benchmark (Change B):** run `--perf-scope <ovi>` (added in Task 6) for
>   the 30 Hz-vs-1:1 comparison on a real fixture; `--perf-render
>   --scope-fps N --scope-opacity X` for the overlay-side costs.

Two targeted changes to the Corrscope layer of the visualizer renderer, on top of
commit `a5deb9fe` (branch `feature/linux-fmp-renderer`):

- **Change A — subordinate waveform layer:** eliminate the opaque full-body
  Corrscope background/grid at composition time and blend only the waveform
  signal at a configurable opacity, keeping the integrated
  waveform-under-piano-roll geometry.
- **Change B — decoupled scope cadence:** render the scope at a lower frame rate
  than the output (e.g. 30 Hz scope into 60 Hz video) with monotonic frame
  reuse, so Corrscope stops being the pipeline bottleneck.

The single-pass FFmpeg architecture is **not** redesigned (explicit non-goal);
only the YAML the config writer emits, the in-memory composite, and the frame
index mapping change.

---

## 1. Executive summary

Today every scope frame is an opaque dark rectangle (`bg_color #080a0f`) that
covers the piano-roll body, and Corrscope renders one full frame per output
frame at the output FPS. The scope layer is visually dominant and, at 1080p60,
is the principal wall-clock cost of composition.

This plan makes the scope a **translucent signal layer**: the config writer
emits a transparent background/grid, the compositor blends only the waveform
pixels into the panel body at configurable opacity (Change A), and the scope
render cadence is decoupled from the output cadence via an explicit
output-frame→scope-frame mapping with frame reuse (Change B).

Both changes are bounded by what was verified against the current code; the
remaining unknowns are listed in [Open questions](#10-open-questions) and are
gated behind a spike task (Task 0).

Expected effect: visually subordinate waveforms, roughly halved Corrscope
render cost at 30 Hz / 60 Hz output, unchanged encode path.

---

## 2. Verified baseline

All statements were checked against `a5deb9fe`; paths are relative to
`MDPlayer/` in the repo.

| # | Fact | Evidence |
|---|------|----------|
| 1 | `corrscope-frames.py` drives `CorrScope(cfg, Arguments(outputs=[RawFramesOutputConfig()])).play()` and streams raw **4-byte RGBA** frames (`width*height*4`) to stdout. `RawFramesOutputConfig` subclasses `FFmpegOutputConfig` so Corrscope takes the recording path and renders every frame (`render_subfps = 1`). | `src/MDPlayer.Fmp.Cli/corrscope-frames.py` (docstring + `write_frame`) |
| 2 | Scope renders at resolved scope geometry; scope FPS == output FPS today. | `src/MDPlayer.Fmp.Application/VisualizationComposition.cs` (`PrepareCorrscope`: `Fps = request.Output.FpsNumerator / (double)FpsDenominator`; `RenderWidth/Height = CorrscopeGrid*`); `src/MDPlayer.Fmp.Core/Visualization/Rendering/OverlayLayout.cs` (`CorrscopeGridHeight => ScopeHeight * RowCount`, `DiagnosticGrid`+`HasRoll`: `TimelineHeight = availableContentHeight`, `ScopeHeight = HasScopes ? TimelineHeight : 0`, `DividerHeight = 0`) |
| 3 | `CorrscopeConfigWriter` defaults `BgColor = "#080a0f"`, `GridColor = "#10141c"`, `MidlineColor = "#10141c"`, `VMidline/HMidline = false`, `HideLabels` from overrides; writes `fps:` from `overrides.Fps ?? Defaults.Fps (60)`. | `src/MDPlayer.Fmp.Core/Rendering/Corrscope/CorrscopeConfigWriter.cs` (Defaults + `Write`) |
| 4 | All scope sources today are effectively opaque at placement: `CorrscopeFrameSource.FramesAreOpaque => true`; `ProcessRawFrameSource.FramesAreOpaque => true`; `MasterWaveformFrameSource` / `InteractiveWaveformFrameSource` use the default `FramesAreOpaque => false`, and `PlaceScopeRows` force-sets `alpha = 255` in that branch. No alpha-preserving blend path exists. Playhead is drawn **before** scope rows (opaque waveform covers it). | `src/MDPlayer.Fmp.Core/Visualization/Rendering/CorrscopeFrameSource.cs` (L29), `SinglePassComposer.cs` (L37), `PanelOverlayRenderer.Core.cs` (`PlaceScopeRows` L1624–1651, `RenderCompositeFrameSingle` L454–499) |
| 5 | Strict 1:1 output-frame→scope-frame mapping in one choke point: `VisualizationFrameRenderer.RenderFrame(frameIndex)` → `_scopeFrames.ReadFrame(frameIndex, _scopeBuffer)` → `_overlay.RenderCompositeFrame(...)`. `SequentialSession.RenderNext` and the `SinglePassComposer.Compose(frameRenderer)` producer both route through `ReadScopeFrame`. `CorrscopeFrameSource.ReadFrame` already supports forward skip (discard buffer) and backward seek (**process restart**). | `src/MDPlayer.Fmp.Core/Visualization/Rendering/VisualizationFrameRenderer.cs` (L48–60, L97–128, L189–216); `CorrscopeFrameSource.cs` (L49–74) |
| 6 | Motion blur calls `RenderCompositeFrameSingle(sampledFrame, scopeGrid, …)` for samples N−1…N+1 with the **same** `scopeGrid` argument — the scope frame is read once per output frame and reused across all blur samples. | `PanelOverlayRenderer.Core.cs` (L509–545) |
| 7 | FFmpeg encode input is raw RGBA, output `yuv420p`: `-f rawvideo -pixel_format rgba -video_size WxH -framerate N/D … -pix_fmt yuv420p`. swscale drops alpha in RGB→YUV conversion, so **per-pixel alpha in the composited frame does not survive the encode** — opacity must be baked into RGB in memory. | `SinglePassComposer.cs` (`BuildArguments` L941–979, `BuildMasterOnlyArguments` L884–935) |
| 8 | Installed Corrscope is **v0.11.0** (pipx venv). `RendererConfig.bg_color: str = "#000000"` is passed to `fig.set_facecolor(...)`; axes facecolor is a hardcoded transparent constant `"#00000000"`; frames are captured via `canvas.buffer_rgba()` (RGBA, 4 bytes/px). matplotlib's `to_rgba` accepts 8-digit `#RRGGBBAA` hex, so `bg_color: "#080a0f00"` is a plausible transparent background. **Runtime behavior is unverified** (Agg buffer alpha + parallel renderer path) — see Task 0. No chroma-key or dedicated transparency option exists in this version. | pipx venv `/home/jose/.local/share/pipx/venvs/corrscope` (corrscope v0.11.0): `config.py` L170, `renderer.py` L626/L727/L761/L1080 |
| 9 | Production composition goes through `composer.Compose(audioPath, videoPath, frameRenderer, …)` (the `VisualizationFrameRenderer` path). The process-bridge variant `Compose(corrProcess, …)` has **no production callers** (definition only). | `src/MDPlayer.Fmp.Cli/VisualizationRunner.Core.cs` (L219, L233) |
| 10 | In-process fallback sources derive their audio window from the **frame index** (`OverlayLayout.FrameToSample(frameIndex, …)`) — a mapped index makes them step at scope cadence consistently. The interactive source paints line pixels with default alpha `0x70` (relying on the force-255 placement). | `InteractiveWaveformFrameSource.cs` (L144–181, L133); `MasterWaveformFrameSource.cs` (L212) |
| 11 | Benchmark harness: `benchmarks/MDPlayer.Fmp.Benchmarks/Program.cs` — `--perf-render` (synthetic timeline, `SequentialCompositeSession` with `scopeFramesAreOpaque: true`), `--perf-encode` (ffmpeg rgba→null), `--perf-video <ovi>` (real `visualize --json` run; parses `corrscopeWaitSeconds`, `overlayCpuSeconds`, `scopeFrameReadSeconds`, `rendererIdleSeconds`, `encoderIdleSeconds`, `rendererBlockedSeconds`, `queueWaitSeconds`, `muxFinalizationSeconds`, starvation, bottleneck classification). `BenchmarkSuite1/RendererBenchmark.cs` is a synthetic-only foundation. | `MDPlayer/benchmarks/MDPlayer.Fmp.Benchmarks/Program.cs` (`RunPerfRender` L155, `RunPhase2` L407) |
| 12 | Doc terminology mismatch: docstrings and `CORRSCOPE.md` say "raw RGB0 frames", but the bridge streams RGBA (its own stderr print says `rgba, packed stride`). Cosmetic, but must not confuse the blend implementation. | `corrscope-frames.py` (L72–77), `CORRSCOPE.md` (L33–40), `PanelOverlayRenderer.Core.cs` (L435) |

---

## 3. Goals and explicit non-goals

### Goals

- **G1 (Change A):** Corrscope background/grid no longer contribute an opaque
  layer at composition time; only the waveform signal is composited, at a
  configurable opacity, over the painted panel body.
- **G2 (Change A):** The three scope sources (Corrscope, `MasterWaveformFrameSource`,
  `InteractiveWaveformFrameSource`) produce visually identical waveform layers
  at the same opacity setting.
- **G3 (Change B):** Scope render cadence decoupled from output cadence with an
  explicit, monotonic output-frame→scope-frame mapping and frame reuse; the
  YAML `fps:` changes, the bridge and `render_subfps = 1` do not.
- **G4 (Change B):** Scope render wall-time roughly halved at 30 Hz scope /
  60 Hz output with zero new pipeline starvation.
- **G5:** All changes measurable before/after with the existing benchmark
  harness; no regression in final-frame bytes for unchanged configurations.

### Non-goals

- **N1:** No redesign of the single-pass FFmpeg architecture (one encode, raw
  pipe in, in-memory composite). Explicitly out of scope.
- **N2:** No geometry changes: `CorrscopeGridWidth/Height`, panel topology,
  `DiagnosticGrid`/`HasRoll` integrated body all stay as-is.
- **N3:** No changes to `corrscope-frames.py` (bridge, `render_subfps`, stdout
  transport, EOF tail behavior).
- **N4:** No per-channel opacity, no keyframes/effects for the waveform layer,
  no scope fps per composition (one global scope fps per render).
- **N5:** The playhead semantics are preserved (background time reference); the
  change is only its *visibility* through a translucent waveform.

---

## 4. Change A — subordinate waveform layer

### 4.1 Decision: baked pre-blend, not real per-pixel alpha

**Verified (baseline #7):** the composited frame reaches ffmpeg as raw RGBA and
is converted to `yuv420p`; swscale ignores alpha in that conversion. Per-pixel
alpha cannot survive the encode, so Change A is **(b) a pre-blend that bakes
opacity into RGB inside `PlaceScopeRows`** — the destination alpha byte stays
255 and the encoder path is untouched. This also keeps preview/review/final
byte-identical, since all three consume the same `RenderFrame` bytes.

### 4.2 Config-writer changes (`CorrscopeConfigWriter`)

Emit a transparent background and grid (mask-only frames) when scope
transparency is enabled (default on):

- `bg_color: "#080a0f00"` — 8-digit RGBA hex; alpha 0 background.
- `grid_color: "#10141c00"` and `midline_color: "#10141c00"` — remove the grid
  contribution entirely (a low-alpha grid can be reintroduced later by writing
  e.g. `#10141c22`; the writer already accepts any hex string).
- `v_midline`/`h_midline` stay `false`; `HideLabels` stays as-is (labels are
  part of the scope layer and are already hidden for the grid).
- Keep everything else (line colors, widths, trigger config) unchanged.

This is gated on the **Task 0 spike** confirming that `buffer_rgba()` yields
alpha-0 pixels for alpha-00 backgrounds (see Open question Q1). If the spike
fails, fall back to opaque-bg + opacity-only blend (Section 4.6, Fallback
option) — the compositor design below supports both.

### 4.3 Compositor blend (`PlaceScopeRows`)

Replace the copy/force-alpha logic with a per-pixel alpha blend over the scope
region:

```text
a = (srcAlpha / 255) * ScopeOpacity          // srcAlpha from the scope frame
dst.RGB = src.RGB * a + dst.RGB * (1 - a)    // dst = painted panel body
dst.A   = 255                                // bake; encoder drops alpha anyway
```

- `ScopeOpacity` is a new `PanelOverlayRenderer.Options` value, `double`,
  range `0.05..1.0`, default `1.0` (proposal; see Section 4.5).
- Fast path preserved: when `scopeFramesAreOpaque && ScopeOpacity == 1.0`, keep
  the existing raw copy (no per-pixel work, byte-identical to today).
- Corrscope frames with a transparent background are **not** opaque anymore, so
  `CorrscopeFrameSource.FramesAreOpaque` becomes `false` after the config
  change. The `FramesAreOpaque` flag semantics become: *"the frame's alpha
  channel is a trustworthy mask"*. All three sources set it to `false` after
  Change A (see 4.7). The legacy `ProcessRawFrameSource` keeps `true` until it
  is removed (baseline #9).
- Per-pixel cost is one mul-add per channel over the scope grid only
  (`CorrscopeGridWidth × CorrscopeGridHeight`), bounded by the existing
  `ScopeCopyPlan` row loop — measure in `--perf-render` (acceptance AC-A4).

### 4.4 Playhead interplay

Today the playhead is drawn before the scope rows so the opaque waveform covers
it, "keeping the current-sample signal visible exactly at the playhead column"
(`RenderCompositeFrameSingle`, baseline #4). With a translucent waveform the
playhead line becomes visible *through* the waveform, which matches its
documented role as a background time reference.

**Proposal (Q5): keep the current draw order** (playhead → scope rows →
dynamic content). The waveform sample at the playhead column stays visible and
the playhead reads as a faint time reference. Visual check required; the
alternative (draw playheads after `PlaceScopeRows`) makes the playhead crisp but
covers the current-sample waveform column.

### 4.5 Opacity plumbing

Smallest surface that keeps the request the single source of truth:

| Layer | Change |
|-------|--------|
| Request | `StyleSettings.ScopeOpacity` (`double`, default `1.0`) — style-level, like `Effects`/`Palette`. (Alternative: new `ScopeSettings` record; rejected for now as more plumbing for two fields.) |
| Renderer | `PanelOverlayRenderer.Options.ScopeOpacity` — built in `VisualizationRendererOptions.Build` (`src/MDPlayer.Fmp.Application/VisualizationRendererOptions.cs`), the one place overlay options derive from a request (final/preview/review parity). |
| CLI | `--scope-opacity <0.05..1.0>` in `RenderCommandParser` (same pattern as `--effects`); Application formatter emits it; parity tests pin round-trip (the parser docstring requires this). |
| GUI | (optional, Task 5) slider in `BasicSettingsViewModel` advanced section. |

Validation: `0.05 <= ScopeOpacity <= 1.0`, else argument error (exit 2).

### 4.6 Motion-blur interplay (Change A)

`RenderMotionBlurFrame` averages destination RGB across samples; the blended
scope pixels are part of that average. Linear RGB averaging of
already-blended pixels is correct (the blend is linear in RGB). At
`ScopeOpacity == 1.0` with an opaque source, the fast path keeps output
byte-identical to today, so motion blur is unchanged in the default
configuration.

### 4.7 Fallback sources consistency

Both in-process sources must emit a real alpha mask so the blend treats them
identically to Corrscope:

- `InteractiveWaveformFrameSource`: line pixels currently use default alpha
  `0x70` (baseline #10) — raise line alpha to 255, background stays 0.
- `MasterWaveformFrameSource`: same audit (draw columns with alpha 255, bg 0).
- Both keep `FramesAreOpaque => false`; `PlaceScopeRows` no longer force-sets
  alpha, so the mask survives to the blend.

With that, a parity test can assert that a Corrscope-style mask grid and the
fallback grids composite to identical RGB at the same opacity.

### 4.8 Fallback option (if the Task 0 spike fails)

If Corrscope cannot produce alpha-0 backgrounds: keep `bg_color` opaque, and
blend the whole scope cell at `ScopeOpacity` (`a = ScopeOpacity` for every
pixel). This darkens the panel body behind the waveform (a translucent dark
wash) and partially defeats G1; it is a documented fallback, not the primary
design.

---

## 5. Change B — decoupled scope cadence

### 5.1 Scope fps setting and default

New nullable request field `ViewSettings.ScopeFps` (`double?`, default `null`):

- `null` → **auto: `min(outputFps, 30)`** (proposal, Q3). Rationale: 30 Hz is
  the conventional floor for oscilloscope-style waveforms; at 30 fps output the
  mapping is 1:1 and nothing changes; at 60 fps output the scope load halves.
  Alternative considered: default 1:1 (opt-in decoupling) — zero visual change
  by default, but the headline win (halved scope cost) requires explicit
  opt-in. Decision deferred to review (Q3).
- explicit value → validated `1..outputFps` (values above output fps are
  clamped to 1:1, see 5.3).
- `0` reserved as "follow output exactly" (1:1) if auto becomes default.

Flows into:

- `CorrscopeConfigWriter` `fps:` via `PrepareCorrscope` — currently
  `overrides.Fps = request.Output.FpsNumerator / FpsDenominator`
  (`VisualizationComposition.cs` L52); becomes `scopeFps` (same `FormatFps`
  fractional support, e.g. 59.94 output → scope 29.97).
- `VisualizationFrameRenderer` (mapping) via its constructor; the factory
  (`VisualizationFrameRendererFactory`) already builds both the source and the
  overlay and is the natural place to pass it.

`corrscope-frames.py` is untouched: `RawFramesOutputConfig` keeps forcing
`render_subfps = 1`; only the YAML `fps:` value changes, so Corrscope emits
`scopeFps × duration + 1` frames (baseline #1, tail note in `SinglePassComposer`).

### 5.2 Mapping function and reuse semantics

Choke point: `VisualizationFrameRenderer.ReadScopeFrame` (baseline #5) — the
single method behind `RenderFrame`, `SequentialSession.RenderNext`, and the
`Compose(frameRenderer)` producer.

```text
scopeFrame(out) = (scopeFps >= outputFps)
    ? out                                  // 1:1, no decoupling
    : (long)Math.Floor(out * scopeFps / outputFps)   // monotonic non-decreasing
```

- With `scopeFps < outputFps` the mapped index is monotonic non-decreasing →
  `CorrscopeFrameSource` only ever reads forward (skip-discard path), never
  restarts.
- **Reuse requires a 1-frame cache in `VisualizationFrameRenderer`**
  (`_lastScopeFrameIndex`, `_lastScopeFrame` buffer): calling
  `CorrscopeFrameSource.ReadFrame(i)` twice for the same `i` would trigger a
  **process restart** (backward read, baseline #5). On cache hit, copy the
  cached grid instead of touching the pipe.
- Cache memory: one extra `ScopeFrameByteCount` buffer (≈ grid mosaic RGBA,
  e.g. ~8–16 MB at 1080p) — acceptable; note it in the renderer.
- EOF tail: max mapped index `floor((TotalFrames-1) * scopeFps / outputFps)`
  stays within `scopeFps × duration + 1`, so the existing "freeze last scope
  frame during the audio tail" path (`SinglePassComposer` L198–200) is
  unchanged and no new starvation appears.

### 5.3 Behavior when outputFps <= scopeFps

1:1 (no mapping). Skipping ahead (`scopeFrame > out`) would waste Corrscope
frames and is explicitly avoided.

### 5.4 Fallback sources under the mapping

`MasterWaveformFrameSource` and `InteractiveWaveformFrameSource` derive their
audio window from the frame index (baseline #10). Passing the *mapped* index
makes them step at scope cadence, matching Corrscope's behavior frame-for-frame
(parity requirement G2). Both are in-process random-access — a mapped index
breaks nothing; the cache is a no-op for them (they are cheap) but harmless.

### 5.5 Motion blur interplay (Change B)

`RenderMotionBlurFrame` reuses the same `scopeGrid` across samples N−1…N+1
(baseline #6). With mapping, all blur samples get the same *mapped* scope frame
(computed once per output frame). Consequence: a motion-blurred frame's scope
content is slightly stale temporally (blur samples should ideally span the
scope cadence). Accepted; noted in risks (R4).

### 5.6 Legacy process-bridge `Compose(corrProcess, …)`

No production callers (baseline #9). If it stays (tests), it is *also* coupled
to YAML fps and would freeze the scope mid-video at 30/60 — it needs the same
reuse logic (keep a persistent last-grid and re-submit it for unchanged mapped
indices) or must be deleted. Decision deferred to implementation (Q4).

### 5.7 GUI preview impact

Preview renders through `RenderFrame` at the preview FPS (request output FPS).
The mapping applies automatically at the choke point: at 60 fps preview with
auto scope fps the preview shows reused frames; at ≤ 30 fps it is 1:1 and
visually identical to today. The interactive source follows the same mapping,
so preview and final stay consistent.

---

## 6. Benchmark plan

Harness: `benchmarks/MDPlayer.Fmp.Benchmarks/Program.cs` (baseline #11).

| Mode | Before | After | Metric of interest |
|------|--------|-------|--------------------|
| `--perf-video <ovi> --overwrite` (real pipeline, `--json`) | current HEAD | same command + `--scope-fps 30` | `corrscopeWaitSeconds`, `scopeFrameReadSeconds`, `overlayCpuSeconds`, `starvations`, `encoderIdleSeconds`, `rendererBlockedSeconds`, wall time |
| `--perf-render` (synthetic, no Corrscope) | current | + `--scope-fps` option simulating reuse (skip re-read, reuse grid every Nth frame) | overlay wall time, allocated bytes/frame, blend overhead |
| `--perf-encode` | current | current | sanity: encode path untouched (baseline of encoder cost) |

New harness work (Task 6):

- `--perf-render --scope-fps N`: emulate mapped reads (reuse the same
  `scopeGrid` buffer for `N` consecutive `RenderNext` calls) to isolate the
  overlay-side cost of reuse.
- `--perf-scope` (full-pipeline pair): run `--perf-video` with `--scope-fps 30`
  vs `--scope-fps 60` (1:1) on the same fixture and emit a JSON comparison.

Success criteria:

- Scope render wall-time roughly halved at 30/60: `scopeFrameReadSeconds` ≈ 50 %
  of baseline and `corrscopeWaitSeconds` reduced (the pipe is no longer fed at
  output cadence).
- `StarvationCount == 0` before and after; `encoderIdleSeconds` /
  `rendererBlockedSeconds` within noise (encode path untouched, G4/N1).
- Overlay blend overhead (Change A) measured via `--perf-render` at
  `ScopeOpacity 1.0` fast path vs `0.5`: target < 5 % frame-time increase.
- Final video decodes to the same frame count and duration before/after.

---

## 7. Risks and mitigations

| # | Risk | Mitigation |
|---|------|------------|
| R1 | Corrscope audio-triggered rendering produces non-uniform frame timing; at 30 Hz YAML each rendered frame consumes ~33 ms of audio, so waveform detail differs from a 60 Hz capture (trigger windows are audio-driven). | Accepted; this is the point of the change. The queue pipeline already tolerates non-uniform arrival (bounded queue, starvation counting). Mapping is index-based and monotonic, so timing jitter cannot reorder frames. |
| R2 | Frame reuse makes fast waveform motion steppy (30 Hz updates). | Scope fps is configurable up to 1:1; auto default is `min(output, 30)`; risk is highest for percussion (rhythm stems already use transient-oriented triggers). Visual acceptance check on a percussive track. |
| R3 | Alpha through the pipe: `yuv420p` drops alpha (verified) — if the blend is not baked, the waveform silently renders at full opacity. | Blend bakes into RGB in `PlaceScopeRows`; acceptance AC-A1 asserts alpha byte == 255 and correct RGB for known inputs. |
| R4 | Motion blur samples reuse one mapped scope frame (slightly stale scope across N−1…N+1). | Bounded by blur window (≤ 8 samples ≈ 133 ms at 60 fps); same scope grid is already reused today across samples, so the change only affects *which* scope frame; documented. |
| R5 | Fallback sources diverge from Corrscope (different alpha encodings). | All sources emit alpha-255 lines / alpha-0 background after Change A; parity test (AC-A5) pins identical composite output. |
| R6 | Transparent `bg_color` unverified at runtime (Agg buffer + parallel renderer path). | Task 0 spike first; documented fallback (4.8) if it fails. Grid alignment / geometry is unaffected either way (colors only). |
| R7 | `CorrscopeFrameSource` process restart on repeated mapped indices (cache missing). | Mandatory 1-frame cache in `VisualizationFrameRenderer` (5.2); unit test asserts `ReadFrame` is not called twice with the same index through the renderer. |
| R8 | Legacy `Compose(corrProcess, …)` would starve at 30/60. | No production callers (verified); either apply reuse or delete — decided in Task 3 (Q4). |
| R9 | Blend cost in the hot path. | Fast path at opaque + opacity 1.0; bounded region; measured in `--perf-render`. |

---

## 8. Task breakdown (work-unit commits)

Each commit is a reviewable unit (behavior + tests + docs together,
conventional-commit message). Order matters; every commit leaves the repo
functional.

**Task 0 — Spike (no commit): transparent Corrscope background.**
Render one frame via `corrscope-frames.py` with `bg_color: "#080a0f00"` and
inspect the raw buffer: background alpha == 0? waveform alpha == 255? same
result with `Parallelism(parallel=True)`? Record findings; decide 4.2 vs 4.8.

**Task 1 — `feat(core): blend scope waveform over panel body at configurable opacity`**
- `PanelOverlayRenderer.Options.ScopeOpacity` (default 1.0, validation 0.05–1.0).
- `PlaceScopeRows` alpha-aware blend (4.3) + fast path; `FramesAreOpaque`
  semantics doc update.
- Tests: blend math for known src/dst/opacity; alpha byte stays 255; fast path
  byte-identical to pre-change copy for opaque input; motion-blur path at
  opacity 1.0 byte-identical (regression).
- Docs: `CORRSCOPE.md` note (RGB0→RGBA terminology + blend).

**Task 2 — `feat(core): emit transparent corrscope background and grid`** (after Task 0)
- `CorrscopeConfigWriter` defaults → 8-digit alpha-00 colors (4.2); existing
  `BgColor/GridColor` overrides keep working.
- Tests: YAML contains `bg_color`/`grid_color` with 00 alpha; overrides still
  win. Verify one real frame's buffer via the spike script (manual evidence).

**Task 3 — `feat(core): decouple scope render cadence from output cadence`**
- `ViewSettings.ScopeFps` (nullable), auto = `min(outputFps, 30)` (Q3).
- `VisualizationFrameRenderer`: mapping in `ReadScopeFrame` + 1-frame cache;
  ctor takes `scopeFps`.
- `PrepareCorrscope`: `overrides.Fps = scopeFps` (YAML only; bridge untouched).
- Fallback parity: line alpha 255 in `InteractiveWaveformFrameSource` and
  `MasterWaveformFrameSource` (4.7).
- Tests: mapping monotonicity + reuse (0,0,1,1,2,2… at 30/60); 1:1 when
  scopeFps >= outputFps; cache prevents duplicate `ReadFrame`; sequential
  compose never reads backward; fallback sources composite identically to a
  Corrscope mask grid (parity).
- Legacy `Compose(corrProcess, …)`: apply reuse or delete (Q4).

**Task 4 — `feat(cli): add --scope-fps and --scope-opacity options`**
- `RenderCommandParser` cases + validation; Application formatter emits both;
  parity tests pin round-trip (existing parity-test contract).
- Request serializer round-trip test (defaults omitted, non-defaults written).

**Task 5 — `feat(gui): expose scope opacity and cadence in basic settings`**
- `BasicSettingsViewModel`: opacity slider (advanced section) + scope fps
  dropdown (Auto / Follow output / 30 / 60). Optional if GUI is deferred —
  CLI-only is a valid milestone (mark in PR).

**Task 6 — `bench: add scope-cadence benchmark and record before/after`**
- `--perf-render --scope-fps N` reuse emulation; `--perf-scope` comparison
  runner (6).
- Record before/after table (same machine, same fixture, Release build) into
  this doc or a benchmark notes file; paste JSON summaries into the PR.

**Task 7 — `docs(visualizer-compositions): document waveform layer and scope cadence`**
- This plan: mark decisions as implemented, resolve open questions; cross-link
  from `docs/visualizer-compositions/README.md`; update `CORRSCOPE.md` options
  table (`--scope-fps`, `--scope-opacity`).

---

## 9. Acceptance criteria

### Change A

- **AC-A1:** Given a scope grid with known RGB/alpha and `ScopeOpacity = 0.5`,
  the composited frame RGB equals `src*0.5 + dst*0.5` per channel (unit test);
  destination alpha byte == 255.
- **AC-A2:** With `ScopeOpacity = 1.0` and an opaque source, output is
  byte-identical to pre-change output (regression test, fast path).
- **AC-A3:** Generated YAML contains `bg_color`/`grid_color` with 00 alpha
  (Task 2 test); spike evidence shows alpha-0 background pixels in the real
  bridge stream.
- **AC-A4:** `--perf-render` blend overhead at opacity 0.5 < 5 % vs 1.0 fast
  path.
- **AC-A5:** Master and interactive fallback sources composite to the same RGB
  as a Corrscope-style mask grid at equal opacity (parity test).
- **AC-A6:** Visual check: waveform reads as a subordinate layer over the piano
  roll; playhead visible through the waveform; stills compared at t = 8 s
  (same convention as `docs/visualizer-compositions/README.md`).

### Change B

- **AC-B1:** At output 60 / scope 30, mapped indices are 0,0,1,1,2,2,… (unit
  test); at scope >= output they are 1:1.
- **AC-B2:** Sequential compose never issues a backward `ReadFrame` (test
  doubles/recording source); the cache serves repeated mapped indices without
  a Corrscope restart.
- **AC-B3:** YAML `fps:` equals the scope fps; `corrscope-frames.py` diff is
  empty; `render_subfps = 1` path unchanged.
- **AC-B4:** `--perf-video` at scope 30: `scopeFrameReadSeconds` ≈ 50 % of
  baseline; `StarvationCount == 0`; `encoderIdleSeconds` /
  `rendererBlockedSeconds` within noise; final video frame count and duration
  identical.
- **AC-B5:** Preview at 30 fps is byte-identical to pre-change; preview at
  60 fps follows the mapping (no restart storms — GUI scrub of a 60 fps
  preview stays responsive).

---

## 10. Open questions

Everything below was **not** settled by the code; each is marked with where it
gets resolved.

- **Q1 (blocking, Task 0):** Does `bg_color: "#080a0f00"` actually produce
  alpha-0 background pixels in corrscope v0.11.0's `buffer_rgba()` output,
  including the parallel renderer path (`Parallelism(parallel=True)`)? The
  config surface accepts 8-digit hex (verified), the runtime behavior is not.
- **Q2:** Should the grid be fully removed (`#10141c00`) or kept at low alpha
  (e.g. `#10141c22`)? Proposal: fully removed for G1; visual decision during
  Task 2.
- **Q3:** Default for `ScopeFps`: auto `min(outputFps, 30)` vs opt-in 1:1.
  Proposal: auto. Requires review sign-off because it changes default output
  (R2).
- **Q4:** Legacy `SinglePassComposer.Compose(corrProcess, …)`: kept with reuse
  logic, or deleted? No production callers (verified); check test references in
  Task 3.
- **Q5:** Playhead draw order with a translucent waveform: proposal is keep
  current order (visible-through); alternative is draw-after-scope. Visual
  check in AC-A6.
- **Q6:** GUI surface scope for Task 5 (opacity slider + fps dropdown in
  `BasicSettingsViewModel`) — is CLI-only acceptable for the first milestone?
- **Q7:** Default `ScopeOpacity` value: 1.0 (transparent bg alone subordinates
  the layer) vs e.g. 0.85 (additional muting). Proposal: 1.0, keep the setting
  for user preference.
