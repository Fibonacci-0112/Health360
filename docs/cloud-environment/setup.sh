#!/usr/bin/env bash
# Setup script for a Claude Code cloud environment running Health360.
# Paste into: environment settings -> Setup script. Runs as root on Ubuntu 24.04,
# before the session starts. It can't read the environment's variables or secrets.
#
# The base image already has Node 22, pnpm 10, corepack, Bun, Rust/cargo,
# Docker (daemon not started), psql 16 and Playwright Chromium.
# This adds what docs/PLAN.md needs on top: Deno (Edge Functions), the Supabase
# CLI (local stack, pgTAP, migrations), EAS CLI, Tauri 2 Linux build deps,
# a running Docker daemon (local Supabase + PowerSync), and the workspace deps.
set -euo pipefail

INSTALL_TAURI_DEPS=1   # set to 0 to skip ~300 MB of GTK/WebKit packages

log() { printf '\n==> %s\n' "$*"; }

# --- Deno, Supabase CLI, EAS CLI ------------------------------------------
# Installed from npm: GitHub release downloads are blocked under the default
# network policy, but npm (and the platform binaries it ships) is allowed.
# The Supabase CLI refuses `npm i -g`, so it gets its own prefix.
log "Deno + EAS CLI"
npm install -g --no-fund --no-audit deno@2 eas-cli@latest

log "Supabase CLI"
mkdir -p /opt/supabase-cli
npm install --prefix /opt/supabase-cli --no-fund --no-audit supabase@2
ln -sf /opt/supabase-cli/node_modules/.bin/supabase /usr/local/bin/supabase

# --- Tauri 2 (desktop shell, Phase 0 test) --------------------------------
if [ "$INSTALL_TAURI_DEPS" = 1 ]; then
  log "Tauri Linux build dependencies"
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends \
    build-essential pkg-config file libssl-dev libxdo-dev \
    libwebkit2gtk-4.1-dev libayatana-appindicator3-dev librsvg2-dev
  rm -rf /var/lib/apt/lists/*
fi

# --- Docker daemon ----------------------------------------------------------
# `supabase start` and powersync/docker-compose.yml need it. Images pull from
# public.ecr.aws (Supabase) and Docker Hub (PowerSync), both allowed.
log "Docker daemon"
if ! docker info >/dev/null 2>&1; then
  nohup dockerd >/var/log/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
  docker info >/dev/null 2>&1 || echo "warning: dockerd did not start; see /var/log/dockerd.log"
fi

# --- Workspace dependencies -------------------------------------------------
# No-op until the pnpm monorepo (PLAN.md 2.2) lands. The preinstalled pnpm 10
# is used unless package.json pins one in "packageManager"; corepack is only
# enabled then, since its shim otherwise fetches the newest pnpm major.
log "Workspace dependencies"
REPO_DIR="${CLAUDE_PROJECT_DIR:-$(pwd)}"
if grep -qs '"packageManager"' "$REPO_DIR/package.json"; then
  corepack enable pnpm
fi
if [ -f "$REPO_DIR/pnpm-lock.yaml" ]; then
  (cd "$REPO_DIR" && pnpm install --frozen-lockfile) \
    || (cd "$REPO_DIR" && pnpm install)
elif [ -f "$REPO_DIR/package.json" ]; then
  (cd "$REPO_DIR" && pnpm install)
else
  echo "no package.json yet, skipping"
fi

log "Versions"
node --version; pnpm --version; deno --version | head -1
supabase --version; eas --version | head -1
