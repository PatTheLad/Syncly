#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

dotnet build Syncly.slnx
dotnet test Syncly.slnx --no-build
echo "OK"
