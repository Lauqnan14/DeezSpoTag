#!/usr/bin/env bash
set -euo pipefail

# Install Bento4 mp4decrypt locally (no sudo). Usage:
#   BENTO4_URL="https://www.bento4.com/downloads/Bento4-SDK-1-6-0-640.x86_64-unknown-linux.zip" ./scripts/install_mp4decrypt.sh
#   ./scripts/install_mp4decrypt.sh <bento4_zip_url>

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS_DIR="${ROOT_DIR}/tools"
BIN_DIR="${TOOLS_DIR}/bin"

ZIP_URL="${1:-${BENTO4_URL:-https://www.bok.net/Bento4/binaries/Bento4-SDK-1-6-0-641.x86_64-unknown-linux.zip}}"
if [[ -z "${ZIP_URL}" ]]; then
  echo "BENTO4_URL is required (pass as env var or first arg)." >&2
  exit 1
fi

mkdir -p "${TOOLS_DIR}" "${BIN_DIR}"
TMP_DIR="$(mktemp -d)"
cleanup() { rm -rf "${TMP_DIR}"; }
trap cleanup EXIT

ZIP_PATH="${TMP_DIR}/bento4.zip"

if command -v curl >/dev/null 2>&1; then
  curl -L --fail -o "${ZIP_PATH}" "${ZIP_URL}"
elif command -v wget >/dev/null 2>&1; then
  wget -O "${ZIP_PATH}" "${ZIP_URL}"
else
  echo "Neither curl nor wget is available to download Bento4." >&2
  exit 1
fi

if ! unzip -tq "${ZIP_PATH}" >/dev/null 2>&1; then
  echo "Downloaded file is not a valid zip. URL may require a browser or has changed." >&2
  echo "URL: ${ZIP_URL}" >&2
  exit 1
fi

unzip -q "${ZIP_PATH}" -d "${TMP_DIR}"

MP4DECRYPT_PATH="$(find "${TMP_DIR}" -type f -name mp4decrypt -perm -111 | head -n 1 || true)"
if [[ -z "${MP4DECRYPT_PATH}" ]]; then
  echo "mp4decrypt not found in extracted Bento4 archive." >&2
  exit 1
fi

cp -f "${MP4DECRYPT_PATH}" "${BIN_DIR}/mp4decrypt"
chmod +x "${BIN_DIR}/mp4decrypt"

echo "mp4decrypt installed to ${BIN_DIR}/mp4decrypt"
echo "Add to PATH for this session:"
echo "  export PATH=\"${BIN_DIR}:\$PATH\""
