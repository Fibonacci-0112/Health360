import { isTauri } from './runtime';

/**
 * Asks the browser not to evict this origin's storage. Safari on iPhone can otherwise clear an
 * installed web app's data after a few weeks unused (PLAN.md §2.7), and unsynced entries with it.
 */
export async function requestPersistentStorage(): Promise<boolean> {
  if (typeof navigator === 'undefined' || !navigator.storage?.persist) return false;
  if (await navigator.storage.persisted()) return true;
  return navigator.storage.persist();
}

/** The service worker makes the shell open offline. Skipped in development and inside Tauri. */
export function registerServiceWorker(): void {
  if (process.env.NODE_ENV !== 'production') return;
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;
  if (isTauri()) return; // Tauri serves the app from disk already.
  navigator.serviceWorker.register('/sw.js').catch((error: unknown) => {
    console.warn('Service worker registration failed', error);
  });
}
