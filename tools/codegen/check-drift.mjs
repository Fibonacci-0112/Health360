#!/usr/bin/env node
/**
 * Fails when the client schema (packages/local-db) and the Postgres schema drift apart (PLAN.md §4).
 * Run against a database with the migrations applied (`supabase start` in CI):
 *
 *   DATABASE_URL=postgresql://postgres:postgres@127.0.0.1:54322/postgres node tools/codegen/check-drift.mjs
 *
 * Checks, for every table in the `powersync` publication:
 *   - the client has the table, with the same columns (PowerSync supplies `id` itself);
 *   - each column's SQLite type matches its Postgres type;
 *   - every jsonb and boolean column is in SERVER_COLUMN_TYPES, so uploads convert it back.
 * And that every synced client table is published.
 */
import { getTableConfig } from 'drizzle-orm/sqlite-core';
import pg from 'pg';
import { drizzleSchema, SERVER_COLUMN_TYPES } from '../../packages/local-db/src/schema.ts';

const LOCAL_ONLY = new Set(['failed_uploads']);

const SQLITE_TYPE = {
  text: 'text', uuid: 'text', date: 'text', 'timestamp with time zone': 'text', 'timestamp without time zone': 'text',
  jsonb: 'text', json: 'text', boolean: 'integer', integer: 'integer', bigint: 'integer', smallint: 'integer',
  'double precision': 'real', real: 'real', numeric: 'real',
};

const client = new pg.Client({ connectionString: process.env.DATABASE_URL });
await client.connect();
const { rows } = await client.query(`
  select c.table_name, c.column_name, c.data_type
  from information_schema.columns c
  join pg_publication_tables p on p.schemaname = c.table_schema and p.tablename = c.table_name
  where p.pubname = 'powersync' and c.table_schema = 'public'
  order by c.table_name, c.ordinal_position`);
await client.end();

const server = new Map();
for (const r of rows) {
  if (!server.has(r.table_name)) server.set(r.table_name, new Map());
  server.get(r.table_name).set(r.column_name, r.data_type);
}

const local = new Map();
for (const table of Object.values(drizzleSchema)) {
  const config = getTableConfig(table);
  if (LOCAL_ONLY.has(config.name)) continue;
  local.set(config.name, new Map(config.columns.map((c) => [c.name, c.getSQLType()])));
}

const problems = [];
for (const [table, columns] of server) {
  const mine = local.get(table);
  if (!mine) {
    problems.push(`${table}: published but missing from packages/local-db`);
    continue;
  }
  for (const [column, type] of columns) {
    const expected = SQLITE_TYPE[type];
    if (!expected) problems.push(`${table}.${column}: no SQLite mapping for Postgres type ${type}`);
    if (!mine.has(column)) problems.push(`${table}.${column}: missing from the client table`);
    else if (expected && mine.get(column) !== expected) {
      problems.push(`${table}.${column}: client type ${mine.get(column)}, expected ${expected} for ${type}`);
    }
    const codec = SERVER_COLUMN_TYPES[table]?.[column];
    if ((type === 'jsonb' || type === 'json') && codec !== 'json') {
      problems.push(`${table}.${column}: ${type} must be listed as 'json' in SERVER_COLUMN_TYPES`);
    }
    if (type === 'boolean' && codec !== 'boolean') {
      problems.push(`${table}.${column}: boolean must be listed as 'boolean' in SERVER_COLUMN_TYPES`);
    }
  }
  for (const column of mine.keys()) {
    if (!columns.has(column)) problems.push(`${table}.${column}: on the client but not in Postgres`);
  }
}
for (const table of local.keys()) {
  if (!server.has(table)) problems.push(`${table}: client table is not in the powersync publication`);
}

if (problems.length) {
  console.error(`Schema drift between Postgres and packages/local-db:\n  ${problems.join('\n  ')}`);
  process.exit(1);
}
console.log(`client schema matches Postgres (${server.size} synced tables)`);
