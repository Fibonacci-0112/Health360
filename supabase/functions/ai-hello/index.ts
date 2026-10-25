import Anthropic from '@anthropic-ai/sdk';
import { authenticateUser } from '../_shared/auth.ts';
import { makeHandler } from './handler.ts';

// ANTHROPIC_API_KEY comes from Supabase secrets; the key never reaches a client (PLAN.md §2.4).
// The gateway checks the JWT signature (config.toml: verify_jwt = true); the handler then requires a
// signed-in user, because the public anon key passes the gateway check too.
const client = new Anthropic();
const model = Deno.env.get('AI_HELLO_MODEL') ?? 'claude-opus-5-5';

Deno.serve(makeHandler(client.messages.parse.bind(client.messages), model, authenticateUser));
