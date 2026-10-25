import { aiHelloResponseSchema, type AiHelloRequest, type AiHelloResponse } from '@health360/schemas';
import type { SupabaseClient } from '@supabase/supabase-js';

/** Calls the Phase 0 `ai-hello` function and validates its response against the shared schema. */
export async function callAiHello(client: SupabaseClient, body: AiHelloRequest): Promise<AiHelloResponse> {
  const { data, error } = await client.functions.invoke('ai-hello', { body });
  if (error) throw error;
  return aiHelloResponseSchema.parse(data);
}
