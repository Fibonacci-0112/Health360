# Health360: feature catalogue

What Health360 will do, module by module, and when each feature is planned. Architecture, data model, roadmap, costs and tests are in [PLAN.md](PLAN.md); the stack decision is in [ADR 0001](adr/0001-language-and-stack.md).

Tags: **[MVP]** means Phase 1; **[P2]**, **[P3]** and **[P4]** are later phases. The ★ items were borrowed from competitor research.

### 1.1 Goals and plans
- **[MVP]** Profile: sex for calculations, birth date, height, activity level, time zone, currency, and **unit preferences for each measurement type, metric or imperial** ([PLAN.md §2.6](PLAN.md)). Lab and test results go in the physiology profile (§1.6).
- **[MVP]** Goal types: fat loss, muscle gain, recomposition, maintenance, strength target, endurance event, and VO₂max, lean-mass or visceral-fat targets (§1.6). Later: lab-marker targets and habits.
- **[MVP]** Goal wizard: you pick a target, then a date or a weekly rate. It checks whether the goal is realistic, warns above about 1% of body weight per week, and won't go below a calorie floor. It creates milestones that tick off automatically when your measurements or PRs reach them, and it links your nutrition targets.
- **[MVP]** Targets, each of which you can override:
  - Calories: RMR × activity level. RMR is your measured value if you've entered one; otherwise Katch-McArdle when fat-free mass is known, otherwise Mifflin-St Jeor (§1.6).
  - Protein set by goal type, in g/kg or g/lb (defaults from your body-weight unit), of body weight or, once DEXA gives it, of lean mass.
  - Micronutrients from DRI/RDA/UL tables.
- **[P2]** ★ Adaptive expenditure (MacroFactor):
  - TDEE is back-calculated from the smoothed weight trend and logged intake.
  - A weekly check-in suggests new targets, changing them by at most ±150 kcal per week.
- **[P2]** ★ AI "goal → plan" wizard: nutrition targets, training program, supplement shortlist and budget in one flow.
- **[P4]** Habits with ★ forgiving streaks (Streaks66): a few grace days per week, and a "minimum viable" version of each habit.

### 1.2 Cost planning and budget
- **[MVP]** Price book: stores, products and prices you enter, with a price history. There is no reliable official real-time grocery price API. Instacart's developer platform returns hosted links rather than raw prices, and Kroger's API covers product search only.
- **[MVP]** Unit prices (per gram or per serving), ★ cost per 25 g of protein, and food cost per day from your food logs.
- **[MVP]** Cost items: gym membership and subscriptions (recurring), equipment (one-time, spread over months), and coaching.
- **[MVP]** Basic supplement entries: product, servings per container and daily dose, giving cost per serving and per month.
- **[MVP]** Per-goal cost estimate over the goal's time span: food + supplements + gym + equipment. Monthly projection, budgets by category, and a home gym vs gym membership break-even calculator.
- **[P2]** Meal plans and shopping lists with costs, a budget cap on AI meal plans, and ★ receipt OCR that records prices automatically.
- **[P4]** Instacart link-out and Kroger product lookup.

### 1.3 Nutrition
- **[MVP]** Food search across:
  - **USDA FoodData Central**: Foundation, SR Legacy and FNDDS, about 15k foods with full micronutrients, imported. Branded foods are searched through the USDA API.
  - **Open Food Facts** (ODbL licence): used for barcodes.
  - Custom foods for your household.
- **[MVP]** Barcode lookup, in this order:
  1. Your household's own foods.
  2. Open Food Facts (OFF). The app checks the `status` field in the response, because OFF can return HTTP 200 for unknown barcodes.
  3. USDA branded foods by GTIN.
  4. If nothing matches, it offers a label scan.
- **[MVP]** **Nutrition-label OCR**:
  1. The camera shows a framing guide.
  2. The photo is resized on the device.
  3. Claude vision returns structured JSON: per-serving and per-100 g values, fields left empty when unreadable rather than guessed, and allergens.
  4. Automatic checks: 4P + 4C + 9F must roughly match the stated calories, kJ must equal about 4.184 × kcal, macros can't add up to more than the serving weight, and the two columns must agree.
  5. A confirm screen highlights suspect fields. Nothing is saved without your confirmation.
- **[MVP]** Logging:
  - Meal slots, portions, quick-add, and copy a meal or day.
  - Recents and favorites, which work offline because each logged entry stores its own nutrition values.
  - Recipes with per-serving nutrition, saved meals, and water.
