import { z } from 'zod';

/**
 * Contract for the Phase 0 `ai-hello` Edge Function: proves a structured-output Claude call
 * and that `packages/domain` math runs inside the function.
 */
export const aiHelloRequestSchema = z.object({
  /** Body weight as typed, used to show domain code running server-side. */
  weight: z.number().positive().max(1000),
  weight_unit: z.enum(['kg', 'lb']),
});
export type AiHelloRequest = z.infer<typeof aiHelloRequestSchema>;

/** What Claude must return. Numbers the code can compute are never asked of the model. */
export const aiHelloOutputSchema = z.object({
  greeting: z.string().min(1).max(200),
  tip: z.string().min(1).max(300),
});
export type AiHelloOutput = z.infer<typeof aiHelloOutputSchema>;

export const aiHelloResponseSchema = z.object({
  output: aiHelloOutputSchema,
  /** Computed by `@health360/domain` inside the function, not by the model. */
  weight_kg: z.number(),
  model: z.string(),
  usage: z.object({ input_tokens: z.number(), output_tokens: z.number() }),
});
export type AiHelloResponse = z.infer<typeof aiHelloResponseSchema>;
