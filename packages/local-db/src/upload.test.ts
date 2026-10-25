import { describe, expect, it } from 'vitest';
import { UpdateType, type CommonPowerSyncDatabase } from '@powersync/common';
import {
  isPermanentError,
  RemoteWriteError,
  retryFailedUpload,
  toServerRow,
  uploadPending,
  type RemoteWriter,
} from './upload.ts';
import { AppSchema, UPLOADABLE_TABLES } from './schema.ts';

describe('isPermanentError', () => {
  it.each(['23505', '23503', '22P02', '42501', '42703', 'PGRST204', 'PGRST116'])('%s is permanent', (code) => {
    expect(isPermanentError(code)).toBe(true);
  });
  it.each([undefined, '', '08006', '53300', '57014', 'PGRST301', '40001'])('%s is retried', (code) => {
    expect(isPermanentError(code)).toBe(false);
  });
});

describe('AppSchema', () => {
  const tables = new Map(AppSchema.tables.map((t) => [t.name, t]));

  it('includes every uploadable table', () => {
    for (const name of UPLOADABLE_TABLES) expect(tables.has(name)).toBe(true);
  });

  it('keeps failed_uploads local only', () => {
    expect(tables.get('failed_uploads')?.localOnly).toBe(true);
    expect(tables.get('notes')?.localOnly).toBe(false);
  });

  it('leaves the id column to PowerSync', () => {
    for (const t of AppSchema.tables) expect(t.columns.map((c) => c.name)).not.toContain('id');
  });
});

describe('toServerRow', () => {
  it('sends jsonb columns as JSON, not as a string of JSON', () => {
    expect(toServerRow('profiles', { unit_prefs: '{"load":"kg"}', display_name: 'A' })).toEqual({
      unit_prefs: { load: 'kg' },
      display_name: 'A',
    });
  });

  it('sends booleans as booleans', () => {
    expect(toServerRow('consents', { granted: 1 })).toEqual({ granted: true });
    expect(toServerRow('consents', { granted: 0 })).toEqual({ granted: false });
  });

  it('leaves nulls, absent columns and other tables alone', () => {
    expect(toServerRow('profiles', { unit_prefs: null })).toEqual({ unit_prefs: null });
    expect(toServerRow('notes', { body: '{"x":1}' })).toEqual({ body: '{"x":1}' });
  });

  it('marks malformed JSON as a permanent error', () => {
    expect(() => toServerRow('profiles', { unit_prefs: '{oops' })).toThrow(RemoteWriteError);
    try {
      toServerRow('profiles', { unit_prefs: '{oops' });
    } catch (e) {
      expect(isPermanentError((e as RemoteWriteError).code)).toBe(true);
    }
  });
});

describe('uploadPending', () => {
  it('quarantines a rejected op once, even when its transaction is retried', async () => {
    // One transaction: op 1 is rejected for good, op 2 hits a network error the first time.
    const crud = [
      { clientId: 1, op: UpdateType.PUT, table: 'notes', id: 'a', opData: { body: 'forged' } },
      { clientId: 2, op: UpdateType.PUT, table: 'notes', id: 'b', opData: { body: 'fine' } },
    ];
    let completed = false;
    const executed: unknown[][] = [];
    const db = {
      getNextCrudTransaction: async () => (completed ? null : { crud, complete: async () => void (completed = true) }),
      execute: async (_sql: string, params: unknown[]) => void executed.push(params),
    } as unknown as CommonPowerSyncDatabase;
    let networkDown = true;
    const writer: RemoteWriter = {
      put: async (_t, id) => {
        if (id === 'a') throw new RemoteWriteError('rls', '42501');
        if (networkDown) throw new Error('offline');
      },
      patch: async () => {},
      delete: async () => {},
    };

    await expect(uploadPending(db, writer)).rejects.toThrow('offline');
    networkDown = false;
    await uploadPending(db, writer);

    expect(completed).toBe(true);
    // Written twice (once per attempt), but under the same id, so INSERT OR REPLACE keeps one row.
    expect(executed.map((p) => p[0])).toEqual(['crud-1', 'crud-1']);
  });
});

describe('retryFailedUpload', () => {
  /** One quarantined entry, a fake database recording what it executes, and a writer that does `outcome`. */
  function setup(op: string, outcome: 'ok' | Error) {
    const executed: unknown[][] = [];
    const db = {
      getOptional: async () => ({ table_name: 'notes', row_id: 'n1', op, data: JSON.stringify({ body: 'edited offline' }) }),
      execute: async (sql: string, params: unknown[]) => void executed.push([sql.trim().split(/\s+/)[0], ...params]),
    } as unknown as CommonPowerSyncDatabase;
    const sent: unknown[][] = [];
    const send = async (...args: unknown[]) => {
      sent.push(args);
      if (outcome !== 'ok') throw outcome;
    };
    const writer: RemoteWriter = { put: send, patch: send, delete: send };
    return { db, writer, executed, sent };
  }

  it('sends the saved payload itself, so an edit already in the local row still reaches the server', async () => {
    const { db, writer, executed, sent } = setup(UpdateType.PATCH, 'ok');
    expect(await retryFailedUpload(db, 'crud-1', writer)).toBe('sent');
    expect(sent).toEqual([['notes', 'n1', { body: 'edited offline' }]]);
    expect(executed).toEqual([['DELETE', 'crud-1']]);
  });

  it('keeps the entry, with the new error, when the server refuses it again', async () => {
    const { db, writer, executed } = setup(UpdateType.DELETE, new RemoteWriteError('denied', '42501'));
    expect(await retryFailedUpload(db, 'crud-1', writer)).toBe('rejected');
    expect(executed).toHaveLength(1);
    expect(executed[0]?.slice(0, 2)).toEqual(['UPDATE', '42501']);
  });

  it('keeps the entry untouched when the server cannot be reached', async () => {
    const { db, writer, executed } = setup(UpdateType.PUT, new Error('network down'));
    expect(await retryFailedUpload(db, 'crud-1', writer)).toBe('offline');
    expect(executed).toEqual([]);
  });
});
