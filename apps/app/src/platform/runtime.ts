/** True inside the Tauri desktop shell, whose bridge is injected before the page's own scripts. */
export function isTauri(): boolean {
  return typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window;
}
