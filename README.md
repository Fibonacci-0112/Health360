# Health360

An all-in-one app for planning and reaching health and fitness goals: nutrition with label OCR, workouts and progression, body metrics and progress photos, a physiology profile (lab RMR, DEXA, VO₂max, LTHR, max HR and HR zones, or estimates when you don't have them), aerobic/anaerobic and fat/carb estimates for workouts, goals with cost planning, and later labs, supplements, mental and cognitive tracking, and AI meal plans and workouts.

Status: **Phase 0** (foundation and risk tests). See [docs/phase0.md](docs/phase0.md) for what's done, what the tests found, and how to run it.

- [Implementation plan](docs/PLAN.md): architecture, data model, phased roadmap, risks, tests, running costs.
- [Feature catalogue](docs/FEATURES.md)
- [ADR 0002: .NET MAUI Blazor Hybrid and ASP.NET Core](docs/adr/0002-dotnet-maui-blazor.md): the current stack. Platforms: Android and Windows.
- [ADR 0001: language and stack](docs/adr/0001-language-and-stack.md) (superseded)

## Layout

This is the **Phase 0 TypeScript prototype**. [ADR 0002](docs/adr/0002-dotnet-maui-blazor.md) replaces it with a .NET solution in Phase 0b ([PLAN.md §2.2](docs/PLAN.md)); the Supabase migrations, pgTAP tests and PowerSync config carry over.

| Path | What it is |
|---|---|
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

```sh
pnpm install
pnpm typecheck && pnpm lint && pnpm test
```

Running the backend locally and the cloud setup are in [docs/phase0.md](docs/phase0.md).
