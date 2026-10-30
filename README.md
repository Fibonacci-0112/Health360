# Health360

An all-in-one app for planning and reaching health and fitness goals: nutrition with label OCR, workouts and progression, body metrics and progress photos, a physiology profile (lab RMR, DEXA, VO₂max, LTHR, max HR and HR zones, or estimates when you don't have them), aerobic/anaerobic and fat/carb estimates for workouts, goals with cost planning, and later labs, supplements, mental and cognitive tracking, and AI meal plans and workouts.

Status: **Phase 0b** (.NET foundation) has started: the .NET solution is scaffolded ([docs/phase0b.md](docs/phase0b.md)). The Phase 0 TypeScript prototype is still in place; see [docs/phase0.md](docs/phase0.md) for what it proved, what the tests found, and how to run it.

- [Implementation plan](docs/PLAN.md): architecture, data model, phased roadmap, risks, tests, running costs.
- [Feature catalogue](docs/FEATURES.md)
- [ADR 0002: .NET MAUI Blazor Hybrid and ASP.NET Core](docs/adr/0002-dotnet-maui-blazor.md): the current stack. Platforms: Android and Windows.
- [ADR 0001: language and stack](docs/adr/0001-language-and-stack.md) (superseded)

## Layout

The repository holds the new **.NET solution** (Phase 0b, [ADR 0002](docs/adr/0002-dotnet-maui-blazor.md), [PLAN.md §2.2](docs/PLAN.md)) next to the **Phase 0 TypeScript prototype** it replaces. The Supabase migrations, pgTAP tests and PowerSync config carry over.

| Path | What it is |
|---|---|
| `Health360.slnx` | The .NET solution. `Health360.Core.slnf` is everything except the MAUI app (builds without MAUI workloads) |
| `src/Health360.Domain` | Pure C# domain logic (units, ids, calculations); no I/O, enforced by `BannedSymbols.txt` |
| `src/Health360.Contracts` | Records and validators for API payloads and Claude's structured output |
| `src/Health360.UI` | Razor Class Library with the app's screens, sign-in and session, and the profile and sync-state services |
| `src/Health360.LocalDb` | The app's offline database: PowerSync schema, uploads and the `failed_uploads` quarantine |
| `src/Health360.Maui` | MAUI Blazor Hybrid app for Android and Windows |
| `src/Health360.Server` | ASP.NET Core API (replaces Edge Functions) |
| `tests/` | xUnit and bUnit tests for the .NET projects |
| `apps/app` | Expo app: Android and web (prototype) |
| `apps/desktop` | Tauri 2 shell around the web build (prototype; desktop is Windows only, through MAUI) |
| `packages/domain` | Deterministic health math (units first), shared with Edge Functions |
| `packages/schemas` | zod schemas for forms, API payloads and Claude's structured output |
| `packages/local-db` | Client SQLite schema (Drizzle → PowerSync) and the upload queue |
| `packages/api` | supabase-js upload writer and typed Edge Function clients |
| `supabase` | Migrations, RLS, pgTAP tests, Edge Functions |
| `powersync` | Sync Streams and local PowerSync config |
| `e2e/sync` | Two-client leak and offline tests |
| `tools` | Codegen for Edge Functions, schema drift check, local DB test runner |

## Quick start

.NET (SDK version pinned in `global.json`):

```sh
dotnet build Health360.Core.slnf
dotnet test --solution Health360.Core.slnf
```

The MAUI app also needs the `maui-android` (any OS) or `maui-windows` (Windows) workload; see [docs/phase0b.md](docs/phase0b.md).

TypeScript prototype:

```sh
pnpm install
pnpm typecheck && pnpm lint && pnpm test
```

Running the backend locally and the cloud setup are in [docs/phase0.md](docs/phase0.md).
