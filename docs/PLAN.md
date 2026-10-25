# Health360: implementation plan

Health360 is one app for planning and reaching health and fitness goals. It covers goals and cost estimates (food, supplements, gym, equipment), workout and cardio tracking, body composition, nutrition (macros and micronutrients) with nutrition-label OCR, labs and vitals, mental and cognitive tracking, progress photos, AI meal plans and workouts, and food and supplement shopping lists.

**Related docs**
- [FEATURES.md](FEATURES.md): the feature catalogue, module by module, with competitor sources.
- [adr/0001-language-and-stack.md](adr/0001-language-and-stack.md): why TypeScript and Expo, compared with C#/.NET, Flutter, Kotlin Multiplatform, Capacitor and a web-only PWA.

## Decisions
- **Platforms:** Android, web (installable as an app), and Windows and Linux desktop. **Apple is deferred** (§2.7): iPhone and Mac users use the web app until a native iPhone app is added later for $99/yr. The code stays iPhone-ready.
- **Audience:** a household of 2–4 people first. It is built so it could go public later, so compliance and billing stay light for now.
- **MVP:** all four core modules: Nutrition with label OCR, Workouts with progression, Body metrics with photos, and Goals with cost planning.
- **Backend:** Supabase.
- **Stack:** TypeScript with Expo, Tauri, PowerSync and Supabase ([ADR 0001](adr/0001-language-and-stack.md)).
- **Units:** metric and imperial, stored as entered (§2.6).

---

## 2. Architecture

Section 1 (the feature catalogue) is in [FEATURES.md](FEATURES.md), and section 2.0 (the language choice) is in [ADR 0001](adr/0001-language-and-stack.md).

### 2.1 Stack
| Layer | Choice | Why |
|---|---|---|
| App | **Expo** (current SDK), expo-router, TypeScript strict, **EAS dev builds from day 1** (not Expo Go) | One codebase for Android and web now. iOS is added later with no rewrite. Native modules (SQLite, Health Connect) need dev builds |
| Web | Expo web export as an **installable PWA**: manifest + service worker, persistent-storage request, Web Push | Gives iPhone and Mac users a home-screen or Dock app until the native iPhone app exists |
| Desktop | **Tauri 2** wrapping `expo export -p web` (single-page output) for **Windows and Linux**; macOS is deferred. Electron is the fallback if the Phase 0 test fails | Small installers, native menus, auto-update, file drag-and-drop for lab PDFs |
| Offline sync | **PowerSync** (Sync Streams) + `@powersync/drizzle-driver`. The Drizzle schema generates the client schema | Gives SQLite on iOS, Android and web. Writes go through supabase-js, so database permissions still apply. Has a Node SDK for headless tests, and can be self-hosted |
| Backend | **Supabase**: Postgres + RLS, Auth (emailed 6-digit code in the MVP; Google later; Sign in with Apple when the iPhone app is added), Storage, Edge Functions (Deno), pg_cron | Fits relational health data, per-user and per-household security, and private files |
| UI | NativeWind + react-native-reusables, FlashList, **react-native-gifted-charts** (SVG) | Works on native and web. SVG charts avoid the ~3 MB WebAssembly download that Skia needs on web |
| State | PowerSync live queries for local data; TanStack Query for online-only calls (search, AI); Zustand for in-session UI state such as the active workout | Each tool does one job |
| Forms and validation | react-hook-form + **zod** in a shared package | The same schemas check forms, API payloads and the JSON Claude returns |
| Device | expo-camera (barcodes on Android), a WebAssembly barcode reader on the web (Safari has no built-in one), expo-image-manipulator (resize, strip EXIF), expo-notifications (on-device scheduling), expo-local-authentication | |
| Health (P3) | `react-native-health-connect`; `@kingstinct/react-native-healthkit` when the iPhone app is added | Reads directly on the device |
| AI | `npm:@anthropic-ai/sdk` in Edge Functions only | The API key never reaches the client |
| Ops | Sentry with PII scrubbing; GitHub Actions + EAS; Supabase Pro (backups) or a nightly `pg_dump` | Health values must never land in error reports |

