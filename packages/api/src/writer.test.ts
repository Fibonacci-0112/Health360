import { isPermanentError, RemoteWriteError } from '@health360/local-db';
import type { SupabaseClient } from '@supabase/supabase-js';
import { describe, expect, it } from 'vitest';
import { supabaseWriter } from './writer.ts';

/** Just enough of the PostgREST builder chain for `update().eq().select()`. */
function fakeClient(result: { data: unknown[] | null; error: { message: string; code: string } | null }) {
  const builder = { update: () => builder, eq: () => builder, select: () => Promise.resolve(result) };
  return { from: () => builder } as unknown as SupabaseClient;
}

describe('supabaseWriter.patch', () => {
  it('succeeds when the row was updated', async () => {
    await expect(supabaseWriter(fakeClient({ data: [{ id: 'n1' }], error: null })).patch('notes', 'n1', { body: 'x' })).resolves.toBeUndefined();
  });

  it('treats an update that matched no row as a permanent failure', async () => {
    const write = supabaseWriter(fakeClient({ data: [], error: null })).patch('notes', 'gone', { body: 'x' });
    await expect(write).rejects.toBeInstanceOf(RemoteWriteError);
    await write.catch((e: RemoteWriteError) => expect(isPermanentError(e.code)).toBe(true));
  });

  it('passes PostgREST error codes through', async () => {
    const write = supabaseWriter(fakeClient({ data: null, error: { message: 'denied', code: '42501' } })).patch('notes', 'n1', {});
    await expect(write).rejects.toMatchObject({ code: '42501' });
  });
});
