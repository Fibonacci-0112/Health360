# AGENTS.md

## Scope and starting points

This file applies to the entire repository. Follow any more specific `AGENTS.md` in the directory being changed.

Health360 is a health and fitness app built with .NET MAUI Blazor Hybrid for **Android and Windows only**, plus an ASP.NET Core API. Supabase provides Postgres, Auth and Storage; PowerSync provides offline SQLite and sync. Do not reintroduce the retired TypeScript/Expo/Tauri prototype, a browser app, or additional platform targets without an explicit scope change and an ADR.

Before changing a feature, read its implementation and tests, then the relevant documentation:

- [README.md](README.md): project map and quick start.
- [docs/FEATURES.md](docs/FEATURES.md): feature catalogue; listed features are not proof of implementation.
- [docs/PLAN.md](docs/PLAN.md): architecture, data rules and roadmap; planned work is not proof of implementation.
- [docs/phase0b.md](docs/phase0b.md): foundation, local setup, configuration and integration-test prerequisites.
- [ADR 0002](docs/adr/0002-dotnet-maui-blazor.md): current platform and stack decision; supersedes ADR 0001.
- [ADR 0003](docs/adr/0003-svg-charts.md): shared Razor SVG charts.
- [docs/calculations.md](docs/calculations.md): implemented body and physiology formulas and their primary sources.

Use current source, project files and `.github/workflows/ci.yml` to verify commands and implemented behavior. Treat `docs/phase0.md` as historical prototype context. If code and documentation disagree, identify the discrepancy rather than silently assuming either is correct.

## Project boundaries

| Path | Responsibility |
| --- | --- |
| `src/Health360.Domain` | Pure calculations, units, IDs and metadata processing; no I/O or runtime dependencies |
| `src/Health360.Contracts` | API and AI payload records, serialization and validators |
| `src/Health360.LocalDb` | PowerSync schema, local queries, upload queues, account binding and photos |
| `src/Health360.UI` | Shared Razor screens and app services; no MAUI dependency |
| `src/Health360.Maui` | Android/Windows host and platform adapters, secure storage and device integration |
| `src/Health360.Server` | Authenticated API, user-scoped database access and server-side AI calls |
| `supabase/migrations`, `supabase/tests/database` | Database evolution, access policies and pgTAP tests |
| `powersync` | Sync Streams and local service configuration |
| `tools/drift` | Postgres publication versus `AppSchema` consistency check |
| `tools/importers` | Reference-data imports (free-exercise-db) into system rows, keyed by `external_id` |
| `tests` | Domain, UI, local database, server, drift and two-client sync tests |

Keep business calculations in Domain, shared screens in UI, and platform APIs behind host-supplied adapters. Preserve Domain's purity analyzer (`BannedSymbols.txt`) and purity tests. Inject clocks and randomness; do not use ambient time, randomness, environment, network or filesystem access in Domain.

## Build and test

Run commands from the repository root. Install the SDK version and roll-forward policy pinned in `global.json`; treat that file as the source of truth. The test runner is **Microsoft.Testing.Platform**: retain the `--solution`, `--project` and `--filter-trait` syntax used by CI.

In a fresh Linux or cloud agent environment, run `bash tools/cloud/setup.sh` first. It installs the pinned .NET SDK plus Postgres and pgTAP for `tools/db/test-local.sh`, and puts `dotnet` on `PATH`. It needs root for `apt-get`, does not install MAUI workloads, and reads no secrets.

The core solution excludes MAUI and works without mobile workloads. Match CI for core validation:

```sh
dotnet restore Health360.Core.slnf -p:ContinuousIntegrationBuild=true
dotnet format Health360.Core.slnf --verify-no-changes --no-restore
dotnet build Health360.Core.slnf -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet test --solution Health360.Core.slnf -c Release --no-build
```

For a focused test run, use the affected project, for example:

```sh
dotnet test --project tests/Health360.Domain.Tests -c Release
dotnet test --project tests/Health360.UI.Tests -c Release
```

MAUI changes also need the affected target build:

```sh
dotnet build src/Health360.Maui -c Release -f net10.0-android
dotnet build src/Health360.Maui -c Release -f net10.0-windows10.0.19041.0
```

Android needs the `maui-android` workload, JDK 17 and Android SDK components. Windows builds require Windows and `maui-windows`. Use the workload set version pinned in CI's `maui-*` jobs, and follow its Android dependency-install step if needed. Use the core filter on hosts without these prerequisites. Do not change target frameworks or SDK pins just to accommodate a local machine.

### Database and sync validation

With the local Supabase stack available, CI's database checks are:

```sh
supabase db start
supabase test db
supabase db lint --level warning --fail-on error
dotnet run --project tools/drift -c Release -p:ContinuousIntegrationBuild=true
dotnet test --project tests/Health360.Server.Tests -c Release --filter-trait "Category=Database"
dotnet test --project tests/Health360.Importers.Tests -c Release --filter-trait "Category=Database"
```

Set `HEALTH360_DRIFT_DATABASE` and `HEALTH360_TEST_DATABASE` to a migrated, disposable local database for their respective checks. Server database tests need permission to create temporary roles and users. Never point test fixtures at production.

