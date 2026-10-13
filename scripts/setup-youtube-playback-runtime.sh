#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime_dir="${1:-$repo_root/DeezSpoTag.Web/Tools/youtube_playback_runtime}"
source_revision=26475e9d3665b972c4aeb83b4dbaafcf64b88438
source_digest=a8144eab6a4f38f320063688531f461afe18c0a58be2dd3152b780f4851e954d
plugin_digest=a26b980bf6d8e8400d8f21445daa4d8381bf6183d20f0be309391f86b31e27ef
scratch_dir="$(mktemp -d)"
trap 'rm -rf "$scratch_dir"' EXIT
node -e 'if (Number(process.versions.node.split(".")[0]) !== 24) throw Error("Node 24 is required")'
curl --fail --silent --show-error --location "https://codeload.github.com/Brainicism/bgutil-ytdlp-pot-provider/tar.gz/$source_revision" -o "$scratch_dir/source.tar.gz"
printf '%s  %s\n' "$source_digest" "$scratch_dir/source.tar.gz" | sha256sum --check --status
curl --fail --silent --show-error --location https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/2.0.2/bgutil-ytdlp-pot-provider.zip -o "$scratch_dir/plugin.zip"
printf '%s  %s\n' "$plugin_digest" "$scratch_dir/plugin.zip" | sha256sum --check --status
tar -xzf "$scratch_dir/source.tar.gz" -C "$scratch_dir"
mkdir -p "$runtime_dir/server" "$runtime_dir/plugins"
cp -a "$scratch_dir/bgutil-ytdlp-pot-provider-$source_revision/server/." "$runtime_dir/server/"
cp "$scratch_dir/bgutil-ytdlp-pot-provider-$source_revision/LICENSE" "$runtime_dir/LICENSE"
cp "$scratch_dir/plugin.zip" "$runtime_dir/plugins/bgutil-ytdlp-pot-provider.zip"
# Keep the verified script provider; do not discover an unrelated local HTTP server.
python3 - "$runtime_dir/plugins/bgutil-ytdlp-pot-provider.zip" <<'PY'
import sys, zipfile, os
path = sys.argv[1]
with zipfile.ZipFile(path) as source, zipfile.ZipFile(path + '.tmp', 'w') as target:
    for entry in source.infolist():
        if not entry.filename.endswith('/getpot_bgutil_http.py'):
            target.writestr(entry, source.read(entry))
os.replace(path + '.tmp', path)
PY
cd "$runtime_dir/server"
npm ci --no-audit --no-fund
./node_modules/.bin/tsc
node -e 'await import("canvas"); await import("bgutils-js/webpo")'
node build/generate_once.js --version
