import Anthropic from '@anthropic-ai/sdk';
import { makeHandler } from './handler.ts';

// ANTHROPIC_API_KEY comes from Supabase secrets; the key never reaches a client (PLAN.md §2.4).
// The gateway verifies the caller's JWT before this runs (config.toml: verify_jwt = true).
const client = new Anthropic();
const model = Deno.env.get('AI_HELLO_MODEL') ?? 'claude-opus-5-5';

Deno.serve(makeHandler(client.messages.parse.bind(client.messages), model));