### 2.2 Monorepo layout (pnpm + Turborepo)
```
Health360/
  apps/
    app/                 Expo (Android + web/PWA now; iOS-ready)
      app/               expo-router: (auth)/ (tabs)/today|food|train|body|plan settings/
      src/features/      nutrition/ workouts/ body/ goals/ costs/ shopping/ labs/ mental/
      src/db/            powersync.ts, connector.ts (upload + permanent-error quarantine), attachments.ts
      src/platform/      *.native.ts / *.web.ts adapters (health, camera, files, secure storage)
    desktop/             Tauri 2 shell (frontendDist -> ../app/dist)
  packages/
    domain/              pure TS, no I/O: units, energy/TDEE (formula + adaptive), macros, recipe
                         math, portion solver, DRI/UL checks, e1RM, progression, plates, volume,
                         cost math, milestones, goal feasibility, streaks
    schemas/             zod: entities, edge-function contracts, AI output schemas
    local-db/            Drizzle SQLite tables -> DrizzleAppSchema; typed queries
    api/                 typed edge-function client (supabase-js)
    ui/                  design system, charts, theme, i18n
    config/              tsconfig, eslint, tailwind preset
  supabase/
    migrations/          SQL schema, RLS, helper functions, triggers, storage policies
    functions/           _shared/ (auth, anthropic client, quota, generated copy of domain+schemas),
                         food-search, barcode-lookup, ai-label-ocr, ai-meal-plan, ai-workout-program,
                         ai-weekly-checkin, ai-meal-photo, ai-lab-parse, ai-receipt,
                         household-invite, account-delete, data-export
    tests/               pgTAP RLS + storage tests, RLS-coverage test
  powersync/             sync-config.yaml (Sync Streams), docker-compose.yml (local)
  tools/importers/       USDA FDC, free-exercise-db, DRI tables, biomarkers, NIH DSLD
  tools/codegen/         copy domain/schemas into functions/_shared; schema-drift check
  evals/                 label-ocr/ lab-parse/ meal-plan/ workout-program/ (fixtures + runners)
  e2e/                   playwright/ maestro/
  docs/                  PLAN.md FEATURES.md adr/
```

### 2.3 Data model (Postgres; compact)

**Conventions**
- Every table: `id uuid` (UUIDv7, generated on the client), plus `created_at` and `updated_at`.
- Event rows also store `local_date`, so a "day" means your local day.
- Units (§2.6): each quantity is stored **as entered**, as a value plus a unit, together with a canonical metric column used for math, charts and comparisons. Values use `double precision`.
- Categories: `text` + CHECK constraints instead of Postgres enums, which are painful to migrate.
- Money: `amount_minor bigint` + `currency`.
- Nutrient maps: jsonb keyed by nutrient code, per 100 g.
- Imports: `source` + `external_id`, so re-imports don't create duplicates.
- **Every row carries `profile_id` and/or `household_id`** (copied down onto child rows), so access rules never need joins.
- Personal rows key on `profile_id`, not on the login user id, so managed child profiles can be added later.

**Access scopes:**
- **U:** the owner, plus household members if a `sharing_grants` row allows it.
- **H:** household members.
- **G:** global reference data; anyone signed in can read, only the server can write.
- **S:** server-only, never synced.

| Area | Tables (key columns) |
|---|---|
| Identity (U/H) | `profiles` (managed_by, birth_date, sex_for_calcs, height + height_unit + height_cm, `unit_prefs` jsonb, timezone, currency, activity_level) · `households` · `household_members` (role owner/adult/teen) · `household_invites` (S) · `sharing_grants` (owner, grantee/null=all, category, access) · `consents` (tos/privacy/ai_processing/photo_ai/health_data) |
| Reference (G) | `nutrients` · `nutrient_dri` (sex, age band, rda/ai/ul) · `metric_definitions` (code, unit, category, valid range) · `exercises` (system rows + household custom; tracking_type, equipment, muscles[]) · `biomarkers` (loinc, unit conversions, retest_days) · `biomarker_ranges` (reference/optimal, citation) · `interaction_rules` |
| Nutrition | `foods`: global catalog rows are searched online and not synced; custom rows are H. Columns: source, barcode, macro columns per 100 g, `micros_100g`, verified · `food_portions` · `recipes`, `recipe_ingredients` (H) · `saved_meals` (U) · `food_log_entries` (U; nutrient **snapshot**, entry_method, is_estimate, cost_minor) · `nutrition_targets` (U; method formula/manual/adaptive) · `label_scans` (U; image_path, extracted, checks, food_id) |
| Workouts | `training_locations` (H; equipment_tags, plate_inventory with a unit per plate, bar_weight + bar_unit) · `programs` · `workout_templates` · `template_exercises` (set_scheme, rest, progression) · `scheduled_workouts` · `workout_sessions` · `session_exercises` · `set_logs` (set_type, reps, weight + weight_unit + weight_kg, rpe/rir, duration, distance + distance_unit + distance_m, e1rm_kg, is_pr) · `personal_records` (all U) |
| Body | `measurements` (U; metric_code, value + unit + value_canonical, measured_at, group_id: weight, body fat, circumferences, BP, HR, HRV, SpO₂, glucose, steps) · `measurement_groups` · `progress_photos` (U; pose, storage_path, thumb_path; never shared by default) · `checkins` (weekly adaptive TDEE) · `sleep_sessions` (P3) |
| Goals and costs | `goals` (U/H; type, metric/exercise/biomarker, start/target, rate, plan) · `goal_milestones` · `goal_cost_estimates` (breakdown, assumptions) · `stores` · `products` (food/supplement/equipment) · `price_observations` (price_minor, package qty, observed_on, source) · `cost_items` (gym/equipment/supplements/food/coaching, recurrence) · `equipment` · `budgets` · `shopping_lists`, `shopping_list_items` (all H) |
| Later | `meal_plans`/`meal_plan_entries` (H) · `supplement_products`/`_ingredients` (G/H) · `supplement_regimens`/`_intakes` (U) · `lab_reports`/`lab_results` (U; raw + canonical value, flag, confirmed) · `retest_reminders` · `mental_checkins` · `cognitive_tests` · `habits`/`habit_logs` · `reminders` · `pantry_items`, `expenses` (H) · `device_connections` (S; tokens in Vault) · `ai_generations` (written by the server, the owner can read; feature, model, prompt_version, tokens, cost, validation, accepted) |

