#!/usr/bin/env bash
# Runs the migrations and pgTAP tests against a throwaway local Postgres, without Docker.
# Needs the Postgres server binaries and pgTAP (Debian/Ubuntu: postgresql-16 postgresql-16-pgtap).
# CI uses `supabase test db` instead; this is the fast loop for writing migrations.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PG_BIN="${PG_BIN:-$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)}"
DATA="$(mktemp -d)"
PORT="${PGPORT_TEST:-54329}"
SOCK="$DATA"
cleanup() { "$PG_BIN/pg_ctl" -D "$DATA" -m immediate stop >/dev/null 2>&1 || true; rm -rf "$DATA"; }
trap cleanup EXIT

# initdb refuses to run as root; use the postgres OS user when we are root.
RUN=()
if [ "$(id -u)" = 0 ]; then chown postgres "$DATA"; RUN=(runuser -u postgres --); fi

"${RUN[@]}" "$PG_BIN/initdb" -D "$DATA" -U postgres -A trust >/dev/null
"${RUN[@]}" "$PG_BIN/pg_ctl" -D "$DATA" -l "$DATA/log" -o "-p $PORT -k $SOCK -c wal_level=logical -c listen_addresses=''" -w start >/dev/null

PSQL=(psql -X -q -v ON_ERROR_STOP=1 -h "$SOCK" -p "$PORT" -U postgres -d postgres)
"${PSQL[@]}" -f "$ROOT/tools/db/supabase-shim.sql" >/dev/null
for f in "$ROOT"/supabase/migrations/*.sql; do
  echo "migrate: $(basename "$f")"
  "${PSQL[@]}" -f "$f" >/dev/null
done
"${PSQL[@]}" -c 'create extension pgtap with schema extensions' >/dev/null

PGOPTIONS='--search_path=public,extensions' pg_prove -h "$SOCK" -p "$PORT" -U postgres -d postgres \
  "$ROOT"/supabase/tests/database/*.sql
