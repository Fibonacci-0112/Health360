import { DrizzleAppSchema, type DrizzleTableWithPowerSyncOptions } from '@powersync/drizzle-driver';
import { integer, real, sqliteTable, text } from 'drizzle-orm/sqlite-core';

/*
 * Client tables mirror the synced Postgres tables column for column (PowerSync stores everything as
 * text, integer or real; jsonb arrives as JSON text and booleans as 0/1). Server-only columns that
 * clients never read can be left out. tools/codegen checks these against the migrations in CI.
 */

const timestamps = {
  created_at: text('created_at'),
  updated_at: text('updated_at'),
};

export const profiles = sqliteTable('profiles', {
  id: text('id').primaryKey(),
  user_id: text('user_id'),
  managed_by: text('managed_by'),
  display_name: text('display_name'),
  birth_date: text('birth_date'),
  sex_for_calcs: text('sex_for_calcs'),
  height: real('height'),
  height_unit: text('height_unit'),
  height_cm: real('height_cm'),
  unit_prefs: text('unit_prefs'),
  timezone: text('timezone'),
  currency: text('currency'),
  activity_level: text('activity_level'),
  physiology_prefs: text('physiology_prefs'),
  onboarded_at: text('onboarded_at'),
  ...timestamps,
});

export const households = sqliteTable('households', {
  id: text('id').primaryKey(),
  name: text('name'),
  ...timestamps,
});

export const householdMembers = sqliteTable('household_members', {
  id: text('id').primaryKey(),
  household_id: text('household_id'),
  profile_id: text('profile_id'),
  user_id: text('user_id'),
  role: text('role'),
  ...timestamps,
});

export const sharingGrants = sqliteTable('sharing_grants', {
  id: text('id').primaryKey(),
  profile_id: text('profile_id'),
  household_id: text('household_id'),
  grantee_profile_id: text('grantee_profile_id'),
  category: text('category'),
  access: text('access'),
  ...timestamps,
});

export const consents = sqliteTable('consents', {
  id: text('id').primaryKey(),
  profile_id: text('profile_id'),
  kind: text('kind'),
  version: text('version'),
  granted: integer('granted'),
  decided_at: text('decided_at'),
  ...timestamps,
});

export const referenceDatasets = sqliteTable('reference_datasets', {
  id: text('id').primaryKey(),
  code: text('code'),
  version: text('version'),
  attribution: text('attribution'),
  imported_at: text('imported_at'),
  ...timestamps,
});

/** Phase 0 throwaway table that proves sync on every target. */
export const notes = sqliteTable('notes', {
  id: text('id').primaryKey(),
  profile_id: text('profile_id'),
  body: text('body'),
  local_date: text('local_date'),
  ...timestamps,
});

/**
 * Local only, never uploaded. When the server permanently rejects a write (constraint or RLS
 * violation), the connector parks it here instead of blocking the upload queue (PLAN.md §4).
 */
export const failedUploads = sqliteTable('failed_uploads', {
  id: text('id').primaryKey(),
  table_name: text('table_name'),
  row_id: text('row_id'),
  op: text('op'),
  /** The CRUD payload as JSON, so the user can retry or discard it. */
  data: text('data'),
  error_code: text('error_code'),
  error_message: text('error_message'),
  failed_at: text('failed_at'),
});

const localOnly = (table: typeof failedUploads): DrizzleTableWithPowerSyncOptions => ({
  tableDefinition: table,
  options: { localOnly: true },
});

export const drizzleSchema = {
  profiles,
  households,
  householdMembers,
  sharingGrants,
  consents,
  referenceDatasets,
  notes,
  failedUploads,
};

export const AppSchema = new DrizzleAppSchema({
  ...drizzleSchema,
  failedUploads: localOnly(failedUploads),
});

/**
 * Columns stored differently in SQLite than in Postgres: jsonb is JSON text locally and boolean is
 * 0/1. Uploads convert them back so the server receives real JSON objects and booleans. Every jsonb
 * and boolean column of a synced table must be listed; tools/codegen/check-drift.mjs enforces it.
 */
export const SERVER_COLUMN_TYPES: Readonly<Record<string, Readonly<Record<string, 'json' | 'boolean'>>>> = {
  profiles: { unit_prefs: 'json', physiology_prefs: 'json' },
  consents: { granted: 'boolean' },
};

/** Tables the client may write. Everything else synced is read-only on the client. */
export const UPLOADABLE_TABLES = ['profiles', 'consents', 'sharing_grants', 'notes'] as const;
export type UploadableTable = (typeof UPLOADABLE_TABLES)[number];
