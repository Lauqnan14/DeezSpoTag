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

if [[ -d "${venv_dir}" ]] && [[ ! -x "${venv_dir}/bin/python3" || ! -x "${venv_dir}/bin/pip" ]]; then
  echo "Detected broken Shazam venv at ${venv_dir}; rebuilding."
  rm -rf "${venv_dir}"
fi

"${python_bin}" -m venv --clear "${venv_dir}"

venv_python="${venv_dir}/bin/python3"
if [[ ! -x "${venv_python}" ]]; then
  venv_python="${venv_dir}/bin/python"
fi

if [[ ! -x "${venv_python}" ]]; then
  echo "Shazam venv python executable not found in ${venv_dir}/bin." >&2
  exit 1
fi

if [[ ! -x "${venv_dir}/bin/pip" ]]; then
  "${venv_python}" -m ensurepip --upgrade
fi

"${venv_python}" -m pip install --no-cache-dir --upgrade pip
"${venv_python}" -m pip install --no-cache-dir -r "${requirements_file}"

echo "Shazam runtime ready:"
echo "  ${venv_python}"
