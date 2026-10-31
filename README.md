# Health360

An all-in-one app for planning and reaching health and fitness goals: nutrition with label OCR, workouts and progression, body metrics and progress photos, a physiology profile (lab RMR, DEXA, VO₂max, LTHR, max HR and HR zones, or estimates when you don't have them), aerobic/anaerobic and fat/carb estimates for workouts, goals with cost planning, and later labs, supplements, mental and cognitive tracking, and AI meal plans and workouts.

Status: **Phase 1** (MVP, [PLAN.md §3](docs/PLAN.md)). Phase 0b, the .NET foundation, is complete ([docs/phase0b.md](docs/phase0b.md)). The Phase 0 TypeScript prototype has been removed; [docs/phase0.md](docs/phase0.md) records what it proved and what its tests found, and its code is in the git history.

- [Implementation plan](docs/PLAN.md): architecture, data model, phased roadmap, risks, tests, running costs.
- [Feature catalogue](docs/FEATURES.md)
- [ADR 0002: .NET MAUI Blazor Hybrid and ASP.NET Core](docs/adr/0002-dotnet-maui-blazor.md): the current stack. Platforms: Android and Windows.
- [ADR 0001: language and stack](docs/adr/0001-language-and-stack.md) (superseded)

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
| `tools` | Schema drift check (`tools/drift`), local DB test runner (`tools/db`), cloud environment setup (`tools/cloud`) |

## Quick start

.NET (SDK version pinned in `global.json`):

```sh
dotnet build Health360.Core.slnf
dotnet test --solution Health360.Core.slnf
```

The MAUI app also needs the `maui-android` (any OS) or `maui-windows` (Windows) workload; see [docs/phase0b.md](docs/phase0b.md), which also covers running the backend locally. The Supabase and PowerSync cloud setup is in [docs/phase0.md](docs/phase0.md#cloud-setup-needs-your-accounts).
