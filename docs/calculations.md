# Calculations: body, physiology, training and nutrition (Phase 1, steps 1.1–1.3)

The exact rules `Health360.Domain` implements for the body and physiology screens, with their sources. Code, tests and this page must agree; change them together. General rules are in [PLAN.md §2.8](PLAN.md) and the units rules in §2.6. Every formula here takes canonical units (kg, cm, years). Convert with `Health360.Domain/Units` and never through a rounded display value.

Status: what's below is implemented and tested in `Health360.Domain` (`Body/`, `Physiology/`, `Training/`, `Nutrition/`). Not yet specified: the LTHR estimate and the Nes et al. (2011) VO₂max estimator. They wait until they're checked against primary sources, not secondary ones (see [Still to specify](#still-to-specify)).

## Navy method (body fat from circumferences)

**Source:** Hodgdon, J. A. and Beckett, M. B. (1984). *Prediction of percent body fat for U.S. Navy men from body circumferences and height*, Naval Health Research Center Report No. 84-11 ([DTIC ADA143890](https://www.dtic.mil/docs/citations/ADA143890)), and *... for U.S. Navy women ...*, Report No. 84-29 ([doi:10.21236/ADA146456](https://doi.org/10.21236/ADA146456)). Checked against scans of the original reports.

The method predicts body density, then converts it to percent fat with Siri's equation. Both steps are as printed (p. 12 of each report). The later one-step forms in cm or inches are not used.

| | Men (84-11) | Women (84-29) |
|---|---|---|
| Body density (g/cc) | −0.19077 × log₁₀(abdomen II − neck) + 0.15456 × log₁₀(height) + 1.0324 | −0.35004 × log₁₀(abdomen I + hip − neck) + 0.22100 × log₁₀(height) + 1.29579 |
| Units | cm | cm |
| Standard error of estimate | 3.52 %BF | 3.72 %BF |
| Development sample | 602 men, aged 18–56 | 214 women, aged 18–44 |

**Siri (1961)**, as both reports state it: %BF = 100 × (4.95 / density − 4.50).

**Where to measure** (§2.2.2 of each report). The tape conforms to the skin without compressing it, and abdominal measurements are taken at the end of a normal breath out.
- **Neck:** just below the larynx, with the tape sloping slightly down towards the front.
- **Abdomen II (men):** at the level of the navel.
- **Abdomen I (women):** at the narrowest part of the abdomen, about midway between the bottom of the breastbone (xiphoid) and the navel.
- **Hip (women):** at the greatest backward protrusion of the buttocks.

The two equations use different abdominal sites, so the entry screen must say which one to measure.

**Invalid inputs:** the method returns no result when any input is missing, not finite or not positive; when the log argument (abdomen − neck, or abdomen + hip − neck) is ≤ 0; or when the resulting percentage is outside [0, 100). Ages and sizes outside the development samples are not rejected yet. When they are, the UI should warn rather than refuse.

**Display:** an estimate, labelled "Navy method", shown as the value ± its standard error of estimate. It is its own trend series, never merged with DEXA or scale readings (§2.8).

**Published tables:**
- **Men:** Appendix A of 84-11 lists %BF by (abdomen II − neck) and height in inches, rounded to whole percent. Every one of the 21 cells checked matches the equation, including cells whose exact value is close to .5. They are golden tests (`NavyMethodTests`).
- **Women:** Appendix A of 84-29 does **not** reproduce its printed equation exactly. Across 216 transcribed cells it runs on average 0.6 points, and up to about 1.9 points, lower than the equation, most at the lean end. It is also not always monotonic: in the 37.0 in row, 59.0 in reads 5 and 59.5 in reads 4. The closest fit is the printed equation truncated rather than rounded, which matches 144 of the 216 cells; no coefficient change found explains the rest. The app follows the printed equation, as decided, and a test pins every checked cell to within 2 points of the table.

## Body composition

Definitions, not estimates:
- **BMI:** kg / m².
- **Fat-free mass:** weight × (1 − %BF / 100). It inherits the status of the %BF it came from: measured from DEXA, estimated from the Navy method or a scale.

## Weight trend

**Source:** Walker, J. *The Hacker's Diet* (Fourmilab), "Signal and Noise". The trend is an exponentially smoothed moving average with smoothing 0.1 per day ([fourmilab.ch](https://www.fourmilab.ch/hackdiet/www/subsubsection1_4_1_0_8_3.html)). The constant is an algorithm choice, not a clinical threshold.

- **Recurrence (one day):** Tᵢ = Tᵢ₋₁ + 0.1 × (Wᵢ − Tᵢ₋₁). The new weight is weighted 0.1 and the previous trend 0.9. Swapping the two would make the trend follow every day's noise.
- **Start:** the first day's trend is that day's weight.
- **Several weigh-ins in one day:** the day's weight is the mean of that local day's canonical values.
- **Missing days:** after a gap of g days, T = W + (Tprev − W) × 0.9ᵍ. This treats each missing day as if the new weight had been seen on it. Walker assumes daily weighing; this gap rule is this app's choice.
- **Edits and deletions:** the trend is a pure function of the whole series and is never stored, so it is recomputed after any change.
- **Units:** computed on canonical kg, so switching the display unit never changes it (a property test checks this).

**Golden fixture** (`WeightTrendTests`), computed by hand independently of the code:

| Day | Weigh-ins (kg) | Mean | Trend |
|---|---|---|---|
| 1 Jan | 80.0 | 80.0 | 80.0 |
| 2 Jan | 79.6, 80.4 | 80.0 | 80.0 |
| 3 Jan | 79.2 | 79.2 | 79.92 |
| 6 Jan (3-day gap) | 78.9 | 78.9 | 79.64358 |
| 7 Jan | 79.5 | 79.5 | 79.629222 |

When the sync window arrives (PLAN.md §2.3), truncating history must not change the trend. It will need a stored starting value or enough history before the window.

## Resting metabolic rate (kcal/day)

- **Mifflin-St Jeor** (Mifflin et al., 1990, [doi:10.1093/ajcn/51.2.241](https://doi.org/10.1093/ajcn/51.2.241)), with the coefficients as published, not the common rounded form: 9.99 × kg + 6.25 × cm − 4.92 × years + 166 × sex (male 1, female 0) − 161. Golden values: male 80 kg, 180 cm, 30 y → 1781.6; female 60 kg, 165 cm, 40 y → 1272.85.
- **Katch-McArdle** (McArdle, Katch and Katch, *Exercise Physiology*): 370 + 21.6 × fat-free mass (kg). Used instead of Mifflin-St Jeor whenever fat-free mass is known. Golden value: 60 kg → 1666.
- A measured RMR (indirect calorimetry) beats both through the resolver.

## Maximum heart rate (bpm)

- **Tanaka** (Tanaka et al., 2001, [doi:10.1016/s0735-1097(00)01054-8](https://doi.org/10.1016/s0735-1097(00)01054-8)): 208 − 0.7 × age. Golden values: 40 y → 180; 25 y → 190.5.
- **Not estimated** when the heart-rate-lowering medication flag is set (PLAN.md §2.5).

## VO₂max: non-exercise estimate (ml/kg/min)

**Source:** Jackson, A. S., Blair, S. N., Mahar, M. T., Wier, L. T., Ross, R. M. and Stuteville, J. E. (1990). Prediction of functional aerobic capacity without exercise testing. *Med Sci Sports Exerc* 22(6):863–870 ([doi:10.1249/00005768-199012000-00021](https://doi.org/10.1249/00005768-199012000-00021)). Checked against the original paper, p. 866.

The paper fits two models, N-Ex %fat and N-Ex BMI, each with resting HR (I) and without it (II). Resting HR added little, so the paper drops it and prints only the model II equations, which the app uses. Sex is coded female 0, male 1.

| Model II | Equation | R | SE (ml/kg/min) |
|---|---|---|---|
| %fat | 50.513 + 1.589 × PA-R − 0.289 × age − 0.552 × %fat + 5.863 × sex | 0.812 | 5.35 |
| BMI | 56.363 + 1.921 × PA-R − 0.381 × age − 0.754 × BMI + 10.987 × sex | 0.783 | 5.70 |

The paper also prints the men's equations with the sex term folded in: intercepts 56.376 (%fat) and 67.350 (BMI). A property test checks they agree. Several secondary sources print the %fat coefficient as 0.522; the paper says **0.552**.

- **PA-R** is the NASA/Johnson Space Center physical activity rating, an integer from 0 to 7. The paper doesn't print the scale; it cites Ross, R. M. and Jackson, A. S. (1990), *Exercise Concepts, Calculations, and Computer Applications*, Benchmark Press. The questionnaire and its scoring are taken from the **NHANES Cardiovascular Fitness Procedures Manual** (CDC, NHANES 1999–2004), Appendix E, pp. E-1 and E-3 ([cdc.gov](https://wwwn.cdc.gov/nchs/data/nhanes/public/2003/manuals/cv_99-04.pdf)), which cites both sources. NHANES Appendix F uses the same BMI equation to predict VO₂max. `PhysicalActivityRating` implements the scoring; the Physiology screen asks the questions and stores the code in `profiles.activity_rating`. The five onboarding activity levels are not PA-R and are not mapped onto it.

- **%fat:** the paper measured it from three skinfolds. The app accepts %fat from any source (DEXA, scale, Navy method), which adds that method's error.
- **Which model:** %fat when body fat is known (the paper's more accurate model), otherwise BMI (`Vo2Max.Jackson`). It is used only when there is no resting HR; with one, PLAN.md §2.8 calls for Nes et al. (2011), which isn't implemented yet.
- **Population:** 2,009 NASA employees (9.7% women) aged 18–70, VO₂peak 20–70. The models underestimate fit people: for measured VO₂peak above 55 the cross-validation SE was 10.7 (%fat) and 12.1 (BMI). Show the estimate with its SE, labelled as an estimate.
- **Invalid inputs:** PA-R outside 0–7, negative age, BMI not positive, or %fat outside [0, 100) are rejected. A result that isn't positive gives no estimate.

**The PA-R questions** (NHANES, Appendix E, p. E-1), asked in this order, with only the follow-up that applies:
1. "Would you say that you did little or no regular recreation, sport, or physical activity? OR Would you say that you participated regularly in recreation or work requiring modest physical activity? OR Would you say that you participated regularly in heavy physical exercise?" Modest activities include walking, calisthenics, bowling, weight lifting and yard work. Heavy activities include jogging, swimming, cycling, rowing, tennis and basketball.
2. Little or no activity: "Would you say that you avoid walking or exertion (for example, always use the elevator, drive whenever possible instead of walking)? OR Would you say that you walk for pleasure, routinely use stairs, occasionally exercise sufficiently to cause heavy breathing or perspiration?"
3. Modest activity: "for 10 to 60 minutes per week? OR More than 60 minutes per week?"
4. Heavy exercise: "less than 30 minutes per week, 30 to 60 minutes per week, 1 to 3 hours per week, or more than 3 hours per week?"

| Answers | PA-R |
|---|---|
| Little or no activity, avoids walking or exertion | 0 |
| Little or no activity, walks for pleasure, uses stairs, occasionally exercises hard | 1 |
| Modest activity, 10–60 min/week | 2 |
| Modest activity, > 60 min/week | 3 |
| Heavy exercise, < 30 min/week | 4 |
| Heavy exercise, 30–60 min/week | 5 |
| Heavy exercise, 1–3 h/week | 6 |
| Heavy exercise, > 3 h/week | 7 |

Golden values: BMI, male, PA-R 5, 40 y, BMI 25 → 42.865; female, PA-R 3, 35 y, BMI 22 → 32.203. %fat, male, PA-R 5, 40 y, 20% → 41.721; female, PA-R 4, 30 y, 28% → 32.743.

## Age

Whole years between the birth date and the local date the value is computed for. A 29 February birthday counts as reached on 28 February in non-leap years.

## Activity level and maintenance energy

**Source:** FAO/WHO/UNU (2004). *Human energy requirements: report of a Joint FAO/WHO/UNU Expert Consultation*, Rome, 17–24 October 2001. FAO Food and Nutrition Technical Report Series 1, §5.3 and Table 5.3 ([fao.org](https://www.fao.org/4/y5686e/y5686e07.htm)). PAL = total energy expenditure / BMR over 24 hours. Table 5.3 classifies lifestyles in three bands:

| FAO lifestyle | PAL |
|---|---|
| Sedentary or light activity | 1.40–1.69 |
| Active or moderately active | 1.70–1.99 |
| Vigorous or vigorously active | 2.00–2.40 (values above 2.40 are hard to sustain) |

**How the five levels map** (the app's choice): Sedentary and Light split the first band in half, Moderate is the whole second band, and Active and Very active split the third. Each level uses the midpoint of its range.

| Level (`profiles.activity_level`) | PAL range | PAL used | FAO band |
|---|---|---|---|
| `sedentary` | 1.40–1.55 | 1.475 | Sedentary or light |
| `light` | 1.55–1.70 | 1.625 | Sedentary or light |
| `moderate` | 1.70–2.00 | 1.85 | Active or moderately active |
| `active` | 2.00–2.20 | 2.10 | Vigorous |
| `very_active` | 2.20–2.40 | 2.30 | Vigorous |

The onboarding choices should describe each level using FAO §5.3.2's lifestyle examples, for example "an office job, no regular exercise" for Sedentary, and "a sedentary job plus about an hour of moderate to vigorous exercise daily" for Moderate. (FAO notes that this hour raises PAL from about 1.55 to 1.75.)

These values sit well above the common 1.2–1.9 multipliers, whose primary source we could not trace. That is deliberate: the lowest PAL FAO considers sustainable for free-living adults is about 1.40.

**Maintenance energy** = resolved RMR × PAL used. FAO defines PAL against BMR, measured fasted and fully at rest. The app multiplies it by RMR, whether measured or from Mifflin-St Jeor or Katch-McArdle, and RMR runs slightly higher than BMR. The result is labelled an estimate and its inputs are shown ("RMR 1,720 kcal (lab, Mar 2026) × 1.85"). A measured RMR still gives an **estimated** maintenance figure, because the multiplier is an estimate.

## Heart-rate zones

`HeartRateZoneRules.Derive` picks the best basis the resolved values allow (PLAN.md §2.8) and returns each zone's lower bound in bpm. That's what `zone_sets.boundaries` stores for metric `hr`: zone 1 first, strictly ascending, and zone i holds heart rates ≥ its lower bound and below the next zone's. The last zone has no far end. Boundaries are kept unrounded; round them only for display.

**Order, best first:**

| Basis | Run and bike | Other sports (swim, row, other, all) |
|---|---|---|
| 1. Lab thresholds: LT1 (or VT1) and LT2 (or VT2), both measured | Friel, lactate column | Seiler and Kjerland, three zones |
| 2. Measured or test-derived LTHR | Friel, FTHR column | Skipped |
| 3. Heart rate reserve: max HR and a measured resting HR | Karvonen with Garber's bands | Karvonen with Garber's bands |
| 4. % of max HR | Friel, MHR column | Garber's bands |

Friel's guide covers running and cycling only, so other sports keep the Seiler and Garber bases. Friel gives no heart-rate-reserve column, so basis 3 uses Garber's bands for every sport.

Only measured values (any source tier) feed bases 1–3; estimated thresholds, LTHR and resting HR are ignored, so **the LTHR estimate never sets zones**. Max HR may be the Tanaka estimate, except when the heart-rate-lowering medication flag is set (PLAN.md §2.5). Then an estimated max HR is not used, and with no measured value there are no HR zones. A basis whose boundaries wouldn't be finite and strictly ascending (LT1 ≥ LT2, LT1 ≤ 10 bpm for Friel's lactate column, resting ≥ max, or a value so large the arithmetic overflows) is skipped, and the next basis is tried.

**LT2 is not used as threshold HR for zones.** PLAN.md §2.8 takes LT2 as LTHR when there's no LTHR, but Friel's 2025 table puts LT2 at the zone 3/4 border and threshold HR at the zone 4/5a border, so the two differ. With LT2 but no LT1 and no LTHR, zones fall through to heart rate reserve or % of max HR.

**Reading whole-percent ranges.** The sources give bands as whole percentages with small gaps or overlaps ("less than 72%", then "73–82%"; "88–92%", then "92–100%"). Each zone starts at the first number of its own range, and a "more than X" zone starts at X. A value in a gap belongs to the lower zone. Every lower bound is computed as offset + percent × (base / 100).

### Friel (2025): run and bike

**Source:** Friel, J. *A Quick Guide to Setting Zones*, joefrieltraining.com, revised September 2025 ([joefrieltraining.com](https://joefrieltraining.com/a-quick-guide-to-setting-zones/)). The zones are in a table image on that page (reproduced from p. 63 of one of his books), checked by reading the image. The guide gives one table for running and cycling. It replaces the older TrainingPeaks guide (separate run and bike % of LTHR, zones up to 5c), which this app no longer uses.

Heart-rate columns of the table:

| Zone | % of max HR (MHR) | % of functional threshold HR (FTHR) | Lactate (LT1, LT2) |
|---|---|---|---|
| 1 | < 72 | < 78 | Below zone 2 |
| 2 | 73–82 | 78–86 | LT1 − 10 bpm up to LT1 |
| 3 | 83–87 | 87–93 | LT1 to LT2 |
| 4 | 88–92 | 94–99 | LT2 to LT2 + 10 bpm |
| 5a | 92–100 (5a and 5b together) | 100–102 | Higher than zone 4 (5a and 5b together) |
| 5b | | > 102 | |
| 5c | Not applicable | Not applicable | Not applicable |

- **FTHR** is the app's measured or test-derived LTHR. Friel's 20-minute test takes the test's average HR minus 5%; his 30-minute solo test takes the average HR of its last 20 minutes. Lower bounds used (%): 0, 78, 87, 94, 100, 102; zones 1, 2, 3, 4, 5a, 5b.
- **MHR:** lower bounds used (%): 0, 73, 83, 88, 92; zones 1, 2, 3, 4, 5 (Friel's 5a and 5b share one range). Friel warns not to use 220 − age. The app may use the Tanaka estimate here (PLAN.md §2.8) and labels the zones as resting on an estimate.
- **Lactate:** lower bounds used (bpm): 0, LT1 − 10, LT1, LT2, LT2 + 10; zones 1, 2, 3, 4, 5. Friel puts LT1 at about 2 mmol/L.

Golden values:
- FTHR 170 → 0, 132.6, 147.9, 159.8, 170, 173.4. FTHR 160 → 0, 124.8, 139.2, 150.4, 160, 163.2.
- Max HR 180 → 0, 131.4, 149.4, 158.4, 165.6.
- LT1 140, LT2 165 → 0, 130, 140, 165, 175.

### Lab thresholds, other sports: three zones

**Source:** Seiler, K. S. and Kjerland, G. Ø. (2006). Quantifying training intensity distribution in elite endurance athletes: is there evidence for an "optimal" distribution? *Scand J Med Sci Sports* 16(1):49–56 ([doi:10.1111/j.1600-0838.2004.00418.x](https://doi.org/10.1111/j.1600-0838.2004.00418.x)). VT1 and VT2 delineate three intensity zones.

| Zone | Starts at |
|---|---|
| 1 | 0 (below LT1/VT1) |
| 2 | LT1/VT1 |
| 3 | LT2/VT2 |

Golden value: LT1 140, LT2 165 → 0, 140, 165.

### Heart rate reserve (all sports) and % of max HR (other sports): Garber et al. (2011)

**Sources:**
- Karvonen, M. J., Kentala, E. and Mustala, O. (1957). The effects of training on heart rate; a longitudinal study. *Ann Med Exp Biol Fenn* 35(3):307–315. Target HR = resting HR + fraction × (max HR − resting HR).
- Bands: Garber, C. E. et al. (2011). American College of Sports Medicine position stand. Quantity and quality of exercise for developing and maintaining cardiorespiratory, musculoskeletal, and neuromotor fitness in apparently healthy adults. *Med Sci Sports Exerc* 43(7):1334–1359 ([doi:10.1249/MSS.0b013e318213fefb](https://doi.org/10.1249/MSS.0b013e318213fefb)), Table 4.

| Zone (Garber's intensity) | %HRR | %HRmax |
|---|---|---|
| Very light | < 30 | < 57 |
| Light | 30–39 | 57–63 |
| Moderate | 40–59 | 64–76 |
| Vigorous | 60–89 | 77–95 |
| Near-maximal to maximal | ≥ 90 | ≥ 96 |

Zone 1 starts at resting HR for heart rate reserve (0% HRR) and at 0 for % of max HR. Golden values: max 190, resting 60 → 60, 99, 112, 138, 177; max 180 → 0, 102.6, 115.2, 138.6, 172.8.

The intensity names are Garber's. They aren't the same scale as Friel's zone numbers, so the UI must show the basis next to the zones.

## Resolver (which value is used)

Implements PLAN.md §2.8 for each metric:
1. **A pin**, if it can be honoured. A pin is a specific measurement (by id), or the estimate. A pin to a deleted measurement, or to the estimate when there isn't one, is ignored *and reported*, so the UI can say so. When nothing at all is left, there's no result; the caller still holds the pin and can say it couldn't be honoured.
2. Otherwise **lab**, then **field test**, then **device or manual** (one tier), then **the estimate**.
3. Within a tier, the newest `measured_at` wins; on the same day, the later id (UUIDv7, so the later entry).
4. An old measured value still beats an estimate. Instead, it gets a **retest** flag:
   - VO₂max, LTHR and DEXA: 12 months after the test.
   - Measured RMR: 12 months after the test, or once trend weight has changed by 5% or more since it.
5. The result lists the values passed over, best first, and the estimate when a measured value won, for the info view.

Pinning a measurement, rather than a source tier, lets a user keep an older lab value they trust. When the sync window arrives, pinned measurements and current winners must stay synced at any age.

Pins are stored in `metric_pins`, one row per profile and metric (target `measurement` with its id, or `estimate`). The row id is a UUIDv8 from SHA-256 of `health360/metric_pins/<profile id>/<metric code>` (`UuidV8.FromName`; the server's `private.metric_pin_id` checks it), so the same metric pinned on two offline devices ends as one row, the later upload winning. Deleting a pinned measurement keeps the pin; the Physiology screen then shows that the choice is no longer available (with the next value in use, or none) and offers to clear it. The screen pins from each value's "Other values on file" and unpins with "Go back to the best value" (`PhysiologyTests`).

Deleting a measurement re-resolves straight away: the next measured value, then the estimate (`ResolverTests`).

## Putting the profile together

`PhysiologyProfile.Build` turns the stored measurements and the profile's fields into what the Body and Physiology screens show. It is a pure function of its inputs and is rebuilt after every change, so entering or deleting a value changes everything that uses it at once. The choices below are this app's, not a published method's:

- **Values:** each measurement's canonical value (`value_canonical`) and its local date. Each metric goes through the resolver on its own (with the user's pin, when there is one).
- **Body weight in formulas:** the newest point of the weight trend (every weigh-in, whatever its source), not the last raw weigh-in. BMI uses it with the profile height.
- **Body fat:** measured `body_fat_pct` values only (DEXA, a scale, a value entered by hand). The Navy estimate is shown beside them but isn't stored or resolved. Fat-free mass is trend weight × (1 − resolved %BF / 100), so Katch-McArdle replaces Mifflin-St Jeor as soon as any body-fat value is entered.
- **Navy estimate:** from the newest neck and waist (and hip, for women) measurements, each taken on its own: the latest day, then the later entry. Men use the waist at the navel and women the waist at the narrowest point, as the equations require. A combination the equation can't use (a waist not larger than the neck) gives no estimate.
- **Thresholds:** lactate thresholds (LT1 and LT2) when both have a value, otherwise ventilatory ones (VT1 and VT2) when both do. Zones need both of a pair (see above). Cardio intensity can use a standalone measured VT2 (then LT2) when no complete pair is available, before falling back to measured LTHR; it does not combine thresholds from different pairs to derive zones.
- **RMR retest flag:** the weight "then" is the trend on or before the test date. With no weigh-in by then, only the 12-month rule applies.
- **VO₂max estimate:** Jackson only without a measured resting HR, and only with a PA-R answer: `profiles.activity_rating` (smallint 0–7, null until answered; a check constraint holds the range). The Physiology screen asks the questions above, scores them with `PhysicalActivityRating` and stores only the code. Until they're answered, or once a resting HR is entered (Nes et al., 2011, isn't implemented), VO₂max shows only measured values.
- **Medication flag:** `profiles.takes_hr_lowering_medication`. When it's true, Tanaka gives nothing and zones need a measured max HR (or a measured basis above it).

The golden checks are in `PhysiologyProfileTests` (Domain) and, through the screens, `PhysiologyTests` (UI): a lab RMR changes maintenance and deleting it falls back, and a measured max HR changes the zones and deleting it falls back.

## Estimated one-rep max (e1RM)

`OneRepMax.Estimate(load, reps, formula)` in `Training/`. The result is in the load's unit, so PRs compare e1RMs computed from canonical kg (§2.6: switching units never creates a PR).

| Formula | Equation | Source |
| --- | --- | --- |
| Epley | load × (1 + reps / 30) | Epley B. (1985). *Poundage Chart.* Boyd Epley Workout. Lincoln, NE: Body Enterprises. A coaching chart, not a peer-reviewed paper. |
| Brzycki | load × 36 / (37 − reps) | Brzycki M. (1993). Strength testing: predicting a one-rep max from reps-to-fatigue. *JOPERD* 64(1):88–90. |

App rules (product choices, not from either source):
- A single (1 rep) is its own 1RM for both formulas. Brzycki already gives that; Epley would give 1.033 × load.
- Nothing is estimated above 10 reps (`OneRepMax.MaxReps`); the two equations agree within about 4% up to 10 reps (they meet exactly at 10) and diverge beyond.

Golden values: 100 × 5 → Epley 116.667, Brzycki 112.5; 225 × 8 → Epley 285, Brzycki 279.310; 80 × 10 → both 106.667.

Stored on `set_logs.e1rm_kg` with the formula in `e1rm_formula` (the logger uses Epley). The `set_logs` trigger recomputes it with `private.e1rm_kg` from `weight_kg`, in the same operations and order as `OneRepMax.Estimate`, so a set's estimate syncs back unchanged; `CanonicalUnitParityTests` checks the two agree bit for bit. Only a completed set that isn't a warm-up gets an estimate (a warm-up isn't taken to fatigue; a planned set wasn't done); otherwise both columns are null.

## Personal records

`PersonalRecords.Detect(earlier, set)` in `Training/`. App rules, not from a published source. A completed set (warm-ups left out) is compared with the profile's sets of the same exercise completed before it, all in canonical kg. Editing a set later evaluates it against the same earlier sets, so a better set done afterwards never takes its record away:

| Record | Beats the earlier best when |
| --- | --- |
| e1RM | its e1RM is greater than every earlier e1RM, recomputed with the same formula |
| Heaviest weight | its load is greater than every earlier load |
| Reps at a weight | an earlier set was at the same load (within tolerance) and it has more reps than any of them |
| Set volume | load × reps is greater than every earlier set's |

- "Greater" is `Quantity.CanonicalGreater` (relative tolerance 1e-9) and "same load" is `Quantity.CanonicalEquals`, so repeating a lift in the other unit is never a record (§2.6).
- A record needs something to beat: an exercise's first set is a baseline, an e1RM record needs an earlier estimate, and a reps record an earlier set at that load.
- Each record is a `personal_records` row (kind, canonical value, the set's load and reps, the formula for e1RM, the set). The server files it under the set's exercise whatever the client sends. Its id is derived from the set and the kind, so a record re-evaluated on two devices offline stays one row. The rows are the history; deleting or un-ticking the set removes its records, and once a workout is finished its sets and records can't be changed. Records of later sets aren't recomputed when an earlier set is deleted or edited.

Golden values: after 100 kg × 5, 102.5 × 5 beats e1RM (119.583), weight and volume (512.5); 100 × 6 then beats reps at 100 kg, e1RM (120) and volume (600); 60 × 9 is a volume record only (540).

## Progression

`Progression` in `Training/`. These are rules, not published equations. Loads and the increment share one unit, the lifting unit the increment was set in (PLAN.md §2.6); a set logged in another unit is converted first, so the same session gives the same suggestion in lb or kg.

- **Linear** (target reps *r*): if every work set reached *r* reps at the prescribed load or heavier, the next load is load + increment. Otherwise the session is a miss.
- **Double** (rep range *min*–*max*): if every set reached *max* reps at the prescribed load or heavier, the load goes up by the increment and the target restarts at *min*. If any set fell below *min* or was lighter than prescribed, the session is a miss. Otherwise the load stays and nothing is counted.
- **Misses and deloads** (`DeloadPolicy`, default 3 misses and 10%): misses in a row are counted from the history (below), not stored. On the third, the load drops by 10% of the load in whole increments, rounded half away from zero and at least one increment, and the count resets. A deload that would reach 0 is skipped. Setting the miss count to 0 turns deloads off.
- Suggestions move from the prescribed load in whole increments, so a load on the increment grid stays on it; an off-grid load (101 with +2.5) keeps its offset (103.5). Rounding to loads the plate inventory can build is the plate calculator's job (not yet built).
- A session within the double-progression range ends a miss streak, so only misses in a row count toward a deload.
- **Replay** (`Progression.Replay`): the next suggestion comes from applying the rule to each earlier session in order, oldest first, so the miss streak needs no stored counter and two devices agree once synced. Each session is judged against the load the previous one suggested; the first is judged against the template's starting load, or, without one, against its lightest work set. A session whose every work set was heavier than suggested is judged against its lightest set, so a load raised by hand is built on; a lighter one is a miss. Linear progression targets the top of the scheme's rep range, and its work sets are planned at that target even before there is a load to suggest. Sessions without work sets, or with every work set at a load of 0, are skipped rather than counted as misses.
- **What counts** (`WorkoutProgression` in `Health360.LocalDb`): every finished session of the same template that includes the exercise and started before the one being planned. All are replayed, because the replay starts from the template's starting load: dropping older sessions would change the result. A template may list an exercise more than once (heavy sets, then back-off sets); each workout entry records the template row it was planned from (`session_exercises.template_exercise_id`), and each row replays only its own entries, so reordering the template or removing an entry mid-workout never swaps two histories. An entry with no live link (none, as for an exercise added by hand, or one to a row since removed, as after removing an exercise and adding it back) counts for a row only when the template lists the exercise once; for a repeated exercise such entries can't be told apart, so they don't count. A swapped-in exercise keeps its slot's link (so the slot's superset group and rest still apply); that row names another exercise, so the entry counts for no row. Only that entry's completed `working` sets with a load and reps count, each converted to the rule's unit through `UnitCodes.Convert` (so a set entered in lb counts at its kg equivalent); an entry with none (only warm-ups, say) counts as nothing rather than being replaced by a later entry. The rule is stored as `template_exercises.progression` (`ProgressionRule`: kind, increment and its unit, misses before a deload). Planned work sets get the suggested load in the rule's unit, the bottom of the range after an increase or deload, and last time's reps (clamped to the range) while a load repeats.

Golden values (increment 2.5): linear 100 kg, 3 × 5 done → 102.5; replaying double 8–12 from 60 through 10/9/8, 12/12/12, then 7/7/7, 7/7/6 at 62.5 → 62.5 with two misses, and a further 7/6/6 → 55; third miss at 100 → 90; third miss at 20 → 17.5. With increment 5, third miss at 225 → 200. Double 8–12 at 60: 10/9/8 → 60 (streak reset to 0), 12/12/11 → 60, 12/12/12 → 62.5 (target 8), 9/8/7 → 62.5 with one miss.

## Workout and cardio energy (step 1.2)

`Training/CardioEnergy` estimates **gross** session kcal, including resting expenditure. It never changes the calorie target. `CardioLog` saves a version 2 snapshot in the owner-only `cardio_estimates` row; `workout_sessions.energy_estimate` holds a redacted summary without physiology, zones or measurement provenance. The server's allow-list (`private.cardio_workout_summary`) keeps the summary's entry, activity, method, energy, intensity, fuel, distance and elevation fields, and drops everything else, including zone times and notes. Original session inputs, canonical distance/elevation, activity code, exact resolved inputs, zone boundaries, ranges and source/method version are retained privately. Only winning resolutions are copied. Reads never recompute snapshots; version 1 remains readable. Later profile, zone or preference edits leave history unchanged. The form watches HR zones and clears watch times when boundaries change. Backdated entries use physiology and effective zones as of the session's start date.

**Source:** Keytel, L. R. et al. (2005). Prediction of energy expenditure from heart rate monitoring during submaximal exercise. *Journal of Sports Sciences* 23(3):289–297, [doi:10.1080/02640410470001730089](https://doi.org/10.1080/02640410470001730089). Checked against the [full primary paper text](https://www.researchgate.net/publication/7777759_Prediction_of_energy_expenditure_from_heart_rate_monitoring_during_submaximal_exercise), mixed-model prediction equations. The printed equation coefficients below are used, rather than the extra digits in the parameter tables. Output is **kJ/min**, converted by division by 4.184, then multiplied by duration in minutes. Inputs are age in years, weight in kg, HR in bpm, and VO₂max in ml/kg/min.

| Model | Equation (kJ/min) |
| --- | --- |
| Men, no VO₂max | −55.0969 + 0.6309 × HR + 0.1988 × kg + 0.2017 × age |
| Women, no VO₂max | −20.4022 + 0.4472 × HR − 0.1263 × kg + 0.074 × age |
| Men, with VO₂max | −59.3954 − 36.3781 + 0.271 × age + 0.394 × kg + 0.404 × VO₂max + 0.634 × HR |
| Women, with VO₂max | −59.3954 + 0.274 × age + 0.103 × kg + 0.380 × VO₂max + 0.450 × HR |

The study involved regularly exercising adults aged 18–45, 47–120 kg, during submaximal treadmill/cycle stages (roughly 57–90% HRmax). The app uses it for adults only; other ages, sizes and harder efforts are extrapolations, explicitly disclosed. HR-lowering medication disables Keytel even with measured HR and VO₂max. Missing age/sex also selects METs; missing weight leaves energy unavailable while allowing the session to be logged. Negative regression values at low HR are floored to zero. Invalid/nonfinite session inputs and inconsistent average/max HR are rejected.

Session input priority and **product range policy**, not published confidence intervals:

1. Watch time in zones, when entered, must cover the full duration within 0.01 s, with one non-negative time for every displayed zone. When average HR is also entered, it must be within the time-weighted bounds of the occupied zones. Each occupied zone contributes Keytel(lower HR) × minutes to the low total and Keytel(upper HR) × minutes to the high total. The last zone needs the session max, else the resolved profile max; if neither can bound it, fall back to average HR or METs. If the resolved profile maximum makes the time-weighted zone bounds inconsistent with the entered average HR, use average-HR energy instead. Session max caps other upper bounds. The resulting range is widened by 25% on each side, or 40% for intervals. A zone lower bound above recorded max is invalid.
2. Average HR: steady-state kcal widened by 30% on each side, or 50% for intervals. No threshold time split is inferred for average-HR intervals.
3. No usable HR energy: MET × kg × hours, widened by 30% on each side. METs are explicitly selected through the activity description, not inferred from the sport alone. Modified Borg effort can supply the explicitly approximate categories described below when HR cannot be used.

These deliberately broad policy ranges describe model and summary-input uncertainty; they are not validated individual prediction intervals. Even a measured input yields estimated energy. A zero lower end can occur when a broad low zone includes resting HR.

**MET sources:** verified 2024 Adult Compendium [Running](https://pacompendium.com/running/) and [Bicycling](https://pacompendium.com/bicycling/). Initial codes: 12020 jogging self-selected 7.5; 12030 running 5.0–5.2 mph 8.5; 12050 running 6.0–6.3 mph 9.3; 01010 leisure cycling under 10 mph 4.0; 01030 cycling 12–13.9 mph 8.0; 01200 stationary cycling general 6.8. This is a small catalogue, not all Compendium activities.

**Threshold minutes:** the measured resolved LT2/VT2 HR, otherwise a standalone measured VT2 (then LT2), otherwise measured LTHR. Time at or above it is labelled anaerobic intensity and time below aerobic intensity, with copy that both energy systems operate at every intensity. For zone summaries, a zone wholly above or below the threshold is assignable; an occupied zone straddling the threshold leaves both totals unavailable. Average HR yields a whole-session steady-state intensity approximation only. A resolved estimated LTHR can divide intensity minutes, and is labelled as estimated; under HR-lowering medication only measured thresholds are used. These labels are product intensity conventions, not measures of energy-system contribution. Sport-specific threshold measurements are not supported by the current measurement schema; these are the profile's all-sports values.

**Golden checks:** male 80 kg/30 y/150 bpm/30 min without VO₂max → 440.9161089866 kcal before widening (308.6412762906–573.1909416826); female 60 kg/40 y/140 bpm → 269.5109942639 (188.6576959847–350.3642925430). With VO₂max 50 and 40 respectively: 424.3056883365 and 257.7289674952 before widening. Male 80 kg/30 y, zones starting at 0/120/150/170, 20 min in zone 2 and 10 min in zone 3 → 262.8321582218–588.8423159656 kcal. No HR, jogging 30 min/80 kg → 300 kcal before widening (210–390). `CardioEnergyTests` pins these independently calculated values; `CardioLogTests` checks snapshot persistence and uploads; `CardioTests` exercises the form and totals.

### Expanded activity catalogue and strength

Additional verified [Walking](https://pacompendium.com/walking/), [Water activities](https://pacompendium.com/water-activities/) and [Conditioning exercise](https://pacompendium.com/conditioning-exercise/) codes:

| Activity code | MET | Description |
| --- | ---: | --- |
| 17151 / 17190 / 17200 | 2.3 / 3.8 / 4.8 | Slow / moderate / brisk level walking |
| 17080 | 6 | Cross-country hiking |
| 02048 / 02049 | 5 / 9 | Moderate / vigorous elliptical |
| 02071 / 02072 / 02073 | 5 / 7.5 / 11 | Rowing machine <100 / 100–149 / 150–199 W |
| 18240 / 18230 | 5.8 / 9.8 | Recreational / fast freestyle swimming |
| 18255 / 18265 | 4.8 / 5.3 | Recreational backstroke / breaststroke |
| 18100 | 5 | Moderate kayaking |
| 02054 | 3.5 | Multiple-exercise strength training, 8–15 reps |

The catalogue is a compiled, versioned subset, not a claim to contain the entire Compendium. Elevation is recorded but does not add an invented calorie correction. Strength always uses METs rather than HR, and is described as mainly anaerobic and carbohydrate-heavy without a numeric substrate or threshold-time split. At 80 kg for one hour it gives 280 gross kcal, widened to 196–364. A logged strength workout's duration runs from its start to its last completed set, not to when Finish is tapped, so a workout left open is not billed for the wait; with no completed set, or more than 24 hours, it has no estimate.

### Population substrate proxy

Primary sources checked in full: Romijn et al. (1993), *Am J Physiol* 265:E380–E391, [DOI](https://doi.org/10.1152/ajpendo.1993.265.3.E380), [primary paper scan](https://cannonbol.com/blog/articles/Romijn_(1993).pdf), methods pp. E381–E382 and Table 1 p. E386; Péronnet & Massicotte (1991), *Can J Sport Sci* 16:23–29, [PubMed](https://pubmed.ncbi.nlm.nih.gov/1645211/), [primary paper scan](https://www.scribd.com/document/637790240/Table-of-nonprotein-respiratory-quotient-an-update), Table 1/equations 5–8 p. 26 and Table 2 p. 27. The revised table differs from Lusk's older table: RQ 0.85 means **48.3% fat energy**, not 49.3%.

Romijn studied **five fasted endurance-trained cyclists**, with mean VO₂max 67 ml/kg/min, not a representative general population sample. At 30 minutes, total fatty-acid oxidation was 26.8, 42.8 and 29.6 µmol/kg/min at 25%, 65% and 85% VO₂max respectively. The study used 860 g/mol triglyceride and three fatty acids per molecule. The app reconstructs a nonprotein RQ proxy using those published oxidation means and the revised gas constants; these are **derived calibration points, not RER observations transcribed from Romijn**:

```
fat_g_per_kg_min = fatty_acid_umol_per_kg_min × 860 / 3,000,000
o2_l_per_kg_min = 0.067 × fraction_of_vo2max
glucose_g_per_kg_min = max(0, (o2_l_per_kg_min − 2.0092 × fat_g_per_kg_min) / 0.7455)
rq = (1.4136 × fat_g_per_kg_min + 0.7426 × glucose_g_per_kg_min) / o2_l_per_kg_min
```

| Fraction VO₂max | Reconstructed RQ |
| ---: | ---: |
| 0.25 | 0.7265129742365304 |
| 0.65 | 0.8305136612663864 |
| 0.85 | 0.9085323839419700 |

Linear interpolation between these points is a **product modelling choice**, not a published universal curve. Below 25%, the RQ interval spans the pure-fat endpoint to the first calibration point; above 85%, it spans the last point to the pure-carbohydrate endpoint. A ±0.05 RQ margin (±0.08 for intervals and effort-only categories), clamped to the endpoints, is an explicit broad product range, **not** a statistical confidence interval or an individually validated prediction. Diet, training, sex, duration and fed state can move actual substrate use substantially. No individualized FatMax or crossover is inferred from this cohort.

HR becomes %HRR and then %VO₂max as planned: `((HR−rest)/(max−rest) × (VO₂max−3.5) + 3.5) / VO₂max`, following [Swain & Leutholtz (1997)](https://doi.org/10.1097/00005768-199703000-00018). When resting HR or usable VO₂max is missing, `HR/max` is a less-specific fallback. HR-lowering medication disables both HR substrate paths. Zone totals allocate the same whole-zone kcal ranges used by the energy calculation, with the RQ range across the zone's HR bounds; average HR assumes steady state.

For RQ between 0.7036 and 0.9960, Péronnet–Massicotte equations 5–8 give:

```
x = (0.7426 − 0.7455 × RQ) / (2.0092 × RQ − 1.4136)  // fat g per glucose g
E = 3.8683 + 9.7460 × x
carbohydrate_energy_share = 3.8683 / E
fat_energy_share = 1 − carbohydrate_energy_share
```

Endpoints are exactly 100% fat or carbohydrate; RER ≥1 is treated as all carbohydrate, never negative fat. Fat grams use **9.746 kcal/g**, glucose-equivalent grams **3.8683 kcal/g**, from the paper, rather than food-label 9/4 factors. The returned kcal/gram ranges are marginal bounds, so adding their independent endpoints does not produce a total-energy interval. The bar displays the midpoint of the shown ranges. Golden tests match Table 2 at RQ 0.80/0.85/0.90/0.95 to its printed precision, and test energy conservation when the RQ is fixed.

### Intensity and effort-only estimates

When measured LT1 and LT2 HR are available, low is below LT1, moderate is LT1 up to LT2, and high is LT2 or above. Occupied zones crossing a boundary leave this split unavailable. One average HR classifies the whole session only when it is steady, never an interval workout. These are intensity labels; both energy systems operate at every intensity.

For a steady session without usable HR, modified Borg CR10 effort supplies low (<4), moderate (4 to <6) or high (≥6), consistent with the [modified scale's category labels](https://my.clevelandclinic.org/health/articles/17450-rated-perceived-exertion-rpe-scale). The fuel fallback uses broad %VO₂max category ranges 0–46%, 46–64%, 64–100%, using the light/moderate/vigorous boundaries in Garber (2011) Table 4. **Linking these two categorical scales is a product assumption, not a validated individual RPE→VO₂ equation.** The wider RQ margin applies. It should be interpreted only as a coarse population proxy. Without HR or effort, fuel and intensity are unavailable. A single effort rating never invents an interval time distribution.

### Plate loading and training volume

`PlateLoading` combines matching pairs from the finite location inventory, after grouping identical weight/unit entries. Canonical kg uses the existing exact 0.45359237 kg/lb factor. Plates up to 1000 in their own unit are accepted, matching the stored location inventory. The bounded search groups reachable per-side loads at 1 g resolution and keeps the lightest exact load in each group, so a plate that fits on a slightly heavier near-equal load also fits on it; the answer is the heaviest exact combination found that does not exceed the target, and the empty bar is the minimum. With 2.5 kg and 5.512 lb (≈2.50018 kg) plates in the same 1 g group, 1.25 kg can still be added to reach exactly 27.5 kg on a 20 kg bar. Returned totals are computed from the original exact plate weights, not rounded display values. An odd leftover plate cannot be used. The result is applied explicitly to unfinished non-warm-up sets, leaving completed sets and records unchanged. A 20 kg bar, two 15 kg plates and six 10 kg plates can load 80 kg: three 10 kg plates per side, a combination a greedy loader misses.

Weekly volume is completed non-warm-up set load in canonical kg × reps, grouped by primary muscle, in finished sessions dated Monday–Sunday. Each primary muscle gets the full set, so totals across muscles are not additive. Secondary muscles do not receive guessed fractional sets. e1RM history uses completed Epley estimates from finished sessions only, best per local date, so changing display units or mixing formula histories cannot manufacture a trend.

## Nutrition amounts and recipes (step 1.3)

Implemented in `Health360.Domain/Nutrition`; tests in `tests/Health360.Domain.Tests/Nutrition`.

**Nutrients.** Each nutrient has a database code and one fixed unit (`Nutrient`): energy in kcal, macronutrients in g, cholesterol, sodium, potassium, calcium and iron in mg, vitamin D in µg (code `ug`). Amounts are never entered in a user's unit, so they carry no unit column. The catalogue is the Nutrition Facts label set, then the vitamins and minerals with an FDA Daily Value ([21 CFR 101.9(c)(8)(iv)](https://www.ecfr.gov/current/title-21/section-101.9)) that USDA FoodData Central reports, then alcohol and caffeine. Where a code's measure is narrower than its name: `vitamin_a` is µg retinol activity equivalents (RAE), `vitamin_e` is mg alpha-tocopherol, `vitamin_k` is µg phylloquinone, `niacin` is preformed niacin in mg (not niacin equivalents, which add tryptophan), and `folate` is µg dietary folate equivalents (DFE). `nutrients` in the database is seeded with the same codes, units and order (migrations `20261107000001_nutrition.sql` and `20261107000002_micronutrients.sql`; `CanonicalUnitParityTests` checks them).

**Unknown is not zero.** A nutrient a food doesn't list is absent from its `NutrientAmounts`, not 0. A sum (`NutrientTotal.Sum`) adds the known parts and lists in `Incomplete` every nutrient some parts knew and others didn't: that total is only a lower bound, and the app must say so. A nutrient no part knew stays absent.

**Basis and amounts.** A food's values are per 100 g, or per 100 ml for a drink labelled by volume (`FoodBasis`). An amount as entered is converted to the basis unit with `Health360.Domain/Units` (`FoodAmount.ToBase`): ounces of a per-100 g food become grams exactly, cups of a per-100 ml drink become millilitres. A mass unit is refused for a volume-basis food and a volume unit for a mass-basis one: grams per cup depend on the food, so a cup of flour is a portion (`FoodPortion`, grams per portion), and count × grams per portion gives the base amount.

**Scaling.** The nutrients in an amount are each per-100 value × amount / 100, in that order (`NutrientAmounts.ForAmount`); `Per100` reverses it (value × 100 / amount).

**Recipes.** `RecipeNutrition.Compute` adds each ingredient's nutrients for its amount, in ingredient order, and divides the total by the number of servings. Per 100 g of the dish uses the cooked weight when it was weighed (cooking changes weight through water, not the nutrients counted here) and otherwise the raw ingredients' total mass, which is known only when every ingredient is measured by mass. An ingredient with a zero amount is left out, so it can't make a nutrient incomplete or the raw weight unknown.

Golden values (test fixture): 80 g oats (379 kcal, 13.2 g protein, 10.1 g fiber per 100 g), 120 g banana (89 kcal, 1.09 g protein, 2.6 g fiber) and 32 g peanut butter (588 kcal, 25.1 g protein, no fiber listed), 2 servings:

| | Hand calculation | Result |
|---|---|---|
| Energy | 379 × 80/100 + 89 × 120/100 + 588 × 32/100 = 303.2 + 106.8 + 188.16 | 598.16 kcal; 299.08 per serving |
| Protein | 10.56 + 1.308 + 8.032 | 19.9 g; 9.95 per serving |
| Fiber | 8.08 + 3.12, peanut butter unknown | 11.2 g, incomplete |
| Per 100 g, raw weight 232 g | 598.16 × 100 / 232 | 257.83 kcal |
| Per 100 g, cooked weight 400 g | 598.16 × 100 / 400 | 149.54 kcal |

## USDA FoodData Central import (step 1.3)

Implemented in `tools/importers` (`FoodDataCentral`, `FoodImporter`); tests in `tests/Health360.Importers.Tests`. It reads the JSON downloads of Foundation Foods, SR Legacy and FNDDS survey foods from [FoodData Central](https://fdc.nal.usda.gov/download-datasets) (public domain, CC0 1.0) and upserts them as catalogue foods (`household_id` null, `source` `usda_fdc`, `external_id` the `fdcId`, `basis` g). Branded foods are not imported. The mapping was checked against the Foundation 2024-10-31, SR Legacy 2018-04 and FNDDS 2024-10-31 files: 286, 7,793 and 5,431 foods.

**Nutrients by FoodData Central nutrient id**, with the unit the file reports required to be the nutrient's own (a food listing one in another unit is skipped, so a changed file can't store wrong amounts). Values are per 100 g, as the datasets give them, stored unrounded. An entry without an amount is unknown and left out; a listed 0 is 0.

| Code | FDC nutrient id (name) |
|---|---|
| energy | 1008 (Energy, kcal); else 2048 (Energy, Atwater Specific Factors); else 2047 (Energy, Atwater General Factors) |
| protein, fat, carbohydrate | 1003, 1004, 1005 (Carbohydrate, by difference, the total US labels declare) |
| saturated_fat, trans_fat, cholesterol, sodium, fiber | 1258, 1257, 1253, 1093, 1079 |
| sugars | 2000 (Total Sugars); else 1063 (Sugars, Total, which Foundation Foods use) |
| vitamin_d | 1114 (Vitamin D (D2 + D3), µg; the IU entry 1110 is ignored) |
| calcium, iron, potassium | 1087, 1089, 1092 |
| vitamin_a, vitamin_c, vitamin_e, vitamin_k | 1106 (RAE), 1162, 1109 (alpha-tocopherol), 1185 (phylloquinone) |
| thiamin, riboflavin, niacin, vitamin_b6, folate, vitamin_b12 | 1165, 1166, 1167, 1175, 1190 (Folate, DFE), 1178 |
| biotin, pantothenic_acid, choline | 1176, 1170, 1180 |
| phosphorus, iodine, magnesium, zinc, selenium, copper, manganese, molybdenum | 1091, 1100, 1090, 1095, 1103, 1098, 1101, 1102 |
| alcohol, caffeine | 1018, 1057 |

Energy: most Foundation Foods give only the Atwater energies, which USDA computes from the food's own composition; the specific-factor value is preferred as the closer one, and the dataset's own Energy wins whenever it is present. kJ (1062) is not converted, so a food with no kcal energy is skipped. Folate: Foundation Foods report total folate (1177), not DFE; the two differ for foods with folic acid, so their folate stays unknown rather than substituted. Added sugars and sugar alcohols are not in these datasets.

**Skipped foods**, each reported with the field at fault: no positive `fdcId`, no description or longer than 200 characters, a repeated id, no energy, a nutrient in an unexpected unit, negative or listed twice with different amounts, or more than 100 g of protein, carbohydrate or fat per 100 g. In the files above that is 30 Foundation Foods, none listing energy (mostly dry-basis "0% moisture" bean analyses, and iodized table salt), and 1 FNDDS food with no nutrients.

**Portions** become named measures in grams, in the file's `sequenceNumber` order: Foundation Foods as count, unit and modifier ("1 cup, slices with skin"), SR Legacy as count and modifier ("1 cup, chopped"), FNDDS by its description ("1 meatball"). A portion is dropped without a positive gram weight (at most 100,000 g), with a name over 80 characters, a name already used (the first is kept), past the 50th, or as one of FNDDS's survey-coding defaults ("Quantity not specified", "Guideline amount per …").

**Re-importing** updates a food only when one of its values changed, leaves identical rows untouched, never deletes a food gone from the file (households' copies keep their own values), and records the release in `reference_datasets` per dataset (`usda-fdc-foundation`, `usda-fdc-sr-legacy`, `usda-fdc-survey`).

## Label checks (step 1.3)

`LabelChecks.Check` looks for misreads in values read from a Nutrition Facts label before the confirm screen. It only makes fields suspect for the user to check; it never changes or rejects them.

**Printed values are intervals.** Each printed value stands for every true value that rounds to it under the FDA rules, [21 CFR 101.9(c)](https://www.ecfr.gov/current/title-21/section-101.9), checked against the eCFR text:
- Calories (c)(1): nearest 5 kcal up to and including 50, nearest 10 above; under 5 may be 0. So 0 means 0–5, 50 means 47.5–55 and 120 means 115–125.
- Fat (c)(2): nearest 0.5 g below 5 g, nearest gram above; under 0.5 g is 0.
- Carbohydrate and fiber (c)(6), protein (c)(7): nearest gram; under 1 g may be printed "less than 1 gram" and under 0.5 g as 0. A printed 0 is taken as 0–1 g in case "less than 1 g" was read as 0. Sugar alcohols are given the same interval (an assumption; no separate rule is applied).
- kJ: the FDA sets no rounding for kJ, so a kJ value gets the interval of the kcal value it stands for, × 4.184.
- Serving size: ± 0.5 g or ml (an assumption: labels print whole units).

A label rounded more finely (EU labels give fat to 0.1 g, for example) has narrower true intervals that lie inside these, so it is not flagged for its rounding. Per-100 columns are given the same intervals. A check fails only when no values inside the intervals can agree.

**Energy against macros.** The general factors are 4, 4 and 9 kcal per gram of protein, total carbohydrate and fat, and labels may instead count carbohydrate less non-digestible carbohydrates and sugar alcohols (101.9(c)(1)(i)(B) and (C)). EU labels count carbohydrate without fibre and fibre at 2 kcal/g ([Regulation (EU) 1169/2011, Annex XIV](https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=CELEX:32011R1169)). The energy may be anywhere from

lowest = 4 × protein + 4 × max(0, carbohydrate − fiber − sugar alcohols) + 9 × fat, with each interval's low end and the fiber and sugar alcohols' high ends,

to highest = 4 × protein + 4 × carbohydrate + 9 × fat + 2 × fiber, with the high ends.

Both add 7 kcal/g of alcohol and 3 kcal/g of organic acids (Annex XIV) when the label prints them, each given the whole-gram interval. Labels rarely print organic acids, so a correct label whose energy comes mostly from them (vinegar, kombucha) can still be flagged; the user then confirms the value. That range is widened by **10% on each side, a product choice and not a sourced figure**: labels may also use food-specific Atwater factors or bomb calorimetry (methods (A) and (E)), which the general factors can't reproduce. The check needs kcal, protein, carbohydrate and fat. Without a fiber value the lowest estimate counts all carbohydrate, so a high-fiber food can be flagged until its fiber is read.

**kJ against kcal.** Annex XIV's factors give between 4.0 kJ/kcal (fibre, 8 kJ / 2 kcal) and 4.33 kJ/kcal (organic acid, 13 kJ / 3 kcal); 4.184 (the thermochemical calorie) lies between. The kJ interval must meet [4.0 × kcal low, 13/3 × kcal high].

**Weight checks**, only when the basis is mass (a volume's weight depends on density):
- Protein + carbohydrate + fat, plus alcohol and organic acids when printed, low ends, may not exceed the amount's high end (100 g, or the serving size). Fiber is left out, because on US labels it is part of carbohydrate already.
- Energy may not exceed 9 kcal per gram (fat's general factor, the densest), widened by the same 10%.

**Columns.** When both columns and the serving size are known, each value present in both must agree: the per-serving interval must meet the per-100 interval × the serving interval / 100.

A property test rounds labels built from exact 4/4/9 energy with the FDA rules, independently written in the test, and checks that none is ever flagged.

## What the 1.1 exit criterion proves

Step 1.1 proves that entering a lab RMR changes the physiology profile's maintenance estimate at once, and that deleting it falls back to the next value or the estimate. It also proves that a measured max HR replaces the Tanaka estimate, and that the zones derived from max HR follow it (`HeartRateZoneRules.Derive`). Nutrition targets built on this come in step 1.3 and have their own integration test (PLAN.md §3).

## Still to specify

These are in PLAN.md §2.8 but not implemented, because they couldn't be checked against primary sources yet:
- **Non-exercise VO₂max, Nes et al. (2011)**, *Med Sci Sports Exerc* 43(11):2024–2030 ([doi:10.1249/MSS.0b013e31821d3f6f](https://doi.org/10.1249/MSS.0b013e31821d3f6f)). The full text is paywalled. The equations and the PA-index scoring need checking against the paper.
- **LTHR estimate (about 90% of max HR).** No primary source found yet for the 90% figure. Until there is one it stays unimplemented. Zones don't depend on it, because the LTHR estimate never sets zones.
