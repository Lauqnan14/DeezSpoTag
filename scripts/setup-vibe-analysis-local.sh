#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA_DIR="${DATA_DIR:-"$ROOT_DIR/DeezSpoTag.Workers/Data"}"
VENV_DIR="${VENV_DIR:-"$DATA_DIR/analysis/vibe/.venv"}"
MODELS_DIR="${MODELS_DIR:-"$DATA_DIR/analysis/models"}"
ESSENTIA_TF_PACKAGE="${ESSENTIA_TF_PACKAGE:-essentia-tensorflow==2.1b6.dev1389}"

echo "Data dir:    $DATA_DIR"
echo "Venv dir:    $VENV_DIR"
echo "Models dir:  $MODELS_DIR"

mkdir -p "$DATA_DIR" "$MODELS_DIR"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 not found. Install Python 3 first." >&2
  exit 1
fi

if [ ! -x "$VENV_DIR/bin/python" ]; then
  python3 -m venv "$VENV_DIR"
fi

"$VENV_DIR/bin/python" -m pip install --upgrade pip
"$VENV_DIR/bin/python" -m pip install "$ESSENTIA_TF_PACKAGE"

MODELS_DIR="$MODELS_DIR" "$ROOT_DIR/scripts/fetch-vibe-models.sh"

echo
echo "Done."
echo "Use these env vars for local runs:"
echo "  DEEZSPOTAG_DATA_DIR=$DATA_DIR"
echo "  DEEZSPOTAG_CONFIG_DIR=$DATA_DIR"
echo "  VIBE_ANALYZER_PATH=$ROOT_DIR/DeezSpoTag.Web/Tools/vibe_analyzer.py"
echo "  VIBE_ANALYZER_MODELS=$MODELS_DIR"
echo "  VIBE_ANALYZER_PYTHON=$VENV_DIR/bin/python"
