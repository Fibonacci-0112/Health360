import { supabaseWriter } from '@health360/api';
import { uploadPending } from '@health360/local-db';
import type { CommonPowerSyncDatabase, PowerSyncBackendConnector } from '@powersync/common';
import { env } from '@/lib/env';
import { supabase } from '@/lib/supabase';

/**
 * Downloads authenticate with the Supabase access token; uploads go through PostgREST with the
 * same session, so RLS checks every write. A write the server rejects for good (RLS, a constraint)
 * is moved to `failed_uploads` instead of blocking the queue (PLAN.md §4).
 */
export const connector: PowerSyncBackendConnector = {
  async fetchCredentials() {
    const { data, error } = await supabase.auth.getSession();
    if (error) throw error;
    if (!data.session) return null;
    return { endpoint: env.powersyncUrl, token: data.session.access_token };
  },

  async uploadData(db: CommonPowerSyncDatabase) {
    await uploadPending(db, supabaseWriter(supabase), (op) => {
      // Sentry (with PII scrubbing) replaces this in Phase 1. Never log row data: it is health data.
      console.warn(`Upload quarantined: ${op.op} ${op.table}/${op.id} (${op.code ?? 'no code'})`);
    });
  },
};