**RLS and sync**
- **Helper functions:** `private.my_profile_ids()` and `private.my_household_ids()`, both SECURITY DEFINER and STABLE.
- **Policies:**
  - U: read if it's my profile or a grant allows it; write only my own profiles.
  - H: read and write if I'm a member of the household.
- **Storage:** private buckets `progress-photos`, `lab-reports` and `scans`. The first path segment is the profile id, files are read through short-lived signed URLs, and `scans` files expire after 30 days.
- **Sync Streams:**
  - `me`: U tables, last 24 months; older history is fetched on demand.
  - `household`: H tables.
  - `reference`: G tables.
  - `shared_profile` (on demand): **must** check `sharing_grants`.
- Delete triggers or functions remove the matching Storage files.

### 2.4 AI layer
- **Where it runs:** Edge Functions only. `ANTHROPIC_API_KEY` is kept in Supabase secrets.
- **Model:** `claude-opus-5-5` ($4 / $20 per million input/output tokens), configurable per function. You can trial cheaper models (`claude-sonnet-5-5`, `claude-haiku-5-5`) for high-volume OCR. Switch only if the eval set shows no accuracy drop.
- **Effort:**
  - `low` for extraction (labels, labs, receipts).
  - `medium` or `high` for meal plans and programs.
  - Effort is the only control on Opus 5.5, because thinking can't be turned off.
- **Request rules:**
  - Structured output through `messages.parse` with JSON schemas generated from `packages/schemas`.
  - Don't send `temperature`, `top_p` or `top_k`.
  - Check `stop_reason` for `refusal` and `max_tokens` before parsing.
  - Turn on server-side refusal fallback: `fallbacks: "default"` with beta header `server-side-fallback-2026-07-01`.
  - Send lab PDFs as `document` blocks.
- **The model proposes, code checks.**
  - Every result goes through a validator in `packages/domain`: label arithmetic, macro tolerances, budget, exercise IDs and equipment, sets per muscle.
  - If validation fails, the errors go back to the model for one repair round; after that the request fails and you get a manual fallback.
  - The AI never writes numbers that code can compute. For example, the weekly check-in only describes the computed TDEE.
- **Cost:**
  - Resize images on the device and cache the fixed prompt prefix.
  - Run weekly jobs through the **Batches API** (50% cheaper) on a pg_cron schedule.
  - Per-profile daily quota; usage and cost logged in `ai_generations`.

### 2.5 Privacy and safety
- **Data protection:**
  - RLS is required on every table, enforced by a CI test.
  - Photo location data is stripped on the device, and photos are kept out of the camera roll.
  - No AI on photos without `photo_ai` consent. Optional biometric app lock. Account deletion also removes stored files.
- **Wellness framing, not a medical device:**
  - Disclaimers on onboarding, labs, supplements and AI screens.
  - Calorie floors and rate caps, and a mode that hides calorie numbers.
  - AI prompts forbid diagnosis and dosing.
  - Lab results show the lab's reference ranges first.

