import { UpdateType, type CommonPowerSyncDatabase, type CrudEntry } from '@powersync/common';
import { SERVER_COLUMN_TYPES } from './schema.ts';

/** Writes one CRUD op to the server. The app uses supabase-js (so RLS applies); tests can swap it. */
export interface RemoteWriter {
  put(table: string, id: string, data: Record<string, unknown>): Promise<void>;
  patch(table: string, id: string, data: Record<string, unknown>): Promise<void>;
  delete(table: string, id: string): Promise<void>;
}

/** An error from the server, carrying its Postgres SQLSTATE or PostgREST code when there is one. */
export class RemoteWriteError extends Error {
  readonly code: string | undefined;
  constructor(message: string, code: string | undefined) {
    super(message);
    this.name = 'RemoteWriteError';
    this.code = code;
  }
}

/**
 * Errors that will fail the same way however often the write is retried. These are quarantined so
 * one bad row can't block every later write (PLAN.md §4). Anything else (network, 5xx, an expired
 * token) is thrown so PowerSync retries the whole transaction later.
 *   22xxx data exception · 23xxx integrity violation · 42xxx access rule violation or schema
 *   mismatch (42501 is RLS) · PGRST1xx/2xx malformed request (PGRST3xx are auth, so transient).
 */
export function isPermanentError(code: string | undefined): boolean {
  if (!code) return false;
  if (/^(22|23|42)[0-9A-Z]{3}$/.test(code)) return true;
  return /^PGRST[12]\d\d$/.test(code);
}

export interface QuarantinedOp {
  table: string;
  id: string;
  op: UpdateType;
  code: string | undefined;
  message: string;
}

/**
 * Uploads every pending transaction. Each op either reaches the server, is quarantined in the
 * local-only `failed_uploads` table, or (on a transient error) aborts the upload so it is retried.
 */
export async function uploadPending(
  db: CommonPowerSyncDatabase,
  writer: RemoteWriter,
  onQuarantine?: (op: QuarantinedOp) => void,
): Promise<void> {
  for (;;) {
    const tx = await db.getNextCrudTransaction();
    if (!tx) return;
    for (const op of tx.crud) {
      try {
        await applyOp(writer, op);
      } catch (error) {
        const code = error instanceof RemoteWriteError ? error.code : undefined;
        if (!isPermanentError(code)) throw error;
        const message = error instanceof Error ? error.message : String(error);
        await quarantine(db, op, code, message);
        onQuarantine?.({ table: op.table, id: op.id, op: op.op, code, message });
      }
    }
    await tx.complete();
  }
}

/**
 * Converts a row from its SQLite form to what Postgres expects. Without this, a jsonb column would
 * be uploaded as a JSON string inside JSON, and a boolean as a number.
 */
export function toServerRow(table: string, data: Record<string, unknown>): Record<string, unknown> {
  const types = SERVER_COLUMN_TYPES[table];
  if (!types) return data;
  const out: Record<string, unknown> = { ...data };
  for (const [column, type] of Object.entries(types)) {
    const value = out[column];
    if (value === null || value === undefined) continue;
    if (type === 'boolean') {
      out[column] = value === 1 || value === true || value === '1' || value === 'true';
    } else if (typeof value === 'string') {
      try {
        out[column] = JSON.parse(value);
      } catch {
        // Can never succeed on retry: quarantine it (22P02 is Postgres' invalid_text_representation).
        throw new RemoteWriteError(`${table}.${column} is not valid JSON`, '22P02');
      }
    }
  }
  return out;
}

async function applyOp(writer: RemoteWriter, op: CrudEntry): Promise<void> {
  const data = toServerRow(op.table, op.opData ?? {});
  switch (op.op) {
    case UpdateType.PUT:
      return writer.put(op.table, op.id, data);
    case UpdateType.PATCH:
      return writer.patch(op.table, op.id, data);
    case UpdateType.DELETE:
      return writer.delete(op.table, op.id);
  }
}

async function quarantine(db: CommonPowerSyncDatabase, op: CrudEntry, code: string | undefined, message: string) {
  await db.execute(
    `INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code, error_message, failed_at)
     VALUES (uuid(), ?, ?, ?, ?, ?, ?, ?)`,
    [op.table, op.id, op.op, JSON.stringify(op.opData ?? {}), code ?? null, message, new Date().toISOString()],
  );
}

/** Number of writes waiting to upload, for the "unsynced entries" badge. */
export async function pendingUploadCount(db: CommonPowerSyncDatabase): Promise<number> {
  return (await db.getUploadQueueStats()).count;
}

/** Drops a quarantined write for good. */
export async function discardFailedUpload(db: CommonPowerSyncDatabase, id: string): Promise<void> {
  await db.execute('DELETE FROM failed_uploads WHERE id = ?', [id]);
}

const IDENTIFIER = /^[a-z_][a-z0-9_]*$/;

/** Re-queues a quarantined write as a fresh local write (after the user has fixed what was wrong). */
export async function retryFailedUpload(db: CommonPowerSyncDatabase, id: string): Promise<void> {
  await db.writeTransaction(async (tx) => {
    const row = await tx.getOptional<{ table_name: string; row_id: string; op: string; data: string }>(
      'SELECT table_name, row_id, op, data FROM failed_uploads WHERE id = ?',
      [id],
    );
    if (!row) return;
    const data = JSON.parse(row.data) as Record<string, unknown>;
    const columns = Object.keys(data);
    if (!IDENTIFIER.test(row.table_name) || !columns.every((c) => IDENTIFIER.test(c))) {
      throw new Error('Refusing to replay a write with an unexpected table or column name');
    }
    if (row.op === UpdateType.DELETE) {
      await tx.execute(`DELETE FROM ${row.table_name} WHERE id = ?`, [row.row_id]);
    } else if (row.op === UpdateType.PUT) {
      const names = ['id', ...columns];
      await tx.execute(
        `INSERT OR REPLACE INTO ${row.table_name} (${names.join(', ')}) VALUES (${names.map(() => '?').join(', ')})`,
        [row.row_id, ...columns.map((c) => data[c])],
      );
    } else if (columns.length > 0) {
      await tx.execute(
        `UPDATE ${row.table_name} SET ${columns.map((c) => `${c} = ?`).join(', ')} WHERE id = ?`,
        [...columns.map((c) => data[c]), row.row_id],
      );
    }
    await tx.execute('DELETE FROM failed_uploads WHERE id = ?', [id]);
  });
}
