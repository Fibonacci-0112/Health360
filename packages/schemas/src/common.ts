import { z } from 'zod';

export const uuid = z.uuid();
/** ISO timestamp with offset, as Postgres timestamptz serialises it. */
export const timestamp = z.iso.datetime({ offset: true });
/** The user's local calendar day (PLAN.md §2.3), YYYY-MM-DD. */
export const localDate = z.iso.date();
