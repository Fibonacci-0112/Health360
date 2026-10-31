#!/usr/bin/env bash
# Setup script for Claude Code cloud environments (claude.ai/code).
# Paste into the environment's "Setup script" field, or run `bash tools/cloud/setup.sh`.
# Installs what the checks in CLAUDE.md need: the .NET SDK from global.json, and Postgres + pgTAP
# for `tools/db/test-local.sh` (no Docker in the cloud container).
# It runs before the repo's env vars are available, so it reads no secrets.
set -euo pipefail

cd "${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null || pwd)}"

# Postgres server + pgTAP for tools/db/test-local.sh.
if ! command -v pg_prove >/dev/null || ! ls /usr/lib/postgresql/*/bin/initdb >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq postgresql-16 postgresql-16-pgtap libtap-parser-sourcehandler-pgtap-perl >/dev/null
fi

# .NET SDK pinned in global.json (MAUI workloads are not installed; Health360.Core.slnf builds without them).
SDK_VERSION="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json | head -1)"
export DOTNET_ROOT="$HOME/.dotnet"
if ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q "^$SDK_VERSION "; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --version "$SDK_VERSION" --install-dir "$DOTNET_ROOT" --no-path
fi

# Put the tools on PATH for later shells.
PROFILE_LINE='export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1'
grep -qxF "$PROFILE_LINE" "$HOME/.bashrc" 2>/dev/null || echo "$PROFILE_LINE" >> "$HOME/.bashrc"
eval "$PROFILE_LINE"

# Dependencies (locked, as in CI).
dotnet restore Health360.Core.slnf

echo "Health360 setup done: dotnet $(dotnet --version)"
