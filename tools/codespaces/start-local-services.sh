#!/usr/bin/env bash
# Explicitly start Health360's local Supabase and PowerSync stacks.
# Deliberately NOT called by any Dev Container lifecycle hook.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
  printf 'Docker is not ready. Check the Docker-in-Docker feature.\n' >&2
  exit 1
fi
if ! docker compose version >/dev/null 2>&1; then
  printf 'Docker Compose v2 is required.\n' >&2
  exit 1
fi

SUPABASE="${HOME}/.local/bin/supabase"
if [[ ! -x "$SUPABASE" ]]; then
  printf 'Run bash tools/codespaces/install-supabase-cli.sh first.\n' >&2
  exit 1
fi
if [[ "$("$SUPABASE" --version)" != '2.122.0' ]]; then
  printf 'Supabase CLI must be version 2.122.0 (matching CI).\n' >&2
  exit 1
fi

# Match the CI sync job. Keep Auth, REST and Storage, but skip unused services.
"$SUPABASE" start -x studio,imgproxy,edge-runtime,logflare,vector,realtime,postgres-meta

# PowerSync uses the same local Auth JWT secret. Do not print or persist credentials.
SUPABASE_ENV="$("$SUPABASE" status -o env)"
JWT_LINE="$(printf '%s\n' "$SUPABASE_ENV" | grep -m 1 '^JWT_SECRET=' || true)"
if [[ -z "$JWT_LINE" ]]; then
  printf 'Supabase status did not provide JWT_SECRET; PowerSync was not started.\n' >&2
  exit 1
fi
JWT_SECRET="${JWT_LINE#JWT_SECRET=}"
JWT_SECRET="${JWT_SECRET#\"}"
JWT_SECRET="${JWT_SECRET%\"}"
if [[ -z "$JWT_SECRET" ]]; then
  printf 'Supabase JWT secret is empty; PowerSync was not started.\n' >&2
  exit 1
fi
export PS_SUPABASE_JWT_SECRET="$JWT_SECRET"
unset SUPABASE_ENV JWT_LINE JWT_SECRET

docker compose -f powersync/docker-compose.yml -f tools/codespaces/compose.manual.yml up -d
printf '\nLocal services started: Supabase API (127.0.0.1:54321), Mailpit (127.0.0.1:54324), PowerSync (127.0.0.1:8080).\n'
