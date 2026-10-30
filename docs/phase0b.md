# Phase 0b: .NET foundation

Status of the move to .NET MAUI Blazor Hybrid and ASP.NET Core ([ADR 0002](adr/0002-dotnet-maui-blazor.md)). The deliverables and exit criteria are in [PLAN.md §3](PLAN.md). The TypeScript prototype ([phase0.md](phase0.md)) stays in place and in CI until the PowerSync .NET gate passes.

## Status

| Deliverable | State |
|---|---|
| Solution layout (§2.2) and CI: build, format and analyzers, tests | **Scaffolded.** Projects compile, with placeholder tests only |
| `Health360.Domain` units and UUIDv7 ids, with FsCheck property tests | **Done.** `Units/` and `Ids/UuidV7` port `packages/domain`, with the same factors and property tests (FsCheck) |
| `Health360.Server`: Supabase JWT and signed-in-user validation, queries as that user, one test Claude call | **Auth and user-scoped queries done** (see [API authentication](#api-authentication)), with `/me` and `/profiles/{id}` as the first endpoints. **Test Claude call started:** `POST /ai/hello` (see [Claude calls](#claude-calls)), tested against recorded responses only; not yet run against the live API |
| MAUI app: tabs, emailed-code login, onboarding, PowerSync .NET, `failed_uploads` quarantine | **Built; Android build verified, not yet run on a device** (see [MAUI app](#maui-app)): sign-in by emailed code, onboarding with every unit type and locale defaults, the five tabs and Settings (units, rejected writes with retry and discard, sign-out), syncing over the existing `me`, `household` and `reference` streams. The app's services (everything below the BlazorWebView) were run against `supabase start` and the local PowerSync: emailed-code sign-in, the profile arriving over `me`, onboarding reaching the server, an RLS-rejected write quarantined without blocking the queue, and a second user never receiving the first user's rows. Not yet run in the Android emulator or on Windows |
| Throwaway `notes` table on Android and Windows | **Built, not yet run on a device** (see [Notes sync test](#notes-sync-test)): the Today tab adds and lists notes through the local database. The two-device, kill-while-offline and two-client checks of the exit criteria are still to do |
| `tools/drift` against the PowerSync .NET schema | **Done for `AppSchema`** (see [Schema drift](#schema-drift)): runs in CI's database job next to `tools/codegen/check-drift.mjs`, which still checks the TypeScript client schema. The C# contracts are not compared yet |

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
3. Its `role` is `authenticated`, its `sub` is a user id, and its `session_id` is a session id. This rejects the anon and service-role keys, which are validly signed but carry no user. Every token Auth issues to a signed-in user names its session, and Auth skips its session check for tokens that don't, so the API requires one.
4. Auth still accepts it: the API calls `GET /auth/v1/user`, as `authenticateUser` does in the Edge Functions, so signed-out sessions and deleted users are rejected before their token expires. A confirmed session is cached for 60 s (`Supabase:SessionCacheDuration`), so a sign-out takes effect within a minute. Auth looks the session up by `session_id` and answers 403 `session_not_found` once it is gone. If Auth can't be reached, or its signing keys can't be loaded, the API answers 503 with `Retry-After`, not 401, so the app doesn't treat the user as signed out. Once loaded, the keys are cached, and a failed refresh keeps the old ones.

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

The API refuses to start without `Supabase:Url`, `Supabase:ApiKey` or `ConnectionStrings:Database`.

Tests: `AuthenticationTests` covers the token rules against a fake Auth and needs no database. `UserDatabaseTests` needs a migrated database. Set `HEALTH360_TEST_DATABASE` to an Npgsql connection string for a role that can create roles and users. The tests create a throwaway login role set up like `health360_api` (random name and password) and two users, A and B, and remove all of them afterwards. Without the variable those tests are skipped; CI's database job runs them against `supabase db start`:

```sh
HEALTH360_TEST_DATABASE="Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres" \
  dotnet test --project tests/Health360.Server.Tests --filter-trait "Category=Database"
```

Not yet tested against a real Auth server: the `/auth/v1/user` check and the JWKS format are tested with a fake that follows Supabase's documented responses.

## Claude calls

`POST /ai/hello` ports the prototype's `ai-hello` Edge Function: it validates the request, computes the body weight in kg with `Health360.Domain`, and asks Claude for a greeting and a wellness tip. It is the template for later AI endpoints (PLAN.md §2.4):

- `Health360.Contracts/Ai` holds the records, their FluentValidation validators and `AiJsonContext`. `StructuredOutputSchema.For` generates the JSON schema sent as `output_config.format` from the same type the output is parsed into (`JsonSchemaExporter`, every object closed with `additionalProperties: false`).
- `Health360.Server/Ai/StructuredClaude` makes the call with the official `Anthropic` package: `claude-opus-5-5`, effort set per request (`low` here), no sampling parameters, and server-side refusal fallback (`fallbacks: "default"`, beta `server-side-fallback-2026-07-01`). It checks `stop_reason` for `refusal` and `max_tokens` before parsing, then parses strictly (missing, null or unknown properties fail) and runs the validator. Failures go back to the model once, as a fresh request that lists the errors; after that the endpoint answers 502 `invalid_output`. Refusals, truncation and rejected outputs are logged with the model, attempt, token counts and the failed rules (property and rule code, such as `Greeting:MaximumLengthValidator`; JSON errors are logged as `$:Json` only, because their path can hold a property name the model made up); API errors with the exception type and status. Prompts and model output are never logged.
- Every AI endpoint uses the `ai` rate-limit policy (`RequireRateLimiting(AiServices.RateLimitPolicy)`): each signed-in user gets `Anthropic:RequestsPerUserPerHour` requests (default 20, must be at least 1; the API refuses to start otherwise), then 429 `rate_limited`. The count is in memory per API process, a cost backstop until per-profile quotas are kept in `ai_generations`.
- `StructuredClaude` is a singleton holding one `HttpClient`, so the factory never rotates its handler; the handler recycles pooled connections every 2 minutes instead, so DNS changes are picked up.
- Outcomes: 200; 400 for an invalid request (no model call); 422 `refused`; 502 `truncated`, `invalid_output` or `upstream_error`; 429 `rate_limited` (from Anthropic or the per-user limit); 503 `not_configured` when no key is set (the API still starts). Callers without a valid user token get 401 before any of this.

Configuration: `Anthropic:ApiKey` (or `ANTHROPIC_API_KEY`; keep it in user secrets or the host's secret store), `Anthropic:Model`, `Anthropic:MaxRetries` (SDK retries, default 2), `Anthropic:Timeout` and `Anthropic:RequestsPerUserPerHour`.

Tests: `AiHelloTests` runs the real SDK against `FakeAnthropic`, an HTTP handler that records requests and replays canned Messages API responses, so CI makes no live calls. `AiHelloContractTests` pins the generated schema and the validators. Not yet done: per-profile daily quotas and `ai_generations` usage logging, prompt caching, and a manual run against the live API.

## Local database

`src/Health360.LocalDb` ports `packages/local-db` (schema and upload queue) and the upload writer in `packages/api`. It is a plain `net10.0` library, not part of the MAUI project, so it builds and is tested in `Health360.Core.slnf` on Linux; the MAUI app only supplies the platform SQLite (`src/Health360.Maui/Data`). Keep it identical to the TypeScript while both exist.

- **SDK:** `PowerSync.Common` and `PowerSync.Maui` 1.0.0, pinned together in `Directory.Packages.props`. The README still calls the SDK beta, so the gate in PLAN.md §3 stands.
- `AppSchema` mirrors `schema.ts` table for table: the same columns and types, `failed_uploads` local only, and the same `ServerColumnTypes`, `InsertOnlyTables` and `UploadableTables`.
- `Uploads.UploadPendingAsync` is `uploadPending`: each op reaches the server, is quarantined in `failed_uploads` (permanent errors: SQLSTATE 22/23/42, PGRST1xx/2xx), or aborts the upload so PowerSync retries it. Quarantine entries are keyed by the op's queue position, so a retried transaction replaces its entry instead of adding one, and an op that later succeeds clears it. An update that changes nothing sends nothing. `RetryFailedUploadAsync` and `DiscardFailedUploadAsync` are the Settings actions.
- `PostgrestWriter` uploads through PostgREST with the user's access token and the publishable key, so RLS checks every write. PUT is an upsert (insert-only tables ignore duplicates); PATCH asks for the row back and quarantines an update that matched no row (`PGRST116`); DELETE checks whether a row RLS hid from the delete is still there (`42501`). An error without a code (a gateway page) and a missing session are retried, never quarantined.
- Sync and uploads use the session's token only when it belongs to the database's owner (`ISupabaseSession` returns the user id with the token). If someone else has signed in, nothing syncs or uploads until `ConnectAsync` has rebound and cleared the database, so the previous user's queued writes are never sent as the new user. A 2xx reply that isn't JSON (a captive portal) is retried, not quarantined.
- `SupabaseConnector` gives PowerSync the access token and the PowerSync URL, and logs only the op, table and error code of a quarantined write (no ids, no row data).
- `LocalDatabase` binds the database to the user who owns it. `ConnectAsync(userId)` clears it first when another user (or nobody known) owned it, including pending uploads and `failed_uploads`; `DisconnectAsync` keeps everything for the same user's return; `ClearAsync` is for a sign-out the user asked for. The owner is kept in MAUI `Preferences` (`PreferencesOwnerStore`). Connections retry every 2 s, as in the prototype.

Not done yet: the two-device and kill-while-offline checks of the gate.

`Profiles` holds the profile queries the app shell and onboarding use: a watched query for the signed-in user's row, the onboarding save and the unit-preference update. PowerSync tables are views with `INSTEAD OF` triggers, whose row counts SQLite reports as 0, so a save first checks that the row exists in the same transaction and throws if it doesn't (a save that changed nothing is never shown as done). `FailedUploads` lists the quarantine for Settings without the row data.

Tests: `tests/Health360.LocalDb.Tests` runs the upload queue and the account binding against a real PowerSync database (the SDK's native core and SQLite, in a temporary file) with a fake writer, and `PostgrestWriter` against a fake PostgREST. No server is involved.

## Schema drift

`tools/drift` (`Health360.Drift`) compares `AppSchema` with the tables a migrated database publishes to PowerSync (the `powersync` publication), and exits non-zero on drift. It ports `tools/codegen/check-drift.mjs`, with the same Postgres-to-SQLite type map:

- every published table is in `AppSchema` with the same columns and storage types, and has an `id` (PowerSync supplies it on the client). Columns left out of a publication column list count as not synced;
- every jsonb and boolean column is in `ServerColumnTypes` with the right codec, and no other column is, including columns that are no longer synced and `id` (uploads apply every entry, so a stale one would turn text into JSON);
- every synced client table and every `UploadableTables` entry is published, and every `ServerColumnTypes` table is a synced client table. `failed_uploads` (local only) is skipped.

```sh
HEALTH360_DRIFT_DATABASE='Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres' \
  dotnet run --project tools/drift
```

Exit codes: 0 matches, 1 drift (or an empty publication: migrations not applied), 2 no connection string, a malformed one, or no connection. The connection string is never printed. `tests/Health360.Drift.Tests` covers the comparison; CI's database job runs the tool against `supabase db start`. It works the same against `tools/db/test-local.sh`'s kind of database (the shim plus the migrations on a plain Postgres 16).

Not covered yet: the C# contracts (PLAN.md §2.2 lists them for `tools/drift`), since no contract mirrors a synced row yet.

## MAUI app

The screens and their services are in `src/Health360.UI` (no MAUI dependency, so they build and are tested on Linux); `src/Health360.Maui` supplies secure session storage, the platform SQLite and the endpoints. The flow ports the prototype's `_layout.tsx`, `(auth)`, `onboarding.tsx`, `(tabs)` and `settings.tsx`.

- **Sign-in** (`Health360.UI/Auth`): `SupabaseAuthClient` calls Supabase Auth directly: `POST /auth/v1/otp` (creates the account the first time), `/verify` with the 6-digit code, `/token?grant_type=refresh_token` and `/logout?scope=local`. Errors are sorted into rejected (wrong or expired code, a refused refresh token), rate limited, and unavailable (offline, 5xx, a gateway or captive-portal page), so the app never signs someone out because the network failed. Nothing logs a token, code or email address.
- **Session**: `SessionManager` is the `ISupabaseSession` the sync connector and uploads ask for tokens. It refreshes a token within 60 s of expiry, one refresh at a time (Auth rotates refresh tokens, so a second refresh with the old one would be refused), and saves the rotated session straight away. Offline, it keeps the session and hands out the token while it is still valid. The session is kept in MAUI `SecureStorage` (`SecureSessionStore`): Android Keystore-encrypted preferences, and on Windows (unpackaged) a DPAPI-encrypted `securestorage.dat` in the app's data folder.
- **Account binding** (`AuthService`): every session binds the local database to its user before any screen sees it. Restoring a saved session at launch binds and connects without going online. Only a sign-out the user asks for clears the device; it first counts pending and rejected writes and asks before deleting them, and if Auth can't be reached nothing is cleared. A refresh token Auth refuses ends the session but keeps the data for the same user's return (the next user to sign in gets a cleared database through `LocalDatabase`).
- **Shell** (`Layout/MainLayout.razor`): loading, then sign-in, then "Setting up this device" until the profile row the server created at sign-up has synced, then onboarding until `onboarded_at` is set, then the tabs. Today, Food, Train, Body and Plan are a bottom bar on phones and a side rail in windows at least 840 px wide; Settings is in the top bar, next to the sync badge (online or offline, unsynced writes, rejected writes).
- **Onboarding**: name, all 14 unit types (`UnitPrefsEditor`; defaults from the device locale by `UnitPrefs.DefaultFor`, and the protein ratio follows body weight while it is on its default), height in cm or feet and inches, birth date, sex for calculations and activity level. The time zone (IANA; Windows ids are converted) and currency come from the device (`DeviceLocale`). `Health360.Contracts/Profiles` holds `UnitPrefsJson` (the prototype's `unit_prefs` format, byte for byte), `OnboardingForm` with its validator (it also checks the table's constraints, such as the height pair and a three-letter uppercase currency, so a valid form is never quarantined) and `HeightEntry` (ports `height.ts`). The save goes to the local database and uploads when online.
- **Settings**: unit preferences, the rejected writes with retry and discard, and sign-out.
- **Endpoints**: set at build time as `Health360SupabaseUrl`, `Health360SupabaseApiKey` (the publishable key) and `Health360PowerSyncUrl`, as `-p:` properties or environment variables. They default to the local stack; the Android emulator reaches it at 10.0.2.2, and a network security config allows plain HTTP only to 10.0.2.2, 127.0.0.1 and localhost. Without a key the sign-in screen says the build isn't connected.

Deviations from the prototype: sign-out ends only this device's session (`scope=local`; supabase-js signs out every device by default), the sign-out confirmation is shown in the page rather than a system dialog, and the currency check is the table's (`^[A-Z]{3}$`) rather than zod's length-only one.

Tests: `tests/Health360.UI.Tests` runs the Auth client, the session and `AuthService` against a fake Auth server (Supabase's documented responses), and the profile, sync state and screens (bUnit) against a real PowerSync database in a temporary file. The project keeps culture data (`UseCultureData`), as the MAUI app does, so the locale defaults are tested with real ICU data. `ServicesTests` resolves the host's whole service graph.

## Notes sync test

The throwaway `notes` table (PLAN.md §3) ports the prototype's `NotesTest`; don't build features on it. The table, its RLS and the `me` stream already existed, so this is client code only:

- `Health360.Contracts/Notes`: `NoteInput` and its validator (ports `noteInputSchema`: 1 to 2000 characters after trimming, the table's constraint). Characters are counted as Postgres's `char_length` counts them (code points), so an emoji counts once; zod and an HTML `maxlength` count UTF-16 units, which would refuse notes the table accepts.
- `Health360.LocalDb/Notes`: `AddAsync` writes the note with a UUIDv7 id made on the device, the signed-in profile, today's date in the device's time zone (`TimeProvider.GetLocalNow`) and `created_at`/`updated_at`; it uploads through the usual queue. `Watch` lists every note the device has, newest first. It doesn't filter by profile, so a row the streams shouldn't have delivered would show up rather than be hidden.
- `Health360.UI/Components/NotesTest.razor`, on the Today tab: the text box (Add disabled for a blank or too-long note), the list, and its loading, empty and error states. A failed save keeps the text and says so; text typed while a save is in flight is kept. If the watched query fails, the error shows even over an earlier list, and only the exception type is logged.

Tests: `NotesTests` in `Health360.LocalDb.Tests` (id, local date across midnight UTC, trimming, the queued PUT, empty notes write nothing, order) and in `Health360.UI.Tests` (adding, the disabled Add, a synced note appearing). Not yet done: running it on Android and Windows against a real stack, and the exit criteria's two-device, kill-while-offline and two-client isolation checks.

## Running it

```sh
dotnet build Health360.Core.slnf
dotnet test --solution Health360.Core.slnf
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

### Running the app

Against the local stack (`supabase start`, then `docker compose -f powersync/docker-compose.yml up -d`), pass the publishable key that `supabase status` prints; the URLs default to the local stack:

```sh
dotnet build src/Health360.Maui -t:Run -f net10.0-android -p:AndroidSdkDirectory=$HOME/android-sdk -p:Health360SupabaseApiKey=<publishable key>
```

On Windows, build the Windows target instead (no Android SDK needed). The key is baked in per build, so an app built without it, including one started from Visual Studio, shows "This build isn't connected to a server yet". To run from Visual Studio, set `Health360SupabaseApiKey` as an environment variable and start Visual Studio from that shell.

```sh
dotnet build src/Health360.Maui -t:Run -f net10.0-windows10.0.19041.0 -p:Health360SupabaseApiKey=<publishable key>
```

The sign-in code arrives in the local mail catcher (Mailpit, http://127.0.0.1:54324). For a cloud project, also set `Health360SupabaseUrl` and `Health360PowerSyncUrl`. Never pass the service-role key.

## CI

| Job | Runner | What it runs |
|---|---|---|
| `.NET build, format, tests` | Linux | locked restore, `dotnet format --verify-no-changes`, Release build, `dotnet test` on `Health360.Core.slnf` |
| `Database (pgTAP, lint, drift)` | Linux | also `tools/drift` (`AppSchema` against the migrations) and the API's database tests (`Category=Database`) against `supabase db start` |
| `MAUI app (Android build)` | Linux | `maui-android` workload, Android SDK components, Release build |
| `MAUI app (Windows build)` | Windows | `maui-windows` workload, Release build |

`dotnet format` can't load the MAUI project's workspace, so formatting in `Health360.Maui` is only checked by the build's code-style analyzers (`EnforceCodeStyleInBuild`).

## Findings

- `PowerSync.Common` 1.0.0 depends on `SQLitePCLRaw.bundle_e_sqlite3` 2.1.10, which has a high-severity advisory (GHSA-2m69-gcr7-jv3q). NuGet's audit warning fails the restore because warnings are errors. `Health360.LocalDb` references the bundle directly, now 3.0.5, which also lifts `SQLitePCLRaw.core` under `Microsoft.Data.Sqlite.Core` 9.0.1 (built against 2.x) to 3.x. The LocalDb and UI tests open a real PowerSync database on it; Android and Windows rely on the MAUI CI builds and are not yet run on a device.
- The SDK parses each queued write's data (`CrudEntry.OpData`) with Newtonsoft, which turns any text that looks like an ISO date-time into a `DateTime`: `created_at` (or a note that reads like a timestamp) loses its text and UTC offset. `Uploads` reads the op's JSON from `ps_crud` instead; `SendsTimestampsExactlyAsStored` guards it.
- `PowerSync.Common` ships its native core for Linux, macOS and Windows desktop as content files, so a plain `net10.0` test project can open a real PowerSync database on the Linux CI runner.
- PowerSync .NET 1.0.0 builds the sync URL as the credentials' endpoint plus `/sync/stream`, and `Uri.ToString()` ends a bare host with `/`, so `http://127.0.0.1:8080/` requested `//sync/stream`, which the service answers with 404. Sync then retried forever and never connected; unit tests with fakes couldn't see it. `SupabaseConnector.Endpoint` trims the trailing slash.
- `PowerSyncDatabase.Connect` returns only after the first successful connection, so offline (or with that 404) it never returns. `LocalDatabase.ConnectAsync` used to await it while holding its gate, which would have blocked sign-out and account switches until the network came back; it now returns once sync has started.
- PowerSync tables are views with `INSTEAD OF` triggers, so `Execute` reports 0 rows affected for an UPDATE that did change a row. Check that the row exists instead (`Profiles`).
- PowerSync reads rows with Dapper, which emits IL at run time. .NET 10 file-based apps (`dotnet run app.cs`) default to `PublishAot`, which turns dynamic code off, so a scratch harness needs `#:property PublishAot=false`. The MAUI app runs on Mono (Android) and CoreCLR (Windows) and is unaffected.
- bUnit's synchronous `Click()`, `Change()` and `Submit()` can race the app's background re-renders (sync status, watched queries): an event lands on a handler a concurrent render just replaced and is lost, which made about half the component-test runs fail. The UI tests await `ClickAsync`, `SubmitAsync` and `TriggerEventAsync` instead (`AppTestContext`).
- With invariant globalization (every project except the MAUI app and those that set `UseCultureData`), Windows can't resolve an IANA time zone id such as `America/Chicago`: `FindSystemTimeZoneById` needs ICU to map it to a Windows id and throws `TimeZoneNotFoundException`. Linux reads IANA ids from tzdata, so the Linux CI runner never sees it. Tests in invariant projects build their zones with `TimeZoneInfo.CreateCustomTimeZone`; only projects that keep culture data (the MAUI app and `Health360.UI.Tests`) look zones up by IANA id.
- `Directory.Build.props` is read before the project file, so a condition there on a property the project sets (such as `UseMaui`) is always false. Put such settings in `Directory.Build.targets`.
- The Windows targets set the MAUI app's `PackageVersion` from `ApplicationDisplayVersion`, while Android keeps the `1.0.0` default. Visual Studio evaluates both target frameworks and fails with "Unable to read project information for 'Health360.Maui': The property PackageVersion was expected to have a single value across all target frameworks". The project sets `Version` to `$(ApplicationDisplayVersion)` so both agree; the command line, which builds one framework, doesn't hit it.
- A `workloadVersion` in `global.json` makes every build fail ("Workload version ... was not found") unless that workload set is installed, including builds that use no workloads. That's why the version is pinned on `dotnet workload install` instead.
- The Android SDK installer (`InstallAndroidDependencies`) fails with "CommonUtilities.Helpers.UserName must have a valid value" when `USER` isn't set, as in some containers. Set `USER` before running it.
- JavaScript's `Number.EPSILON` (2^-52) is not C#'s `double.Epsilon` (the smallest subnormal). `Rounding.RoundTo` spells out 2^-52 for its half-up nudge.
- xunit.v3 4 runs only on Microsoft.Testing.Platform: with the .NET 10 SDK, the old VSTest `dotnet test` fails with "Testing with VSTest target is no longer supported". `global.json` opts in (`"test": {"runner": "Microsoft.Testing.Platform"}`), so `dotnet test` takes `--solution` or `--project`, and trait filters are `--filter-trait "Category=Database"` instead of `--filter Category=Database`.
- FsCheck 3's C# API (`FsCheck.Fluent`) has `Gen.Zip` for two generators only; nest it for three.
- `using` directives needed only in `#if DEBUG` code must sit inside the same `#if`. Otherwise the Release build fails on IDE0005 (unnecessary using), because warnings are errors.
- JwtBearer fetches signing keys with its own `HttpClient`, which tests can't redirect. The API builds its configuration manager on the named client `supabase-auth` instead, and tests point that client at a fake Auth. Give the factory a new handler each time (`ConfigurePrimaryHttpMessageHandler`), because the HTTP client factory disposes the handlers it is given.
- With a fallback authorization policy, unknown routes answer 401 instead of 404 to callers without a token, so probing for endpoints reveals nothing.
- Don't test startup failures through `WebApplicationFactory`. When the app throws at startup, its deferred host races the app's own disposal, so the test sometimes sees `ObjectDisposedException` instead of the real error. `StartupValidationTests` runs the same registrations and `IStartupValidator` directly.
- The `Anthropic` C# SDK (13.x) sends `ApiKey` as `Authorization: Bearer`, not `x-api-key` (its XML docs say so); tests assert that header.
- `ToJsonString()` escapes `'` as `\u0027`, so tests that look for text inside a recorded request body read the string value instead of searching the serialized JSON.
- Supabase Auth checks the session (`auth.sessions`) only for tokens that carry a `session_id` claim. Tokens it issues to signed-in users always do, so the API rejects tokens without one rather than relying on the user lookup alone.
