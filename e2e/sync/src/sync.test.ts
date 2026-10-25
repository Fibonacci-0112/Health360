import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { backendFromEnv, type TestBackend } from './backend.ts';
import { device, waitFor, type Device } from './client.ts';

/**
 * Phase 0 exit criteria (PLAN.md §3): a note written offline reaches a second device within about
 * 5 seconds of reconnecting, and user B never receives it. pgTAP proves the RLS half; this proves
 * the Sync Streams half, through the same upload loop the app uses.
 */
describe.skipIf(!process.env.SYNC_E2E)('two-client sync', () => {
  let backend: TestBackend;
  let a1: Device;
  let a2: Device;
  let b: Device;

  const notes = (d: Device) => d.db.getAll<{ id: string; body: string; profile_id: string }>('SELECT * FROM notes');

  beforeAll(async () => {
    backend = backendFromEnv();
    const alice = await backend.createUser('alice');
    const bob = await backend.createUser('bob');
    a1 = device(backend, alice, 'a1');
    a2 = device(backend, alice, 'a2');
    b = device(backend, bob, 'b');
    await Promise.all([a1.connect(), a2.connect(), b.connect()]);
  });

  afterAll(async () => {
    await Promise.all([a1, a2, b].filter(Boolean).map((d) => d.close()));
    await backend?.close();
  });

  it('each device starts with only its own profile and household', async () => {
    expect(await a1.db.getAll('SELECT id FROM profiles')).toEqual([{ id: a1.user.profileId }]);
    expect(await b.db.getAll('SELECT id FROM profiles')).toEqual([{ id: b.user.profileId }]);
    const members = await b.db.getAll<{ profile_id: string }>('SELECT profile_id FROM household_members');
    expect(members).toEqual([{ profile_id: b.user.profileId }]);
  });

  it('an offline note reaches the second device within about 5 seconds', async () => {
    await a1.disconnect();
    await a1.db.execute(`INSERT INTO notes (id, profile_id, body, local_date) VALUES (uuid(), ?, ?, '2026-10-25')`, [
      a1.user.profileId,
      'written offline',
    ]);
    expect((await a1.db.getUploadQueueStats()).count).toBe(1);

    const started = Date.now();
    await a1.connect();
    await waitFor(() => notes(a2), (rows) => rows.some((n) => n.body === 'written offline'), 15_000);
    const elapsed = Date.now() - started;
    expect(elapsed).toBeLessThan(5_000);
    expect((await a1.db.getUploadQueueStats()).count).toBe(0);
  });

  it('user B never receives it', async () => {
    // B writes a note of its own and waits for it to come back through sync. Once it has, B's
    // stream has caught up past A's write, so anything of A's that leaked would be here too.
    const [{ id }] = (await b.db.getAll<{ id: string }>('SELECT uuid() AS id')) as [{ id: string }];
    await b.db.execute(`INSERT INTO notes (id, profile_id, body, local_date) VALUES (?, ?, 'bob''s', '2026-10-25')`, [
      id,
      b.user.profileId,
    ]);
    // ps_oplog holds only rows the server has sent, so this waits for the server's copy of B's note.
    await waitFor(
      () => b.db.getAll("SELECT 1 FROM ps_oplog WHERE row_type = 'notes' AND row_id = ?", [id]),
      (rows) => rows.length === 1,
    );

    const bNotes = await notes(b);
    expect(bNotes.map((n) => n.body)).toEqual(["bob's"]);
    expect(await b.db.getAll('SELECT id FROM profiles WHERE id = ?', [a1.user.profileId])).toEqual([]);
    expect((await notes(a2)).map((n) => n.body)).not.toContain("bob's");
  });

  it('profile edits upload with server types (jsonb as an object, canonical height recomputed)', async () => {
    await a1.db.execute('UPDATE profiles SET unit_prefs = ?, height = 70, height_unit = ?, height_cm = 1 WHERE id = ?', [
      JSON.stringify({ load: 'lb' }),
      'in',
      a1.user.profileId,
    ]);
    await waitFor(() => a1.db.getUploadQueueStats(), (s) => s.count === 0);
    expect(a1.quarantined).toEqual([]);
    const row = await waitFor(
      () => a2.db.getOptional<{ unit_prefs: string; height_cm: number }>('SELECT unit_prefs, height_cm FROM profiles'),
      (r) => r?.height_cm !== null && r?.height_cm !== undefined && r.height_cm > 100,
    );
    expect(JSON.parse(row!.unit_prefs)).toEqual({ load: 'lb' });
    expect(row!.height_cm).toBeCloseTo(177.8, 10);
  });

  it('an offline edit to a note another device deleted is quarantined, not lost', async () => {
    const [{ id }] = (await a2.db.getAll<{ id: string }>("SELECT id FROM notes WHERE body = 'written offline'")) as [
      { id: string },
    ];
    await a1.disconnect();
    await a1.db.execute("UPDATE notes SET body = 'edited offline' WHERE id = ?", [id]);
    await a2.db.execute('DELETE FROM notes WHERE id = ?', [id]);
    await waitFor(() => a2.db.getUploadQueueStats(), (s) => s.count === 0);

    await a1.connect();
    await waitFor(() => a1.db.getUploadQueueStats(), (s) => s.count === 0);
    const failed = await a1.db.getAll<{ row_id: string; op: string; error_code: string }>(
      'SELECT row_id, op, error_code FROM failed_uploads',
    );
    expect(failed).toEqual([{ row_id: id, op: 'PATCH', error_code: 'PGRST116' }]);
    await waitFor(() => a1.db.getAll('SELECT id FROM notes WHERE id = ?', [id]), (rows) => rows.length === 0);
  });

  it('a delete RLS silently blocks (consents keep their history) is quarantined, not reported as done', async () => {
    const [{ id }] = (await b.db.getAll<{ id: string }>('SELECT uuid() AS id')) as [{ id: string }];
    await b.db.execute(
      `INSERT INTO consents (id, profile_id, kind, version, granted, decided_at) VALUES (?, ?, 'tos', '1', 1, ?)`,
      [id, b.user.profileId, new Date().toISOString()],
    );
    await waitFor(() => b.db.getUploadQueueStats(), (s) => s.count === 0);
    await waitFor(
      () => b.db.getAll("SELECT 1 FROM ps_oplog WHERE row_type = 'consents' AND row_id = ?", [id]),
      (rows) => rows.length === 1,
    );

    await b.db.execute('DELETE FROM consents WHERE id = ?', [id]);
    await waitFor(() => b.db.getUploadQueueStats(), (s) => s.count === 0);
    const failed = await b.db.getAll<{ op: string; error_code: string }>(
      "SELECT op, error_code FROM failed_uploads WHERE table_name = 'consents' AND row_id = ?",
      [id],
    );
    expect(failed).toEqual([{ op: 'DELETE', error_code: '42501' }]);
    // The server still has it, so sync puts it back.
    await waitFor(() => b.db.getAll('SELECT id FROM consents WHERE id = ?', [id]), (rows) => rows.length === 1);
  });

  it('a forged write is quarantined without blocking the queue', async () => {
    // B tries to write into A's profile: RLS rejects it (42501), so it is parked in failed_uploads,
    // and B's next, valid write still goes through.
    await b.db.execute(`INSERT INTO notes (id, profile_id, body, local_date) VALUES (uuid(), ?, 'forged', '2026-10-25')`, [
      a1.user.profileId,
    ]);
    await b.db.execute(`INSERT INTO notes (id, profile_id, body, local_date) VALUES (uuid(), ?, 'after', '2026-10-25')`, [
      b.user.profileId,
    ]);
    await waitFor(() => b.db.getUploadQueueStats(), (s) => s.count === 0);

    const failed = await b.db.getAll<{ table_name: string; error_code: string }>(
      "SELECT table_name, error_code FROM failed_uploads WHERE table_name = 'notes'",
    );
    expect(failed).toEqual([{ table_name: 'notes', error_code: '42501' }]);
    expect(b.quarantined.filter((q) => q.table === 'notes')).toHaveLength(1);

    // The server's state wins: the forged row disappears locally, the valid one stays.
    const bodies = await waitFor(
      async () => (await notes(b)).map((n) => n.body).sort(),
      (v) => !v.includes('forged') && v.includes('after'),
    );
    expect(bodies).toEqual(['after', "bob's"]);
    expect((await notes(a2)).map((n) => n.body)).not.toContain('forged');
  });
});
