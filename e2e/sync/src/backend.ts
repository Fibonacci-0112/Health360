import { randomUUID } from 'node:crypto';
import { supabaseWriter } from '@health360/api';
import { RemoteWriteError, type RemoteWriter } from '@health360/local-db';
import { createClient } from '@supabase/supabase-js';
import { SignJWT } from 'jose';
import pg from 'pg';

export interface TestUser {
  userId: string;
  profileId: string;
  token: string;
  writer: RemoteWriter;
}

export interface TestBackend {
  powersyncUrl: string;
  createUser(name: string): Promise<TestUser>;
  close(): Promise<void>;
}

const env = (name: string) => {
  const v = process.env[name];
  if (!v) throw new Error(`${name} is not set`);
  return v;
};

/**
 * Picks the backend from the environment:
 *   SUPABASE_URL set → the real stack (`supabase start` + PowerSync): users through the Auth admin
 *   API, uploads through PostgREST. This is what CI runs.
 *   Otherwise DATABASE_URL + POWERSYNC_JWT_SECRET → plain Postgres with the Supabase shim, tokens
 *   minted here, and uploads run as role `authenticated` with the same JWT claims PostgREST sets.
 */
export function backendFromEnv(): TestBackend {
  return process.env.SUPABASE_URL ? supabaseBackend() : directBackend();
}

function supabaseBackend(): TestBackend {
  const url = env('SUPABASE_URL');
  const admin = createClient(url, env('SUPABASE_SERVICE_ROLE_KEY'), { auth: { persistSession: false } });
  return {
    powersyncUrl: env('POWERSYNC_URL'),
    async createUser(name) {
      const email = `${name}-${randomUUID()}@example.test`;
      const password = randomUUID();
      const created = await admin.auth.admin.createUser({
        email,
        password,
        email_confirm: true,
        user_metadata: { display_name: name },
      });
      if (created.error) throw created.error;
      const client = createClient(url, env('SUPABASE_ANON_KEY'), {
        auth: { persistSession: false, autoRefreshToken: false },
      });
      const signedIn = await client.auth.signInWithPassword({ email, password });
      if (signedIn.error) throw signedIn.error;
      const profile = await client.from('profiles').select('id').single();
      if (profile.error) throw profile.error;
      return {
        userId: signedIn.data.user.id,
        profileId: profile.data.id as string,
        token: signedIn.data.session.access_token,
        writer: supabaseWriter(client),
      };
    },
    async close() {},
  };
}

const IDENT = /^[a-z_][a-z0-9_]*$/;
const ident = (s: string) => {
  if (!IDENT.test(s)) throw new Error(`Bad identifier: ${s}`);
  return `"${s}"`;
};

function directBackend(): TestBackend {
  const pool = new pg.Pool({ connectionString: env('DATABASE_URL') });
  const secret = new TextEncoder().encode(env('POWERSYNC_JWT_SECRET'));
  const kid = process.env.POWERSYNC_JWT_KID ?? 'local-test';

  /** Runs one statement exactly as PostgREST would for this user. */
  const asUser = async (userId: string, sql: string, params: unknown[]) => {
    const client = await pool.connect();
    try {
      await client.query('begin');
      await client.query('set local role authenticated');
      await client.query(`select set_config('request.jwt.claims', $1, true)`, [
        JSON.stringify({ sub: userId, role: 'authenticated' }),
      ]);
      const res = await client.query(sql, params);
      await client.query('commit');
      return res;
    } catch (error) {
      await client.query('rollback').catch(() => {});
      const e = error as { message?: string; code?: string };
      throw new RemoteWriteError(e.message ?? String(error), e.code);
    } finally {
      client.release();
    }
  };

  // Payloads are decoded with json_populate_record, as PostgREST does, so a value with the wrong JSON
  // type (e.g. a jsonb column sent as a string) fails here exactly as it would in production.
  const writer = (userId: string): RemoteWriter => ({
    async put(table, id, data) {
      const row = { ...data, id };
      const cols = Object.keys(row).map(ident).join(', ');
      const updates = Object.keys(data).map((c) => `${ident(c)} = excluded.${ident(c)}`);
      await asUser(
        userId,
        `insert into public.${ident(table)} (${cols})
         select ${cols} from json_populate_record(null::public.${ident(table)}, $1::json)
         on conflict (id) do ${updates.length ? `update set ${updates.join(', ')}` : 'nothing'}`,
        [JSON.stringify(row)],
      );
    },
    async patch(table, id, data) {
      const cols = Object.keys(data).map(ident);
      if (cols.length === 0) return;
      const res = await asUser(
        userId,
        `update public.${ident(table)} set (${cols.join(', ')}) =
           (select ${cols.join(', ')} from json_populate_record(null::public.${ident(table)}, $2::json))
         where id = $1`,
        [id, JSON.stringify(data)],
      );
      // Same contract as supabaseWriter: an update that matched no row is a permanent failure.
      if (res.rowCount === 0) throw new RemoteWriteError(`${table}/${id}: the update matched no row`, 'PGRST116');
    },
    async delete(table, id) {
      const res = await asUser(userId, `delete from public.${ident(table)} where id = $1`, [id]);
      if (res.rowCount) return;
      // Same contract as supabaseWriter: a row that is still visible after a no-op delete was protected.
      const still = await asUser(userId, `select 1 from public.${ident(table)} where id = $1`, [id]);
      if (still.rowCount) throw new RemoteWriteError(`${table}/${id}: the delete was not allowed`, '42501');
    },
  });

  return {
    powersyncUrl: env('POWERSYNC_URL'),
    async createUser(name) {
      const userId = randomUUID();
      await pool.query('insert into auth.users (id, email, raw_user_meta_data) values ($1, $2, $3)', [
        userId,
        `${name}-${userId}@example.test`,
        { display_name: name },
      ]);
      const { rows } = await pool.query<{ id: string }>('select id from public.profiles where user_id = $1', [userId]);
      const token = await new SignJWT({ role: 'authenticated' })
        .setProtectedHeader({ alg: 'HS256', kid })
        .setSubject(userId)
        .setAudience('authenticated')
        .setIssuedAt()
        .setExpirationTime('1h')
        .sign(secret);
      return { userId, profileId: rows[0]!.id, token, writer: writer(userId) };
    },
    close: () => pool.end(),
  };
}
