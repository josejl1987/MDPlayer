#!/usr/bin/env bash
set -euo pipefail

python3 -m venv .analysis-test-venv
.analysis-test-venv/bin/python -m pip install \
  -r tools/music-analysis/requirements.lock

.analysis-test-venv/bin/python -m pytest \
  tools/music-analysis

MDPLAYER_ANALYSIS_PYTHON="$PWD/.analysis-test-venv/bin/python" \
  dotnet test \
  MDPlayer/tests/MDPlayer.Fmp.Tests \
  --filter Analysis