### 2.6 Units: metric and imperial
- **Per-type preferences** in `profiles.unit_prefs`. Defaults come from your locale (US → imperial), and you can change each type:

  | Measurement type | Choices |
  |---|---|
  | Body weight | kg / lb / st+lb |
  | Lifting load | kg / lb |
  | Height | cm / ft+in |
  | Circumference | cm / in |
  | Distance | km / mi (plus m / yd for track and swim) |
  | Pace | min/km / min/mi |
  | Food mass | g / oz |
  | Volume | ml / fl oz / cups |
  | Energy | kcal / kJ |
  | Temperature | °C / °F |
  | Blood glucose | mg/dL / mmol/L |
  | Lab values | US conventional / SI |

  Household members each have their own preferences.
- **Store what was typed.** Every quantity column is a trio: `value`, `unit`, and a canonical value (`*_kg`, `*_cm`, `*_m`, `*_g`, `*_ml`, `value_canonical`).
  - The original value is shown when you view it in the same unit, so 225 lb never turns into 224.99 lb.
  - The canonical value drives TDEE, trends, PRs, charts and AI prompts.
- **One conversion module.** `packages/domain/units` uses exact factors:
  - 1 lb = 0.45359237 kg
  - 1 in = 2.54 cm
  - 1 mi = 1609.344 m
  - 1 oz = 28.349523125 g
  - 1 US fl oz = 29.5735295625 ml
  - 1 kcal = 4.184 kJ
  - Per-analyte factors for glucose and lab values, kept in the `biomarkers.unit_conversions` table

  Canonical values are computed on write. A Postgres trigger recomputes them, so client and server can't drift.
- **Mixed-unit history:** charts convert every point to your current display unit. PR comparisons use the canonical value with a small tolerance, so switching units never creates fake PRs.
- **Training:**
  - Progression increments are set in your lifting unit (e.g. +5 lb or +2.5 kg).
  - The plate calculator works with lb plates, kg plates or a mix, and rounds suggestions to loads you can actually build.
- **Food:**
  - Portions can be g, oz, ml, fl oz, cups, tbsp, tsp, pieces or servings; each one is converted to grams through `food_portions` or a density value.
  - Label OCR keeps the serving size exactly as printed on the label, both the household measure and the metric amount.
- **AI:** prompts get values in your display units with the units spelled out, and structured outputs must include units. Validators convert to canonical values before checking.

### 2.7 Apple devices: deferred, not dropped
**Now:** iPhone and Mac users use the web app.
- On iPhone, Safari's "Add to Home Screen" installs it.
- On Mac, Safari's "Add to Dock" (macOS 14+) does the same, or they use any browser.

**What works for them:** food logging, label photos (camera capture), barcode scanning (WebAssembly reader), workouts, body metrics, progress photos, goals, costs and shopping lists. Offline logging also works, through PowerSync's web SDK.

**What they don't get until the iPhone app exists, and how the web app softens it:**
- **No Apple Health or Apple Watch data.** Browsers can't read HealthKit. Weight, sleep and other values can still be entered by hand.
- **Rest timer and reminders don't fire while the app is closed.**
  - Supplement and workout reminders are sent as server Web Push (iOS 16.4+ home-screen apps; pg_cron + an Edge Function, VAPID keys, free).
  - The rest timer alerts only while the app is open.
- **iPhone can clear website storage.** Mitigations:
  - Ask for persistent storage (`navigator.storage.persist()`).
  - Sync the moment the app is back online.
  - Show an "unsynced entries" badge.
  - Keep a short offline window.
- **No native Mac desktop app.** An unsigned Tauri Mac build is possible for family members willing to override Gatekeeper.

**Keeping the code iPhone-ready (costs nothing):**
- Use only libraries that support both Android and iOS.
- Keep platform code behind the `src/platform/` adapters.
- **Run a monthly EAS iOS *simulator* build** to catch iOS compile breaks. It's covered by the free tier and needs no Apple account.

**Adding the iPhone app later (the "Apple add-on" in §3):**
1. Apple Developer account ($99/yr).
2. EAS iOS build profile and TestFlight.
3. HealthKit adapter (`src/platform/health.ios.ts`).
4. Sign in with Apple (required by the App Store when Google sign-in is offered).
5. Signed and notarized Tauri Mac build.
6. Maestro iOS flows and the device checklist.

The screens and logic are already shared, so this is mostly setup and testing.

---

## 3. Phased roadmap (each phase has an exit criterion)

