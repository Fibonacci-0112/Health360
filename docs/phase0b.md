# Phase 0b: .NET foundation

Status of the move to .NET MAUI Blazor Hybrid and ASP.NET Core ([ADR 0002](adr/0002-dotnet-maui-blazor.md)). The deliverables and exit criteria are in [PLAN.md §3](PLAN.md). The TypeScript prototype ([phase0.md](phase0.md)) stays in place and in CI until the PowerSync .NET gate passes.

## Status

| Deliverable | State |
|---|---|
| Solution layout (§2.2) and CI: build, format and analyzers, tests | **Scaffolded.** Projects compile, with placeholder tests only |
| `Health360.Domain` units and UUIDv7 ids, with FsCheck property tests | **Done.** `Units/` and `Ids/UuidV7` port `packages/domain`, with the same factors and property tests (FsCheck) |
| `Health360.Server`: Supabase JWT and signed-in-user validation, queries as that user, one test Claude call | **Auth and user-scoped queries done** (see [API authentication](#api-authentication)), with `/me` and `/profiles/{id}` as the first endpoints. The test Claude call is not started |
| MAUI app: tabs, emailed-code login, onboarding, PowerSync .NET, `failed_uploads` quarantine | Not started. The app shows one placeholder page |
| Throwaway `notes` table on Android and Windows | Not started |
| `tools/drift` against the PowerSync .NET schema | Not started (`tools/codegen/check-drift.mjs` still checks the TypeScript client schema) |

## What the scaffold contains

- `global.json` pins the .NET SDK (10.0.403, the current LTS line). The MAUI workload set is pinned where it's installed (`dotnet workload install ... --version 10.0.403`), not with `workloadVersion` in `global.json`.
- `Directory.Build.props`: nullable, implicit usings, warnings as errors, `latest-recommended` analyzers and code style enforced in the build. `Directory.Build.targets`: NuGet lock files (`packages.lock.json`, locked restore in CI) and invariant globalization for every project except the MAUI app. The MAUI app has no lock file, because its restore graph differs between Linux and Windows, and it keeps culture data for locale defaults.
- `Directory.Packages.props`: central package versions.
- `src/Health360.Domain/BannedSymbols.txt`: the banned-API analyzer fails the build on file, network, console or environment access, and on reading the clock or ambient randomness (`DateTime.UtcNow`, `Guid.NewGuid`, `Random.Shared`, ...). Pass a `TimeProvider` or a random source in instead. `tests/Health360.Domain.Tests/PurityTests.cs` also fails if Domain gains a package or project reference.
- `Health360.Maui` targets `net10.0-android` on every OS and adds `net10.0-windows10.0.19041.0` on Windows. It is an unpackaged WinUI 3 app (`WindowsPackageType=None`). The Android manifest sets `allowBackup="false"` so the local health database and its pending uploads are not copied to cloud backups.
- `Health360.UI` holds the router, layout and pages. The MAUI app's `BlazorWebView` renders `Health360.UI.Routes`.

## Units and ids

`src/Health360.Domain/Units` ports `packages/domain/src/units` and `Ids/UuidV7.cs` ports `src/ids`. The TypeScript copy stays the prototype's source until the prototype is retired; keep the two identical.

- Units are `Unit` singletons (`Unit.Pound`, `Unit.Parse("lb")`) carrying the database code. Unit preferences are enums, and `UnitPrefOptions` maps them to the codes stored in `profiles.unit_prefs` (the same codes as `UNIT_PREF_OPTIONS`).
- Conversions evaluate `value × a.num × b.den / (a.den × b.num)` in the same order as the TypeScript, so both stacks produce the same doubles. This was checked bit for bit against the TypeScript on about 38,700 inputs (conversions, rounding, stone/feet display, pace, protein ratio, UUIDv7 bytes, locale defaults).
- `UuidV7.Create` takes the clock (a `TimeProvider` or Unix milliseconds) and a `RandomFill`; the app passes `RandomNumberGenerator.Fill`. It returns a `Guid`, whose string form and `CompareTo` both sort by creation time.
- Deviations from the TypeScript: units, quantity kinds and units of the wrong dimension are checked at run time (`ArgumentException`), not by the type system; stone/pound and feet/inch display also reject NaN and infinity, and `Pace.FromDistance` rejects a NaN distance (the TypeScript returns NaN); `UuidV7.TimestampOf` rejects ids that aren't version 7.

## API authentication

Every endpoint requires a signed-in user unless it is marked `AllowAnonymous` (only `/health` is); a fallback authorization policy enforces this, so a new endpoint can't forget it. `src/Health360.Server/Auth` accepts a bearer token only if:

1. It is signed by the project's Auth. Keys come from the JWKS at `/auth/v1/.well-known/jwks.json` (ES256 or RS256). HS256 is accepted only when `Supabase:JwtSecret` is set, for projects or local stacks that still use the legacy secret.
2. Its issuer is `{Supabase:Url}/auth/v1`, its audience is `authenticated`, and it hasn't expired (30 s clock skew).
3. Its `role` is `authenticated` and its `sub` is a user id. This rejects the anon and service-role keys, which are validly signed but carry no user.
4. Auth still accepts it: the API calls `GET /auth/v1/user`, as `authenticateUser` does in the Edge Functions, so signed-out sessions and deleted users are rejected before their token expires. A confirmed session is cached for 60 s (`Supabase:SessionCacheDuration`), so a sign-out takes effect within a minute. If Auth can't be reached, the API answers 503 with `Retry-After`, not 401, so the app doesn't treat the user as signed out.

Endpoints take a `SignedInUser` parameter and query through `IUserDatabase` (`src/Health360.Server/Data`). It runs each request's queries in one transaction after `set_config('role', 'authenticated', true)` and `set_config('request.jwt.claims', <the token's payload>, true)`, as PostgREST does, so `auth.uid()` and RLS work as they do for the app. Both settings are transaction-local, so a pooled connection carries nothing over to the next request.

The API connects as its own login role, never the service role or `postgres`. The role is `noinherit`, so on its own it has no table privileges; it can only become `authenticated`. Create it once per environment, like PowerSync's replication role ([phase0.md](phase0.md#cloud-setup-needs-your-accounts)):

```sql
create role health360_api login noinherit password '<generate one>';
grant authenticated to health360_api;
```

Configuration (environment variables use `__` for `:`, such as `Supabase__ApiKey`; keep secrets in user secrets or the host's secret store, never in `appsettings*.json`):

| Setting | Value |
|---|---|
| `Supabase:Url` | Project URL. `appsettings.Development.json` sets the local stack's `http://127.0.0.1:54321` |
| `Supabase:ApiKey` | The publishable (anon) key; Auth needs it as the `apikey` header |
| `Supabase:JwtSecret` | Only for HS256 projects, such as a local stack whose `supabase status` lists a JWT secret |
| `ConnectionStrings:Database` | Npgsql connection string for `health360_api` |

Tests: `AuthenticationTests` covers the token rules against a fake Auth and needs no database. `UserDatabaseTests` needs a migrated database. Set `HEALTH360_TEST_DATABASE` to an Npgsql connection string for a role that can create roles and users. The tests create `health360_api` and two users, A and B, and delete the users afterwards. Without the variable those tests are skipped; CI's database job runs them against `supabase db start`:

```sh
HEALTH360_TEST_DATABASE="Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres" \
  dotnet test tests/Health360.Server.Tests --filter Category=Database
```

Not yet tested against a real Auth server: the `/auth/v1/user` check and the JWKS format are tested with a fake that follows Supabase's documented responses.

## Running it

```sh
dotnet build Health360.Core.slnf
dotnet test Health360.Core.slnf
dotnet format Health360.Core.slnf --verify-no-changes
dotnet run --project src/Health360.Server      # http://localhost:5080/health; needs the settings in "API authentication"
```

The MAUI app needs a workload:

```sh
dotnet workload install maui-android --version 10.0.403
# Installs the Android SDK platform and build tools the project needs (needs a JDK 17+):
dotnet build src/Health360.Maui -t:InstallAndroidDependencies -f net10.0-android -p:AndroidSdkDirectory=$HOME/android-sdk -p:AcceptAndroidSDKLicenses=True
dotnet build src/Health360.Maui -f net10.0-android -p:AndroidSdkDirectory=$HOME/android-sdk
```

On Windows, install `maui-windows` (same `--version`) and build with `-f net10.0-windows10.0.19041.0`.

## CI

| Job | Runner | What it runs |
|---|---|---|
| `.NET build, format, tests` | Linux | locked restore, `dotnet format --verify-no-changes`, Release build, `dotnet test` on `Health360.Core.slnf` |
| `Database (pgTAP, lint, drift)` | Linux | also the API's database tests (`Category=Database`) against `supabase db start` |
| `MAUI app (Android build)` | Linux | `maui-android` workload, Android SDK components, Release build |
| `MAUI app (Windows build)` | Windows | `maui-windows` workload, Release build |

`dotnet format` can't load the MAUI project's workspace, so formatting in `Health360.Maui` is only checked by the build's code-style analyzers (`EnforceCodeStyleInBuild`).

## Findings

- `Directory.Build.props` is read before the project file, so a condition there on a property the project sets (such as `UseMaui`) is always false. Put such settings in `Directory.Build.targets`.
- A `workloadVersion` in `global.json` makes every build fail ("Workload version ... was not found") unless that workload set is installed, including builds that use no workloads. That's why the version is pinned on `dotnet workload install` instead.
- The Android SDK installer (`InstallAndroidDependencies`) fails with "CommonUtilities.Helpers.UserName must have a valid value" when `USER` isn't set, as in some containers. Set `USER` before running it.
- JavaScript's `Number.EPSILON` (2^-52) is not C#'s `double.Epsilon` (the smallest subnormal). `Rounding.RoundTo` spells out 2^-52 for its half-up nudge.
- FsCheck 3's C# API (`FsCheck.Fluent`) has `Gen.Zip` for two generators only; nest it for three.
- `using` directives needed only in `#if DEBUG` code must sit inside the same `#if`. Otherwise the Release build fails on IDE0005 (unnecessary using), because warnings are errors.
- JwtBearer fetches signing keys with its own `HttpClient`, which tests can't redirect. The API builds its configuration manager on the named client `supabase-auth` instead, and tests point that client at a fake Auth. Give the factory a new handler each time (`ConfigurePrimaryHttpMessageHandler`), because the HTTP client factory disposes the handlers it is given.
- With a fallback authorization policy, unknown routes answer 401 instead of 404 to callers without a token, so probing for endpoints reveals nothing.
