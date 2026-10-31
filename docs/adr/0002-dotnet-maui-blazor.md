# ADR 0002: .NET MAUI Blazor Hybrid and ASP.NET Core; Android and Windows only

- **Status:** Accepted. Supersedes [ADR 0001](0001-language-and-stack.md).
- **Date:** 2026-10-30
- **Amended:** 2026-10-30. The web app is dropped; the targets are Android and Windows only.

## Context

ADR 0001 chose TypeScript (Expo, Tauri, Supabase Edge Functions), largely because it covered iOS, macOS and Linux desktop as well as Android, web and Windows, and because PowerSync's JavaScript SDK is GA on every target.

The platform scope has changed. Health360 now targets only:
- **Android** (phone app),
- **Windows** desktop.

Apple (iPhone, iPad, Mac), Linux desktop and a web (browser) app are out of scope, not deferred. ADR 0001 named C#/.NET as reasonable "if you'll maintain the code yourself and you know C# much better than TypeScript and React". That is now the case, and dropping Apple, Linux and the web removes most of the .NET option's disadvantages: no Mac needed for builds, no missing Linux desktop, and no online-only web app, because PowerSync .NET covers every remaining target.

Supabase (Postgres, RLS, Auth, Storage) and PowerSync stay.

## Decision

- **.NET MAUI Blazor Hybrid** for Android and Windows (WinUI 3). The screens are Razor components in a shared Razor Class Library.
- **ASP.NET Core API** (`Health360.Server`), a standalone app, for server logic and Claude calls. It replaces Supabase Edge Functions.
- **One C# domain library** (`Health360.Domain`) and one contracts library (`Health360.Contracts`) shared by the MAUI app and the API.
- **Supabase** for Postgres + RLS, Auth, Storage and pg_cron. The existing migrations, RLS policies and pgTAP tests carry over unchanged.
- **PowerSync .NET SDK** for offline SQLite and sync in the MAUI app. `powersync/sync-config.yaml` carries over unchanged.

The full stack is in [PLAN.md §2.1](../PLAN.md).

## Options compared

### Server logic

| | Supabase Edge Functions (TypeScript/Deno) | **ASP.NET Core API**, chosen |
|---|---|---|
| Code reuse | Two languages; TypeScript validators kept in step with the C# domain library by parity tests | **One C# domain library** in the app and the API |
| Existing work | Keeps `_shared/auth.ts`, `ai-hello` and its tests | Rewrite JWT and user validation, the Claude client and tests in C# |
| Hosting | Included in Supabase | One more service to run, patch and monitor |
| Claude SDK | Official TypeScript SDK | Official `Anthropic` NuGet package |
| Jobs | pg_cron calls a function | Hosted background services, or pg_cron → pg_net → an API endpoint |
| Security | Runs next to Postgres; service role never leaves Supabase | Another place holding secrets; must call Postgres as the signed-in user so RLS still applies |
| Runtime limits | Wall-clock limits per call | No such limits for long AI calls |

The API was chosen so that deterministic calculations and AI-output validation are written once, in C#.

## Consequences

- **Platforms:** Android and Windows only. No web or browser app, no HealthKit, no Sign in with Apple, no iOS or macOS builds, and no Linux desktop. Adding a web app later needs a new ADR; a Blazor Web App could reuse the Razor Class Library.
- **Offline:** offline logging works on every supported platform (PowerSync .NET with SQLite).
- **PowerSync .NET is beta** (v0.1.x in May 2026). Phase 0b tests it on Android and Windows. If it fails, the fallback is recorded in a new ADR.
- **A new server to secure and host:** the API runs as its own ASP.NET Core app (a small container or VPS). It validates the Supabase JWT and the signed-in user on every request and calls Postgres as that user, so RLS still applies. The service-role key is used only for admin jobs and never reaches a client.
- **No over-the-air updates:** Android fixes ship through Google Play (or an APK), Windows fixes through the installer's auto-update, and API fixes by redeploying the API.
- **Retired:** the Expo app (including its web/PWA build), the Tauri shell, Supabase Edge Functions, and the TypeScript `packages/*`. They stayed in the repository as the Phase 0 prototype until Phase 0b replaced them, and were removed on 2026-10-31 ([phase0.md](../phase0.md)).
- **Carried over unchanged:** the Postgres migrations, RLS policies and pgTAP tests, `powersync/sync-config.yaml`, the data model, the unit conventions and exact conversion factors, and the privacy and AI rules.
- **Desktop:** MAUI on WinUI 3, unpackaged, with an installer and auto-updater (Velopack), so a code-signing certificate stays optional for family use.
- **CI:** Linux runners for most jobs; a Windows runner for the Windows build and smoke tests.

## Sources

- [PowerSync .NET SDK docs](https://docs.powersync.com/client-sdks/reference/dotnet.md) · [PowerSync SDK feature status](https://docs.powersync.com/resources/feature-status)
- [Supabase .NET SDK now official](https://supabase.com/changelog/supabase-dotnet-official-support)
- [Shiny.Health](https://feed.nuget.org/packages/Shiny.Health/)
- [ASP.NET Core Blazor Hybrid](https://learn.microsoft.com/aspnet/core/blazor/hybrid/)
- [MAUI vs Avalonia vs Uno (2026)](https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/)
