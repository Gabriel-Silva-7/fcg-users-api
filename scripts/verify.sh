#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
DOTNET=${DOTNET:-dotnet}
"$DOTNET" restore FCG.Users.sln --locked-mode
"$DOTNET" build FCG.Users.sln -c Release --no-restore
"$DOTNET" test FCG.Users.sln -c Release --no-restore
