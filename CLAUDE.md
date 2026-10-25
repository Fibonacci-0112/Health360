# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

Health360 is still in **planning**. The repo has no application code, package manifest, build, lint or tests yet. It holds three design documents:

- `docs/PLAN.md` is the source of truth for architecture (§2), the phased roadmap (§3), risks (§4), the test strategy (§5), running costs (§6) and the first files to create (§7).
- `docs/FEATURES.md` is the feature catalogue (PLAN's "section 1"). Each feature is tagged by phase: `[MVP]` (Phase 1), `[P2]`, `[P3]`, `[P4]`. ★ marks features taken from competitor research.
- `docs/adr/0001-language-and-stack.md` records why the stack is TypeScript + Expo + Tauri + PowerSync + Supabase. New architectural decisions go in new numbered ADRs (for example, PLAN says an Electron fallback must be recorded in an ADR).

### Editing the docs
- The three docs cross-reference each other by section number (`§2.8`, `PLAN.md §2.7`). If you add, move or renumber a section, update every reference to it in all three files.
- Facts appear in more than one doc (formulas, phase tags, fallback rules, retest intervals). A change in one place usually needs the same change in the others. Keep FEATURES and PLAN §2.8/§2.9 consistent in particular.
- Prices, SDK versions and statuses are dated (for example, "list prices checked Oct 2026"). When you change one, update the date and the source link.
- Keep the existing style: short plain sentences, tables for comparisons, a cited source for every formula and price.

## Planned commands (not yet runnable)

From PLAN §2.2 and §5, once Phase 0 sets up the monorepo (pnpm + Turborepo):
- Typecheck, lint and unit tests with Vitest (+ fast-check property tests for `packages/domain`).
- `supabase start` for the local stack; `supabase test db` for pgTAP RLS/storage tests; `supabase gen types` for the schema-drift check.
- Deno tests for Edge Functions, using recorded Claude responses (no live API calls in CI).
- Playwright for web E2E (Chromium, Firefox, WebKit), Maestro for Android E2E, `tauri-driver` for desktop smoke tests.
- `expo export -p web` produces the web build that Tauri wraps.

Replace this section with the real commands when they exist.

## Planned architecture (big picture)

The layout is in PLAN §2.2. The points below span several parts of the system:

- **Clients:** an Expo app (`apps/app`) for Android and web/PWA, with iOS deferred but kept buildable. Tauri 2 (`apps/desktop`) wraps the Expo web export for Windows and Linux. Platform-specific code lives only behind `src/platform/*.native.ts` / `*.web.ts` adapters, and only libraries that support both Android and iOS are allowed.
- **Offline-first data:** PowerSync syncs local SQLite with Supabase Postgres. The Drizzle schema in `packages/local-db` generates the PowerSync client schema. Writes go up through supabase-js, so RLS still applies. Permanent upload errors go to a `failed_uploads` table so they don't block the queue.
- **Shared logic:** `packages/domain` is pure TypeScript with no I/O (units, TDEE, macros, progression, costs, the physiology resolver, exercise energy). `packages/schemas` holds zod schemas used by forms, Edge Function contracts and Claude's structured output. Both are copied into `supabase/functions/_shared/` by `tools/codegen`.
- **AI:** Claude is called only from Supabase Edge Functions; the API key never reaches the client. The model proposes and code checks: every output goes through a validator in `packages/domain`, gets one repair round, then falls back to a manual flow. The AI never produces numbers that code can compute. Model, effort and request rules are in PLAN §2.4.

### Invariants code must keep
- **RLS on every table**, enforced by a CI coverage test. Every row carries `profile_id` and/or `household_id` (copied onto child rows) so policies never need joins. Personal rows key on `profile_id`, not the auth user id. Sync Streams must mirror RLS; `shared_profile` must check `sharing_grants`.
- **Table conventions:** client-generated UUIDv7 `id`, `created_at`/`updated_at`, `local_date` on event rows, `text` + CHECK instead of Postgres enums, money as `amount_minor bigint` + `currency`, `source` + `external_id` on imported rows. Migrations are additive only.
- **Units:** store each quantity as entered (value + unit) plus a canonical metric column, recomputed by a Postgres trigger. All conversions go through `packages/domain/units` with the exact factors in PLAN §2.6. PR comparisons use canonical values with a tolerance. Lab conversions are defined per biomarker, never guessed. Physiology values use fixed scientific units.
- **Measured beats estimated:** the physiology resolver (PLAN §2.8) prefers pinned, then lab, then field test, then device/manual, then estimate, newest first within a tier. Estimates are computed on demand and not stored. Every derived number exposes its inputs and source.
- **Session energy estimates are snapshots** (`workout_sessions.energy_estimate`, with inputs and method version). Later edits to zones or physiology never change them unless the user chooses "recalculate history". Exercise kcal is never added to the calorie target.
- **Heart-rate medication flag:** when it is set, nothing is derived from age-predicted max HR. A threshold known only as pace or power is never applied to HR data. The LTHR estimate never sets zones.
- **Wellness, not medical:** estimates are shown as ranges with the formula named; fat burned in a session is never presented as body fat lost; no diagnosis or dosing; lab reference ranges come first. Health values must never reach Sentry. No AI on photos without `photo_ai` consent.
