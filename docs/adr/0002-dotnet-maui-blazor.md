# ADR 0002: .NET MAUI, Blazor and ASP.NET Core; Android, Web and Windows only

- **Status:** Accepted. Supersedes [ADR 0001](0001-language-and-stack.md).
- **Date:** 2026-10-30

## Context

ADR 0001 chose TypeScript (Expo, Tauri, Supabase Edge Functions), largely because it covered iOS, macOS and Linux desktop as well as Android, web and Windows, and because PowerSync's JavaScript SDK is GA on every target.

The platform scope has changed. Health360 now targets only:
- **Android** (phone app),
- **Web** (browser app),
- **Windows** desktop.

Apple (iPhone, iPad, Mac) and Linux desktop are out of scope, not deferred. ADR 0001 named C#/.NET as reasonable "if you'll maintain the code yourself and you know C# much better than TypeScript and React". That is now the case, and dropping Apple and Linux removes most of the .NET option's disadvantages (no Mac needed for builds, no missing Linux desktop).

Supabase (Postgres, RLS, Auth, Storage) and PowerSync stay.

## Decision

- **.NET MAUI Blazor Hybrid** for Android and Windows (WinUI 3). The screens are Razor components in a shared Razor Class Library.
- **Blazor Web App** (ASP.NET Core, Interactive Auto render mode, with WebAssembly for interactive pages) for the browser, using the same Razor Class Library.
- **ASP.NET Core API** for server logic and Claude calls, hosted in the same ASP.NET Core app as the Blazor Web App. It replaces Supabase Edge Functions.
- **One C# domain library** (`Health360.Domain`) and one contracts library (`Health360.Contracts`) shared by the MAUI app, the web app and the API.
- **Supabase** for Postgres + RLS, Auth, Storage and pg_cron. The existing migrations, RLS policies and pgTAP tests carry over unchanged.
- **PowerSync .NET SDK** for offline SQLite and sync in the MAUI app. `powersync/sync-config.yaml` carries over unchanged.

The full stack is in [PLAN.md §2.1](../PLAN.md).

## Options compared

### Web project type

Both options share a Razor Class Library with MAUI Blazor Hybrid, so the screens are written once either way.

| | Blazor WebAssembly standalone PWA | **Blazor Web App (Auto / WebAssembly)**, chosen |
|---|---|---|
| Offline | Designed for it: service worker and manifest; the app opens without a network | Online-first. Auto mode starts on Blazor Server (SignalR) before WebAssembly loads; no PWA template |
| Offline data | PowerSync .NET doesn't run in WebAssembly; would need PowerSync's GA JavaScript web SDK through JS interop | Same interop would be needed for the WebAssembly pages; none in server mode |
| Hosting | Static files, $0 | Needs an ASP.NET Core host (~$5–15/mo) |
| First load | Downloads the .NET runtime (a few MB) on first visit | Fast first paint from server prerendering, then WebAssembly in the background |
| Server logic | Needs a separate API host anyway | The same host serves the API |

The Blazor Web App was chosen because it shares a host with the ASP.NET Core API (one deployable), loads fast, and avoids the JS-interop sync layer at the start. The cost is that **the web app is online-first** (see Consequences).

### Server logic

| | Supabase Edge Functions (TypeScript/Deno) | **ASP.NET Core API**, chosen |
|---|---|---|
| Code reuse | Two languages; TypeScript validators kept in step with the C# domain library by parity tests | **One C# domain library** in the app, web and API |
| Existing work | Keeps `_shared/auth.ts`, `ai-hello` and its tests | Rewrite JWT and user validation, the Claude client and tests in C# |
| Hosting | Included in Supabase | One more service to run, patch and monitor; shares the Blazor Web App host |
| Claude SDK | Official TypeScript SDK | Official `Anthropic` NuGet package |
| Jobs | pg_cron calls a function | Hosted background services, or pg_cron → pg_net → an API endpoint |
| Security | Runs next to Postgres; service role never leaves Supabase | Another place holding secrets; must call Postgres as the signed-in user so RLS still applies |
| Runtime limits | Wall-clock limits per call | No such limits for long AI calls |

The API was chosen so that deterministic calculations and AI-output validation are written once, in C#, and so the Blazor Web App and the API share one host.

## Consequences

- **Platforms:** Android, Web and Windows only. No HealthKit, no Sign in with Apple, no iOS or macOS builds, no Linux desktop, and no Safari/WebKit testing.
- **Offline:** offline logging is guaranteed on Android and Windows (PowerSync .NET with SQLite). **The web app is online-first** and shows a clear offline state; it doesn't queue writes at launch. The upgrade path, if needed, is WebAssembly render mode plus PowerSync's JavaScript web SDK through JS interop for the logging screens (PLAN.md §2.7).
- **PowerSync .NET is beta** (v0.1.x in May 2026). Phase 0b tests it on Android and Windows. If it fails, the fallback is recorded in a new ADR.
- **A new server to secure:** the API validates the Supabase JWT and the signed-in user on every request and calls Postgres as that user, so RLS still applies. The service-role key is used only for admin jobs and never reaches a client.
- **No over-the-air updates:** Android fixes ship through Google Play (or an APK), Windows fixes through the installer's auto-update, and web fixes by redeploying the host.
- **Retired:** the Expo app, the Tauri shell, Supabase Edge Functions, and the TypeScript `packages/*`. They remain in the repository as the Phase 0 prototype until Phase 0b replaces them ([phase0.md](../phase0.md)).
- **Carried over unchanged:** the Postgres migrations, RLS policies and pgTAP tests, `powersync/sync-config.yaml`, the data model, the unit conventions and exact conversion factors, and the privacy and AI rules.
- **Desktop:** MAUI on WinUI 3, unpackaged, with an installer and auto-updater (Velopack), so a code-signing certificate stays optional for family use.
- **CI:** Linux runners for most jobs; a Windows runner for the Windows build and smoke tests.

## Sources

- [PowerSync .NET SDK docs](https://docs.powersync.com/client-sdks/reference/dotnet.md) · [PowerSync SDK feature status](https://docs.powersync.com/resources/feature-status)
- [Supabase .NET SDK now official](https://supabase.com/changelog/supabase-dotnet-official-support)
- [Shiny.Health](https://feed.nuget.org/packages/Shiny.Health/)
- [MAUI Blazor Hybrid + Blazor Web App](https://learn.microsoft.com/aspnet/core/blazor/hybrid/tutorials/maui-blazor-web-app)
- [MAUI vs Avalonia vs Uno (2026)](https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/)