- **[MVP]** ★ About 80 nutrients (Cronometer depth): daily and weekly micronutrient coverage against DRIs, with upper-limit (UL) warnings.
- **[P2]** ★ AI meal plans:
  - Claude picks recipes from your household's own catalog.
  - A deterministic solver scales servings so calories land within ±5% and protein at most 10 g under target.
  - The plan is priced from your price book and checked against your budget, then turned into a shopping list.
- **[P2]** Training-day fueling, off by default: the day's carb target can rise with the planned session's estimated carbohydrate use, within the same calorie target (§1.7).
- **[P2]** ★ Meal-photo logging, always shown as a range with an estimate badge and never saved without confirmation. Independent 2026 testing found that photo apps underestimated calories by about 345 kcal per meal.
- **[P3]** Fasting timer, caffeine and alcohol tracking, glucose (CGM) overlay.

### 1.4 Workouts and cardio
- **[MVP]** About 800 exercises from free-exercise-db (Unlicense), plus custom exercises. Each has muscles, equipment and how it is tracked (weight and reps, reps, time, or distance and time).
- **[MVP]** Training locations with equipment tags and a plate inventory in **lb or kg**, mixed if the gym has both. The bar weight is stored in its own unit.
- **[MVP]** Templates with supersets, rep ranges and rest times. A weekly schedule and calendar.
- **[MVP]** ★ Live logger (Hevy, Strong):
  - Last session's values pre-filled, so accepting a set takes one tap.
  - Set types, RPE or RIR, and a rest timer with a notification. In the iPhone web app the timer only alerts while the app is open ([PLAN.md §2.7](PLAN.md)).
  - Exercise swap and a plate calculator. Suggested loads round to weights you can actually load (5 lb or 2.5 kg steps, depending on the plates).
  - Survives the app being killed mid-workout, and works fully offline.
- **[MVP]** ★ PRs: estimated 1RM (Epley/Brzycki), heaviest weight, best reps at a given weight, and volume. Plus weekly sets per muscle and e1RM history.
- **[MVP]** Double or linear progression rules that suggest the next session's loads.
- **[MVP]** Cardio entered by hand: activity, distance, duration, pace, average and max heart rate, time in each HR zone (from your watch summary), RPE, elevation.
- **[MVP]** Aerobic vs anaerobic minutes and fat vs carb estimates for every cardio session, plus weekly totals (§1.7).
- **[P2]** ★ AI program generator (Fitbod, Hevy Trainer). It can only choose exercises that exist and that the location's equipment supports. Code checks weekly sets per muscle (e.g. 10–20), training frequency, session length and rest days. Cardio is prescribed in your own HR zones.
- **[P3]** ★ Per-muscle recovery heatmap (Fitbod) and a readiness score that adjusts training. Training load is split into aerobic and anaerobic parts (§1.7).
- **[P4]** GPS-tracked walks, runs and rides with routes and splits (Strava-style).

### 1.5 Body, health and labs
- **[MVP]** Weight with a smoothed trend line (EWMA), body fat %, lean mass, circumference measurements, an optional Navy-method body-fat estimate, and DEXA results (§1.6). Each body-fat method gets its own trend line.
- **[MVP]** Vitals entered by hand: blood pressure, resting HR, HRV, SpO₂, glucose, temperature.
- **[MVP]** **Progress photos**:
  - Standard poses, with a ★ ghost overlay of the previous photo at 30% opacity for consistent framing.
  - ★ Side-by-side and slider comparison (ZOZOFIT).
  - Location data stripped on the device, private storage, offline upload queue.
  - Never shared with household members unless you turn it on.
- **[P3]** Health Connect (Android): weight, body fat, steps, workouts, sleep, resting HR, HRV, VO₂max and HR samples, with duplicate removal. VO₂max, resting HR and HR samples feed the physiology profile as device values (§1.6). **HealthKit (Apple Health and Apple Watch) comes with the deferred iPhone app** ([PLAN.md §2.7](PLAN.md)).
- **[P3]** Labs:
  - Upload a PDF or photo (drag-and-drop on desktop). Claude parses it, the app maps each value to a biomarker (LOINC) and converts units, and you confirm.
  - Trend charts, with your lab's reference range shown first and ★ cited "optimal" ranges after it (InsideTracker).
  - ★ Retest reminders (Health3, BioMap).
  - DEXA, RMR and metabolic-test reports use the same upload, parse and confirm flow, and fill the physiology profile (§1.6).
- **[P3]** ★ Readiness, strain, sleep and stress scores (Bevel), each with an explanation of how it is calculated, and a comparison against your personal 30-day baseline.
- **[P4]** Wearable aggregator (Terra, or the open-source Open Wearables) for Garmin, Oura, WHOOP and Fitbit.