Without Docker, `bash tools/db/test-local.sh` runs migrations and pgTAP against temporary Postgres with a Supabase shim. It needs Postgres server binaries and pgTAP; it does not replace the real Auth, Storage or PowerSync integration tests.

Two-client sync tests require `SYNC_E2E=1`, the local Supabase and PowerSync services, and the environment settings described in `docs/phase0b.md` and CI's `sync` job. Run:

```sh
dotnet test --project tests/Health360.Sync.Tests -c Release
```

A default core test run does **not** establish database or two-client integration coverage: those tests are gated by environment configuration. Report omitted prerequisites and checks explicitly.

## Coding and dependency conventions

- Follow `.editorconfig`: UTF-8, LF, final newline, four spaces for C#/Razor and two for XML/project files. Use file-scoped C# namespaces and existing naming patterns.
- Nullable analysis, recommended analyzers and warnings-as-errors are enabled. Fix warnings rather than weakening shared build settings or adding broad suppressions.
- Package versions belong in `Directory.Packages.props`; keep project references versionless. Update affected committed `packages.lock.json` files when dependencies intentionally change. MAUI deliberately has no lock file because its restore graph varies by OS.
- Keep patches focused. Preserve unrelated changes and avoid broad formatting, dependency upgrades or architectural rewrites outside the task.
- Use existing xUnit, FsCheck and bUnit patterns. Add regression coverage for changed behavior, especially calculations, authorization, queue handling and account switching.
- For charts, follow ADR 0003: shared Razor SVG, real date spacing, display-unit conversion, accessible descriptions/data tables, and explicit empty/single-point/constant-value states. Use the existing theme in `src/Health360.UI/wwwroot/app.css`.

## Data and calculation invariants

- Store quantities as entered, with their unit and canonical value. Calculate from canonical values, never rounded display values.
- Reuse `Health360.Domain/Units`. Preserve exact conversion factors, database unit codes and floating-point evaluation order; Postgres conversion functions and parity tests must stay aligned.
- Preserve original-value display and mixed-unit history. Use the existing tolerances for synced doubles; a unit-preference change must not create a false personal record.
- Keep formula code, tests and `docs/calculations.md` consistent. Verify new formulas against primary sources before implementation; do not fill documented gaps with guessed equations.
- Preserve measured-versus-estimated provenance, named formulas and uncertainty. Respect the existing heart-rate-medication rules and do not derive clinical diagnoses from wellness estimates.

## Database, offline sync and privacy

### Schema and sync

- Add schema changes as new migrations. Do not rewrite migrations that have already been applied.
- Review the existing RLS policies and sharing helpers before extending them.
- When changing synced data, update these together: the Postgres publication, `powersync/sync-config.yaml`, `src/Health360.LocalDb/AppSchema.cs`, the relevant codecs and upload rules, and their tests.
- Run the drift checker after synced-schema changes. It checks the client schema only, not every C# contract.

### Access control

- Preserve RLS and per-user and per-household sharing.
- API queries must go through `IUserDatabase` with the verified `SignedInUser`. Role and JWT claims stay transaction-local.
- Do not replace this path with privileged (service-role) queries over user data.

### Account switching and offline writes

- Bind local data and uploads to their owner.
- On an account switch, clear the previous owner's rows, pending and quarantined writes, and photo files before the next account uses them.
- A transient network failure must not sign the user out or discard pending work.
- Permanent write failures go to the local quarantine so they cannot block later writes.
- Transient, network and auth failures stay retryable. Preserve the existing retry and discard behavior.

### Photos

- Keep photos private and strip their metadata on the device.
- Preserve owner checks on file uploads.
- Require the documented consent before sending any photo to AI.

### Logging and secrets

- Never log tokens, sign-in codes, email addresses, health rows, photo contents, AI prompts or model responses. Log sanitized metadata and error codes instead.
- Keep secrets in user secrets or environment/host secret stores. Never commit credentials.
- Never put service-role, database or Anthropic secrets in the MAUI client or in `appsettings*.json`. The client uses only the publishable (anon) key.

### Authentication

- Preserve the fallback authorization policy that requires authentication on endpoints by default.
- Preserve session verification.
- Keep invalid credentials and an unavailable Auth service as distinct outcomes.

## AI changes

AI calls run only in `Health360.Server`. Follow the existing structured-output path: schema generated from Contracts, strict deserialization, validation, refusal/truncation handling and at most one validation repair round. Deterministic values come from code. Preserve rate limits and manual fallbacks; do not claim planned quotas or usage logging already exist. Use recorded/fake responses for tests; live calls incur cost and are not part of the default test suite.

## Completing work

Run checks appropriate to the changed layers; documentation-only edits need link/path and command review rather than an application rebuild. For behavioral changes, run focused tests and the core checks, plus relevant database, sync or platform validation. Do not report unrun checks as passing.

Keep relevant documentation current and record material architecture changes in a new ADR. In the handoff, state what changed, what was verified, and any skipped checks or remaining limitations.
