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
download "https://essentia.upf.edu/models/classification-heads/voice_instrumental/voice_instrumental-msd-musicnn-1.pb" "$MODELS_DIR/voice_instrumental-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/tonal_atonal/tonal_atonal-msd-musicnn-1.pb" "$MODELS_DIR/tonal_atonal-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/danceability/danceability-msd-musicnn-1.pb" "$MODELS_DIR/danceability-msd-musicnn-1.pb"
download "https://essentia.upf.edu/models/classification-heads/deam/deam-msd-musicnn-2.pb" "$MODELS_DIR/deam-msd-musicnn-2.pb"
download "https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb" "$MODELS_DIR/discogs-effnet-bs64-1.pb"
download "https://essentia.upf.edu/models/classification-heads/approachability/approachability_regression-discogs-effnet-1.pb" "$MODELS_DIR/approachability_regression-discogs-effnet-1.pb"
download "https://essentia.upf.edu/models/classification-heads/engagement/engagement_regression-discogs-effnet-1.pb" "$MODELS_DIR/engagement_regression-discogs-effnet-1.pb"
download "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.pb" "$MODELS_DIR/genre_discogs400-discogs-effnet-1.pb"
download "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.json" "$MODELS_DIR/genre_discogs400-discogs-effnet-1.json"

echo
echo "Done."
echo "Use these env vars for local runs:"
echo "  DEEZSPOTAG_DATA_DIR=$DATA_DIR"
echo "  DEEZSPOTAG_CONFIG_DIR=$DATA_DIR"
echo "  VIBE_ANALYZER_PATH=$ROOT_DIR/DeezSpoTag.Web/Tools/vibe_analyzer.py"
echo "  VIBE_ANALYZER_MODELS=$MODELS_DIR"
echo "  VIBE_ANALYZER_PYTHON=$VENV_DIR/bin/python"
