# ADR 0001: Language and stack

- **Status:** Superseded by [ADR 0002](0002-dotnet-maui-blazor.md) (2026-10-30): .NET MAUI, Blazor and ASP.NET Core, for Android, Web and Windows only. Kept as the record of the original comparison.
- **Date:** 2026-10-25

## Context

Health360 has to run on phones, the web and desktop, log workouts and food offline, read wearable data (Health Connect now; HealthKit later), scan barcodes and nutrition labels with the camera, and call Claude for OCR and planning. Supabase was already chosen as the backend. TypeScript was compared against C#/.NET, Flutter, Kotlin Multiplatform, Capacitor and a web-only PWA.

## Decision

**TypeScript with Expo** (React Native for Android, iOS later, and web), **Tauri 2** wrapping the web build for desktop, **PowerSync** for offline sync, and **Supabase Edge Functions** (TypeScript/Deno) for server logic and Claude calls. The full stack is in [PLAN.md §2.1](../PLAN.md).

Deferring Apple ([PLAN.md §2.7](../PLAN.md)) doesn't change this decision. Expo keeps the iPhone app one build away. The comparison below was written assuming Apple was included.

## Options compared

The strongest .NET option for this app's platforms:
- **.NET MAUI Blazor Hybrid** for iOS, Android, Windows and macOS (Mac Catalyst).
- **A Blazor Web App** for the browser, sharing one Razor UI library with the MAUI app.
- **An ASP.NET Core API** for AI and server logic, which also shares a C# domain library with the app.
- **Supabase** stays for Postgres, Auth, Storage and RLS.

Uno Platform 6 is the alternative if one native .NET UI has to cover web and Linux too. But PowerSync .NET doesn't support WebAssembly, and the health libraries are written for MAUI.

| Criterion | TypeScript (Expo + Tauri + Supabase) | C#/.NET (MAUI Blazor Hybrid + Blazor Web + ASP.NET Core + Supabase) |
|---|---|---|
| Platform coverage | iOS, Android, web, Windows, macOS, Linux from one UI codebase (desktop through the Tauri wrapper) | iOS, Android, Windows, macOS (Catalyst only) through MAUI. Web through Blazor. **No Linux desktop** (Linux users use the web app) |
| Offline sync | PowerSync JS SDK is **GA** on React Native and web | PowerSync .NET is **beta** (v0.1.x, May 2026) and MAUI-only. The **web app would be online-only** |
| Server logic | Supabase Edge Functions run TypeScript (Deno), so client and server use one language | Edge Functions are TypeScript/Deno only, so you'd need a **separate ASP.NET Core service** to host and run (or write the functions in TypeScript anyway) |
| Supabase client | `supabase-js` is the reference client | The C# SDK became **official in Oct 2026** (v8); Supabase is still closing feature gaps with the JS client |
| HealthKit / Health Connect | Two maintained React Native libraries | Mainly **Shiny.Health** (1.1.0) |
| Camera / barcode | expo-camera has barcode scanning built in | ZXing.Net.Maui or BarcodeScanning.Native.Maui |
| Claude SDK | Official `@anthropic-ai/sdk` | Official `Anthropic` NuGet package |
| Domain modelling | Strong static types; runtime checks through zod; no `decimal` type (money stored in minor units) | **Stronger**: records, `decimal`, LINQ, and the same domain library compiled into both the app and the API |
| iOS and macOS builds | EAS cloud builds, **no Mac needed** | Needs a Mac (or a macOS CI runner) to build and sign |
| Shipping fixes | **Over-the-air JavaScript updates** (EAS Update) without App Store review | Every code change needs a store release |
| Desktop | Tauri wrapper; needs the Phase 0 test | **First-party native** Windows (WinUI) and Mac Catalyst apps, with no wrapper |
| Ecosystem and tooling | Much larger npm/React Native ecosystem; fast refresh | Smaller ecosystem, excellent IDEs (Rider, Visual Studio); some MAUI quality complaints in 2026 reviews |
| Testing | Vitest, Playwright, Maestro | xUnit, bUnit, Playwright for .NET, Maestro or Appium |

