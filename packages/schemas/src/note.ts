import { z } from 'zod';
import { localDate, uuid } from './common.ts';

/** Phase 0 throwaway table used to prove sync on every target. */
export const noteInputSchema = z.object({
  body: z.string().trim().min(1).max(2000),
});

export const noteSchema = noteInputSchema.extend({
  id: uuid,
  profile_id: uuid,
  local_date: localDate,
  created_at: z.string(),
  updated_at: z.string(),
});
export type Note = z.infer<typeof noteSchema>;
