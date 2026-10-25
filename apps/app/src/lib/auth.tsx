import { SyncStreamConnectionMethod } from '@powersync/common';
import type { Session } from '@supabase/supabase-js';
import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { connector } from '@/db/connector';
import { powersync } from '@/db/powersync';
import { isTauri } from '@/platform/runtime';
import { supabase } from './supabase';

interface AuthState {
  session: Session | null;
  /** True until the stored session has been read on launch. */
  loading: boolean;
}

const AuthContext = createContext<AuthState>({ session: null, loading: true });

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>({ session: null, loading: true });

  useEffect(() => {
    const { data } = supabase.auth.onAuthStateChange((event, session) => {
      setState({ session, loading: false });
      if (session) {
        // Starts syncing, and uploads whatever was written while signed out of the network.
        void powersync.connect(connector, {
          // A 2 s retry (default 5 s) keeps "reconnect, then visible on another device" near 5 s (PLAN.md §3).
          retryDelayMs: 2000,
          // WebKitGTK buffers streamed fetch responses, which delayed live updates in Tauri on Linux by
          // ~20 s in the Phase 0 test. WebSockets aren't buffered.
          ...(isTauri() ? { connectionMethod: SyncStreamConnectionMethod.WEB_SOCKET } : {}),
        });
      } else if (event === 'SIGNED_OUT') {
        // Another person may sign in next on this device: drop every local row.
        void powersync.disconnectAndClear();
      }
    });
    return () => data.subscription.unsubscribe();
  }, []);

  return <AuthContext.Provider value={state}>{children}</AuthContext.Provider>;
}

export const useAuth = () => useContext(AuthContext);

export async function signOut() {
  await supabase.auth.signOut();
}