### Phase 0: Foundation and risk tests
**Deliverables**
- Monorepo setup and CI: typecheck, lint, Vitest, `supabase test db`.
- Supabase local and cloud projects, with identity and household migrations, RLS helpers, and a pgTAP RLS-coverage test.
- PowerSync running locally (Docker) and in the cloud, with the `me`, `household` and `reference` streams. Permanent upload errors go to a `failed_uploads` table instead of blocking the queue.
- Expo dev clients, tab navigation, login by emailed code, and onboarding, including per-type unit preferences with locale defaults.
- `packages/domain/units` with exact conversion factors and property tests. It comes first because every module depends on it.
- A throwaway `notes` table synced on **all six targets**:
  - The Android app.
  - Chrome and Firefox.
  - The web app installed on an iPhone (Safari) and in the Mac Dock (Safari).
  - Tauri on Windows and on Linux.

  This checks the web workers, PWA install and persistent storage, the Tauri content security policy, and reloading on a deep link.
- PWA basics: web app manifest, service worker, and an "unsynced entries" indicator.
- One test Claude call with structured output from an Edge Function, plus a check that `packages/domain` can be imported there (with a codegen copy as the fallback).

**Exit criteria**
- On all six targets: sign in, create a note offline, reconnect, and see it on a second device within about 5 seconds.
- User B never receives it; pgTAP and a two-client `@powersync/node` test both prove this.
- CI is green.
- If Tauri fails, the Electron decision is recorded in an ADR.

### Phase 1: MVP
1. **Body and photos**
   - Delivers: metric definitions, weight trend, measurements, body-fat estimate and vitals, plus progress photos with overlay, offline upload queue and comparison view.
   - **Exit:** the trend matches a test fixture. A photo taken in airplane mode uploads later and only its owner can see it; another member gets a 403.
2. **Workouts and progression**
   - Delivers: exercise import, locations, templates, schedule, live logger, PRs and e1RM, volume per muscle, cardio, and progression suggestions.
   - **Exit:** a 60-minute session logged offline, with the app killed mid-session, loses nothing and syncs. Load suggestions match the progression tests.
3. **Nutrition and label OCR**
   - Delivers: USDA import, food search, the barcode lookup chain, `ai-label-ocr` with checks and a confirm screen, food logging, recipes, the micronutrient panel, and formula targets.
   - **Exit:**
     - ≥90% exact match on serving size, kcal, protein, carbs and fat across a **50+ photo eval set from your own pantry**, measured before any user edits.
     - 18 of 20 common barcodes resolve without OCR.
     - A full day logged offline syncs.
     - Recipe nutrition matches a hand calculation.
4. **Goals and cost planning**
   - Delivers: the goal wizard (feasibility, milestones, targets), price book, cost items, supplement cost entries, per-goal and monthly estimates, budgets, shared shopping lists, and the Today dashboard.
   - **Exit:**
     - "Lose 6 kg by date X" produces the right deficit, a warning when the pace is too fast, and milestones.
     - The monthly estimate matches a spreadsheet to the cent.
     - Milestones tick off automatically.
5. **Ship to the household**
   - Desktop: Tauri builds for Windows (signing optional) and AppImage/deb for Linux, plus the auto-updater.
   - Phones: Android through the Play internal testing track (or a direct APK); iPhone users install the web app to the home screen.
   - Web: hosted on Cloudflare Pages, plus server Web Push for reminders.
   - Also: consent screens, export, account deletion, Sentry with scrubbing, and backups.
   - **Phase 1 exit:** 2–3 household members use it daily for 2 weeks on phone, web and desktop, with zero lost entries, zero leaks between users, and every suite green.

### Phase 2: AI depth
**Deliverables**
- Adaptive TDEE and weekly check-in.
- `ai-meal-plan`: plans built from your household catalog, scaled by the solver, priced, and turned into a shopping list.
- `ai-workout-program`: limited to exercises that exist and that your equipment supports.
- Meal-photo estimates.
- Receipt OCR into the price book.
- AI coach chat and trends.

**Exit criteria**
- A 7-day plan for 2 people meets the macro tolerances every day and stays within budget.
- 100 generated programs (eval) contain no exercises outside the available equipment.
- Adaptive TDEE recovers a known synthetic TDEE within ±100 kcal after 3 weeks.

### Phase 3: Health depth
**Deliverables**
- Health Connect sync (HealthKit comes with the Apple add-on).
- Vitals baselines and sleep.
- Labs: parse, confirm, trends, optimal ranges, retest reminders.
- The full supplement module: regimens, refills, upper-limit and interaction checks.
- Mental check-ins and cognitive tests.
- Recovery heatmap and readiness score.