### 1.6 Physiology profile: lab values if you have them, estimates if you don't
Details and formulas are in [PLAN.md §2.8](PLAN.md).
- **[MVP]** Enter results from lab tests, field tests or a device, each with a date, a source and an optional report file:
  - **RMR** from indirect calorimetry.
  - **DEXA**: body-fat %, fat and lean mass (total, and by region: arms, legs, trunk), bone mineral content and density with T- and Z-scores, visceral fat (VAT), android/gynoid fat, and ALMI. The scanner make is recorded, because Hologic and GE Lunar results differ.
  - **VO₂max**, **max HR**, **resting HR**, **LTHR**, **lactate or ventilatory thresholds** (LT1/LT2, VT1/VT2), **FTP** and **threshold pace**.
  - **HR zones** for each sport, typed in or taken from a lab report.
- **[MVP]** Values with a published estimator are estimated when you haven't entered them, marked "estimated", with the formula named. BMD, visceral fat, FTP, threshold pace and LT1 stay empty until you enter them:
  - RMR: Katch-McArdle when fat-free mass is known (from DEXA or another body-fat reading), otherwise Mifflin-St Jeor.
  - Max HR: Tanaka (208 − 0.7 × age).
  - VO₂max: a non-exercise estimate from age, sex, waist, resting HR and activity (the HUNT study model), or from age, sex, BMI or body fat, and activity when there's no resting HR (Jackson et al.).
  - LTHR: about 90% of max HR, low confidence.
  - If you take medication that lowers heart rate, nothing is estimated from age-predicted max HR; the app uses your measured values, or RPE.
  - HR zones, best basis first: lab thresholds, a measured LTHR (Friel), HR reserve (Karvonen, needs resting HR), % of max HR. The LTHR estimate is never used to set zones.
- **[MVP]** The best value wins: a source you pinned, then lab, field test, a device or manual reading, and estimate last; the newest value within each. Old lab values still count but get a "retest?" flag: VO₂max, LTHR and DEXA after 12 months, and RMR after 12 months or a 5% change in body weight.
- **[MVP]** Measured values flow into everything that uses them: calorie and protein targets, zones, exercise estimates, goals and AI prompts. Every derived number shows its inputs, for example "RMR 1,720 kcal (lab, Mar 2026) × 1.55".
- **[P2]** Guided field tests, each behind a PAR-Q+ health screen: 30-minute LTHR test, 20-minute FTP test, Cooper 12-minute run, Rockport 1-mile walk. When a session records a higher max HR than the one in use, the app offers to adopt it.
- **[P3]** Health Connect values (VO₂max, resting HR, HR samples), parsing of DEXA, RMR and metabolic-test PDFs, and retest flags turned into reminders.

### 1.7 Energy systems: aerobic vs anaerobic, fat vs carbs
Details and formulas are in [PLAN.md §2.9](PLAN.md).
- **[MVP]** Each cardio session gets estimates of:
  - Energy used, as a range: from heart rate with the Keytel equation (more accurate when VO₂max is known), or from 2024 Compendium METs when there's no HR.
  - Fat vs carbohydrate, in kcal and grams: heart rate is mapped to exercise intensity, then to a respiratory exchange ratio (RER) from a cited population curve, then to fat and carb shares.
  - Aerobic vs anaerobic minutes: time below and above your LT2/LTHR.
  - Time in each zone, from what you entered or from average HR.
- **[MVP]** Strength sessions get a MET-based calorie estimate, labelled as mainly anaerobic and carbohydrate-heavy, with no numeric fat/carb split.
- **[MVP]** A zones screen shows each zone's bpm range and its estimated fat/carb mix. The Train tab shows weekly totals: aerobic vs anaerobic minutes, fat vs carb kcal, and the low/moderate/high split for 80/20 training when LT1 is known.
- **[MVP]** Clear framing:
  - Fat loss comes from energy balance over days and weeks, not from which fuel a workout burned. Harder sessions burn a larger share of carbohydrate, but often more energy in total.
  - Exercise calories aren't added to the calorie target, because the activity level (and adaptive TDEE in P2) already covers training.
- **[P2]** Training-day fueling, off by default: the day's carb target can rise with the planned session's estimated carbohydrate use. The calorie target stays the same and fat comes down to make room, down to a minimum; AI meal plans follow the adjusted macros.
- **[P3]** A metabolic (gas-exchange) test replaces the population curve with your own: fat and carb burn per stage, FatMax (the intensity where fat burn peaks) and the crossover point, marked on your zones.
- **[P3]** HR samples from Health Connect give exact time in zone. Training load (TRIMP), split into aerobic and anaerobic parts, feeds the strain and readiness scores.

