import AsyncStorage from '@react-native-async-storage/async-storage';
import { pendingUploadCount } from '@health360/local-db';
import { SyncStreamConnectionMethod } from '@powersync/common';
import type { AuthChangeEvent, Session } from '@supabase/supabase-js';
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

/** The user whose rows (and pending writes) are in the local database. */
const DB_OWNER_KEY = 'health360.local-db-owner';

/**
 * Set only while the user is signing out on purpose. Supabase also emits SIGNED_OUT on its own (a
 * refresh token that stopped working), and that must not delete writes nobody chose to give up.
 */
let signOutRequested = false;

/**
 * Makes sure the local database belongs to `userId` before anything reads or syncs it. A session
 * can vanish without a SIGNED_OUT event (revoked or expired while the app was closed), so the next
 * person to sign in must never inherit the previous user's rows or their unsent writes.
 */
async function bindLocalDatabase(userId: string): Promise<void> {
  await withOwnerLock(async () => {
    if ((await AsyncStorage.getItem(DB_OWNER_KEY)) === userId) return;
    await powersync.disconnectAndClear();
    await AsyncStorage.setItem(DB_OWNER_KEY, userId);
  });
}

async function clearLocalDatabase(): Promise<void> {
  await withOwnerLock(async () => {
    await powersync.disconnectAndClear();
    await AsyncStorage.removeItem(DB_OWNER_KEY);
  });
}

/**
 * On the web, several tabs share one database and each gets the same auth events. Without a
 * cross-tab lock, two tabs could both read the old owner and the slower one would clear the
 * database after the other had already accepted the new user's writes. Native apps have one
 * instance and no Web Locks, so the work just runs.
 */
function withOwnerLock(work: () => Promise<void>): Promise<void> {
  const locks = typeof navigator === 'undefined' ? undefined : navigator.locks;
  return locks ? locks.request('health360-local-db-owner', work) : work();
}

function connect() {
  return powersync.connect(connector, {
    // A 2 s retry (default 5 s) keeps "reconnect, then visible on another device" near 5 s (PLAN.md §3).
    retryDelayMs: 2000,
    // WebKitGTK buffers streamed fetch responses, which delayed live updates in Tauri on Linux by
    // ~20 s in the Phase 0 test. WebSockets aren't buffered.
    ...(isTauri() ? { connectionMethod: SyncStreamConnectionMethod.WEB_SOCKET } : {}),
  });
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>({ session: null, loading: true });

  useEffect(() => {
    // Events are handled one at a time, so a database clear always finishes before a new
    // session is published to the screens or connected.
    let queue = Promise.resolve();
    const handle = async (event: AuthChangeEvent, session: Session | null) => {
      if (session) {
        await bindLocalDatabase(session.user.id);
        setState({ session, loading: false });
        // Token refreshes keep the existing connection; PowerSync fetches fresh credentials itself.
        if (event === 'INITIAL_SESSION' || event === 'SIGNED_IN') await connect();
      } else {
        setState({ session: null, loading: false });
        if (event === 'SIGNED_OUT' && signOutRequested) {
          signOutRequested = false;
          await clearLocalDatabase();
        } else {
          // Signed out without asking (or no session at launch): keep the rows, unsent writes
          // included, for the same user's return. bindLocalDatabase clears them if someone else
          // signs in.
          await powersync.disconnect();
        }
      }
    };
    const { data } = supabase.auth.onAuthStateChange((event, session) => {
      queue = queue.then(() => handle(event, session)).catch((error: unknown) => console.warn('Auth change failed', error));
    });
    return () => data.subscription.unsubscribe();
  }, []);

  return <AuthContext.Provider value={state}>{children}</AuthContext.Provider>;
}

export const useAuth = () => useContext(AuthContext);

export interface UnsyncedChanges {
  /** Writes still waiting to upload. */
  pending: number;
  /** Writes the server rejected, kept in Settings for retry. */
  rejected: number;
}

/**
 * Signs out and removes this account's data from the device. If that would delete changes that
 * never reached the server, nothing happens and the counts come back so the caller can ask first;
 * pass `discardUnsynced` once the user has agreed.
 */
export async function signOut(options: { discardUnsynced?: boolean } = {}): Promise<UnsyncedChanges | null> {
  if (!options.discardUnsynced) {
    const pending = await pendingUploadCount(powersync);
    const rejected = (await powersync.get<{ n: number }>('SELECT count(*) AS n FROM failed_uploads')).n;
    if (pending > 0 || rejected > 0) return { pending, rejected };
  }
  signOutRequested = true;
  await supabase.auth.signOut();
  return null;
}
