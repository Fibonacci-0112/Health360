# Health360: implementation plan

Health360 is one app for planning and reaching health and fitness goals. It covers goals and cost estimates (food, supplements, gym, equipment), workout and cardio tracking with heart-rate zones and aerobic/anaerobic and fat/carb estimates, body composition, a physiology profile (RMR, DEXA, VO₂max, LTHR, max HR and HR zones from lab tests, or estimates when you don't have them), nutrition (macros and micronutrients) with nutrition-label OCR, labs and vitals, mental and cognitive tracking, progress photos, AI meal plans and workouts, and food and supplement shopping lists.

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
- **Measured beats estimated:** lab and test values you enter (RMR, DEXA, VO₂max, LTHR, max HR, HR zones) replace formula estimates everywhere they are used, and every derived number shows which inputs it used (§2.8).

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
      src/features/      nutrition/ workouts/ body/ physiology/ goals/ costs/ shopping/ labs/ mental/
      src/db/            powersync.ts, connector.ts (upload + permanent-error quarantine), attachments.ts
      src/platform/      *.native.ts / *.web.ts adapters (health, camera, files, secure storage)
    desktop/             Tauri 2 shell (frontendDist -> ../app/dist)
  packages/
    domain/              pure TS, no I/O: units, energy/TDEE (formula + adaptive), macros, recipe
                         math, portion solver, DRI/UL checks, e1RM, progression, plates, volume,
                         cost math, milestones, goal feasibility, streaks, physiology (estimators,
                         best-value resolver, zones), exercise energy (kcal, fat/carb, aerobic/anaerobic)
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
  tools/importers/       USDA FDC, free-exercise-db, DRI tables, biomarkers, NIH DSLD,
                         Compendium METs, population RER-by-intensity table
  tools/codegen/         copy domain/schemas into functions/_shared; schema-drift check
  evals/                 label-ocr/ lab-parse/ (incl. DEXA, RMR, metabolic tests) meal-plan/
                         workout-program/ (fixtures + runners)
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
| Identity (U/H) | `profiles` (managed_by, birth_date, sex_for_calcs, height + height_unit + height_cm, `unit_prefs` jsonb, timezone, currency, activity_level, `physiology_prefs` jsonb: pinned sources, heart-rate-lowering medication flag) · `households` · `household_members` (role owner/adult/teen) · `household_invites` (S) · `sharing_grants` (owner, grantee/null=all, category, access) · `consents` (tos/privacy/ai_processing/photo_ai/health_data) |
| Reference (G) | `nutrients` · `nutrient_dri` (sex, age band, rda/ai/ul) · `metric_definitions` (code, unit, category, valid range) · `exercises` (system rows + household custom; tracking_type, equipment, muscles[]) · `biomarkers` (loinc, unit conversions, retest_days) · `biomarker_ranges` (reference/optimal, citation) · `interaction_rules` · `activity_mets` (2024 Compendium code, activity, MET) · `substrate_curves` (population RER by % VO₂max and training status, citation) |
| Nutrition | `foods`: global catalog rows are searched online and not synced; custom rows are H. Columns: source, barcode, macro columns per 100 g, `micros_100g`, verified · `food_portions` · `recipes`, `recipe_ingredients` (H) · `saved_meals` (U) · `food_log_entries` (U; nutrient **snapshot**, entry_method, is_estimate, cost_minor) · `nutrition_targets` (U; method formula/manual/adaptive; protein basis body_weight/lean_mass, protein ratio + unit g/kg or g/lb + canonical g/kg) · `label_scans` (U; image_path, extracted, checks, food_id) |
| Workouts | `training_locations` (H; equipment_tags, plate_inventory with a unit per plate, bar_weight + bar_unit) · `programs` · `workout_templates` · `template_exercises` (set_scheme, rest, progression) · `scheduled_workouts` · `workout_sessions` (avg_hr, max_hr, `zone_seconds` jsonb, `energy_estimate` jsonb snapshot: kcal range, fat/carb kcal and g, aerobic/anaerobic minutes, inputs and method version) · `session_exercises` · `set_logs` (set_type, reps, weight + weight_unit + weight_kg, rpe/rir, duration, distance + distance_unit + distance_m, e1rm_kg, is_pr) · `personal_records` (all U) |
| Body | `measurements` (U; metric_code, value + unit + value_canonical, measured_at, group_id, source lab/field_test/device/manual, method: weight, body fat, circumferences, BP, HR, HRV, SpO₂, glucose, steps, plus RMR, VO₂max, max HR, LTHR, thresholds, FTP and the DEXA metrics) · `measurement_groups` (kind manual/dexa/rmr/metabolic_test/lactate_test/field_test, provider, device make, protocol, report_path) · `exercise_test_stages` (U; group_id, stage, HR, speed or power, VO₂, VCO₂, RER, lactate) · `zone_sets` (U; sport, metric hr/power/pace, basis custom/lab/lthr/hrr/max_hr, boundaries, effective_from) · `progress_photos` (U; pose, storage_path, thumb_path; never shared by default) · `checkins` (weekly adaptive TDEE) · `sleep_sessions` (P3) |
| Goals and costs | `goals` (U/H; type, metric/exercise/biomarker, start/target, rate, plan) · `goal_milestones` · `goal_cost_estimates` (breakdown, assumptions) · `stores` · `products` (food/supplement/equipment) · `price_observations` (price_minor, package qty, observed_on, source) · `cost_items` (gym/equipment/supplements/food/coaching, recurrence) · `equipment` · `budgets` · `shopping_lists`, `shopping_list_items` (all H) |
| Later | `meal_plans`/`meal_plan_entries` (H) · `supplement_products`/`_ingredients` (G/H) · `supplement_regimens`/`_intakes` (U) · `lab_reports`/`lab_results` (U; raw + canonical value, flag, confirmed) · `retest_reminders` · `mental_checkins` · `cognitive_tests` · `habits`/`habit_logs` · `reminders` · `pantry_items`, `expenses` (H) · `hr_series` (U; per-session HR samples from Health Connect, compressed) · `device_connections` (S; tokens in Vault) · `ai_generations` (written by the server, the owner can read; feature, model, prompt_version, tokens, cost, validation, accepted) |

**RLS and sync**
- **Helper functions:** `private.my_profile_ids()` and `private.my_household_ids()`, both SECURITY DEFINER and STABLE.
- **Policies:**
  - U: read if it's my profile or a grant allows it; write only my own profiles.
  - H: read and write if I'm a member of the household.
- **Storage:** private buckets `progress-photos`, `lab-reports` and `scans`. The first path segment is the profile id, files are read through short-lived signed URLs, and `scans` files expire after 30 days.
- **Sync Streams:**
  - `me`: U tables, last 24 months, plus the newest physiology value per metric and source tier at any age (§2.8); older history is fetched on demand.
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
  - Prompts get each physiology value with its source (lab, field test, device, manual or estimate, §2.8), so the model never presents an estimate as a measurement.
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
  - Estimates are labelled as estimates, with the formula named and a range. Fat burned in a session is never presented as body fat lost (§2.9).
  - DEXA bone density is shown as the report states it, with the report's own classification. The app adds no diagnosis.
  - A PAR-Q+ screen comes before any maximal field test. If you take medication that lowers heart rate (such as beta blockers), nothing is derived from age-predicted max HR: no estimated max HR, LTHR or HR-based zones. Measured max HR, LTHR and lab thresholds still work. Without them, zones and the aerobic/anaerobic and fat/carb splits come from RPE or aren't shown, and session energy uses METs instead of HR.

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
  | Protein target ratio | g/kg / g/lb, of body weight or lean mass (defaults from the body-weight unit) |

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
  - 1 g/lb = 1 / 0.45359237 g/kg (from the lb factor), for protein targets
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
- **Physiology values** (§2.8) use fixed scientific units, not preferences: HR in bpm, VO₂max in ml/kg/min (the absolute L/min and the test-day weight are kept too when a report gives them), lactate in mmol/L, BMD in g/cm². RMR and exercise energy follow your energy unit, and DEXA masses follow your body-weight unit.

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

### 2.8 Physiology profile: measured values first, estimates otherwise
You can enter values from lab tests, field tests or a device. Anything you haven't entered is estimated where a published method exists (BMD, VAT, FTP, threshold pace, LT1 and per-stage test values are not), and the app always shows which one it is using.

**What you can enter.** Each value has a date, a source (lab, field test, device or manual) and an optional report file, kept in the private `lab-reports` bucket.

| Value | Typical source | Estimate when it's missing |
|---|---|---|
| RMR (kcal/day) | Indirect calorimetry (metabolic cart or handheld device) | Katch-McArdle (370 + 21.6 × fat-free mass in kg) when fat-free mass is known; otherwise Mifflin-St Jeor |
| DEXA: body-fat %, fat and lean mass (total and by region), bone mineral content, BMD with T- and Z-scores, visceral fat (VAT), android/gynoid fat, ALMI | DEXA scan report; the scanner make is stored, because Hologic and GE Lunar results differ | Body fat and fat-free mass from a smart scale or the Navy method. BMD and VAT are not estimated |
| VO₂max (ml/kg/min) | Lab treadmill or bike test | In order: a field test (Cooper 12-minute run, Rockport 1-mile walk), a device value, then a non-exercise estimate: from age, sex, waist, resting HR and activity (Nes et al., 2011), or, without a resting HR, from age, sex, BMI or body fat, and activity (Jackson et al., 1990) |
| Max HR | Lab test, or the highest HR from a hard session | Tanaka: 208 − 0.7 × age (individuals vary by about ±10 bpm) |
| Resting HR | Morning readings in vitals, or a device | Not estimated. Without it, zones use % of max HR instead of HR reserve |
| LTHR | Lab lactate test, or a 30-minute solo time trial (average HR of the last 20 minutes) | About 90% of max HR, marked low confidence. Off when max HR is only age-predicted and the medication flag is set (§2.5) |
| LT1/LT2 or VT1/VT2 (HR, pace or power) | Lab lactate or gas-exchange test | LT2 is taken as LTHR. LT1 is not estimated |
| FTP (W), threshold pace | 20-minute test (95% of average power), recent race | Not estimated; power and pace zones stay off |
| HR zones (per sport) | Your own boundaries, or a lab report | Derived, best basis first: lab thresholds, a measured or test-derived LTHR (Friel), HR reserve (Karvonen), % of max HR. The LTHR estimate never sets zones |
| Metabolic test: VO₂, VCO₂, RER, lactate per stage; FatMax and crossover point | Gas-exchange test with stages | Per-stage values are not estimated. Without a test, the fat/carb split uses the population curve (§2.9), and FatMax and the crossover point are shown only from a test |

**How the app picks a value** (`packages/domain/physiology`)
- A resolver returns, for each value, the number in use plus its source, method, date, confidence and the alternatives it passed over.
- Order: a source you pinned, then lab, then field test, then a device or manual reading, then estimate. Within a tier, the newest value wins.
- Old measured values still beat formulas (a year-old lab VO₂max is usually closer than an age formula), but they get a "retest?" flag. VO₂max, LTHR and DEXA are flagged after 12 months, and measured RMR after 12 months or a 5% change in body weight. In Phase 3 these flags become `retest_reminders`.
- The 24-month sync window (§2.3) doesn't apply to resolver inputs. A trigger marks the newest `measurements` row per metric and source tier, and marked rows sync at any age with their `measurement_groups` and `exercise_test_stages` rows. So an old lab value still resolves offline on a newly set-up device.
- Estimates are computed when needed and not stored, so they follow changes in age, weight and resting HR. Measured values are `measurements` rows with `source` and `method`. One DEXA scan or lab visit is one `measurement_groups` row, and its stage table goes in `exercise_test_stages`.
- Entering or deleting a measured value updates everything that uses it from then on. Past session estimates (§2.9) are snapshots; they change only if you choose "recalculate history".
- Every derived number has an info view that lists its inputs, for example "RMR 1,720 kcal (lab, Mar 2026) × activity 1.55".

**Where the values are used**
- **Calorie targets:** TDEE = RMR × activity factor, with a measured RMR replacing the formula. Adaptive TDEE (Phase 2) takes over once there is enough weight and intake data.
- **Protein:** once DEXA gives fat-free mass, protein can be set per kg or per lb of lean mass instead of body weight.
- **Body-composition trends:** each method is its own series (DEXA, smart scale, Navy method), because methods disagree by several percentage points and mixing them creates fake changes.
- **Goals:** VO₂max, lean-mass and visceral-fat targets are goal types.
- **Cardio:** zones, session energy and the fat/carb split (§2.9).
- **AI:** prompts receive each value with its source (§2.4).

**Entry by phase:** by hand in the MVP. Guided field tests come in Phase 2. In Phase 3, DEXA, RMR and metabolic-test PDFs go through `ai-lab-parse` with the same confirm screen as labs, and Health Connect adds VO₂max, resting HR and HR samples as device values.

### 2.9 Exercise energy: aerobic vs anaerobic, fat vs carbs
For each cardio session, `packages/domain` estimates the energy used, how much of it came from fat and how much from carbohydrate, and how much time was aerobic or anaerobic. It uses the session's heart rate and the physiology profile (§2.8). The result is stored on `workout_sessions.energy_estimate` as a snapshot that records its inputs and method version.

**Session inputs, best first**
1. HR samples (Phase 3, from Health Connect), giving time in each zone second by second.
2. Time in each zone, typed in from a watch summary.
3. Average HR only. The session is treated as steady state, and intervals get a wider range.
4. No HR. Energy comes from 2024 Compendium METs, and the split comes from RPE if you gave one; otherwise there is no split.

**Energy (kcal)**
- With HR, the Keytel et al. (2005) equation, which is more accurate when VO₂max is known. Without HR, METs × body weight × time.
- Always shown as a range.
- Not added to the calorie target. The activity level, and adaptive TDEE in Phase 2, already account for training, so adding it would count it twice.

**Fat vs carbohydrate**
- **Population curve (default):** HR becomes % of HR reserve, which tracks % of VO₂ reserve (Swain & Leutholtz, 1997). That is converted to % of VO₂max using your VO₂max and a resting VO₂ of 3.5 ml/kg/min, then to an RER from the cited `substrate_curves` table, and the RER gives the fat and carbohydrate shares of energy from the non-protein RQ table (Péronnet & Massicotte, 1991). Without a resting HR, % of max HR is used instead. The result is a range, because people differ a lot with training, diet, sex and time since eating.
- **Your own curve (Phase 3):** with a metabolic test, fat and carbohydrate oxidation per stage come from VO₂ and VCO₂ (Frayn, 1983; Jeukendrup & Wallis, 2005 for exercise), and the app interpolates by HR. FatMax and the crossover point are marked on your zones.
- RER at or above 1.0 counts as all carbohydrate. Above 1.0, the extra CO₂ comes from bicarbonate buffering during hard efforts, not from burning fuel.
- Shown in kcal and in grams.
- **Strength sessions** get a MET-based kcal estimate and are labelled as mainly anaerobic and carbohydrate-heavy, with no numeric split, because heart rate tracks energy use poorly in lifting.

**Aerobic vs anaerobic**
- Anaerobic time is time at or above the HR value of LT2 for the session's sport (or the all-sports value). The resolver supplies it from a lab test, which reports HR at each threshold, from LTHR, or from the LTHR estimate (§2.8). A threshold known only as pace or power is never applied to HR data; it splits a session only when that session has matching pace or power data for the same sport.
- When LT1 is known, sessions and weeks also show the low / moderate / high split used for polarized (80/20) training (Seiler, 2010).
- The labels describe intensity, not separate engines: both energy systems work at every intensity, and the copy says so.
- Phase 3 adds training load (TRIMP), split into aerobic and anaerobic parts, which feeds the strain and readiness scores.

**How it's presented**
- **Session card:** kcal range, a fat vs carb bar, aerobic vs anaerobic minutes, time in zones, and the inputs used.
- **Zones screen:** each zone's bpm range with its estimated fat/carb mix, and FatMax when it's known.
- **Train tab:** weekly totals.
- **Framing:** body-fat change comes from energy balance over days and weeks, not from which fuel a workout burned. Harder sessions burn a larger share of carbohydrate, but often more energy in total. Zone 2 time can be a goal for aerobic fitness, but it is never offered as a fat-loss tool.
- **Training-day fueling (Phase 2, off by default):** the day's carbohydrate target can rise with the planned session's estimated carbohydrate use, while the calorie target stays the same. Fat comes down to make room, but never below a minimum fat share (if it hits that floor, the carb increase is capped), and protein is unchanged. Session energy is never added to the calorie target, and AI meal plans follow the adjusted macros.

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
   - Also delivers the physiology profile (§2.8): RMR, DEXA, VO₂max, max and resting HR, LTHR, lab thresholds, FTP and HR zones entered by hand with source and date, and estimates for anything missing.
   - **Exit:**
     - The trend matches a test fixture.
     - A photo taken in airplane mode uploads later and only its owner can see it; another member gets a 403.
     - Entering a lab RMR or max HR changes calorie targets and zones at once, and deleting it falls back to the estimate.
     - Every estimator matches values computed by hand from its published equation.
2. **Workouts and progression**
   - Delivers: exercise import, locations, templates, schedule, live logger, PRs and e1RM, volume per muscle, cardio with HR zones, and progression suggestions.
   - Also delivers per-session and weekly aerobic/anaerobic and fat/carb estimates (§2.9).
   - **Exit:**
     - A 60-minute session logged offline, with the app killed mid-session, loses nothing and syncs.
     - Load suggestions match the progression tests.
     - Session energy estimates match golden fixtures for time-in-zone, average-HR and no-HR inputs, and editing zones later leaves past sessions unchanged.
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
- Guided field tests (§2.8), each behind a PAR-Q+ screen: 30-minute LTHR test, 20-minute FTP test, Cooper 12-minute run and Rockport 1-mile walk. The app also suggests adopting a higher max HR when a session records one.
- Training-day fueling (§2.9), off by default.
- `ai-workout-program` prescribes cardio in your own HR zones.

**Exit criteria**
- A 7-day plan for 2 people meets the macro tolerances every day and stays within budget.
- 100 generated programs (eval) contain no exercises outside the available equipment.
- Each guided field test computes its published formula exactly (golden fixtures), and its result replaces the estimate.
- A training-day fueling fixture: the adjusted carb, fat and protein targets add up to the unchanged calorie target within the macro tolerances, and fat stays at or above its floor.
- Adaptive TDEE recovers a known synthetic TDEE within ±100 kcal after 3 weeks.

### Phase 3: Health depth
**Deliverables**
- Health Connect sync (HealthKit comes with the Apple add-on), including HR samples for exact time in zone, and VO₂max and resting HR as device values.
- Vitals baselines and sleep.
- Labs: parse, confirm, trends, optimal ranges, retest reminders.
- DEXA, RMR and metabolic-test reports through the same parser. A metabolic test gives your own fat/carb curve, FatMax and crossover point (§2.9).
- Training load (TRIMP), split into aerobic and anaerobic parts.
- The full supplement module: regimens, refills, upper-limit and interaction checks.
- Mental check-ins and cognitive tests.
- Recovery heatmap and readiness score.

**Exit criteria**
- Importing the same 30 days of Health Connect data twice gives zero duplicates.
- A real lab PDF parses ≥95% correctly after review, and so does a real DEXA report.
- For a metabolic test, the computed fat oxidation at each stage is within 10% of the report's own figures.
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
| Estimates taken as measurements (max HR varies by about ±10 bpm between people; formula VO₂max and RMR can be well off; fat/carb splits vary widely) | "Estimated" badge with the formula named, ranges instead of single numbers, measured values override, retest flags, guided field tests in Phase 2 |
| Body-fat methods mixed on one trend line (DEXA vs smart scale vs Navy method), creating fake changes | `method` stored on every row, one series per method, DEXA scanner make recorded |
| The "fat-burning zone" read as the way to lose fat | Copy explains energy balance, the split is always shown next to total kcal, no fat-burn goals, AI prompts carry the same rule |
| Data loss (the Supabase free tier pauses projects and has no backups) | Supabase Pro or a nightly `pg_dump` before real data; test export and restore |
| Scope creep | Phase gates tied to exit criteria |

## 5. Verification strategy
1. **`packages/domain`:** Vitest plus fast-check property tests, including:
   - Every unit pair round-trips. A value entered in lb or in is shown exactly as typed.
   - A protein ratio round-trips between g/kg and g/lb, and switching units doesn't change the daily gram target beyond rounding.
   - Switching units never creates a PR.
   - The plate calculator handles lb, kg and mixed plate sets.
   - e1RM rises steadily with weight and reps.
   - The plate calculator stays within the plate inventory.
   - Adaptive TDEE converges on noisy synthetic data.
   - Goal cost equals the sum of its parts.
   - Each physiology estimator (Mifflin-St Jeor, Katch-McArdle, Tanaka, Nes, Cooper, Rockport, Keytel) matches values computed by hand from its paper.
   - The resolver always prefers pinned, then lab, field test, device or manual reading, and estimate, and deleting a measured value falls back to the next one. With only an estimated LTHR, zones come from HR reserve or % of max HR, never from Friel.
   - Zones are contiguous and ascending, and every bpm falls in exactly one zone.
   - With the medication flag set, no output depends on age-predicted max HR, and a pace- or power-only threshold is never applied to HR data.
   - Fat and carbohydrate add up to 100% of energy, the carbohydrate share never falls as intensity rises, and an RER of 1.0 or more gives all carbohydrate.
   - Editing zones or physiology values never changes a stored session estimate.
   - Golden CSV fixtures for trends, progression and session energy estimates.
2. **Database:** pgTAP through `supabase test db`. Tests cover A vs B access, member vs non-member, sharing grants, storage policies and RLS coverage. CI also runs Supabase advisors and `db lint`.
3. **Sync integration (Docker in CI):** local Supabase + PowerSync with two to three `@powersync/node` clients. Covers leak tests, offline write then reconnect, concurrent shopping-list check-offs, and quarantined errors.
4. **Edge Functions:** Deno tests with recorded Claude responses (no live calls in CI). Zod contract tests; validator and repair-loop tests with deliberately bad outputs.
5. **AI evals (`evals/`, nightly or manual):** label OCR per-field accuracy, lab-parse accuracy (including DEXA, RMR and metabolic-test reports), meal-plan tolerance pass rate, workout-program constraint violations. No regression allowed on prompt or model changes.
6. **Components:** jest-expo + React Native Testing Library for the set logger, food entry, OCR confirm screen, goal wizard and physiology entry form.
7. **Web E2E:** Playwright against the exported web build with seeded local Supabase, on Chromium, Firefox and **WebKit** (standing in for iPhone and Mac Safari users):
   - Log food.
   - Build a template and log sets.
   - Add weight → create a goal → check the cost estimate.
   - Enter a lab RMR and max HR → check the calorie target and zones change.
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
| Lab, DEXA or metabolic-test report PDF (~5 pages) | 10k / 3k | $0.10 |

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


## Sources
- Physiology estimates (§2.8): [Mifflin-St Jeor RMR (Mifflin et al., 1990)](https://doi.org/10.1093/ajcn/51.2.241) · Katch-McArdle RMR (McArdle, Katch & Katch, *Exercise Physiology*) · [Age-predicted max HR (Tanaka et al., 2001)](https://doi.org/10.1016/s0735-1097(00)01054-8) · [Non-exercise VO₂max, HUNT study (Nes et al., 2011)](https://doi.org/10.1249/MSS.0b013e31821d3f6f) · [Non-exercise VO₂max without resting HR (Jackson et al., 1990)](https://doi.org/10.1249/00005768-199012000-00021) · [Cooper 12-minute test (1968)](https://pubmed.ncbi.nlm.nih.gov/5694044/) · [Rockport 1-mile walk (Kline et al., 1987)](https://pubmed.ncbi.nlm.nih.gov/3600239/) · [HR reserve (Karvonen et al., 1957)](https://pubmed.ncbi.nlm.nih.gov/13470504/) · [Friel's LTHR zones](https://www.trainingpeaks.com/blog/joe-friel-s-quick-guide-to-setting-zones/) · [PAR-Q+](https://eparmedx.com/)
- Exercise energy (§2.9): [HR-based energy expenditure (Keytel et al., 2005)](https://doi.org/10.1080/02640410470001730089) · [2024 Adult Compendium of Physical Activities](https://doi.org/10.1016/j.jshs.2023.10.010) ([pacompendium.com](https://pacompendium.com/)) · [%HRR ≈ %VO₂R (Swain & Leutholtz, 1997)](https://doi.org/10.1097/00005768-199703000-00018) · [Non-protein RQ table (Péronnet & Massicotte, 1991)](https://pubmed.ncbi.nlm.nih.gov/1645211/) · [Substrate oxidation from gas exchange (Frayn, 1983)](https://doi.org/10.1152/jappl.1983.55.2.628) · [Substrate oxidation during exercise (Jeukendrup & Wallis, 2005)](https://doi.org/10.1055/s-2004-830512) · [FatMax (Achten et al., 2002)](https://doi.org/10.1097/00005768-200201000-00015) · [Crossover concept (Brooks & Mercier, 1994)](https://doi.org/10.1152/jappl.1994.76.6.2253) · [Polarized training (Seiler, 2010)](https://doi.org/10.1123/ijspp.5.3.276)
- Costs: [Supabase pricing](https://supabase.com/pricing) · [PowerSync pricing](https://www.powersync.com/pricing) · [Expo EAS pricing](https://expo.dev/pricing) · [Azure Artifact Signing pricing](https://azure.microsoft.com/en-ca/pricing/details/artifact-signing/) · [Terra pricing](https://tryterra.co/pricing) · [Claude vision token math](https://platform.claude.com/docs/en/build-with-claude/vision-coordinates)
