import Anthropic from '@anthropic-ai/sdk';
import { assertAlmostEquals, assertEquals } from 'jsr:@std/assert@^1';
import { makeHandler, makeNotConfiguredHandler, type ParseFn } from './handler.ts';

// Recorded-shape responses: no live Claude calls in CI (PLAN.md §5.4).
function fakeParse(response: Record<string, unknown>) {
  const calls: { body: Record<string, unknown>; options: Record<string, unknown> }[] = [];
  const parse = ((body: Record<string, unknown>, options: Record<string, unknown>) => {
    calls.push({ body, options });
    return Promise.resolve({
      model: 'claude-opus-5-5',
      usage: { input_tokens: 120, output_tokens: 80 },
      ...response,
    });
  }) as unknown as ParseFn;
  return { parse, calls };
}

const post = (body: unknown) =>
  new Request('http://localhost/ai-hello', { method: 'POST', body: JSON.stringify(body) });

const signedIn = () => Promise.resolve('00000000-0000-0000-0000-00000000000a');
const anonymous = () => Promise.resolve(null);

const ok = {
  stop_reason: 'end_turn',
  parsed_output: { greeting: 'Welcome aboard!', tip: 'Drink some water with each meal.' },
};

Deno.test('returns the parsed output with domain math computed in code', async () => {
  const { parse, calls } = fakeParse(ok);
  const res = await makeHandler(parse, 'claude-opus-5-5', signedIn)(post({ weight: 225, weight_unit: 'lb' }));
  assertEquals(res.status, 200);
  const body = await res.json();
  assertEquals(body.output, ok.parsed_output);
  assertAlmostEquals(body.weight_kg, 102.05828325, 1e-8);

  const [{ body: request, options }] = calls;
  assertEquals(request.model, 'claude-opus-5-5');
  assertEquals(request.fallbacks, 'default');
  assertEquals((request.output_config as { effort: string }).effort, 'low');
  assertEquals('temperature' in request, false);
  assertEquals((options.headers as Record<string, string>)['anthropic-beta'], 'server-side-fallback-2026-07-01');
});

Deno.test('a refusal is reported, not parsed', async () => {
  const { parse } = fakeParse({ stop_reason: 'refusal', parsed_output: null });
  const res = await makeHandler(parse, 'm', signedIn)(post({ weight: 80, weight_unit: 'kg' }));
  assertEquals(res.status, 422);
  assertEquals(await res.json(), { error: 'refused' });
});

Deno.test('a truncated response is reported, not parsed', async () => {
  const { parse } = fakeParse({ stop_reason: 'max_tokens', parsed_output: null });
  const res = await makeHandler(parse, 'm', signedIn)(post({ weight: 80, weight_unit: 'kg' }));
  assertEquals(res.status, 502);
  assertEquals(await res.json(), { error: 'truncated' });
});

Deno.test('output that breaks the shared schema is rejected', async () => {
  const { parse } = fakeParse({ stop_reason: 'end_turn', parsed_output: { greeting: '', tip: 'x' } });
  const res = await makeHandler(parse, 'm', signedIn)(post({ weight: 80, weight_unit: 'kg' }));
  assertEquals(res.status, 502);
  assertEquals((await res.json()).error, 'invalid_output');
});

Deno.test('a bad request never reaches the model', async () => {
  const { parse, calls } = fakeParse(ok);
  const res = await makeHandler(parse, 'm', signedIn)(post({ weight: -1, weight_unit: 'stone' }));
  assertEquals(res.status, 400);
  assertEquals(calls.length, 0);
});

Deno.test('a caller without a user session never reaches the model', async () => {
  const { parse, calls } = fakeParse(ok);
  const res = await makeHandler(parse, 'm', anonymous)(post({ weight: 80, weight_unit: 'kg' }));
  assertEquals(res.status, 401);
  assertEquals(calls.length, 0);
});

Deno.test('SDK failures come back as JSON with CORS headers', async () => {
  const failing = (error: unknown) => (() => Promise.reject(error)) as unknown as ParseFn;
  const rateLimited = new Anthropic.RateLimitError(429, { type: 'error' }, 'rate limited', new Headers());
  const cases: [unknown, number, string][] = [
    [rateLimited, 429, 'rate_limited'],
    [new Anthropic.APIConnectionError({ message: 'network down' }), 502, 'upstream_error'],
  ];
  for (const [error, status, code] of cases) {
    const res = await makeHandler(failing(error), 'm', signedIn)(post({ weight: 80, weight_unit: 'kg' }));
    assertEquals(res.status, status);
    assertEquals(await res.json(), { error: code });
    assertEquals(res.headers.get('Access-Control-Allow-Origin'), '*');
  }
});

Deno.test('without an API key the function answers with a readable error, and preflight still passes', async () => {
  const res = await makeNotConfiguredHandler(signedIn)(post({ weight: 80, weight_unit: 'kg' }));
  assertEquals(res.status, 503);
  assertEquals(await res.json(), { error: 'not_configured' });
  assertEquals(res.headers.get('Access-Control-Allow-Origin'), '*');
  const preflight = await makeNotConfiguredHandler(signedIn)(new Request('http://localhost/ai-hello', { method: 'OPTIONS' }));
  assertEquals(preflight.status, 200);
  // Without a user it stays a plain 401, so anonymous callers can't tell whether AI is set up.
  const anon = await makeNotConfiguredHandler(anonymous)(post({ weight: 80, weight_unit: 'kg' }));
  assertEquals(anon.status, 401);
});
