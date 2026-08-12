#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
dotnet build Syncly.slnx
dotnet test tests/Syncly.Core.Tests --no-build
echo "OK"
