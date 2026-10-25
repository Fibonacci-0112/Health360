import Anthropic from '@anthropic-ai/sdk';
import { zodOutputFormat } from '@anthropic-ai/sdk/helpers/zod';
import { quantity } from '../_shared/generated/domain/index.ts';
import {
  aiHelloOutputSchema,
  aiHelloRequestSchema,
  type AiHelloResponse,
} from '../_shared/generated/schemas/index.ts';
import type { Authenticate } from '../_shared/auth.ts';
import { corsHeaders, json } from '../_shared/http.ts';

/** Anything with the shape of `client.messages.parse`, so tests can pass a recorded response. */
export type ParseFn = Anthropic['messages']['parse'];

const SYSTEM = [
  'You write one short, friendly greeting and one general wellness tip for a fitness app.',
  'This is a wellness app, not a medical device: never diagnose, and never suggest doses of anything.',
  'Do not state or estimate any numbers; the app computes all figures itself.',
].join(' ');

/**
 * Phase 0 smoke test for the AI layer (PLAN.md §2.4): one structured-output call, with the refusal
 * and truncation checks every AI route needs, and domain math computed by code rather than the model.
 */
export function makeHandler(parse: ParseFn, model: string, authenticate: Authenticate) {
  return async (req: Request): Promise<Response> => {
    if (req.method === 'OPTIONS') return new Response('ok', { headers: corsHeaders });
    if (req.method !== 'POST') return json({ error: 'method_not_allowed' }, 405);
    if (!(await authenticate(req))) return json({ error: 'unauthorized' }, 401);

    const input = aiHelloRequestSchema.safeParse(await req.json().catch(() => null));
    if (!input.success) return json({ error: 'invalid_request', issues: input.error.issues }, 400);

    const weight = quantity('body_weight', input.data.weight, input.data.weight_unit);

    let response: Awaited<ReturnType<ParseFn>>;
    try {
      response = await parse(
        {
          model,
          max_tokens: 16000,
          // Effort is the only depth control on this model; extraction-sized work runs at low.
          output_config: { effort: 'low', format: zodOutputFormat(aiHelloOutputSchema) },
          // A declined request is re-run server-side on Anthropic's recommended fallback model.
          fallbacks: 'default',
          system: SYSTEM,
          messages: [{ role: 'user', content: 'Greet someone who just set up their health tracker.' }],
        },
        { headers: { 'anthropic-beta': 'server-side-fallback-2026-07-01' } },
      );
    } catch (error) {
      // The SDK has already retried transient failures. Answer in the same JSON + CORS shape as every
      // other outcome, so the app can show it; rate limits get their own status so it can back off.
      if (error instanceof Anthropic.RateLimitError) return json({ error: 'rate_limited' }, 429);
      console.error('ai-hello: model call failed', error instanceof Anthropic.APIError ? error.status : error);
      return json({ error: 'upstream_error' }, 502);
    }

    if (response.stop_reason === 'refusal') return json({ error: 'refused' }, 422);
    if (response.stop_reason === 'max_tokens') return json({ error: 'truncated' }, 502);
    if (!response.parsed_output) return json({ error: 'unparseable' }, 502);

    // Validate again with the shared schema: the SDK checks the JSON shape, this also enforces lengths.
    const output = aiHelloOutputSchema.safeParse(response.parsed_output);
    if (!output.success) return json({ error: 'invalid_output', issues: output.error.issues }, 502);

    const body: AiHelloResponse = {
      output: output.data,
      weight_kg: weight.canonical,
      model: response.model,
      usage: { input_tokens: response.usage.input_tokens, output_tokens: response.usage.output_tokens },
    };
    return json(body);
  };
}
