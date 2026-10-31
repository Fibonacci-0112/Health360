# CLAUDE.md

Instructions for Claude Code working in the Health360 repository. Keep this file aligned with the codebase and accepted architecture decisions.

## Project goals and current state

Health360 is a household-oriented health and fitness app for nutrition, workouts, body metrics, physiology, and goals. It targets Android and Windows desktop, with offline logging on both. A web (browser) app, Apple (iOS, macOS) and Linux desktop are out of scope ([ADR 0002](docs/adr/0002-dotnet-maui-blazor.md)). Health and fitness values are sensitive personal data.

The repository is in **Phase 0b: .NET foundation** (`docs/phase0b.md` tracks what is and isn't done). The implementation plan describes later phases; do not assume a planned feature is already implemented. Treat the current source, package manifests, migrations, tests, and CI as the facts on the ground. The `notes` table is throwaway Phase 0 scaffolding; do not build features on it.

The Phase 0 TypeScript prototype (Expo, Tauri, Edge Functions) was removed once the PowerSync .NET gate passed. Its code is in the git history, and `docs/phase0.md` records what it proved and what its tests found. Don't reintroduce it, or a TypeScript or Node toolchain, without an ADR.

Before significant work, read the relevant parts of:

- `README.md` — repository layout and quick start.
- `docs/PLAN.md` — architecture, data model, privacy, unit conventions, roadmap, and risks.
- `docs/FEATURES.md` — feature requirements.
- `docs/adr/0002-dotnet-maui-blazor.md` — accepted stack and platform decision (supersedes ADR 0001).
- `docs/phase0b.md` — .NET foundation status, setup and findings.
- `docs/phase0.md` — "What the tests found" in the prototype (bugs and platform limits already hit; don't reintroduce them).

## Stack and repository layout

The stack is fixed by ADR 0002. **Don't change it, or add web, Apple or Linux desktop targets, unless the user explicitly asks and the change is recorded as a new ADR.** See PLAN.md §2.2 for the layout.

- `src/Health360.Domain`: pure C# calculations, units (`Units/`) and UUIDv7 ids (`Ids/`). No I/O; see "Keep boundaries explicit".
- `src/Health360.Contracts`: records and validators for API payloads, forms and Claude's structured output.
- `src/Health360.UI`: a Razor Class Library with the MAUI app's screens, sign-in and session (`Auth/`), and the profile and sync-state services. It has no MAUI dependency, so it builds and is tested on Linux.
- `src/Health360.LocalDb`: the PowerSync client schema (`AppSchema`), uploads and the `failed_uploads` quarantine, and account binding (`LocalDatabase`).
- `src/Health360.Maui` (Android, Windows; supplies the platform SQLite, secure storage and endpoints) and `src/Health360.Server` (the ASP.NET Core API, its own host).
- The API replaces Edge Functions. It must validate the Supabase JWT and the signed-in user on every request, and query Postgres as that user so RLS applies. The service-role key is used only for admin jobs. `src/Health360.Server/Auth` does this for every endpoint not marked `AllowAnonymous`: endpoints take a `SignedInUser` and query only through `IUserDatabase` (`Data/`), never a connection of their own. Don't weaken the fallback policy or the token checks to make a test pass (see `docs/phase0b.md`, "API authentication").
- Backend: Supabase Postgres, Auth, Row-Level Security (RLS) and Storage. Offline data: SQLite through PowerSync .NET. Offline writes must remain reliable and must not weaken server-side authorization.

Other key paths:

- `tests/` — xUnit, FsCheck and bUnit tests for each project. `tests/Health360.Sync.Tests` runs the two-client isolation and offline sync tests (skipped unless `SYNC_E2E=1`, see `docs/phase0b.md`).
- `supabase/migrations`, `supabase/tests/database` — database schema and pgTAP tests. `supabase/templates` — Auth email templates.
- `powersync` — sync streams (`sync-config.yaml`) and local service configuration.
- `tools/drift` — schema-drift check of `AppSchema` against the migrations; `tools/db` — Docker-free local database test runner; `tools/cloud` — setup script for cloud environments.

## Standard development commands

The SDK version is pinned in `global.json` (the MAUI workload set via `dotnet workload install --version`, see `docs/phase0b.md`); package versions are central in `Directory.Packages.props`, with lock files. `Health360.Core.slnf` is everything except the MAUI app, so it builds without workloads. Warnings are errors.

Run the main checks before finishing a change (CI runs these):

```sh
dotnet restore Health360.Core.slnf
dotnet format Health360.Core.slnf --verify-no-changes
dotnet build Health360.Core.slnf -c Release
dotnet test --solution Health360.Core.slnf
```

After changing a package reference, run `dotnet restore` and commit the updated `packages.lock.json` files (CI restores in locked mode). Building `src/Health360.Maui` needs the `maui-android` or `maui-windows` workload; see `docs/phase0b.md`. Don't weaken `src/Health360.Domain/BannedSymbols.txt` or the purity tests to make Domain code compile; pass a `TimeProvider` or random source in instead.

For local database work, Docker and the Supabase CLI are normally used. `supabase db start` (what CI uses for pgTAP) is enough for database tests; `supabase start` also brings up Auth and REST for the app and sync tests.

```sh
supabase db start
supabase test db
supabase db lint --level warning --fail-on error
HEALTH360_DRIFT_DATABASE='Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres' dotnet run --project tools/drift
HEALTH360_TEST_DATABASE='Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres' \
  dotnet test --project tests/Health360.Server.Tests --filter-trait "Category=Database"
dotnet test --project tests/Health360.Sync.Tests   # needs SYNC_E2E=1, supabase start and the local PowerSync
```

Two-client sync tests require local Supabase and PowerSync services plus the environment variables documented in `docs/phase0b.md` ("Two-client sync test"). If Docker is unavailable, `bash tools/db/test-local.sh` runs the migrations and pgTAP tests against a plain local Postgres. Do not claim a check passed unless it was actually run and passed.

## Architecture and implementation rules

### Keep boundaries explicit

- Put deterministic calculations and domain rules in `Health360.Domain`, not inside Razor components, pages, database adapters, or API endpoints.
- Keep `Health360.Domain` pure: no file, network, database, clock, ambient randomness or other I/O, and no package or project references (`BannedSymbols.txt` and `PurityTests` enforce this). It must be usable from the app and the API.
- Put reusable request/response, form and structured-output validation in `Health360.Contracts`. Parse and validate untrusted inputs at system boundaries rather than relying on C# types alone.
- Keep platform-specific behavior in `Health360.Maui` behind interfaces the shared projects define (as with the platform SQLite, `SecureSessionStore` and `PreferencesOwnerStore`). Check Android and Windows behavior before changing shared code.
- Do not upgrade the framework or major dependencies as a side effect of unrelated work.

### Health calculations, units, and numerical correctness

- Use `src/Health360.Domain/Units` as the only source of unit conversion factors. Do not duplicate conversion constants in UI code, API clients, prompts, or migrations. Rows already stored were written by the prototype too, so don't change the factors, the evaluation order or the stored unit codes.
- Preserve both the value and unit as entered and the canonical value used for math and comparisons. The database trigger recomputes canonical values server-side; maintain parity with the client logic.
- Avoid converting through a rounded display value. Round for presentation, not as an intermediate calculation step.
- Money is integer minor units plus a currency code (`amount_minor bigint` / `*_minor` + `currency`, per PLAN.md), never floating point.
- Keep measured values distinct from estimates. Show a value's source and method wherever the design calls for them; never silently replace a lab or field-test measurement with an estimate.
- Add deterministic unit/golden tests for calculation changes. Add property-based tests where useful, following the existing FsCheck patterns in `tests/Health360.Domain.Tests`. Check boundaries, invalid inputs, unit changes, and rounding.
- Do not invent clinical thresholds, formulas, nutrient references, or lab ranges. Use cited authoritative sources and record their provenance in the relevant data or documentation.

### Database, migrations, and offline sync

- Add schema changes as a **new migration** in `supabase/migrations/`; do not rewrite an existing migration that may already have been applied.
- Every personal-data table must enable RLS and have policies appropriate to its access scope. The RLS coverage test in `supabase/tests/database/00_rls_coverage.test.sql` is a required guardrail, not an obstacle to bypass.
- Functions in the `private` schema that RLS policies or triggers call must be granted `EXECUTE` to `authenticated` (and never to `anon`). pgTAP setup running as superuser can hide a missing grant, so write behavioural tests as the `authenticated` role; `00_rls_coverage` checks the grants directly.
- Follow the existing access model and ownership conventions. Personal rows should carry the appropriate `profile_id` and/or `household_id` so authorization and sync filters do not depend on unsafe joins.
- Keep Postgres authorization and PowerSync stream filters aligned. Never loosen an RLS policy or a sync rule simply to make a test pass. Test that one user's records and pending uploads are not visible to another user.
- When adding or changing a synced table or column, review all of the related pieces together: migration, RLS policies and tests, `src/Health360.LocalDb/AppSchema.cs`, `powersync/sync-config.yaml`, upload serialization, and the schema-drift check (`tools/drift`).
- If a Postgres `json`/`jsonb` or boolean column is added or changed, update `ServerColumnTypes` in `AppSchema` so values are serialized correctly on upload. `tools/drift` fails if one is missing.
- Respect access scopes: user-owned, household-shared, global reference, and server-only data are not interchangeable. Invites, secrets, and other server-only data must not be exposed through client sync streams.
- Keep the failed-upload quarantine behavior (`failed_uploads`). Permanent upload errors and updates that match no row must not silently block the queue or be reported as successful writes, and retries must not duplicate quarantine entries.
- Use client-generated UUIDv7 IDs where required by the existing data model. Do not introduce duplicate records during retries or imports.

### Privacy, authentication, and security

- Treat health measurements, lab reports, body photos, account details, and household relationships as sensitive data. Collect, transmit, display, log, and retain only what the feature needs.
- Require explicit authorization on the server. Never trust a client-supplied profile or household ID as proof of access; validate the authenticated Supabase user and enforce RLS.
- A validly signed token is not proof of a signed-in user: the anon and service-role keys are signed too. The API's token checks (`role`, `sub`, `session_id`, and the `/auth/v1/user` lookup) exist for this; keep them.
- Keep service-role keys, database credentials, and the Anthropic API key server-side (user secrets or the host's secret store). Never put them in the MAUI app or its build properties, `appsettings*.json`, logs, tests committed with real credentials, or example files.
- Do not log health values, prompts containing health data, model output, auth tokens, emailed codes, email addresses, signed URLs, or private identifiers. If telemetry is introduced, scrub PII and sensitive payloads.
- Do not send photos to AI without the required `photo_ai` consent. Consents are append-only (users may insert, not update or delete). Strip photo EXIF/location metadata on-device where applicable.
- Sharing grants are read-only for now; write access arrives with the shared-profile stream.
- Preserve account-switch isolation: a local database and its pending uploads must remain bound to the owning user and must be cleared or safely replaced before another user's session is used. Only an explicit, user-requested sign-out clears the device; a refresh token Auth refuses must not (see `AuthService` in `src/Health360.UI/Auth` and `LocalDatabase` in `src/Health360.LocalDb`).
- Add or update authorization tests whenever access boundaries change. For database work, include negative tests proving another user cannot read or mutate the data.

### Claude and other AI features

- Call Claude from the ASP.NET Core API only. No client (the MAUI app) may ever receive the Anthropic API key.
- Use structured output with the records in `Health360.Contracts` (`StructuredOutputSchema`), and validate the parsed result again on the server (`StructuredClaude`).
- Treat model output as untrusted input. Validate calculations, ranges, nutrient arithmetic, costs, exercise IDs, equipment constraints, and other domain rules in `Health360.Domain` before saving or presenting it. Follow the documented one-repair-then-fail/manual-fallback approach where applicable.
- AI may propose content; code must compute anything deterministic. Do not let a model invent a calculated number that the app can calculate itself.
- Preserve the distinction between measured and estimated physiology data in prompts, outputs, and UI.
- Frame the product as wellness and fitness support, not diagnosis or treatment. Do not implement diagnosis or medication-dosing advice. Preserve safety gates, estimate labels, disclaimers, and user-consent requirements.

### UI, accessibility, and platform behavior

- Prefer shared components in `Health360.UI` and the existing theme (`wwwroot/app.css`). The same screens run on phones (bottom bar) and in desktop windows (side rail); check both layouts.
- Use semantic HTML and `aria-*` attributes so screen readers on Android and Windows can use the BlazorWebView.
- Test loading, error, empty, offline, and sync-pending states for data-entry flows. Make failed saves visible; never show success when no row was changed or a write was rejected.

## Documentation

- Update `docs/PLAN.md`, `docs/FEATURES.md`, `docs/phase0b.md`, or an ADR when the corresponding architecture, scope, implementation status, risk, or decision materially changes. Do not mark planned features as complete just because scaffolding exists.

## Definition of done

1. Add or update tests for new behavior, especially calculations, authorization, privacy, offline retry, and schema changes.
2. Run the main checks. For database or sync changes also run `supabase test db`, `tools/drift`, and the two-client tests when the environment permits.
3. Review the final diff for secrets, sensitive logs, and stale docs.
4. Report the checks actually run and their results, and anything not tested. Never fabricate test results.