**Exit criteria**
- Importing the same 30 days of Health Connect data twice gives zero duplicates.
- A real lab PDF parses ≥95% correctly after review.
- Food plus supplements going over an upper limit (vitamin D in the test) triggers a warning.

### Phase 4: Extras and going public
- Wearable aggregator (Terra or Open Wearables) and GPS cardio.
- Habits with forgiving streaks, push notifications, managed child profiles, challenges and circles.
- Pantry, Instacart and Kroger, CSV import, ICS feed.
- RevenueCat and Stripe billing; GDPR and FTC Health Breach Notification Rule review.
- OFF attribution and share-alike compliance, SQLCipher local database encryption, and an accessibility audit.

### Apple add-on (whenever someone needs Apple Health or Apple Watch data, or before going public)
**Deliverables** (from §2.7)
- Apple Developer account, EAS iOS builds and TestFlight.
- HealthKit adapter.
- Sign in with Apple.
- Signed and notarized Tauri Mac build.
- Maestro iOS flows.

**Exit criteria**
- The iPhone app passes the same Phase 1 offline and leak tests.
- Importing 30 days of HealthKit data twice gives zero duplicates.

---

## 4. Key risks and mitigations
| Risk | Mitigation |
|---|---|
| Database rules (RLS) and sync rules drift apart, so data leaks between users | Ownership columns on every row (no joins in rules). Grant checks in parameterized streams. Two-client leak tests and pgTAP in CI on every migration |
| One bad upload blocks PowerSync's write queue | Sort errors by Postgres code. Permanent failures go to `failed_uploads`, with a retry/discard screen and a Sentry alert |
| Schema drift between Postgres, Drizzle and zod | Migrations are additive only. `tools/codegen` compares the schemas against `supabase gen types` in CI |
| Expo web + PowerSync web workers inside Tauri's WebKit (macOS/Linux) | Phase 0 test on every OS, IndexedDB storage mode as the default, Electron fallback |
| Health Connect needs a Play health-permissions declaration and a privacy policy | Dev builds from day 1; sign Phase 3 off on a physical Android phone |
| iPhone web app limits: storage can be cleared, no background alerts, no Apple Health | Persistent-storage request, immediate sync, unsynced badge, server Web Push. The Apple add-on removes these limits when needed |
| The code quietly stops building for iOS while Apple is deferred | Libraries that support both platforms only, platform adapters, and a monthly free EAS iOS simulator build in CI |
| OCR accuracy (EU vs US label formats, glare, curved packs) | Framing guide, empty-when-unreadable fields, arithmetic checks, required confirmation, original image kept for re-processing, eval gate on every prompt or model change |
| Meal-photo calories are underestimated | Range plus estimate badge, never the default entry method, prompt to weigh food |
| Progress-photo privacy | Private bucket keyed by profile id, signed URLs, EXIF stripped on device, not shared by default, consent required before any AI sees a photo |
| Medical-advice liability and harm | Wellness framing, disclaimers, calorie floors and rate caps, an option to hide numbers, no diagnosis or dosing |
| AI cost, latency and failures | Image resizing, prompt caching, Batches API, quotas, refusal handling, a manual fallback for every AI flow |
| Food data licensing | Each row's source recorded, USDA preferred, OFF only for barcodes, attribution screen |
| Unit conversion bugs (rounding drift, false PRs, wrong glucose or lab conversions) | Values stored as entered plus canonical; one tested conversion module with exact factors; a server trigger recomputes canonical values; PR comparisons use a tolerance; lab conversions are defined per biomarker, never guessed |
| Data loss (the Supabase free tier pauses projects and has no backups) | Supabase Pro or a nightly `pg_dump` before real data; test export and restore |
| Scope creep | Phase gates tied to exit criteria |

## 5. Verification strategy
1. **`packages/domain`:** Vitest plus fast-check property tests, including:
   - Every unit pair round-trips. A value entered in lb or in is shown exactly as typed.
   - Switching units never creates a PR.
   - The plate calculator handles lb, kg and mixed plate sets.
   - e1RM rises steadily with weight and reps.
   - The plate calculator stays within the plate inventory.
   - Adaptive TDEE converges on noisy synthetic data.
   - Goal cost equals the sum of its parts.
   - Golden CSV fixtures for trends and progression.
