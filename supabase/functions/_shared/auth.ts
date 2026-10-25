import { createClient } from '@supabase/supabase-js';

/** Returns the signed-in caller's user id, or null when the request has no user session. */
export type Authenticate = (req: Request) => Promise<string | null>;

/**
 * The gateway's `verify_jwt` only checks the signature, and the public anon key passes that check.
 * Asking Auth for the user rejects the anon key, signed-out sessions and deleted users, so no one can
 * spend AI quota without signing in.
 */
export const authenticateUser: Authenticate = async (req) => {
  const token = req.headers.get('Authorization')?.match(/^Bearer\s+(\S+)$/i)?.[1];
  if (!token) return null;
  const supabase = createClient(Deno.env.get('SUPABASE_URL')!, Deno.env.get('SUPABASE_ANON_KEY')!, {
    auth: { persistSession: false, autoRefreshToken: false },
  });
  const { data, error } = await supabase.auth.getUser(token);
  return error || !data.user ? null : data.user.id;
};
