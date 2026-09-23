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
  local expected_sha256="$3"
  local target="$MODELS_DIR/$file"
  local tmp="$target.tmp"
  local attempt=1

  if [ -s "$target" ]; then
    if printf '%s  %s\n' "$expected_sha256" "$target" | sha256sum -c - >/dev/null; then
      return 0
    fi
    echo "Cached model checksum mismatch: $file; downloading a verified replacement." >&2
    rm -f "$target"
  fi

  while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
    echo "Downloading $file ($attempt/$MAX_ATTEMPTS)"
    if curl -fL --connect-timeout "$CONNECT_TIMEOUT_SECONDS" --max-time "$MAX_TIME_SECONDS" -o "$tmp" "$url"; then
      if printf '%s  %s\n' "$expected_sha256" "$tmp" | sha256sum -c - >/dev/null; then
        mv "$tmp" "$target"
        return 0
      fi
      echo "Downloaded model checksum mismatch: $file" >&2
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

download "msd-musicnn-1.pb" "https://essentia.upf.edu/models/feature-extractors/musicnn/msd-musicnn-1.pb" "cdea0722bcee7f731286843f2233e3aa69887bb5c3e2dce011eff55f38d04f3e"
download "mood_happy-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_happy/mood_happy-msd-musicnn-1.pb" "d7382bc60304ea4578c298222968cd8d600c31252c7bf3e90b1f728ebb3ec36d"
download "mood_sad-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_sad/mood_sad-msd-musicnn-1.pb" "a5e908cf7f59e8c379ff7c7d138dd85416985fddaebb5de14ca4193200411f61"
download "mood_relaxed-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_relaxed/mood_relaxed-msd-musicnn-1.pb" "1252d28ca7d2204e34e0cdf84a00aa2bc9627a87bdcf923df3aad39cfa69d2d9"
download "mood_aggressive-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_aggressive/mood_aggressive-msd-musicnn-1.pb" "3b6eb5645e4b47a2ceb28ef3f8612f224640c583048770791b9fc6e8e5627a67"
download "mood_party-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_party/mood_party-msd-musicnn-1.pb" "765b096300ee1d92103cb0a122fc12c33882166fb94d37875284e82ce06322a1"
download "mood_acoustic-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_acoustic/mood_acoustic-msd-musicnn-1.pb" "519ee3af8210fe32e021002a0094546aeb6fb5a59d22b7d53c48e4ee1ac9e6cc"
download "mood_electronic-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/mood_electronic/mood_electronic-msd-musicnn-1.pb" "86c109b504fc6cf666c7513d684381a594218a552c3c954f212dd3a9d0c6cdc5"
download "voice_instrumental-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/voice_instrumental/voice_instrumental-msd-musicnn-1.pb" "eb762cc7ee6751b2ea32179d3716e2d60a1d1a9e615b7e3b8be8a6f79d71675e"
download "tonal_atonal-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/tonal_atonal/tonal_atonal-msd-musicnn-1.pb" "45a36e68a70a6692a60434ee3ae81df9bd5c402204fb04c3355c39f9e3d24aaf"
download "danceability-msd-musicnn-1.pb" "https://essentia.upf.edu/models/classification-heads/danceability/danceability-msd-musicnn-1.pb" "874a4b86afc9e12de3f15a47baf9ff1ac676ace109c56203e26103f2259eb95e"
download "deam-msd-musicnn-2.pb" "https://essentia.upf.edu/models/classification-heads/deam/deam-msd-musicnn-2.pb" "beb5eeb0909266eeb78b8d6bb1323b10829cf2fe55e3c01a13fa1846fa98b371"
download "discogs-effnet-bs64-1.pb" "https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb" "3ed9af50d5367c0b9c795b294b00e7599e4943244f4cbd376869f3bfc87721b1"
download "approachability_regression-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/approachability/approachability_regression-discogs-effnet-1.pb" "7ffc208865426fb3aa2842f676b42fc6128282088c9b1d1fd2aba14b17cd121c"
download "engagement_regression-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/engagement/engagement_regression-discogs-effnet-1.pb" "43031d40b3a380e1995c8495a108d14ca74f620d924fad9b28df4189c84d20c5"
download "genre_discogs400-discogs-effnet-1.pb" "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.pb" "3885ba078a35249af94b8e5e4247689afac40deca4401a4bc888daf5a579c01c"
download "genre_discogs400-discogs-effnet-1.json" "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.json" "2d367319d9b782ffa10f69abf0e805b3ac4e10899025e5bdbaceda3919b243e0"
# Discogs519/MAEST acoustic genre branch (default, VIBE_GENRE_MODEL=discogs519).
download "discogs-maest-30s-pw-519l-2.pb" "https://essentia.upf.edu/models/feature-extractors/maest/discogs-maest-30s-pw-519l-2.pb" "92783feb21187443d058b4f16d7a76f47888d43fbdc7a28e8bcc8e024603bd20"
download "discogs-maest-30s-pw-519l-2.json" "https://essentia.upf.edu/models/feature-extractors/maest/discogs-maest-30s-pw-519l-2.json" "83240aa553ffb491b0ec5a24565eb612553e5f38da5207403c25b890c5b34acd"
download "genre_discogs519-discogs-maest-30s-pw-519l-1.pb" "https://essentia.upf.edu/models/classification-heads/genre_discogs519/genre_discogs519-discogs-maest-30s-pw-519l-1.pb" "0f5d61d9b62e4a27dac058926e986eb424dca0fdb920c066ab53158229cff498"
download "genre_discogs519-discogs-maest-30s-pw-519l-1.json" "https://essentia.upf.edu/models/classification-heads/genre_discogs519/genre_discogs519-discogs-maest-30s-pw-519l-1.json" "07015a89f1a0e9b7cdceb63933783023d85f3ac4b36ce5c1b5488bd1fbad2304"

echo "Vibe model files are ready in $MODELS_DIR."

# Default acoustic genre model for Vibe: discogs519 (MAEST embedding -> 519 head).
# VIBE_GENRE_MODEL=discogs400 is retained temporarily for diagnostics only; there
# is no silent fallback between them.
: "${VIBE_GENRE_MODEL:=discogs519}"
export VIBE_GENRE_MODEL
