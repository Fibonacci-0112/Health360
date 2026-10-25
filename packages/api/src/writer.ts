import { INSERT_ONLY_TABLES, RemoteWriteError, type RemoteWriter } from '@health360/local-db';
import type { PostgrestError, SupabaseClient } from '@supabase/supabase-js';

/** PostgREST's code for "no rows"; isPermanentError treats it as permanent. */
const NO_ROW = 'PGRST116';
/** Postgres' insufficient_privilege, the same code an RLS-rejected insert gets. */
const DENIED = '42501';

const fail = (error: PostgrestError | null) => {
  if (error) throw new RemoteWriteError(error.message, error.code || undefined);
};

/**
 * Uploads through PostgREST with the user's own session, so every write passes the same RLS the
 * pgTAP tests check. PUT is an upsert because PowerSync replays a whole row for inserts.
 */
export function supabaseWriter(client: SupabaseClient): RemoteWriter {
  return {
    async put(table, id, data) {
      // Insert-only tables (consents) have no UPDATE grant; a replayed insert must then be a no-op.
      const { error } = await client
        .from(table)
        .upsert({ ...data, id }, { onConflict: 'id', ignoreDuplicates: INSERT_ONLY_TABLES.has(table) });
      fail(error);
    },
    async patch(table, id, data) {
      // PostgREST answers an UPDATE that matched nothing (RLS hid the row, or another device deleted
      // it) with success. Ask for the row back so that case is quarantined instead of silently lost.
      const { data: rows, error } = await client.from(table).update(data).eq('id', id).select('id');
      fail(error);
      if (!rows?.length) throw new RemoteWriteError(`${table}/${id}: the update matched no row`, NO_ROW);
    },
    async delete(table, id) {
      // Like UPDATE, a DELETE that RLS blocks "succeeds" with no rows. Deleting a row that is already
      // gone is fine; one that is still there means the delete wasn't allowed (consents have no delete
      // policy), so it must be quarantined rather than reported as done.
      const { data: rows, error } = await client.from(table).delete().eq('id', id).select('id');
      fail(error);
      if (rows?.length) return;
      const { data: still, error: readError } = await client.from(table).select('id').eq('id', id).maybeSingle();
      fail(readError);
      if (still) throw new RemoteWriteError(`${table}/${id}: the delete was not allowed`, DENIED);
    },
  };
}
