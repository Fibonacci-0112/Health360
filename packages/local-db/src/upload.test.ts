import { describe, expect, it } from 'vitest';
import { isPermanentError, RemoteWriteError, toServerRow } from './upload.ts';
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
