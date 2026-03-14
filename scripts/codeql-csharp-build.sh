#!/usr/bin/env bash
set -euo pipefail

dotnet clean ./src.sln -c Debug --disable-build-servers

dotnet build ./src.sln -c Debug -t:Rebuild /p:UseSharedCompilation=false --disable-build-servers
