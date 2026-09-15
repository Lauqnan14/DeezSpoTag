#!/usr/bin/env bash
# Layer 1: exercise the DeezSpoTag endpoints the plugin consumes, without
# Navidrome or WASM in the picture. If this fails, nothing downstream can work.
#
#   ./smoke-api.sh "Sauti Sol"
#   BASE=http://localhost:8668 ./smoke-api.sh "Sauti Sol" nd-artist-abc

set -uo pipefail

BASE="${BASE:-http://localhost:8668}"
CONFIG="${CONFIG:-$(dirname "$0")/../../../DeezSpoTag.Workers/Data/deezspotag/config.json}"
ARTIST="${1:-}"
ND_ID="${2:-}"

if [[ -z "$ARTIST" ]]; then
  echo "usage: $0 <artist-name> [navidrome-artist-id]" >&2
  exit 2
fi

TOKEN="${DEEZSPOTAG_API_TOKEN:-}"
if [[ -z "$TOKEN" ]]; then
  # config.json is camelCase on disk; the C# property is ApiToken. Accept either.
  TOKEN=$(python3 -c "
import json,sys
d=json.load(open(sys.argv[1]))
print(d.get('apiToken') or d.get('ApiToken') or '')" "$CONFIG" 2>/dev/null)
fi
if [[ -z "$TOKEN" ]]; then
  echo "No API token. Set DEEZSPOTAG_API_TOKEN or point CONFIG at config.json." >&2
  exit 2
fi

# Builds the query the plugin builds: id when known, name always.
q() {
  local extra="$1"
  if [[ -n "$ND_ID" ]]; then
    printf 'id=%s&name=%s%s' "$ND_ID" "$(python3 -c "import urllib.parse,sys;print(urllib.parse.quote(sys.argv[1]))" "$ARTIST")" "$extra"
  else
    printf 'name=%s%s' "$(python3 -c "import urllib.parse,sys;print(urllib.parse.quote(sys.argv[1]))" "$ARTIST")" "$extra"
  fi
}

hit() {
  local label="$1" url="$2"
  echo "── $label"
  echo "   $url"
  local body code
  body=$(curl -sS -o /tmp/.dst-body -w '%{http_code}' -H "Authorization: Bearer $TOKEN" -H 'Accept: application/json' "$url")
  code="$body"
  case "$code" in
    200) echo "   200 OK"; python3 -m json.tool < /tmp/.dst-body | head -40 ;;
    204) echo "   204 No Content  → Navidrome would fall through to the next agent" ;;
    401) echo "   401 Unauthorized → token rejected, or caller is not trusted-local" ;;
    *)   echo "   $code"; head -c 400 /tmp/.dst-body; echo ;;
  esac
  echo
}

echo "base:   $BASE"
echo "artist: $ARTIST${ND_ID:+  (navidrome id: $ND_ID)}"
echo

hit "biography" "$BASE/api/metadata-agent/artist/biography?$(q '')"
hit "top-songs" "$BASE/api/metadata-agent/artist/top-songs?$(q '&count=10')"

rm -f /tmp/.dst-body
