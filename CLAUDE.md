# CLAUDE.md

Instructions for Claude Code working in the Health360 repository. Keep this file aligned with the codebase and accepted architecture decisions.

## Project goals and current state

Health360 is a household-oriented health and fitness app for nutrition, workouts, body metrics, physiology, and goals. It is intended to work offline and across Android, web/PWA, and Windows/Linux desktop. Health and fitness values are sensitive personal data.

The repository is currently in **Phase 0: foundation and risk tests**. The implementation plan describes later phases; do not assume a planned feature is already implemented. Treat the current source, package manifests, migrations, tests, and CI as the facts on the ground. The `notes` table and the `ai-hello` Edge Function are throwaway Phase 0 scaffolding; do not build features on them.

Before significant work, read the relevant parts of:

- `README.md` — repository layout and quick start.
- `docs/PLAN.md` — architecture, data model, privacy, unit conventions, roadmap, and risks.
- `docs/FEATURES.md` — feature requirements.
- `docs/adr/0001-language-and-stack.md` — accepted stack decision.
- `docs/phase0.md` — current implementation status, local setup, and "What the tests found" (bugs and platform limits already hit; don't reintroduce them).

## Stack and repository layout

Use the existing stack. **Do not migrate the app to Next.js or replace Expo Router with a web-only framework unless explicitly asked and the change is agreed as an architecture decision.** React Native + React Native Web is intentional to share the app across native and web targets.

- Runtime/tooling: TypeScript, Node.js ≥22.18 (`engines` in `package.json`; CI reads the major version from `.nvmrc`), pnpm 10.28.0 (`packageManager`), and Turborepo.
- `.npmrc` sets `node-linker=hoisted` because Metro resolves far more reliably from a flat `node_modules`. Don't change it.
- Client: Expo, React Native, Expo Router, and React Native Web. Android and web/PWA are the current targets; iOS is designed for but native Apple support is deferred.
- Desktop: Tauri 2 wraps the exported web app for Windows and Linux.
- Backend: Supabase Postgres, Auth, Row-Level Security (RLS), Storage, and TypeScript/Deno Edge Functions.
- Offline data: SQLite with Drizzle and PowerSync. Offline writes must remain reliable and must not weaken server-side authorization.
- Shared code: Zod contracts in `packages/schemas`; deterministic, I/O-free calculations in `packages/domain`.

Key paths:

- `apps/app` — Expo app, routes (`app/`), UI, platform adapters (`src/platform`), local DB integration (`src/db`), and PWA assets (`public/`).
- `apps/desktop` — Tauri desktop wrapper (`src-tauri/tauri.conf.json`).
- `packages/config` — shared ESLint config and `tsconfig.base.json`.
- `packages/domain` — pure domain logic, units (`src/units`), and client-side UUIDv7 helpers (`src/ids`).
- `packages/schemas` — shared Zod schemas and validated payload contracts.
- `packages/local-db` — Drizzle/SQLite schema (`src/schema.ts`) and offline upload handling (`src/upload.ts`).
- `packages/api` — typed Supabase writer and Edge Function clients.
- `supabase/migrations`, `supabase/functions`, `supabase/tests/database` — database, server functions, and pgTAP tests.
- `supabase/functions/_shared` — hand-written Edge Function helpers (`auth.ts`, `http.ts`) plus `generated/`, the codegen copy of `packages/domain` and `packages/schemas`.
- `powersync` — sync streams (`sync-config.yaml`) and local service configuration.
- `e2e/sync` — two-client isolation and offline sync tests.
- `tools/codegen` — Edge Function codegen (`copy-shared.mjs`) and schema-drift check (`check-drift.mjs`); `tools/db` — Docker-free local database test runner.

## Standard development commands

Install dependencies with the lockfile respected:

```sh
pnpm install --frozen-lockfile
```

Run the main checks before finishing a change (these are what CI runs):

```sh
pnpm typecheck
pnpm lint
pnpm test
pnpm codegen:check
```

Useful targeted commands:

```sh
pnpm --filter @health360/app web
pnpm --filter @health360/app export:web   # PWA export + service-worker precache
pnpm --filter @health360/desktop dev
pnpm --filter @health360/e2e-sync test
# For each function in supabase/functions/* (CI currently runs ai-hello):
cd supabase/functions/ai-hello && deno check index.ts handler.ts && deno test --no-prompt
```

For local database work, Docker and the Supabase CLI are normally used. `supabase db start` (what CI uses for pgTAP) is enough for database tests; `supabase start` also brings up Auth and REST for the app and sync tests.

```sh
supabase db start
supabase test db
supabase db lint --level warning --fail-on error
DATABASE_URL=postgresql://postgres:postgres@127.0.0.1:54322/postgres pnpm drift:check
```

Two-client sync tests require local Supabase and PowerSync services plus the environment variables documented in `docs/phase0.md`. If Docker is unavailable, `pnpm db:test:local` runs the migrations and pgTAP tests against a plain local Postgres. Do not claim a check passed unless it was actually run and passed.

## Architecture and implementation rules

### Keep boundaries explicit

- Put deterministic calculations and domain rules in `packages/domain`, not inside React components, route files, database adapters, or Edge Functions.
- Keep `packages/domain` pure: no file, network, database, or other I/O; no runtime dependency additions without a strong reason. It must be usable from the app and Edge Functions.
- Put reusable request/response and structured-output validation in `packages/schemas`. Parse and validate untrusted inputs at system boundaries rather than relying on TypeScript types alone.
- Keep platform-specific behavior behind the existing platform adapters in `apps/app/src/platform` (e.g. `isTauri()` in `runtime.ts`) or `.web.ts` file variants. Check the native and web behavior before changing shared code.
- Do not upgrade the framework or major dependencies as a side effect of unrelated work.

### Health calculations, units, and numerical correctness

- Use `packages/domain/src/units` as the single source of truth for unit conversion factors. Do not duplicate conversion constants in UI code, API clients, prompts, or migrations.
- Preserve both the value and unit as entered and the canonical value used for math and comparisons. The database trigger recomputes canonical values server-side; maintain parity with the client logic.
- Avoid converting through a rounded display value. Round for presentation, not as an intermediate calculation step.
- Money is integer minor units plus a currency code (`amount_minor bigint` / `*_minor` + `currency`, per PLAN.md), never floating point.
- Keep measured values distinct from estimates. Show a value's source and method wherever the design calls for them; never silently replace a lab or field-test measurement with an estimate.
- Add deterministic unit/golden tests for calculation changes. Add property-based tests where useful, following the existing `fast-check` patterns. Check boundaries, invalid inputs, unit changes, and rounding.
- Do not invent clinical thresholds, formulas, nutrient references, or lab ranges. Use cited authoritative sources and record their provenance in the relevant data or documentation.

### Database, migrations, and offline sync

- Add schema changes as a **new migration** in `supabase/migrations/`; do not rewrite an existing migration that may already have been applied.
- Every personal-data table must enable RLS and have policies appropriate to its access scope. The RLS coverage test in `supabase/tests/database/00_rls_coverage.test.sql` is a required guardrail, not an obstacle to bypass.
- Functions in the `private` schema that RLS policies or triggers call must be granted `EXECUTE` to `authenticated` (and never to `anon`). pgTAP setup running as superuser can hide a missing grant, so write behavioural tests as the `authenticated` role; `00_rls_coverage` checks the grants directly.
- Follow the existing access model and ownership conventions. Personal rows should carry the appropriate `profile_id` and/or `household_id` so authorization and sync filters do not depend on unsafe joins.
- Keep Postgres authorization and PowerSync stream filters aligned. Never loosen an RLS policy or a sync rule simply to make a test pass. Test that one user's records and pending uploads are not visible to another user.
- When adding or changing a synced table or column, review all of the related pieces together: migration, RLS policies and tests, `packages/local-db/src/schema.ts`, `powersync/sync-config.yaml`, upload serialization, and schema-drift tests.
- If a Postgres `json`/`jsonb` or boolean column is added or changed, update `SERVER_COLUMN_TYPES` in `packages/local-db/src/schema.ts` so values are serialized correctly on upload. `pnpm drift:check` (`tools/codegen/check-drift.mjs`) fails if one is missing.
- Respect access scopes: user-owned, household-shared, global reference, and server-only data are not interchangeable. Invites, secrets, and other server-only data must not be exposed through client sync streams.
- Keep the failed-upload quarantine behavior (`failed_uploads`). Permanent upload errors and updates that match no row must not silently block the queue or be reported as successful writes, and retries must not duplicate quarantine entries.
- Use client-generated UUIDv7 IDs where required by the existing data model. Do not introduce duplicate records during retries or imports.

### Privacy, authentication, and security

- Treat health measurements, lab reports, body photos, account details, and household relationships as sensitive data. Collect, transmit, display, log, and retain only what the feature needs.
- Require explicit authorization on the server. Never trust a client-supplied profile or household ID as proof of access; validate the authenticated Supabase user and enforce RLS.
- Edge Functions must validate the actual signed-in user with `authenticateUser` in `supabase/functions/_shared/auth.ts`. The gateway's `verify_jwt` also accepts the public anon key, so it is not proof that a user is authenticated.
- Keep service-role keys, database credentials, and `ANTHROPIC_API_KEY` server-side in Supabase secrets. Never put them in Expo config, client bundles, logs, tests committed with real credentials, or example files.
- Do not log health values, prompts containing health data, auth tokens, signed URLs, or private identifiers. If telemetry is introduced, scrub PII and sensitive payloads.
- Do not send photos to AI without the required `photo_ai` consent. Consents are append-only (users may insert, not update or delete). Strip photo EXIF/location metadata on-device where applicable.
- Sharing grants are read-only for now; write access arrives with the shared-profile stream.
- Preserve account-switch isolation: a local database and its pending uploads must remain bound to the owning user and must be cleared or safely replaced before another user's session is used. Only an explicit, user-requested sign-out clears the device; Supabase's own `SIGNED_OUT` event (e.g. a dead refresh token) must not (see `apps/app/src/lib/auth.tsx`).
- Add or update authorization tests whenever access boundaries change. For database work, include negative tests proving another user cannot read or mutate the data.

### Claude and other AI features

- Call Claude from Supabase Edge Functions only. The client must never receive the Anthropic API key.
- Use structured output and the shared schemas in `packages/schemas`; validate the parsed result again on the server.
- Treat model output as untrusted input. Validate calculations, ranges, nutrient arithmetic, costs, exercise IDs, equipment constraints, and other domain rules in `packages/domain` before saving or presenting it. Follow the documented one-repair-then-fail/manual-fallback approach where applicable.
- AI may propose content; code must compute anything deterministic. Do not let a model invent a calculated number that the app can calculate itself.
- Preserve the distinction between measured and estimated physiology data in prompts, outputs, and UI.
- Frame the product as wellness and fitness support, not diagnosis or treatment. Do not implement diagnosis or medication-dosing advice. Preserve safety gates, estimate labels, disclaimers, and user-consent requirements.

### UI, accessibility, and platform behavior

- Prefer shared, cross-platform components and existing theme conventions. Avoid web-only APIs in shared React Native code unless guarded behind a platform adapter.
- For React Native Web accessibility, use the `role` and `aria-*` props; React Native Web ignores `accessibilityState`.
- Test loading, error, empty, offline, and sync-pending states for data-entry flows. Make failed saves visible; never show success when no row was changed or a write was rejected.
- Preserve the PWA manifest, service-worker precache generation (`apps/app/scripts/write-precache.mjs`), and offline behavior when changing web export or routing; verify with `export:web`.
- Tauri workarounds are deliberate and gated with `isTauri()`; keep them desktop-only:
  - PowerSync web uses `enableMultiTabs: false` (WebKitGTK can't start a SharedWorker from `tauri://`) in `apps/app/src/db/database.web.ts`.
  - Sync uses WebSockets (WebKitGTK buffers streamed HTTP) in `apps/app/src/lib/auth.tsx`.
  - `tauri.conf.json` keeps `dangerousDisableAssetCspModification: ["style-src"]` so React Native Web's runtime styles load. Check CSP `connect-src` when changing hosts.
  - Known issue: occasional Linux reloads leave the PowerSync DB lock held (see `docs/phase0.md`).

## Generated files and source of truth

- Edit source files under `packages/domain/src` and `packages/schemas/src`, not the generated copies under `supabase/functions/_shared/generated`.
- After changing either shared package, run `pnpm codegen`, then `pnpm codegen:check`. CI rejects stale generated output.
- Keep the client Drizzle schema and Postgres migration schema aligned. Use `pnpm drift:check` against a database with migrations applied.
- Update `docs/PLAN.md`, `docs/FEATURES.md`, `docs/phase0.md`, or an ADR when the corresponding architecture, scope, implementation status, risk, or decision materially changes. Do not mark planned features as complete just because scaffolding exists.

## Definition of done

1. Add or update tests for new behavior, especially calculations, authorization, privacy, offline retry, and schema changes.
2. Run the main checks. For shared-package changes also run codegen; for database or sync changes run `supabase test db`, `pnpm drift:check`, and the two-client tests when the environment permits.
3. Review the final diff for secrets, sensitive logs, generated-file edits, and stale docs.
4. Report the checks actually run and their results, and anything not tested. Never fabricate test results.