### 1.8 Mind and cognition (P3)
- ★ Daily check-in (Coach Ultra): mood, energy, stress, focus, anxiety, tags and a note.
- Cognitive tests: reaction time (PVT), n-back, digit span, Stroop. Scores are compared only against earlier tests on the same device.
- ★ A "brain readiness" view (Lumen+): how sleep, HRV, training, nutrition and caffeine line up with your scores. This shows patterns, not causes.
- HRV-paced breathing sessions.

### 1.9 Supplements (P3, beyond the MVP cost entries)
- Regimens (dose and times), intake logging, reminders and adherence.
- ★ Inventory with refill prediction, which adds items to the shopping list.
- ★ Product data from the NIH Dietary Supplement Label Database.
- ★ Combined food + supplement intake checked against upper limits, and duplicate-ingredient warnings.
- Interaction warnings from a curated, cited rule table (NIH Office of Dietary Supplements), plus "ask your pharmacist" prompts. NLM retired its free drug-interaction API in 2024.

### 1.10 Household and shopping
- **[MVP]** A household with invites and roles. Health data is private by default; you can share a category (workouts, nutrition, photos and so on) with specific members.
- **[MVP]** Shared shopping lists with aisle grouping, quantities, estimated prices and live check-off across devices.
- **[P2]** Lists generated from meal plans: quantities merged by product with unit conversion. **[P3]** supplement refills are added automatically. **[P4]** pantry and expenses.
- **[P4]** Managed child profiles (no account needed), a coach view, ★ household challenges and small accountability circles (Habitica-style).

### 1.11 Dashboard, coach and platform
- **[MVP]** "Today" dashboard: calories and macros left, today's workout, weight trend, next milestone, and month-to-date spend.
- **[MVP]** JSON/CSV export, account deletion, disclaimers and consent screens.
- **[P2]** Weekly check-in summary written by AI from the computed numbers. AI coach chat that reads summaries of your data. Trends and correlations.
- **[P4]** CSV import from MyFitnessPal, Strong, Hevy and Cronometer. ICS calendar feed. Push notifications. Going public: RevenueCat and Stripe billing, GDPR, privacy policy, store release.

## Sources (competitor research)
The papers behind the physiology and exercise-energy formulas (§1.6, §1.7) are listed in [PLAN.md](PLAN.md) under Sources.

- [MacroFactor vs Cronometer](https://macrofactor.com/macrofactor-vs-cronometer/) · [Fitia 2026 food tracker comparison](https://fitia.app/learn/article/best-food-tracking-app-us-2026-comparison/)
- [Fitbod vs Strong vs Jefit vs Hevy](https://www.jefit.com/blog/fitbod-vs-strong-vs-jefit-vs-hevy) · [Fitbod workout tracker roundup](https://fitbod.me/blog/best-workout-tracker-apps-for-2026/)
- [Bevel App Store listing](https://apps.apple.com/us/app/bevel-all-in-one-health-app/id6456176249) · [Bevel review (Kiledjian)](https://kiledjian.com/2026/07/07/bevel-turns-apple-watch-data.html)
- [InsideTracker blood results upload](https://info.insidetracker.com/blood-results-upload) · [Health3](https://apps.apple.com/us/app/id6670444240) · [BioMap](https://apps.apple.com/app/6780337778)
- [Cal AI teardown](https://www.raftlabs.com/blog/cal-ai-product-teardown) · [Photo calorie tracker accuracy roundup](https://programminginsider.com/best-photo-calorie-tracking-apps-2026/)
- [ZOZOFIT](https://apps.apple.com/us/app/1636398776) · [Prism Labs body mapping](https://www.prismlabs.tech/blog/prism-labs-announces-first-of-its-kind-mobile-body-mapping-technology-with-integration-into-the-tempo-fitness-platform)
- [StackSnap](https://apps.apple.com/app/id6761888709) · [Supp AI](https://apps.apple.com/us/app/-/id6749564990) · [Biostacks](https://appshunter.io/ios/app/biostacks-supplement-tracker/id6763328808)
- [Coach Ultra](https://apps.apple.com/app/id6747248347) · [Lumen+](https://www.producthunt.com/products/lumen-app) · [AI habit tracker apps](https://www.asianefficiency.com/technology/best-habit-tracking-apps-with-ai/)
- [Terra wearable API](https://tryterra.co/wearable-data-api) · [Open Wearables](https://openwearables.io/wearable-api) · [Nutrition APIs 2026](https://spikeapi.com/blog/top-nutrition-apis-for-developers-2026) · [Edamam food database](https://developer.edamam.com/food-database-api)
- [Instacart Developer Platform](https://docs.instacart.com/developer_platform_api/) · [MyMealPlan](https://mymealplan.it.com/) · [Mealia](https://mwm.ai/apps/app/6475587257)
