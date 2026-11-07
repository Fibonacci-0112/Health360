#!/usr/bin/env bash
# Stop local services without removing their Docker volumes or Supabase data.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

if ! docker info >/dev/null 2>&1; then
  printf 'Docker is not ready; cannot stop the local services.\n' >&2
  exit 1
fi

docker compose -f powersync/docker-compose.yml down
"${HOME}/.local/bin/supabase" stop
printf 'Health360 local services stopped; persistent data volumes preserved.\n'
