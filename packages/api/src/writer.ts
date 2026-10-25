import { RemoteWriteError, type RemoteWriter } from '@health360/local-db';
import type { PostgrestError, SupabaseClient } from '@supabase/supabase-js';

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
      const { error } = await client.from(table).upsert({ ...data, id });
      fail(error);
    },
    async patch(table, id, data) {
      const { error } = await client.from(table).update(data).eq('id', id);
      fail(error);
    },
    async delete(table, id) {
      const { error } = await client.from(table).delete().eq('id', id);
      fail(error);
    },
  };
}
