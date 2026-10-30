# Phase 0: foundation and risk tests

> **Stack change (2026-10-30):** [ADR 0002](adr/0002-dotnet-maui-blazor.md) moves Health360 to .NET MAUI (Android, Windows) and an ASP.NET Core API, and drops the web app, Apple and Linux desktop. This page records the **TypeScript prototype**. Its database, RLS and sync results still apply: the migrations, pgTAP tests and `powersync/sync-config.yaml` carry over. The Expo and Tauri findings are history. The .NET foundation is Phase 0b in [PLAN.md §3](PLAN.md).

Status of [PLAN.md §3 Phase 0](PLAN.md). The code is done and tested in CI and locally. What's left needs your accounts and devices: cloud projects, the Android dev build, and the six-target check.

## Deliverables

| Deliverable | State | Where |
|---|---|---|
| Monorepo and CI: typecheck, lint, Vitest, `supabase test db` | Done | `pnpm-workspace.yaml`, `turbo.json`, `.github/workflows/ci.yml` |
| Identity and household migration, RLS helpers, pgTAP RLS-coverage test | Done; 39 pgTAP assertions | `supabase/migrations/`, `supabase/tests/database/` |
| PowerSync with `me`, `household` and `reference` streams; permanent upload errors quarantined in `failed_uploads` | Done locally (Docker). **Cloud: you** | `powersync/`, `packages/local-db/src/upload.ts` |
| Expo app: tabs, sign-in by emailed code, onboarding with per-type unit preferences and locale defaults | Done; web verified in a browser. **Android dev client: you** | `apps/app/` |
| `packages/domain/units`: exact factors and property tests | Done; 42 tests incl. fast-check properties | `packages/domain/src/units/` |
| Throwaway `notes` table synced on all six targets | Chromium and Tauri on Linux verified here. **Other targets: you** (checklist below) | `apps/app/src/features/notes/` |
| PWA: manifest, service worker, "unsynced entries" indicator | Done | `apps/app/public/`, `SyncBadge.tsx` |
| Test Claude call with structured output, and `packages/domain` usable in an Edge Function | Done with recorded responses. **Live call: you** (needs `ANTHROPIC_API_KEY`) | `supabase/functions/ai-hello/`, `tools/codegen/` |
| Desktop shell | Tauri 2 builds and runs on Linux | `apps/desktop/` |

Extras that close Phase 0 risks from PLAN.md §4: a schema drift check (`pnpm drift:check`), client-generated UUIDv7 ids, and EAS build profiles including the monthly iOS simulator build (`apps/app/eas.json`).

## Exit criteria

| Criterion | State |
|---|---|
| On all six targets: sign in, create a note offline, reconnect, see it on a second device within about 5 seconds | **Chromium:** 1.1–2.2 s from reconnect to the second device (two browsers, one cut off at the network). **Tauri on Linux (WebKitGTK):** a server-side row reaches the window in 0.3–0.6 s. **Node clients (CI test):** under 5 s, asserted. Android, Firefox, iPhone Safari, Mac Safari and Tauri on Windows still need the manual run below |
| User B never receives it, proven by pgTAP and a two-client `@powersync/node` test | Done. Both suites fail when a policy or a stream is deliberately loosened (checked) |
| CI is green | Runs on the PR |
| If Tauri fails, record the Electron decision in an ADR | Tauri works on Linux after two fixes (below), so no ADR. Windows (WebView2) is still to be checked; it uses Chromium's engine, the same one already verified |

## What the tests found

These were bugs or platform limits; each is fixed and now covered by a test.

1. **Tauri on Linux can't start a SharedWorker.** WebKitGTK fails to load one from the `tauri://` scheme, which PowerSync web uses for multi-tab support, so the local database never opened. Inside Tauri the app now uses dedicated workers (`enableMultiTabs: false`). A desktop window has one tab, so nothing is lost.
2. **WebKitGTK buffers streamed HTTP responses.** Live updates reached the Tauri window about 20 s late. Inside Tauri the app now syncs over WebSockets: 0.3–0.6 s.
3. **A missing EXECUTE grant broke every profile write, and pgTAP didn't notice.** The height trigger calls `private.length_to_cm`, which signed-in users couldn't execute. PL/pgSQL checks that privilege once per transaction, and the test setup (as superuser) had already run it, so the behavioural test passed. The coverage test now checks grants on every `private` function directly.
4. **jsonb columns were uploaded as a string of JSON.** PowerSync keeps jsonb as text in SQLite, so `unit_prefs` reached Postgres as a JSON string and failed its `jsonb_typeof = 'object'` check. Uploads now convert jsonb and boolean columns back (`SERVER_COLUMN_TYPES`), the drift check fails if a new jsonb or boolean column isn't listed, and the e2e writer decodes payloads exactly as PostgREST does.
5. **Accessibility on web.** React Native Web ignores `accessibilityState`, so radio buttons never reported which option was selected. The app uses the cross-platform `role` and `aria-*` props instead.

6. **The desktop app rendered unstyled.** Tauri adds a nonce to the CSP's `style-src`, and with a nonce present browsers ignore `'unsafe-inline'`, so the `<style>` element React Native Web creates at runtime was blocked. `tauri.conf.json` now sets `dangerousDisableAssetCspModification: ["style-src"]`; scripts keep Tauri's hashes.

Both failure paths also worked as designed: the rejected writes in 3 and 4 were quarantined in `failed_uploads` instead of blocking the queue.

### Fixed after code review

