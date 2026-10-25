import { isPermanentError, RemoteWriteError } from '@health360/local-db';
import type { SupabaseClient } from '@supabase/supabase-js';
import { describe, expect, it } from 'vitest';
import { supabaseWriter } from './writer.ts';

type Result = { data: unknown; error: { message: string; code: string } | null };

/**
 * Just enough of the PostgREST builder chain: `update|delete().eq().select()` resolves to `write`,
 * and a plain `select().eq().maybeSingle()` read resolves to `read`.
 */
function fakeClient(write: Result, read: Result = { data: null, error: null }) {
  const writeChain = { eq: () => writeChain, select: () => Promise.resolve(write) };
  const readChain = { eq: () => readChain, maybeSingle: () => Promise.resolve(read) };
  return {
    from: () => ({ update: () => writeChain, delete: () => writeChain, select: () => readChain }),
  } as unknown as SupabaseClient;
}

describe('supabaseWriter.patch', () => {
  it('succeeds when the row was updated', async () => {
    const writer = supabaseWriter(fakeClient({ data: [{ id: 'n1' }], error: null }));
    await expect(writer.patch('notes', 'n1', { body: 'x' })).resolves.toBeUndefined();
  });

  it('treats an update that matched no row as a permanent failure', async () => {
    const write = supabaseWriter(fakeClient({ data: [], error: null })).patch('notes', 'gone', { body: 'x' });
    await expect(write).rejects.toBeInstanceOf(RemoteWriteError);
    await write.catch((e: RemoteWriteError) => expect(isPermanentError(e.code)).toBe(true));
  });

  it('passes PostgREST error codes through', async () => {
    const write = supabaseWriter(fakeClient({ data: null, error: { message: 'denied', code: '42501' } })).patch(
      'notes',
      'n1',
      {},
    );
    await expect(write).rejects.toMatchObject({ code: '42501' });
  });
});

describe('supabaseWriter.delete', () => {
  it('succeeds when the row was deleted', async () => {
    const writer = supabaseWriter(fakeClient({ data: [{ id: 'c1' }], error: null }));
    await expect(writer.delete('consents', 'c1')).resolves.toBeUndefined();
  });

  it('succeeds when the row was already gone', async () => {
    const writer = supabaseWriter(fakeClient({ data: [], error: null }, { data: null, error: null }));
    await expect(writer.delete('notes', 'gone')).resolves.toBeUndefined();
  });

  it('treats a delete that RLS silently blocked as a permanent failure', async () => {
    const writer = supabaseWriter(fakeClient({ data: [], error: null }, { data: { id: 'c1' }, error: null }));
    const write = writer.delete('consents', 'c1');
    await expect(write).rejects.toMatchObject({ code: '42501' });
    await write.catch((e: RemoteWriteError) => expect(isPermanentError(e.code)).toBe(true));
  });
});
