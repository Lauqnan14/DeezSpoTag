#!/usr/bin/env bash
set -euo pipefail

dotnet clean ./DeezSpoTag.Web/DeezSpoTag.Web.csproj -c Debug --disable-build-servers

dotnet build ./DeezSpoTag.Web/DeezSpoTag.Web.csproj -c Debug -t:Rebuild /p:UseSharedCompilation=false --disable-build-servers
