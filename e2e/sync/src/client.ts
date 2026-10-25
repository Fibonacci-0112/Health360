import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { AppSchema, uploadPending, type QuarantinedOp } from '@health360/local-db';
import { PowerSyncDatabase, type PowerSyncBackendConnector } from '@powersync/node';
import type { TestBackend, TestUser } from './backend.ts';

export interface Device {
  db: PowerSyncDatabase;
  user: TestUser;
  quarantined: QuarantinedOp[];
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  close(): Promise<void>;
}

/** One app install: its own SQLite file, signed in as `user`, using the app's upload loop. */
export function device(backend: TestBackend, user: TestUser, name: string): Device {
  const dir = mkdtempSync(join(tmpdir(), `h360-${name}-`));
  const db = new PowerSyncDatabase({ schema: AppSchema, database: { dbFilename: `${name}.sqlite`, dbLocation: dir } });
  const quarantined: QuarantinedOp[] = [];
  const connector: PowerSyncBackendConnector = {
    fetchCredentials: async () => ({ endpoint: backend.powersyncUrl, token: user.token }),
    uploadData: (database) => uploadPending(database, user.writer, (op) => quarantined.push(op)),
  };
  return {
    db,
    user,
    quarantined,
    async connect() {
      await db.connect(connector, { crudUploadThrottleMs: 50, retryDelayMs: 500 });
      await db.waitForFirstSync();
    },
    disconnect: () => db.disconnect(),
    close: () => db.close(),
  };
}

export async function waitFor<T>(
  probe: () => Promise<T>,
  done: (value: T) => boolean,
  timeoutMs = 30_000,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await probe();
    if (done(value)) return value;
    if (Date.now() > deadline) throw new Error(`Timed out; last value: ${JSON.stringify(value)}`);
    await new Promise((r) => setTimeout(r, 50));
  }
}
