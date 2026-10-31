# Health360: implementation plan

Health360 is one app for planning and reaching health and fitness goals. It covers goals and cost estimates (food, supplements, gym, equipment), workout and cardio tracking with heart-rate zones and aerobic/anaerobic and fat/carb estimates, body composition, a physiology profile (RMR, DEXA, VO₂max, LTHR, max HR and HR zones from lab tests, or estimates when you don't have them), nutrition (macros and micronutrients) with nutrition-label OCR, labs and vitals, mental and cognitive tracking, progress photos, AI meal plans and workouts, and food and supplement shopping lists.

**Related docs**
- [FEATURES.md](FEATURES.md): the feature catalogue, module by module, with competitor sources.
- [adr/0002-dotnet-maui-blazor.md](adr/0002-dotnet-maui-blazor.md): the current stack, .NET MAUI + Blazor + ASP.NET Core, and the platform scope.
- [adr/0001-language-and-stack.md](adr/0001-language-and-stack.md): the earlier TypeScript/Expo decision (superseded) and the full language comparison.

## Decisions
- **Platforms:** Android and Windows desktop, both with .NET MAUI. **A web (browser) app, Apple (iPhone, Mac) and Linux desktop are out of scope** (§2.7).
- **Audience:** a household of 2–4 people first. It is built so it could go public later, so compliance and billing stay light for now.
- **MVP:** all four core modules: Nutrition with label OCR, Workouts with progression, Body metrics with photos, and Goals with cost planning.
- **Backend:** Supabase.
- **Stack:** C#/.NET: .NET MAUI Blazor Hybrid and an ASP.NET Core API, with PowerSync and Supabase ([ADR 0002](adr/0002-dotnet-maui-blazor.md)).
- **Units:** metric and imperial, stored as entered (§2.6).
- **Measured beats estimated:** lab and test values you enter (RMR, DEXA, VO₂max, LTHR, max HR, HR zones) replace formula estimates everywhere they are used, and every derived number shows which inputs it used (§2.8).

---

## 2. Architecture

Section 1 (the feature catalogue) is in [FEATURES.md](FEATURES.md), and section 2.0 (the language choice) is in [ADR 0002](adr/0002-dotnet-maui-blazor.md).

### 2.1 Stack
| Layer | Choice | Why |
|---|---|---|
| App | **.NET MAUI Blazor Hybrid** (current .NET LTS) for **Android and Windows**. Screens are Razor components in a shared Razor Class Library (`Health360.UI`) | One UI codebase for phone and desktop. Native APIs (camera, SQLite, Health Connect, notifications) through MAUI |
| Desktop | MAUI on **WinUI 3**, unpackaged, with a **Velopack** installer and auto-updater | Native Windows app with no wrapper; a signing certificate stays optional; file drag-and-drop for lab PDFs |
| Offline sync | **PowerSync .NET SDK** (Sync Streams, SQLite) in the MAUI app. Its client schema mirrors the Postgres tables | Offline logging on Android and Windows. Uploads go through the API or the Supabase C# client with the signed-in user's JWT, so database permissions still apply. Beta, so gated in Phase 0b (§4) |
| Backend | **Supabase**: Postgres + RLS, Auth (emailed 6-digit code in the MVP; Google later), Storage, pg_cron | Fits relational health data, per-user and per-household security, and private files |
| Server | **ASP.NET Core** minimal API (`Health360.Server`), deployed as its own small host. Validates the Supabase JWT (JWKS) and the signed-in user on every request and queries Postgres as that user | One C# domain library on client and server; the service role is used only for admin jobs |
| UI | Plain Razor components and semantic HTML styled by the in-house theme (`wwwroot/app.css`); virtualized lists (`Virtualize`). Charts are hand-written SVG Razor components, with the numbers computed in `Health360.Domain` ([ADR 0003](adr/0003-svg-charts.md)). A Razor component library is added only if a screen needs one, and recorded in an ADR | Works on Android and Windows from one codebase |
| State | PowerSync live queries for local data (MAUI); typed `HttpClient` calls to the API for online-only work (search, AI); scoped services for in-session UI state such as the active workout | Each tool does one job |
| Forms and validation | C# records + **FluentValidation** in `Health360.Contracts` | The same contracts check forms, API payloads and the JSON Claude returns; Claude's JSON Schemas are generated from the same types |
| Device | `BarcodeScanning.Native.Maui` (barcodes on Android and Windows), MAUI MediaPicker, on-device resize and EXIF stripping, local notifications on Android and Windows, biometric app lock | |
| Health (P3) | Health Connect through **Shiny.Health** | Reads directly on the device |
| AI | The official `Anthropic` NuGet package, in the API only | The API key never reaches a client |
| Ops | Sentry .NET with PII scrubbing; GitHub Actions (Linux runners, plus a Windows runner for the Windows build); Supabase Pro (backups) or a nightly `pg_dump` | Health values must never land in error reports |

### 2.2 Repository layout
```
Health360/
  Health360.slnx            Health360.Core.slnf: everything except the MAUI app
  src/
    Health360.Domain/       pure C#, no I/O: units, ids (UUIDv7), energy/TDEE (formula + adaptive),
                            macros, recipe math, portion solver, DRI/UL checks, e1RM, progression,
                            plates, volume, cost math, milestones, goal feasibility, streaks,
                            physiology (estimators, best-value resolver, zones), exercise energy
                            (kcal, fat/carb, aerobic/anaerobic), validators for AI output
    Health360.Contracts/    records + validators: entities, API contracts, AI output schemas
    Health360.UI/           Razor Class Library: pages (today, food, train, body, plan, settings),
                            features (nutrition, workouts, body, physiology, goals, costs, shopping,
                            labs, mental), charts, theme, i18n, platform interfaces
    Health360.LocalDb/      offline database: PowerSync client schema, upload connector with
                            permanent-error quarantine, account binding (no MAUI dependency, so
                            it is tested on every OS)
    Health360.Maui/         MAUI Blazor Hybrid (Android, Windows): the platform SQLite for LocalDb,
                            attachments, platform services (health, camera, files, secure storage,
                            notifications)
    Health360.Server/       ASP.NET Core host for the API: endpoints, auth, Claude client, quotas, jobs: food-search,
                            barcode-lookup, ai-label-ocr, ai-meal-plan, ai-workout-program,
                            ai-weekly-checkin, ai-meal-photo, ai-lab-parse, ai-receipt,
                            household-invite, account-delete, data-export
  tests/                    xUnit + FsCheck (domain), bUnit (components), API integration,
                            two-client sync
  supabase/
    migrations/             SQL schema, RLS, helper functions, triggers, storage policies
    tests/                  pgTAP RLS + storage tests, RLS-coverage test
  powersync/                sync-config.yaml (Sync Streams), docker-compose.yml (local)
  tools/importers/          USDA FDC, free-exercise-db, DRI tables, biomarkers, NIH DSLD,
                            Compendium METs, population RER-by-intensity table
  tools/drift/              schema-drift check (Postgres vs the PowerSync client schema and contracts)
  evals/                    label-ocr/ lab-parse/ (incl. DEXA, RMR, metabolic tests) meal-plan/
                            workout-program/ (fixtures + runners)
  e2e/                      maestro/ windows/
  docs/                     PLAN.md FEATURES.md adr/
```
The Phase 0 TypeScript prototype (`apps/`, `packages/`, `supabase/functions/`, `tools/codegen/`, `e2e/sync/`) was removed on 2026-10-31, after the PowerSync .NET gate passed; it remains in the git history. `supabase/migrations`, `supabase/tests` and `powersync/` carried over unchanged. Phase 0b status is in [phase0b.md](phase0b.md).

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
| Reference (G) | `nutrients` · `nutrient_dri` (sex, age band, rda/ai/ul) · `metric_definitions` (code; sharing category `body` or `physiology`; entry units and canonical unit; definitional limits only, such as 0–100 for a percentage, no clinical ranges) · `exercises` (system rows + household custom; tracking_type, equipment, muscles[]) · `biomarkers` (loinc, unit conversions, retest_days) · `biomarker_ranges` (reference/optimal, citation) · `interaction_rules` · `activity_mets` (2024 Compendium code, activity, MET) · `substrate_curves` (population RER by % VO₂max and training status, citation) |
| Nutrition | `foods`: global catalog rows are searched online and not synced; custom rows are H. Columns: source, barcode, macro columns per 100 g, `micros_100g`, verified · `food_portions` · `recipes`, `recipe_ingredients` (H) · `saved_meals` (U) · `food_log_entries` (U; nutrient **snapshot**, entry_method, is_estimate, cost_minor) · `nutrition_targets` (U; method formula/manual/adaptive; protein basis body_weight/lean_mass, protein ratio + unit g/kg or g/lb + canonical g/kg) · `label_scans` (U; image_path, extracted, checks, food_id) |
| Workouts | `training_locations` (H; equipment_tags, plate_inventory with a unit per plate, bar_weight + bar_unit) · `programs` · `workout_templates` · `template_exercises` (set_scheme, rest, progression) · `scheduled_workouts` · `workout_sessions` (avg_hr, max_hr, `zone_seconds` jsonb, `energy_estimate` jsonb snapshot: kcal range, fat/carb kcal and g, aerobic/anaerobic minutes, inputs and method version) · `session_exercises` · `set_logs` (set_type, reps, weight + weight_unit + weight_kg, rpe/rir, duration, distance + distance_unit + distance_m, e1rm_kg, is_pr) · `personal_records` (all U) |
| Body | `measurements` (U; metric_code, value + unit + value_canonical, measured_at, local_date, group_id, source lab/field_test/device/manual, method, and `category` copied from the metric so RLS needs no join: weight, body fat, circumferences, BP, HR, HRV, SpO₂, glucose, steps, plus RMR, VO₂max, max HR, LTHR, thresholds, FTP and the DEXA metrics) · `measurement_groups` (U; kind manual/dexa/rmr/metabolic_test/lactate_test/field_test, provider, device make, protocol, report_path under the owner's profile id; deleting a group deletes its measurements) · `exercise_test_stages` (U, Phase 3; group_id, stage, HR, speed or power, VO₂, VCO₂, RER, lactate) · `zone_sets` (U; sport, metric hr/power/pace, basis custom/lab/lthr/hrr/max_hr, boundaries + unit + boundaries_canonical, effective_from, optional group_id of the lab test. Boundaries are where each zone starts, from zone 1 up, 2–10 zones, the last open-ended. They ascend for bpm and W, where zone i holds values ≥ its boundary and below the next. They descend for pace in seconds per km or mile, where zone i holds paces ≤ its boundary and slower than the next, so a pace slower than the first boundary is below zone 1) · `progress_photos` (U; pose, storage_path, thumb_path; never shared by default) · `checkins` (weekly adaptive TDEE) · `sleep_sessions` (P3) |
| Goals and costs | `goals` (U/H; type, metric/exercise/biomarker, start/target, rate, plan) · `goal_milestones` · `goal_cost_estimates` (breakdown, assumptions) · `stores` · `products` (food/supplement/equipment) · `price_observations` (price_minor, package qty, observed_on, source) · `cost_items` (gym/equipment/supplements/food/coaching, recurrence) · `equipment` · `budgets` · `shopping_lists`, `shopping_list_items` (all H) |
| Later | `meal_plans`/`meal_plan_entries` (H) · `supplement_products`/`_ingredients` (G/H) · `supplement_regimens`/`_intakes` (U) · `lab_reports`/`lab_results` (U; raw + canonical value, flag, confirmed) · `retest_reminders` · `mental_checkins` · `cognitive_tests` · `habits`/`habit_logs` · `reminders` · `pantry_items`, `expenses` (H) · `hr_series` (U; per-session HR samples from Health Connect, compressed) · `device_connections` (S; tokens in Vault) · `ai_generations` (written by the server, the owner can read; feature, model, prompt_version, tokens, cost, validation, accepted) |

**RLS and sync**
- **Helper functions:** `private.my_profile_ids()` and `private.my_household_ids()`, both SECURITY DEFINER and STABLE.
- **Policies:**
  - U: read if it's my profile or a grant allows it; write only my own profiles.
    - Body tables (step 1.1): `measurements` are read through the grant for their metric's category (`body` or `physiology`), `zone_sets` through `physiology`, and `measurement_groups` through `body` for kind `manual` and `physiology` for every test or scan kind.
  - H: read and write if I'm a member of the household.
- **Storage:** private buckets `progress-photos`, `lab-reports` and `scans`. The first path segment is the profile id, files are read through short-lived signed URLs, and `scans` files expire after 30 days.
- **Sync Streams:**
  - `me`: U tables, last 24 months, plus the newest physiology value per metric and source tier at any age (§2.8); older history is fetched on demand.
    - **For now (Phase 1), `me` syncs all of the user's authorized rows** with no window: measurements, measurement groups, zone sets and photo metadata. Photo files are not synced rows; they follow their own download and cache rule. The window must be in place before Phase 3's high-volume imports (Health Connect, HR samples) or a public release. It must keep pinned measurements and current winners at any age, along with their groups, so deleting a value still falls back correctly. It must also keep enough history, or a stored starting value, that the weight trend doesn't change when old rows drop out.
  - `household`: H tables.
  - `reference`: G tables.
  - `shared_profile` (on demand): **must** check `sharing_grants`.
- Delete triggers or functions remove the matching Storage files.

### 2.4 AI layer
- **Where it runs:** the ASP.NET Core API only. `ANTHROPIC_API_KEY` is kept in the host's secret store and never reaches the MAUI app.
- **Model:** `claude-opus-5-5` ($4 / $20 per million input/output tokens), configurable per function. You can trial cheaper models (`claude-sonnet-5-5`, `claude-haiku-5-5`) for high-volume OCR. Switch only if the eval set shows no accuracy drop.
- **Effort:**
  - `low` for extraction (labels, labs, receipts).
  - `medium` or `high` for meal plans and programs.
  - Effort is the only control on Opus 5.5, because thinking can't be turned off.
- **Request rules:**
  - Structured output with JSON schemas generated from the `Health360.Contracts` types; the result is deserialized and validated again on the server.
  - Don't send `temperature`, `top_p` or `top_k`.
  - Check `stop_reason` for `refusal` and `max_tokens` before parsing.
  - Turn on server-side refusal fallback: `fallbacks: "default"` with beta header `server-side-fallback-2026-07-01`.
  - Send lab PDFs as `document` blocks.
- **The model proposes, code checks.**
  - Every result goes through a validator in `Health360.Domain`: label arithmetic, macro tolerances, budget, exercise IDs and equipment, sets per muscle.
  - If validation fails, the errors go back to the model for one repair round; after that the request fails and you get a manual fallback.
  - The AI never writes numbers that code can compute. For example, the weekly check-in only describes the computed TDEE.
  - Prompts get each physiology value with its source (lab, field test, device, manual or estimate, §2.8), so the model never presents an estimate as a measurement.
- **Cost:**
  - Resize images on the device and cache the fixed prompt prefix.
  - Run weekly jobs through the **Batches API** (50% cheaper) from a hosted background service in the API (or pg_cron calling an API endpoint).
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
- **One conversion module.** `Health360.Domain/Units` uses exact factors:
  - 1 lb = 0.45359237 kg
  - 1 in = 2.54 cm
  - 1 mi = 1609.344 m
  - 1 oz = 28.349523125 g
  - 1 US fl oz = 29.5735295625 ml
  - 1 kcal = 4.184 kJ
  - 1 g/lb = 1 / 0.45359237 g/kg (from the lb factor), for protein targets
  - Per-analyte factors for glucose and lab values, kept in the `biomarkers.unit_conversions` table

  Canonical values are computed on write. A Postgres trigger recomputes them, so client and server can't drift. For `measurements`, `zone_sets` and `profiles.height_cm` (through `private.length_to_cm`), `private.convert_unit` mirrors `Health360.Domain/Units` (same ratios, same evaluation order, pace in seconds per km or mile), and `CanonicalUnitParityTests` checks it against Domain bit for bit. Devices receive `double precision` columns from PowerSync rounded to 15 significant digits (found by the two-client sync test), so on the device canonical values match the server's within about 1e-15 relative, not bit for bit; comparisons already use a tolerance. jsonb numbers (zone boundaries) arrive exact, because the trigger writes them in shortest exact form.
- **Mixed-unit history:** charts convert every point to your current display unit. PR comparisons use the canonical value with a small tolerance, so switching units never creates fake PRs.
- **Training:**
  - Progression increments are set in your lifting unit (e.g. +5 lb or +2.5 kg).
  - The plate calculator works with lb plates, kg plates or a mix, and rounds suggestions to loads you can actually build.
- **Food:**
  - Portions can be g, oz, ml, fl oz, cups, tbsp, tsp, pieces or servings; each one is converted to grams through `food_portions` or a density value.
  - Label OCR keeps the serving size exactly as printed on the label, both the household measure and the metric amount.
- **AI:** prompts get values in your display units with the units spelled out, and structured outputs must include units. Validators convert to canonical values before checking.
- **Physiology values** (§2.8) use fixed scientific units, not preferences: HR in bpm, VO₂max in ml/kg/min (the absolute L/min and the test-day weight are kept too when a report gives them), lactate in mmol/L, BMD in g/cm². RMR and exercise energy follow your energy unit, and DEXA masses follow your body-weight unit.

### 2.7 Supported platforms
| Platform | App | Offline | Notes |
|---|---|---|---|
| **Android** | .NET MAUI (Blazor Hybrid) | **Yes**: PowerSync .NET + SQLite; logging survives the app being killed | Health Connect (P3), camera and barcodes, local notifications, Google Play internal testing or a direct APK |
| **Windows** | .NET MAUI on WinUI 3 | **Yes**: same as Android | Velopack installer with auto-update; drag-and-drop for lab PDFs; signing optional for family use |

**Not supported:** a web (browser) app, iPhone, iPad and Mac apps, HealthKit and Apple Watch data, Sign in with Apple, and Linux desktop. Adding any of these needs a new ADR.

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
| LT1/LT2 or VT1/VT2 (HR, pace or power) | Lab lactate or gas-exchange test | LT2 is taken as LTHR, except when setting zones (Friel puts the two at different zone borders). LT1 is not estimated |
| FTP (W), threshold pace | 20-minute test (95% of average power), recent race | Not estimated; power and pace zones stay off |
| HR zones (per sport) | Your own boundaries, or a lab report | Derived, best basis first: lab thresholds, a measured or test-derived LTHR, HR reserve (Karvonen), % of max HR. Run and bike use Friel's 2025 zones; other sports use Seiler and Garber ([calculations.md](calculations.md)). The LTHR estimate never sets zones, and LT2 alone doesn't stand in for LTHR when setting zones |
| Metabolic test: VO₂, VCO₂, RER, lactate per stage; FatMax and crossover point | Gas-exchange test with stages | Per-stage values are not estimated. Without a test, the fat/carb split uses the population curve (§2.9), and FatMax and the crossover point are shown only from a test |

**How the app picks a value** (`Health360.Domain/Physiology`)
- A resolver returns, for each value, the number in use plus its source, method, date, confidence and the alternatives it passed over.
- Order: a source you pinned, then lab, then field test, then a device or manual reading, then estimate. Within a tier, the newest value wins.
- Old measured values still beat formulas (a year-old lab VO₂max is usually closer than an age formula), but they get a "retest?" flag. VO₂max, LTHR and DEXA are flagged after 12 months, and measured RMR after 12 months or a 5% change in body weight. In Phase 3 these flags become `retest_reminders`.
- The 24-month sync window (§2.3; not in place yet, so all rows sync for now) doesn't apply to resolver inputs. A trigger marks the newest `measurements` row per metric and source tier, and marked rows sync at any age with their `measurement_groups` and `exercise_test_stages` rows. So an old lab value still resolves offline on a newly set-up device.
- Estimates are computed when needed and not stored, so they follow changes in age, weight and resting HR. Measured values are `measurements` rows with `source` and `method`. One DEXA scan or lab visit is one `measurement_groups` row, and its stage table goes in `exercise_test_stages` (Phase 3).
- The exact formulas, the resolver's pin rules and the activity-level mapping are specified with their sources in [calculations.md](calculations.md).
- Entering or deleting a measured value updates everything that uses it from then on. Past session estimates (§2.9) are snapshots; they change only if you choose "recalculate history".
- Every derived number has an info view that lists its inputs, for example "RMR 1,720 kcal (lab, Mar 2026) × activity 1.55".

**Where the values are used**
- **Calorie targets:** TDEE = RMR × PAL, with a measured RMR replacing the formula. The five activity levels map onto the FAO/WHO/UNU (2004) PAL bands ([calculations.md](calculations.md)), and TDEE is always labelled an estimate, even from a measured RMR. Adaptive TDEE (Phase 2) takes over once there is enough weight and intake data.
- **Protein:** once DEXA gives fat-free mass, protein can be set per kg or per lb of lean mass instead of body weight.
- **Body-composition trends:** each method is its own series (DEXA, smart scale, Navy method), because methods disagree by several percentage points and mixing them creates fake changes.
- **Goals:** VO₂max, lean-mass and visceral-fat targets are goal types.
- **Cardio:** zones, session energy and the fat/carb split (§2.9).
- **AI:** prompts receive each value with its source (§2.4).

**Entry by phase:** by hand in the MVP. Guided field tests come in Phase 2. In Phase 3, DEXA, RMR and metabolic-test PDFs go through `ai-lab-parse` with the same confirm screen as labs, and Health Connect adds VO₂max, resting HR and HR samples as device values.

### 2.9 Exercise energy: aerobic vs anaerobic, fat vs carbs
For each cardio session, `Health360.Domain` estimates the energy used, how much of it came from fat and how much from carbohydrate, and how much time was aerobic or anaerobic. It uses the session's heart rate and the physiology profile (§2.8). The result is stored on `workout_sessions.energy_estimate` as a snapshot that records its inputs and method version.

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

### Phase 0: Foundation and risk tests (TypeScript prototype)
Done on the earlier TypeScript stack; status and findings are in [phase0.md](phase0.md). What carries over to .NET: the Supabase identity and household migrations, RLS helpers and pgTAP RLS-coverage test, the PowerSync `me`, `household` and `reference` streams, the `failed_uploads` quarantine design, client-generated UUIDv7 ids, the exact unit factors and their property tests (as a specification to port), and the two-client leak test design.

### Phase 0b: .NET foundation
**Complete** (2026-10-31): every exit criterion below is met; status and findings are in [phase0b.md](phase0b.md).

**Deliverables**
- The solution in §2.2, with CI: `dotnet build`, `dotnet test`, formatting and analyzers, `supabase test db`, and the schema-drift check.
- `Health360.Domain/Units` and UUIDv7 ids ported, with the same exact factors and FsCheck property tests. It comes first because every module depends on it.
- `Health360.Server`: Supabase JWT and signed-in-user validation on every endpoint, queries as the signed-in user, and one test Claude call with structured output validated against `Health360.Contracts`. The call is tested against recorded responses; the first run against the live API is deferred to Phase 1's first AI endpoint (`ai-label-ocr`).
- MAUI app on Android and Windows: tab navigation, login by emailed code, onboarding with per-type unit preferences and locale defaults, PowerSync .NET with the existing streams, and the `failed_uploads` quarantine.
- A throwaway `notes` table on **both targets**: the Android app and the Windows app.

**Exit criteria**
- Android and Windows: sign in, create a note offline, reconnect, and see it on the other device within about 5 seconds; the note survives killing the app while offline.
- User B never receives A's note; pgTAP and a two-client sync test both prove this.
- An API request with the anon key, an expired token or another user's ids is rejected or sees no data (integration tests).
- CI is green.
- **PowerSync .NET gate:** if the beta SDK fails these criteria, the fallback (a SQLite outbox uploaded through the API plus a pull of changed rows) is recorded in an ADR before Phase 1.
- The TypeScript prototype is removed once the gate passes. **Done** (2026-10-31).

### Phase 1: MVP
1. **Body and photos**
   - Delivers: metric definitions, weight trend, measurements, body-fat estimate and vitals, plus progress photos with overlay, offline upload queue and comparison view.
   - Also delivers the physiology profile (§2.8): RMR, DEXA, VO₂max, max and resting HR, LTHR, lab thresholds, FTP and HR zones entered by hand with source and date, and estimates for anything missing.
   - **Exit:**
     - The trend matches a test fixture.
     - A photo taken in airplane mode (Android or Windows) uploads later and only its owner can see it; another member gets a 403.
     - Entering a lab RMR changes the physiology profile's maintenance estimate (resolved RMR × PAL) at once, showing its inputs and source. Deleting it falls back to the next measured value or the estimate. Nutrition targets built on this are step 3's exit.
     - Entering a measured max HR changes the zones that use it at once, and deleting it falls back the same way.
     - Every estimator matches values computed by hand from its published equation (golden values in [calculations.md](calculations.md)).
   - Per-stage metabolic-test data (`exercise_test_stages`, its editor and FatMax/crossover calculations) waits for Phase 3; summary values are entered by hand now.
   - Photo deletion is done by the app through the Storage API, never by deleting `storage.objects` rows in SQL (that leaves the file behind):
     - The row is deleted first, through the normal upload queue, and then the photo and thumbnail files. The queue is bound to the owning account, survives restarts, cancels pending uploads and treats an already-deleted file as success.
     - Other devices drop their cached copies when the row disappears.
     - Only the owner may delete. Photo tests run with Storage enabled in CI.
2. **Workouts and progression**
   - Delivers: exercise import, locations, templates, schedule, live logger, PRs and e1RM, volume per muscle, cardio with HR zones, and progression suggestions.
   - Also delivers per-session and weekly aerobic/anaerobic and fat/carb estimates (§2.9).
   - **Exit:**
     - A 60-minute session logged offline on Android or Windows, with the app killed mid-session, loses nothing and syncs.
     - Load suggestions match the progression tests.
     - Session energy estimates match golden fixtures for time-in-zone, average-HR and no-HR inputs, and editing zones later leaves past sessions unchanged.
3. **Nutrition and label OCR**
   - Delivers: USDA import, food search, the barcode lookup chain, `ai-label-ocr` with checks and a confirm screen, food logging, recipes, the micronutrient panel, and formula targets.
   - Also the first Claude call against the live API (deferred from Phase 0b), which needs an Anthropic API key in the server's secrets.
   - **Exit:**
     - ≥90% exact match on serving size, kcal, protein, carbs and fat across a **50+ photo eval set from your own pantry**, measured before any user edits.
     - 18 of 20 common barcodes resolve without OCR.
     - A full day logged offline on Android syncs.
     - Recipe nutrition matches a hand calculation.
     - Formula nutrition targets use the same maintenance calculation as step 1. An integration test shows them following a lab RMR being entered and deleted, including stored targets if targets are stored. A manual target stays an explicit override.
4. **Goals and cost planning**
   - Delivers: the goal wizard (feasibility, milestones, targets), price book, cost items, supplement cost entries, per-goal and monthly estimates, budgets, shared shopping lists, and the Today dashboard.
   - **Exit:**
     - "Lose 6 kg by date X" produces the right deficit, a warning when the pace is too fast, and milestones.
     - The monthly estimate matches a spreadsheet to the cent.
     - Milestones tick off automatically.
5. **Ship to the household**
   - Desktop: the Windows app as a Velopack installer (signing optional), plus the auto-updater.
   - Phones: Android through the Play internal testing track (or a direct APK).
   - Server: the ASP.NET Core API deployed. Reminders use local notifications on Android and Windows.
   - Also: consent screens, export, account deletion, Sentry with scrubbing, and backups.
   - A server cleanup job (an admin job using the service role) removes Storage files with no matching row, through the Storage API. It skips files newer than a grace period, because a photo taken offline may upload its file before its row syncs. Account deletion relies on it, since nothing on the device can clean up after the app is uninstalled.
   - **Phase 1 exit:** 2–3 household members use it daily for 2 weeks on phone and desktop, with zero lost entries, zero leaks between users, and every suite green.

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
- Health Connect sync, including HR samples for exact time in zone, and VO₂max and resting HR as device values.
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

---

## 4. Key risks and mitigations
| Risk | Mitigation |
|---|---|
| Database rules (RLS) and sync rules drift apart, so data leaks between users | Ownership columns on every row (no joins in rules). Grant checks in parameterized streams. Two-client leak tests and pgTAP in CI on every migration |
| One bad upload blocks PowerSync's write queue | Sort errors by Postgres code. Permanent failures go to `failed_uploads`, with a retry/discard screen and a Sentry alert |
| Schema drift between Postgres, the PowerSync client schema and the C# contracts | Migrations are additive only. `tools/drift` compares them against the migrated database in CI |
| The PowerSync .NET SDK is beta (1.0.0, still labelled beta) | Phase 0b gate on Android and Windows (offline write, kill, reconnect, leak test); pinned version; fallback design (SQLite outbox through the API) recorded in an ADR if it fails |
| .NET MAUI quality issues on Android or Windows | Pinned .NET and MAUI workload versions, upgrades in their own PRs, smoke tests on both platforms on every main-branch build |
| The ASP.NET Core API is a new attack surface between clients and the database | JWT validated against Supabase's keys and the signed-in user checked on every request; queries run as that user so RLS applies; the service role only in admin jobs; rate limits and quotas; negative tests for anon keys, expired tokens and other users' ids |
| Health Connect needs a Play health-permissions declaration and a privacy policy | Dev builds from day 1; sign Phase 3 off on a physical Android phone |
| No over-the-air hot-fixes | Small releases; Windows auto-update; Play internal track for fast Android rollout; API fixes ship by redeploying the API |
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
1. **`Health360.Domain`:** xUnit plus FsCheck property tests, including:
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
3. **Sync integration (Docker in CI):** local Supabase + PowerSync with two to three PowerSync .NET clients (the existing `@powersync/node` test stays until they replace it). Covers leak tests, offline write then reconnect, concurrent shopping-list check-offs, and quarantined errors.
4. **API:** integration tests (`WebApplicationFactory`) against local Supabase with recorded Claude responses (no live calls in CI). Contract tests; auth tests (anon key, expired token, another user's ids); validator and repair-loop tests with deliberately bad outputs.
5. **AI evals (`evals/`, nightly or manual):** label OCR per-field accuracy, lab-parse accuracy (including DEXA, RMR and metabolic-test reports), meal-plan tolerance pass rate, workout-program constraint violations. No regression allowed on prompt or model changes.
6. **Components:** bUnit for the set logger, food entry, OCR confirm screen, goal wizard and physiology entry form.
7. **Mobile E2E:** Maestro on the Android emulator, nightly:
   - Log food.
   - Build a template and log sets.
   - Add weight → create a goal → check the cost estimate.
   - Enter a lab RMR and max HR → check the calorie target and zones change.
   - Airplane mode: log offline, then sync; kill the app mid-workout and lose nothing.
8. **Desktop:** Windows smoke tests (Appium with the Windows driver, or FlaUI) on a Windows runner: install, sign in, offline write then sync, auto-update.
9. **Manual device checklist each phase:**
    - Android: Health Connect permissions, barcode and label capture in poor light, notifications.
    - A Windows PC: installer and auto-update, offline logging, drag-and-drop of a lab PDF, notifications.
10. **CI order:**
    - Every PR: `dotnet build` with analyzers, `dotnet format --verify-no-changes`, unit and component tests, API tests, pgTAP, drift check, sync tests.
    - Main branch: Android (AAB/APK) and Windows (Velopack) builds, Windows smoke tests, API deploy.
    - Nightly: Maestro and AI evals.

## 6. Running costs (list prices checked Oct 2026; USD)

### 6.1 Price list per service
| Service | What it's for | Free tier | Paid tier that matters |
|---|---|---|---|
| **Supabase** | Postgres, Auth, Storage, pg_cron | $0. Paused after 1 week inactive, **no backups**, 500 MB database, 1 GB files | **Pro $25/mo**: never paused, daily backups (7 days), 8 GB database, 100 GB files, 250 GB egress, 2M function calls, and $10 compute credit covering one Micro instance. Each extra project ~$10/mo compute. Small compute $15, Medium $60. PITR $100/mo (not needed) |
| **PowerSync** | Offline sync | $0. 2 GB synced/mo, 50 concurrent clients, 500 MB hosted; **deactivated after about 1–2 weeks unused** | **Pro $49/mo**: 30 GB synced, 1,000 clients. Or **self-host the free Open Edition** on a ~$5–10/mo VPS (you run it yourself) |
| **Claude API** (`claude-opus-5-5`) | OCR, meal plans, programs, coach | none | $4 / $20 per million input/output tokens, cache reads $0.20/M, Batches 50% off (see 6.2) |
| **ASP.NET Core host** | The API | Runs locally while building | A small always-on container or VPS, **~$5–15/mo** |
| **Google Play** | Android distribution | none | **$25 one-time** |
| **Windows code signing** | No SmartScreen warning on install | skip it for family use (click through the warning) | Azure Artifact Signing Basic **$9.99/mo** (optional) |
| Login emails | Emailed sign-in codes (Supabase's built-in email is rate-limited and meant for testing) | Free tier of a transactional email service | AWS SES ~$0.10 per 1,000 emails |
| Sentry | Error reports | Developer plan: $0 | Team ~$26/mo (public launch only) |
| USDA FoodData Central, Open Food Facts | Food data | $0 (API key, rate limits) | — |
| GitHub Actions | CI and the Android and Windows builds | 2,000 private-repo minutes/mo; Linux runners for most jobs | Windows runner minutes count 2× (Windows build and smoke tests, main branch only) |
| Domain | `health360.<tld>` | — | ~$10–20/yr |
| Terra (wearables) | Garmin, Oura, WHOOP | — | **$499/mo, so skipped.** Most wearables already sync into Health Connect, which the app reads for free. Open Wearables (MIT) is the free self-hosted fallback |

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
| **Building (Phases 0–1)** | Supabase Free (plus `supabase start` locally), PowerSync Free, the ASP.NET Core host run locally, Google $25 once | **~$10–30/mo**, which is Claude for development and OCR eval runs. Also $25 one-time for Google, and roughly $25 once for repeated runs of the 50-photo eval |
| **Household in daily use (2–4 people), recommended** | Supabase Pro, PowerSync Free, a small host, domain, no Windows signing | **~$40–70/mo** (~$480–840/yr): Supabase $25 + host ~$5–15 + domain ~$1 + Claude ~$8–25 (Opus 5.5) |
| Same, with sync that's never deactivated | + PowerSync Pro $49, or self-hosted ~$5–10 | ~$50–115/mo |
| Same, with signed Windows installer | + $9.99 | +$10/mo |
| **Public launch (~1,000 monthly users)** | Supabase Pro + Small compute, PowerSync Pro, a larger host (~$30–60), Sentry Team, Windows signing | **~$170–230/mo infrastructure, plus Claude at $1–6 per AI-active user** (~$500–3,000+/mo). AI becomes the main cost, so it needs a paywall or per-user quotas. Also: RevenueCat is free under $2.5k monthly revenue, then 1%; Google takes 15% on small-business subscriptions in the Android app; privacy policy and terms cost $0–500 one-time |

**Cost levers, in order of savings:**
1. Per-user AI quotas, already in the plan.
2. Haiku for the extraction routes, if the eval allows it.
3. Batches API for anything not urgent.
4. Prompt caching and image resizing.
5. Self-hosting PowerSync.
6. Skipping Windows signing for family use.
7. One production Supabase project, with development on local Docker or a free organization.

## 7. First files to create (Phase 0b)
Already in place and carried over: `supabase/migrations/` (identity, households, sharing grants, `private.*` helpers and the RLS pattern later tables copy), `supabase/tests/database/` and `powersync/sync-config.yaml`.
- `src/Health360.Domain/Units/`: the exact conversion factors and their property tests, ported first.
- `src/Health360.Server/Auth/`: Supabase JWT and signed-in-user validation, and a database connection that runs as that user.
- `src/Health360.LocalDb/` (with the platform SQLite in `src/Health360.Maui/Data/`): the PowerSync .NET schema (mirroring the synced Postgres tables), the upload connector and the `failed_uploads` quarantine.
- `src/Health360.UI/`: the shared shell, tabs and sign-in flow.
- `tools/drift/`: the schema-drift check against a migrated database.


## Sources
- Physiology estimates (§2.8): [Mifflin-St Jeor RMR (Mifflin et al., 1990)](https://doi.org/10.1093/ajcn/51.2.241) · Katch-McArdle RMR (McArdle, Katch & Katch, *Exercise Physiology*) · [Age-predicted max HR (Tanaka et al., 2001)](https://doi.org/10.1016/s0735-1097(00)01054-8) · [Non-exercise VO₂max, HUNT study (Nes et al., 2011)](https://doi.org/10.1249/MSS.0b013e31821d3f6f) · [Non-exercise VO₂max without resting HR (Jackson et al., 1990)](https://doi.org/10.1249/00005768-199012000-00021) · [Cooper 12-minute test (1968)](https://pubmed.ncbi.nlm.nih.gov/5694044/) · [Rockport 1-mile walk (Kline et al., 1987)](https://pubmed.ncbi.nlm.nih.gov/3600239/) · [HR reserve (Karvonen et al., 1957)](https://pubmed.ncbi.nlm.nih.gov/13470504/) · [Friel's LTHR zones](https://www.trainingpeaks.com/blog/joe-friel-s-quick-guide-to-setting-zones/) · [PAR-Q+](https://eparmedx.com/)
- Body composition and activity (§2.8, [calculations.md](calculations.md)): Navy circumference method: Hodgdon & Beckett (1984), [NHRC Report 84-11, men](https://www.dtic.mil/docs/citations/ADA143890) and [NHRC Report 84-29, women](https://doi.org/10.21236/ADA146456) · Siri, W. E. (1961), *Body composition from fluid spaces and density* · [FAO/WHO/UNU (2004), *Human energy requirements*, §5.3, Table 5.3](https://www.fao.org/4/y5686e/y5686e07.htm) · [Weight trend: Walker, *The Hacker's Diet*](https://www.fourmilab.ch/hackdiet/www/subsubsection1_4_1_0_8_3.html)
- Exercise energy (§2.9): [HR-based energy expenditure (Keytel et al., 2005)](https://doi.org/10.1080/02640410470001730089) · [2024 Adult Compendium of Physical Activities](https://doi.org/10.1016/j.jshs.2023.10.010) ([pacompendium.com](https://pacompendium.com/)) · [%HRR ≈ %VO₂R (Swain & Leutholtz, 1997)](https://doi.org/10.1097/00005768-199703000-00018) · [Non-protein RQ table (Péronnet & Massicotte, 1991)](https://pubmed.ncbi.nlm.nih.gov/1645211/) · [Substrate oxidation from gas exchange (Frayn, 1983)](https://doi.org/10.1152/jappl.1983.55.2.628) · [Substrate oxidation during exercise (Jeukendrup & Wallis, 2005)](https://doi.org/10.1055/s-2004-830512) · [FatMax (Achten et al., 2002)](https://doi.org/10.1097/00005768-200201000-00015) · [Crossover concept (Brooks & Mercier, 1994)](https://doi.org/10.1152/jappl.1994.76.6.2253) · [Polarized training (Seiler, 2010)](https://doi.org/10.1123/ijspp.5.3.276)
- Platforms and stack: [ADR 0002](adr/0002-dotnet-maui-blazor.md) and its sources.
- Costs: [Supabase pricing](https://supabase.com/pricing) · [PowerSync pricing](https://www.powersync.com/pricing) · [Azure Artifact Signing pricing](https://azure.microsoft.com/en-ca/pricing/details/artifact-signing/) · [Terra pricing](https://tryterra.co/pricing) · [Claude vision token math](https://platform.claude.com/docs/en/build-with-claude/vision-coordinates)
