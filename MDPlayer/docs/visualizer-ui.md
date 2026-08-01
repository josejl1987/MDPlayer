# MDPlayer Visualizer UI

Desktop companion for the MDPlayer visualization CLI (`mdplayer-render`). The UI
configures, previews and launches final renders through the same request model
and renderer as the CLI — every state in the UI corresponds to a serializable
`VisualizationRequest`, every final render is reproducible from that request,
and every accurate preview uses the same visualization implementation as final
output.

## Projects

```
MDPlayer/src/
    MDPlayer.Fmp.Core/          emulation, capture, rendering (unchanged entry points)
    MDPlayer.Fmp.Application/   shared contracts, presets, validation, tool
                                requirements, canonical command formatting,
                                request JSON, project store, preview session
    MDPlayer.Fmp.Cli/           mdplayer-render (visualize/render/inspect/analyze
                                + new plan/preview + --request-json + --progress jsonl)
    MDPlayer.Fmp.Gui/           mdplayer-visualizer (Avalonia 11 desktop app)

MDPlayer/tests/
    MDPlayer.Fmp.Tests/          existing CLI/Core tests + CLI↔request parity
                                 + visualizer architecture invariants
    MDPlayer.Fmp.Application.Tests/  contract/preset/validator/formatter/serializer
                                     /project-store/cache/invalidation tests
    MDPlayer.Fmp.Gui.Tests/      view-model + fake-CLI export-process tests
```

Dependency direction (spec §5.3):

```
MDPlayer.Fmp.Core
        ↑
MDPlayer.Fmp.Application
       ↑              ↑
MDPlayer.Fmp.Cli   MDPlayer.Fmp.Gui
```

`MDPlayer.Fmp.Core` and `MDPlayer.Fmp.Application` contain no Avalonia
references. The GUI references Application only (never CLI parser internals, never
Core, never chip-specific IDs).

## The request model

`VisualizationRequest` (Application/Contracts) is the single authoritative,
immutable contract. The GUI produces new snapshots per edit; the CLI accepts the
same shape through `--request-json`. `VisualizationCommandFormatter` renders the
canonical command in a stable option order (spec §20.2) in Compact or
FullyResolved form; CLI-parity tests prove formatter output round-trips through
the parser to the same options as loading the request JSON directly.

Preset values (Preview/Balanced/Final/Diagnostic) live only in
`VisualizationPresetCatalog`; request validation and feature-gated tool
requirements are pure functions of the request.

## CLI additions

```
mdplayer-render visualize --request-json request.json [options after --request-json override]
mdplayer-render visualize ... --progress jsonl        # JSON-lines progress on stdout
mdplayer-render plan --request-json request.json [--timeline T] [--timeline-out T] --json
mdplayer-render preview --request-json request.json --time 42.5 --output still.png [--json]
mdplayer-render preview --request-json request.json --motion --output-dir DIR [--start --duration --fps --max-width --max-height]
mdplayer-render visualize --include-track ID / --exclude-track ID / --channels custom
```

`plan` resolves the concrete layout (Auto → concrete), applies include/exclude
track filtering, validates, computes tool requirements, representative scrub
points and estimates, and emits one JSON document. `preview` renders a still
(accurate or layout fidelity) or a motion frame sequence with the same renderer
the final composition uses. `--timeline-out` persists a captured timeline so
later `plan`/`preview` calls reuse it instead of re-capturing.

## GUI

`mdplayer-visualizer [input-file]` — one window: settings pane (Basic / Content /
Style / Metadata / Advanced), aspect-locked preview with fidelity and
approximation badges, timeline transport with representative points, canonical
command panel with Copy, tool-status bar, and stage-by-stage export progress.

- **Preview**: the process-based `CliPreviewSession` (Application) drives
  `mdplayer-render plan/preview` with request JSON through an argument-safe child
  process, keeping the session workspace (captured timeline, frames) on disk.
  Cancellation kills the process tree; results are revision-checked so an
  obsolete preview can never replace a newer one; failures keep the last valid
  frame.
- **Export**: `ExportProcessService` runs
  `mdplayer-render visualize --request-json … --progress jsonl`, streams
  structured events into the export progress model, applies a 3 s grace period
  on cancel and then kills the process tree.
- **Projects**: `.mdpviz.json` with relative paths, dirty tracking, and a
  30 s recovery autosave (restore offered on next launch).
- **Accessibility**: full keyboard shortcuts (spec §29), visible focus, reduced
  motion preference pauses automatic preview updates without touching export
  state.

## Build & test

```bash
cd MDPlayer
NUGET_PACKAGES=$PWD/nuget-packages dotnet build MDPlayer.Fmp.sln
NUGET_PACKAGES=$PWD/nuget-packages dotnet test MDPlayer.Fmp.sln
```

Running the GUI requires a display; the CLI `plan`/`preview` commands work
headless and are what the GUI shells out to.

## Documented deviations from the product spec

- **In-process preview**: spec §5.3 prefers in-process preview through shared
  application services. v1 uses the CLI `plan`/`preview` commands as child
  processes (same renderer + layout planner, argument-safe, cancellable,
  headless-testable). `IVisualizationPreviewSession` is the stable seam; an
  in-process implementation can be dropped in without GUI changes.
- **Motion preview** plays PNG frame sequences from the session workspace rather
  than a single animated container.
- **Analysis in previews** is skipped (the still/motion renderer notes
  "Analysis overlay omitted in preview" as an approximation); analysis affects
  final export only.
- **Custom channel mode** maps to `--channels all` + explicit
  `--include-track/--exclude-track` lists; the CLI applies the filtering when
  planning. `--scope-height`/`--timeline-height` are CLI-only and not part of
  the request model (spec's model does not carry them).
