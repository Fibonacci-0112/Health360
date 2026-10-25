import { uuidv7 } from '@health360/domain';
import { getRandomBytes } from 'expo-crypto';

/** A new row id: UUIDv7 from a secure random source, generated on the device (PLAN.md §2.3). */
export const newId = () => uuidv7(getRandomBytes);

/** Today's date in the device's time zone, as YYYY-MM-DD (event rows store a local day). */
export function localDate(d = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
