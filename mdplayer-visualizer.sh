#!/usr/bin/env bash
set -euo pipefail

# mdplayer-visualizer.sh — launch the MDPlayer Visualizer UI (Avalonia GUI)
#
# Usage: ./mdplayer-visualizer.sh [--rebuild] [--check] [gui-args ...]
#
#   --rebuild   force a Release build of the CLI + GUI before launching
#   --check     print the resolved binary paths and exit (does not launch)
#
# The GUI drives the mdplayer-render CLI as a child process for preview and
# export. Resolution order (same as DesktopProcessService.ResolveRenderCli):
#   MDPLAYER_RENDER_PATH env var -> this script's repo Release build
#
# Env overrides:
#   MDPLAYER_RENDER_PATH   explicit path to the mdplayer-render executable
#   MDPLAYER_RENDER        alias for the above

ROOT="$(cd "$(dirname "$0")" && pwd)"
REPO="$ROOT/MDPlayer"
CLI="$REPO/src/MDPlayer.Fmp.Cli/bin/Release/net8.0/mdplayer-render"
GUI="$REPO/src/MDPlayer.Fmp.Gui/bin/Release/net8.0/mdplayer-visualizer"

CLI_PATH="${MDPLAYER_RENDER_PATH:-${MDPLAYER_RENDER:-$CLI}}"

usage() {
    sed -n '/^# mdplayer-visualizer.sh/,/^#   MDPLAYER_RENDER /p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
}

REBUILD=0
CHECK=0
ARGS=()
for arg in "$@"; do
    case "$arg" in
        --rebuild) REBUILD=1 ;;
        --check)   CHECK=1 ;;
        --help|-h) usage ;;
        *)         ARGS+=("$arg") ;;
    esac
done

build() {
    echo "Building mdplayer-render + mdplayer-visualizer (Release) ..."
    dotnet build "$REPO/src/MDPlayer.Fmp.Cli" -c Release --nologo -v q
    dotnet build "$REPO/src/MDPlayer.Fmp.Gui" -c Release --nologo -v q
}

if [ "$REBUILD" = 1 ] || [ ! -x "$CLI" ] || [ ! -x "$GUI" ]; then
    build
fi

if [ ! -x "$CLI_PATH" ]; then
    CLI_PATH="$CLI"
fi

if [ "$CHECK" = 1 ]; then
    echo "GUI: $GUI"
    echo "CLI: $CLI_PATH"
    exit 0
fi

if [ ! -x "$CLI_PATH" ]; then
    echo "error: mdplayer-render not found at '$CLI_PATH' (set MDPLAYER_RENDER_PATH or run --rebuild)" >&2
    exit 1
fi

export MDPLAYER_RENDER_PATH="$CLI_PATH"
exec "$GUI" "${ARGS[@]}"
