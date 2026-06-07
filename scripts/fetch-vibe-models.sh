#!/usr/bin/env bash
set -euo pipefail

MODELS_DIR="${MODELS_DIR:-DeezSpoTag.Web/Tools/models}"
CONNECT_TIMEOUT_SECONDS="${CONNECT_TIMEOUT_SECONDS:-20}"
MAX_TIME_SECONDS="${MAX_TIME_SECONDS:-300}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-4}"

mkdir -p "$MODELS_DIR"

download() {
  local file="$1"
  local url="$2"
  local target="$MODELS_DIR/$file"
  local tmp="$target.tmp"
  local attempt=1

  if [ -s "$target" ]; then
    return 0
  fi

  while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
    echo "Downloading $file ($attempt/$MAX_ATTEMPTS)"
    if curl -fL --connect-timeout "$CONNECT_TIMEOUT_SECONDS" --max-time "$MAX_TIME_SECONDS" -o "$tmp" "$url"; then
      mv "$tmp" "$target"
      return 0
    fi

    rm -f "$tmp"
    if [ "$attempt" -eq "$MAX_ATTEMPTS" ]; then
      echo "Failed to download $file from $url after $MAX_ATTEMPTS attempts." >&2
      return 1
    fi

    sleep $((attempt * 5))
    attempt=$((attempt + 1))
  done
}

download "msd-musicnn-1.pb" "https://essentia.upf.edu/models/feature-extractors/musicnn/msd-musicnn-1.pb"
download "mood_happy-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_happy/mood_happy-msd-musicnn-1.pb"
download "mood_sad-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_sad/mood_sad-msd-musicnn-1.pb"
download "mood_relaxed-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_relaxed/mood_relaxed-msd-musicnn-1.pb"
download "mood_aggressive-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_aggressive/mood_aggressive-msd-musicnn-1.pb"
download "mood_party-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_party/mood_party-msd-musicnn-1.pb"
download "mood_acoustic-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_acoustic/mood_acoustic-msd-musicnn-1.pb"
download "mood_electronic-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_electronic/mood_electronic-msd-musicnn-1.pb"
download "voice_instrumental-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/voice_instrumental/voice_instrumental-msd-musicnn-1.pb"
download "tonal_atonal-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/tonal_atonal/tonal_atonal-msd-musicnn-1.pb"
download "danceability-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/danceability/danceability-msd-musicnn-1.pb"
download "deam-msd-musicnn-2.pb" "https://essentia.upf.edu/models/classification-heads/deam/deam-msd-musicnn-2.pb"
download "discogs-effnet-bs64-1.pb" "https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb"
download "approachability_regression-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/approachability/approachability_regression-discogs-effnet-1.pb"
download "engagement_regression-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/engagement/engagement_regression-discogs-effnet-1.pb"
download "genre_discogs400-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.pb"
download "genre_discogs400-discogs-effnet-1.json" "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.json"

echo "Vibe model files are ready in $MODELS_DIR."