- **Switching accounts on one device.** A session can disappear without a sign-out event (revoked or expired while the app was closed). The local database is now bound to the user who owns it and cleared before a different user's session is used, so the next person can't see the previous user's rows or inherit their unsent writes.
- **`ai-hello` required a signature, not a user.** The gateway's `verify_jwt` also accepts the public anon key, so the function now asks Supabase Auth for the user and returns 401 without one.
- **Offline reload after the first visit.** The service worker now caches the whole exported app when it installs (`apps/app/scripts/write-precache.mjs` writes the file list and a version hash into `sw.js` at export time).
- **Signing out kept or lost the wrong things.** Supabase's own SIGNED_OUT (a refresh token that stopped working) no longer clears the device; only an explicit sign-out does, and it asks first when changes haven't reached the server.
- **Consents are append-only.** Signed-in users can insert consent records but not edit or delete them, so the record of what someone agreed to can't be rewritten.
- **Sharing grants are read-only for now** and can only name a profile that belongs to the household; write access arrives with the shared-profile stream. Changing who manages a profile now updates their household memberships.
- **`ai-hello` without `ANTHROPIC_API_KEY`** answers 503 `not_configured` instead of crashing on start.
- **Lost edits.** An update that matched no row (hidden by RLS, or deleted on another device) used to look like success. It now goes to `failed_uploads`, and a retried transaction no longer duplicates entries there.

## Running it locally

Prerequisites: Node 22.18 or later (for its built-in TypeScript stripping), pnpm 10, Docker, the [Supabase CLI](https://supabase.com/docs/guides/local-development/cli/getting-started), and Deno 2 for Edge Function tests.

```sh
pnpm install
pnpm typecheck && pnpm lint && pnpm test

supabase start                                    # Postgres, Auth, REST; applies migrations and seed.sql
supabase test db                                  # pgTAP
DATABASE_URL=postgresql://postgres:postgres@127.0.0.1:54322/postgres pnpm drift:check

docker compose -f powersync/docker-compose.yml up -d   # PowerSync on :8080

# Two-client leak and offline tests (keys from `supabase status`):
SYNC_E2E=1 SUPABASE_URL=http://127.0.0.1:54321 SUPABASE_ANON_KEY=... SUPABASE_SERVICE_ROLE_KEY=... \
  POWERSYNC_URL=http://127.0.0.1:8080 pnpm --filter @health360/e2e-sync test

cp apps/app/.env.example apps/app/.env            # fill in the anon key
pnpm --filter @health360/app web                  # sign-in codes arrive in Mailpit: http://127.0.0.1:54324
```

Without Docker images for Supabase, `pnpm db:test:local` runs the migrations and pgTAP against a plain local Postgres with pgTAP installed (`tools/db/test-local.sh`).

Edge Function: `cd supabase/functions/ai-hello && deno test`. After changing `packages/domain` or `packages/schemas`, run `pnpm codegen`; CI fails if the copy in `supabase/functions/_shared/generated` is stale.

Desktop: `pnpm --filter @health360/desktop dev` (needs Rust, and on Linux the WebKitGTK 4.1 dev packages listed in the [Tauri prerequisites](https://v2.tauri.app/start/prerequisites/)).

## Cloud setup (needs your accounts)

1. **Supabase:** create the project, then `supabase link` and `supabase db push`. In Auth, keep email sign-in on, set up SMTP for real sign-in emails (the built-in sender is rate-limited), and paste `supabase/templates/sign-in-code.html` into the Magic Link and Confirm Signup templates so emails show the 6-digit code.
2. **PowerSync:** create an instance and connect it to the Supabase database with a replication role (the migration already creates the `powersync` publication):
   ```sql
   create role powersync_role with replication bypassrls login password '<generate one>';
   grant select on all tables in schema public to powersync_role;
   alter default privileges in schema public grant select on tables to powersync_role;
   ```
   Turn on Supabase auth in the instance's client-auth settings and deploy `powersync/sync-config.yaml`.
3. **Edge Function:** `supabase secrets set ANTHROPIC_API_KEY=...`, `supabase functions deploy ai-hello`, then call it once while signed in (`callAiHello` in `packages/api`). That's the live structured-output call; CI only uses recorded responses.
4. **App config:** set `EXPO_PUBLIC_SUPABASE_URL`, `EXPO_PUBLIC_SUPABASE_ANON_KEY` and `EXPO_PUBLIC_POWERSYNC_URL` for each build. If PowerSync or Supabase use a custom domain, add it to `connect-src` in `apps/desktop/src-tauri/tauri.conf.json`.
5. **Android dev client:** `cd apps/app && eas build --profile development --platform android`.
6. **Web:** `pnpm --filter @health360/app export:web` and deploy `apps/app/dist` to Cloudflare Pages with an SPA fallback to `index.html`.

## Six-target check

For each target: sign in, create a note with the network off, turn it back on, and time how long it takes to appear on a second device. Also check:

- [ ] **Android app (dev client):** the note survives killing the app while offline.
- [ ] **Chrome** and **Firefox:** reload on `/settings` works; the badge shows "Offline · 1 unsynced" while offline.
- ~~**iPhone (Safari)** and **Mac (Safari)**~~: dropped; Apple is out of scope (ADR 0002).
- [ ] **Tauri on Windows:** installer runs; offline write then sync; reload on a deep link.
- [x] **Tauri on Linux:** verified headlessly here (WebKitGTK 2.52 through `tauri-driver`). Linux desktop is now out of scope (ADR 0002).

**Known issue (desktop, Linux):** about 1 in 6 full-page reloads inside Tauri leaves the page on "Loading". The page's own PowerSync database lock (`db-lock-health360.sqlite`) stays held, so the local database never opens. Navigation inside the app is client-side and unaffected; only a reload (or a full-page load) can hit it. Closing and reopening the window recovers. It predates the review fixes. Linux desktop is now out of scope and the desktop app moves to MAUI on Windows (ADR 0002), so this no longer blocks shipping.
- [x] **User B never receives A's note:** pgTAP and `e2e/sync`.
