#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA_DIR="${DATA_DIR:-"$ROOT_DIR/DeezSpoTag.Web/Data"}"
VENV_DIR="${VENV_DIR:-"$DATA_DIR/vibe/.venv"}"
MODELS_DIR="${MODELS_DIR:-"$DATA_DIR/models"}"

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
"$VENV_DIR/bin/python" -m pip install essentia-tensorflow

download() {
  local url="$1"
  local out="$2"
  if [ -f "$out" ]; then
    return 0
  fi
  echo "Downloading $(basename "$out")"
  curl -fL -o "$out" "$url"
}

download "https://essentia.upf.edu/models/autotagging/msd/msd-musicnn-1.pb" "$MODELS_DIR/msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/mood_happy/mood_happy-msd-musicnn-1.pb" "$MODELS_DIR/mood_happy-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/mood_sad/mood_sad-msd-musicnn-1.pb" "$MODELS_DIR/mood_sad-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/mood_relaxed/mood_relaxed-msd-musicnn-1.pb" "$MODELS_DIR/mood_relaxed-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/mood_aggressive/mood_aggressive-msd-musicnn-1.pb" "$MODELS_DIR/mood_aggressive-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/mood_party/mood_party-msd-musicnn-1.pb" "$MODELS_DIR/mood_party-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/mood_acoustic/mood_acoustic-msd-musicnn-1.pb" "$MODELS_DIR/mood_acoustic-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/mood_electronic/mood_electronic-msd-musicnn-1.pb" "$MODELS_DIR/mood_electronic-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-effnet-1.pb" "$MODELS_DIR/genre_discogs400-effnet-1.pb"

echo
echo "Done."
echo "Use these env vars for local runs:"
echo "  VIBE_ANALYZER_PATH=$ROOT_DIR/DeezSpoTag.Web/Tools/vibe_analyzer.py"
echo "  VIBE_ANALYZER_MODELS=$MODELS_DIR"
echo "  VIBE_ANALYZER_PYTHON=$VENV_DIR/bin/python"

