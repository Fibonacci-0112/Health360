# Health360

An all-in-one app for planning and reaching health and fitness goals: nutrition with label OCR, workouts and progression, body metrics and progress photos, a physiology profile (lab RMR, DEXA, VO₂max, LTHR, max HR and HR zones, or estimates when you don't have them), aerobic/anaerobic and fat/carb estimates for workouts, goals with cost planning, and later labs, supplements, mental and cognitive tracking, and AI meal plans and workouts.

## Status

**Phase 1** (MVP, [PLAN.md §3](docs/PLAN.md#phase-1-mvp)). Phase 0b, the .NET foundation, is complete ([docs/phase0b.md](docs/phase0b.md)). The Phase 0 TypeScript prototype has been removed; [docs/phase0.md](docs/phase0.md) records what it proved and what its tests found, and its code is in the git history.

| Phase 1 step | State |
|---|---|
| 1. Body and photos | Implemented: weight trend, measurements, vitals, the physiology profile with estimates, and progress photos (on-device metadata stripping, offline upload queue, comparison, ghost overlay, deletion) |
| 2. Workouts and progression | Implemented in code: exercise import, locations, templates, programs and schedule, live logger with rest timer and plate calculator, PRs and e1RM, volume per muscle, progression, manual cardio with HR zones and energy estimates. Physical-device acceptance on Android and Windows is still open ([step2-verification.md](docs/step2-verification.md)) |
| 3. Nutrition and label OCR | In progress: nutrition calculations, the nutrition schema, the USDA food import, the Food tab (household foods, offline logging, quick add, daily totals), recipes, nutrition targets (formula or manual) and online search of the USDA catalogue. Barcode lookup has its USDA and Open Food Facts server endpoints but no on-device chain or scanner yet; label OCR and micronutrient targets are not built |
| 4. Goals and cost planning | Not started |
| 5. Ship to the household | Not started |

## Documentation

- [Implementation plan](docs/PLAN.md): architecture, data model, phased roadmap, risks, tests, running costs.
- [Feature catalogue](docs/FEATURES.md): what is planned, by module and phase.
- [Calculations](docs/calculations.md): every implemented formula, with its primary source and golden values.
- [Phase 0b](docs/phase0b.md): the foundation, local setup, configuration and integration-test prerequisites.
- [ADR 0002: .NET MAUI Blazor Hybrid and ASP.NET Core](docs/adr/0002-dotnet-maui-blazor.md): the current stack. Platforms: Android and Windows.
- [ADR 0003: SVG charts](docs/adr/0003-svg-charts.md): shared Razor SVG charts.
- [ADR 0001: language and stack](docs/adr/0001-language-and-stack.md) (superseded by ADR 0002)
- [AGENTS.md](AGENTS.md): conventions, build and test commands for contributors and coding agents.

## Layout

The **.NET solution** (Phase 0b, [ADR 0002](docs/adr/0002-dotnet-maui-blazor.md), [PLAN.md §2.2](docs/PLAN.md)), plus the Supabase migrations, pgTAP tests and PowerSync config carried over from Phase 0.

| Path | What it is |
|---|---|
| `Health360.slnx` | The .NET solution. `Health360.Core.slnf` is everything except the MAUI app (builds without MAUI workloads) |
| `src/Health360.Domain` | Pure C# domain logic (units, ids, calculations); no I/O, enforced by `BannedSymbols.txt` |
| `src/Health360.Contracts` | Records and validators for API payloads and Claude's structured output |
| `src/Health360.UI` | Razor Class Library with the app's screens, sign-in and session, and the profile and sync-state services |
| `src/Health360.LocalDb` | The app's offline database: PowerSync schema, uploads and the `failed_uploads` quarantine |
| `src/Health360.Maui` | MAUI Blazor Hybrid app for Android and Windows |
| `src/Health360.Server` | ASP.NET Core API (replaces Edge Functions) |
| `tests/` | xUnit and bUnit tests for the .NET projects, including the two-client sync tests (`tests/Health360.Sync.Tests`) |
| `supabase` | Migrations, RLS, pgTAP tests, Auth config |
| `powersync` | Sync Streams and local PowerSync config |
| `tools` | Schema drift check (`tools/drift`), reference-data importers (`tools/importers`: free-exercise-db, USDA FoodData Central), local DB test runner (`tools/db`), cloud environment setup (`tools/cloud`) |

## Quick start

.NET (SDK version pinned in `global.json`; on a fresh Linux machine or cloud agent, `bash tools/cloud/setup.sh` installs it, plus Postgres and pgTAP for `tools/db/test-local.sh`). A dev container is in `.devcontainer/`.

```sh
dotnet build Health360.Core.slnf
dotnet test --solution Health360.Core.slnf
```

The default test run skips the database, Storage and two-client sync tests, which need the local Supabase and PowerSync services; [AGENTS.md](AGENTS.md#database-and-sync-validation) lists the commands.

The MAUI app also needs the `maui-android` (any OS) or `maui-windows` (Windows) workload; see [docs/phase0b.md](docs/phase0b.md), which also covers running the backend locally. The Supabase and PowerSync cloud setup is in [docs/phase0.md](docs/phase0.md#cloud-setup-needs-your-accounts).
