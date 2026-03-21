#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
venv_dir="${script_dir}/.venv"
requirements_file="${script_dir}/requirements-modern.txt"

python_bin="${SHAZAM_BOOTSTRAP_PYTHON:-python3}"

if ! command -v "${python_bin}" >/dev/null 2>&1; then
  echo "Python executable not found: ${python_bin}" >&2
  exit 1
fi

"${python_bin}" -m venv "${venv_dir}"
"${venv_dir}/bin/pip" install --no-cache-dir --upgrade pip
"${venv_dir}/bin/pip" install --no-cache-dir -r "${requirements_file}"

echo "Shazam runtime ready:"
echo "  ${venv_dir}/bin/python3"