2. **Database:** pgTAP through `supabase test db`. Tests cover A vs B access, member vs non-member, sharing grants, storage policies and RLS coverage. CI also runs Supabase advisors and `db lint`.
3. **Sync integration (Docker in CI):** local Supabase + PowerSync with two to three `@powersync/node` clients. Covers leak tests, offline write then reconnect, concurrent shopping-list check-offs, and quarantined errors.
4. **Edge Functions:** Deno tests with recorded Claude responses (no live calls in CI). Zod contract tests; validator and repair-loop tests with deliberately bad outputs.
5. **AI evals (`evals/`, nightly or manual):** label OCR per-field accuracy, lab-parse accuracy, meal-plan tolerance pass rate, workout-program constraint violations. No regression allowed on prompt or model changes.
6. **Components:** jest-expo + React Native Testing Library for the set logger, food entry, OCR confirm screen and goal wizard.
7. **Web E2E:** Playwright against the exported web build with seeded local Supabase, on Chromium, Firefox and **WebKit** (standing in for iPhone and Mac Safari users):
   - Log food.
   - Build a template and log sets.
   - Add weight → create a goal → check the cost estimate.
   - Go offline → log → go online → check the database.
8. **Mobile E2E:** Maestro on the Android emulator, nightly. The same flows plus airplane mode and killing the app mid-workout. iOS flows come with the Apple add-on.
9. **Desktop:** smoke tests via `tauri-driver` on Windows and Linux.
10. **Manual device checklist each phase:**
    - Android: Health Connect permissions, barcode and label capture in poor light, notifications.
    - A real iPhone running the web app: install, offline logging, Web Push reminder, camera.
11. **CI order:**
    - Every PR: typecheck, lint, unit tests, pgTAP, drift check, sync tests, Playwright.
    - Main branch: EAS and Tauri builds.
    - Nightly: Maestro and AI evals.
    - Monthly: an EAS iOS simulator build to keep the code iPhone-ready.

## 6. Running costs (list prices checked Oct 2026; USD)

