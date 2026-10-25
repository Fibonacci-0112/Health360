import { AppSchema } from '@health360/local-db';
import { PowerSyncDatabase, WASQLiteVFS } from '@powersync/web';
import { isTauri } from '@/platform/runtime';

/**
 * Browsers, the installed PWA and the Tauri webview. IndexedDB storage is the default because it
 * works in every engine, including WebKit inside Tauri on Linux (PLAN.md §4). Worker files are
 * copied into public/@powersync by `powersync-web copy-assets`.
 *
 * Tauri: WebKitGTK can't start a SharedWorker from the tauri:// scheme (found in the Phase 0 test),
 * so the desktop app uses dedicated workers. It has one window, so multi-tab support isn't needed.
 */
export function createPowerSync() {
  return new PowerSyncDatabase({
    schema: AppSchema,
    database: {
      dbFilename: 'health360.sqlite',
      vfs: WASQLiteVFS.IDBBatchAtomicVFS,
      worker: '/@powersync/worker.js',
      enableMultiTabs: !isTauri(),
    },
    sync: { worker: '/@powersync/worker.js' },
  });
}
