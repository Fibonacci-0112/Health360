# Calculations: body and physiology (Phase 1, step 1.1)

The exact rules `Health360.Domain` implements for the body and physiology screens, with their sources. Code, tests and this page must agree; change them together. General rules are in [PLAN.md §2.8](PLAN.md) and the units rules in §2.6. Every formula here takes canonical units (kg, cm, years). Convert with `Health360.Domain/Units` and never through a rounded display value.

Status: what's below is implemented and tested in `Health360.Domain` (`Body/`, `Physiology/`). Not yet specified: the LTHR estimate and the Nes et al. (2011) VO₂max estimator. They wait until they're checked against primary sources, not secondary ones (see [Still to specify](#still-to-specify)).

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

- **PA-R** is the NASA/Johnson Space Center physical activity rating, an integer from 0 to 7. The paper doesn't print the scale; it cites Ross, R. M. and Jackson, A. S. (1990), *Exercise Concepts, Calculations, and Computer Applications*, Benchmark Press. The questionnaire and its scoring are taken from the **NHANES Cardiovascular Fitness Procedures Manual** (CDC, NHANES 1999–2004), Appendix E, pp. E-1 and E-3 ([cdc.gov](https://wwwn.cdc.gov/nchs/data/nhanes/public/2003/manuals/cv_99-04.pdf)), which cites both sources. NHANES Appendix F uses the same BMI equation to predict VO₂max. `PhysicalActivityRating` implements the scoring. The five onboarding activity levels are not PA-R and are not mapped onto it.

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

Deleting a measurement re-resolves straight away: the next measured value, then the estimate (`ResolverTests`).

## Putting the profile together

`PhysiologyProfile.Build` turns the stored measurements and the profile's fields into what the Body and Physiology screens show. It is a pure function of its inputs and is rebuilt after every change, so entering or deleting a value changes everything that uses it at once. The choices below are this app's, not a published method's:

- **Values:** each measurement's canonical value (`value_canonical`) and its local date. Each metric goes through the resolver on its own (with the user's pin, when there is one).
- **Body weight in formulas:** the newest point of the weight trend (every weigh-in, whatever its source), not the last raw weigh-in. BMI uses it with the profile height.
- **Body fat:** measured `body_fat_pct` values only (DEXA, a scale, a value entered by hand). The Navy estimate is shown beside them but isn't stored or resolved. Fat-free mass is trend weight × (1 − resolved %BF / 100), so Katch-McArdle replaces Mifflin-St Jeor as soon as any body-fat value is entered.
- **Navy estimate:** from the newest neck and waist (and hip, for women) measurements, each taken on its own: the latest day, then the later entry. Men use the waist at the navel and women the waist at the narrowest point, as the equations require. A combination the equation can't use (a waist not larger than the neck) gives no estimate.
- **Thresholds:** lactate thresholds (LT1 and LT2) when both have a value, otherwise ventilatory ones (VT1 and VT2) when both do. Zones need both of a pair (see above).
- **RMR retest flag:** the weight "then" is the trend on or before the test date. With no weigh-in by then, only the 12-month rule applies.
- **VO₂max estimate:** Jackson only without a measured resting HR, and only with a PA-R answer. The app doesn't ask the PA-R questions yet, so for now VO₂max shows only measured values.
- **Medication flag:** `profiles.takes_hr_lowering_medication`. When it's true, Tanaka gives nothing and zones need a measured max HR (or a measured basis above it).

The golden checks are in `PhysiologyProfileTests` (Domain) and, through the screens, `PhysiologyTests` (UI): a lab RMR changes maintenance and deleting it falls back, and a measured max HR changes the zones and deleting it falls back.

## What the 1.1 exit criterion proves

Step 1.1 proves that entering a lab RMR changes the physiology profile's maintenance estimate at once, and that deleting it falls back to the next value or the estimate. It also proves that a measured max HR replaces the Tanaka estimate, and that the zones derived from max HR follow it (`HeartRateZoneRules.Derive`). Nutrition targets built on this come in step 1.3 and have their own integration test (PLAN.md §3).

## Still to specify

These are in PLAN.md §2.8 but not implemented, because they couldn't be checked against primary sources yet:
- **Non-exercise VO₂max, Nes et al. (2011)**, *Med Sci Sports Exerc* 43(11):2024–2030 ([doi:10.1249/MSS.0b013e31821d3f6f](https://doi.org/10.1249/MSS.0b013e31821d3f6f)). The full text is paywalled. The equations and the PA-index scoring need checking against the paper.
- **LTHR estimate (about 90% of max HR).** No primary source found yet for the 90% figure. Until there is one it stays unimplemented. Zones don't depend on it, because the LTHR estimate never sets zones.
