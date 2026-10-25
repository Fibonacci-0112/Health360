/** Native apps keep their SQLite file until uninstall; nothing to request. */
export async function requestPersistentStorage(): Promise<boolean> {
  return true;
}

export function registerServiceWorker(): void {}