### 6.1 Price list per service
| Service | What it's for | Free tier | Paid tier that matters |
|---|---|---|---|
| **Supabase** | Postgres, Auth, Storage, Edge Functions | $0. Paused after 1 week inactive, **no backups**, 500 MB database, 1 GB files | **Pro $25/mo**: never paused, daily backups (7 days), 8 GB database, 100 GB files, 250 GB egress, 2M function calls, and $10 compute credit covering one Micro instance. Each extra project ~$10/mo compute. Small compute $15, Medium $60. PITR $100/mo (not needed) |
| **PowerSync** | Offline sync | $0. 2 GB synced/mo, 50 concurrent clients, 500 MB hosted; **deactivated after about 1–2 weeks unused** | **Pro $49/mo**: 30 GB synced, 1,000 clients. Or **self-host the free Open Edition** on a ~$5–10/mo VPS (you run it yourself) |
| **Claude API** (`claude-opus-5-5`) | OCR, meal plans, programs, coach | none | $4 / $20 per million input/output tokens, cache reads $0.20/M, Batches 50% off (see 6.2) |
| **Expo EAS** | Android cloud builds, the monthly iOS simulator check, over-the-air updates | $0. 15 iOS + 15 Android builds/mo (slow queue), 1,000 update users | Starter $19/mo (fast queue, $45 build credit, 3,000 update users) |
| **Apple Developer** | iOS distribution, TestFlight, HealthKit, macOS notarization | none | **Deferred: $0 now**, $99/yr once the Apple add-on starts |
| **Google Play** | Android distribution | none | **$25 one-time** |
| **Windows code signing** | No SmartScreen warning on install | skip it for family use (click through the warning) | Azure Artifact Signing Basic **$9.99/mo** (optional) |
| Web hosting (Expo web build) | The web app | Cloudflare Pages: $0 | — |
| Login emails | Emailed sign-in codes (Supabase's built-in email is rate-limited and meant for testing) | Free tier of a transactional email service | AWS SES ~$0.10 per 1,000 emails |
| Sentry | Error reports | Developer plan: $0 | Team ~$26/mo (public launch only) |
| USDA FoodData Central, Open Food Facts | Food data | $0 (API key, rate limits) | — |
| GitHub Actions | CI | 2,000 private-repo minutes/mo; Linux runners are enough while Apple is deferred | macOS minutes count 10×, which only matters once the Apple add-on starts |
| Web Push | Reminders in the installed web app | $0 (VAPID keys; sent from an Edge Function) | — |
| Domain | `health360.<tld>` | — | ~$10–20/yr |
| Terra (wearables) | Garmin, Oura, WHOOP | — | **$499/mo, so skipped.** Most wearables already sync into Health Connect (and Apple Health, once the iPhone app exists), which the app reads for free. Open Wearables (MIT) is the free self-hosted fallback |

### 6.2 Claude cost per action (estimates on Opus 5.5)
These are rough planning numbers. Phase 1 replaces them with real figures from the `ai_generations` table.

| Action | Rough tokens (input / output incl. thinking) | ≈ Cost |
|---|---|---|
| Nutrition-label scan (photo resized to ~1,200×1,600 ≈ 2.5k image tokens) | 4.5k / 1.5k | **$0.04–0.05** |
| Meal-photo estimate or receipt scan | 4k / 1.5k | $0.04 |
| 7-day meal plan for 2 people (with one repair round when needed) | 15k / 13k | $0.30–0.50 |
| Workout program | 10k / 10k | $0.25 |
| Coach chat message (cached context) | 6k / 1.6k | $0.04 |
| Weekly check-in summary (Batches API, 50% off) | 8k / 4k | $0.06 |
| Lab report PDF (~5 pages) | 10k / 3k | $0.10 |

- **Heavy use**, about $4–6 per person per month: 15 label scans, 30 meal photos, 40 coach messages, 4 weekly summaries, a share of 4 meal plans and receipts, and 1 program.
- **Light use** (scans and weekly summaries only): about $1–2 per person per month.

**Optional cheaper model, your call:** `claude-haiku-5-5` costs $0.10 / $0.50 per million tokens. It would make a label scan about $0.002 instead of about $0.05. Switch only if the label eval set shows no drop in accuracy.

### 6.3 Monthly totals by stage
| Stage | Setup | ≈ Monthly |
|---|---|---|
| **Building (Phases 0–1)** | Supabase Free (plus `supabase start` locally), PowerSync Free, EAS Free, Google $25 once, no Apple | **~$10–30/mo**, which is Claude for development and OCR eval runs. Also $25 one-time for Google, and roughly $25 once for repeated runs of the 50-photo eval |
| **Household in daily use (2–4 people), recommended** | Supabase Pro, PowerSync Free, EAS Free, domain, no Apple, no Windows signing | **~$35–55/mo** (~$400–650/yr): Supabase $25 + domain ~$1 + Claude ~$8–25 (Opus 5.5) |
| Same, after the Apple add-on | + Apple $99/yr | +~$8/mo |
| Same, with sync that's never deactivated | + PowerSync Pro $49, or self-hosted ~$5–10 | ~$50–115/mo |
| Same, with signed Windows installer | + $9.99 | +$10/mo |
| **Public launch (~1,000 monthly users)** | Supabase Pro + Small compute, PowerSync Pro, EAS Starter, Sentry Team, Windows signing | **~$150–200/mo infrastructure, plus Claude at $1–6 per AI-active user** (~$500–3,000+/mo). AI becomes the main cost, so it needs a paywall or per-user quotas. Also: RevenueCat is free under $2.5k monthly revenue, then 1%; Apple and Google take 15% on small-business subscriptions; privacy policy and terms cost $0–500 one-time |

**Cost levers, in order of savings:**
1. Per-user AI quotas, already in the plan.
2. Haiku for the extraction routes, if the eval allows it.
3. Batches API for anything not urgent.
4. Prompt caching and image resizing.
5. Self-hosting PowerSync.
6. Skipping Windows signing for family use, and deferring Apple.
7. One production Supabase project, with development on local Docker or a free organization.

## 7. First files to create (Phase 0)
- `supabase/migrations/0001_identity_households_rls.sql`: profiles, households, sharing grants, `private.*` helpers, and the RLS pattern later tables copy.
- `powersync/sync-config.yaml`: Sync Streams that mirror the RLS.
- `packages/local-db/src/schema.ts`: the Drizzle client schema, which generates the PowerSync schema.
- `apps/app/src/db/connector.ts`: the upload connector and error quarantine.
- `packages/domain/src/index.ts`: the deterministic math, shared with Edge Functions through `supabase/functions/_shared/`.


## Sources (pricing)
- Costs: [Supabase pricing](https://supabase.com/pricing) · [PowerSync pricing](https://www.powersync.com/pricing) · [Expo EAS pricing](https://expo.dev/pricing) · [Azure Artifact Signing pricing](https://azure.microsoft.com/en-ca/pricing/details/artifact-signing/) · [Terra pricing](https://tryterra.co/pricing) · [Claude vision token math](https://platform.claude.com/docs/en/build-with-claude/vision-coordinates)
