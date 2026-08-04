#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$DIR/repo"
git init -b main
git add -A
git commit -m "Initial todo API"