**Other options considered**

| Option | Platforms | Offline sync (PowerSync) | Supabase client | HealthKit / Health Connect | Main trade-off |
|---|---|---|---|---|---|
| **Flutter (Dart)** | iOS, Android, web, Windows, macOS, Linux, all first-party with **no desktop wrapper** | Dart SDK **GA** (v2, 2026) | `supabase_flutter`, official | `health` package (mature) | Web draws to a canvas, so the first load is heavier, which is fine behind a login. Edge Functions are still TypeScript, so there are 2 languages and some validators are duplicated. iOS builds need a Mac or Codemagic. Over-the-air updates only through Shorebird (third-party) |
| **Kotlin Multiplatform + Compose** | Android, iOS and desktop stable; **web is beta** (Wasm) | Kotlin SDK **GA** | `supabase-kt`, community-built | Platform code or small KMP libraries | Web not production-grade yet; smaller iOS ecosystem; Claude Java SDK only on the JVM server (Ktor) |
| **Capacitor (TypeScript, web-first)** | One React web app wrapped for iOS and Android; desktop through Tauri | Capacitor SDK **alpha** (or the web SDK inside the webview) | `supabase-js` | `@capgo/capacitor-health` (free) | Simplest way to match the web exactly, but feels less native; fast logging lists depend on webview performance |
| **PWA only (TypeScript)** | Any browser, installable on phone and desktop | Web SDK GA | `supabase-js` | **None** (browsers can't read HealthKit) | Fastest to ship and no app stores, but no wearable sync and weak iOS background notifications. Could be wrapped with Capacitor later |

Flutter is the strongest alternative. It wins on desktop, especially Linux, because it needs no wrapper. It loses on keeping one language end to end, cloud iOS builds without a Mac, and over-the-air fixes.

**Recommendation: TypeScript**, for these requirements:
- Offline logging works on every platform with a GA sync SDK.
- The server logic stays in Supabase's own runtime.
- Linux and the web are covered.
- iOS builds and hot-fixes don't need a Mac or a store release.

**C#/.NET is reasonable if** you'll maintain the code yourself and you know C# much better than TypeScript and React. You would accept a web app that only works online, no native Linux desktop, a beta sync SDK, and an extra ASP.NET Core service to run.

## Consequences

- One language from the app to the server functions; zod schemas are shared by forms, API payloads and Claude's structured output.
- Desktop depends on Tauri wrapping the Expo web export. Phase 0 tests this on Windows and Linux, and Electron is the fallback.
- Offline sync uses PowerSync's GA JavaScript SDKs on every target.
- iOS builds run on EAS in the cloud without a Mac, and JavaScript fixes can ship over the air.

## Sources

- TypeScript vs .NET: [MAUI vs Avalonia vs Uno (2026)](https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/) · [PowerSync .NET SDK docs](https://docs.powersync.com/client-sdks/reference/dotnet.md) · [Supabase .NET SDK now official](https://supabase.com/changelog/supabase-dotnet-official-support) · [Shiny.Health](https://feed.nuget.org/packages/Shiny.Health/) · [MAUI Blazor Hybrid + Blazor Web App](https://learn.microsoft.com/aspnet/core/blazor/hybrid/tutorials/maui-blazor-web-app) · [.NET MAUI vs React Native (2026)](https://sam-solutions.com/blog/net-maui-vs-react-native/)
- Other options: [PowerSync SDK feature status](https://docs.powersync.com/resources/feature-status) · [PowerSync changelog May 2026](https://powersync.com/blog/powersync-changelog-may-2026) · [Kotlin Multiplatform platform stability](https://kotlinlang.org/docs/multiplatform/supported-platforms.html) · [Flutter & Dart 2026 roadmap](https://flutter.dev/blog/flutter-darts-2026-roadmap) · [Capgo Capacitor Health](https://capgo.app/docs/plugins/health/) · [Capawesome Capacitor Health](https://capawesome.io/blog/announcing-the-capacitor-health-plugin/)
