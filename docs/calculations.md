# Calculations: body and physiology (Phase 1, step 1.1)

The exact rules `Health360.Domain` implements for the body and physiology screens, with their sources. Code, tests and this page must agree; change them together. General rules are in [PLAN.md §2.8](PLAN.md) and the units rules in §2.6. Every formula here takes canonical units (kg, cm, years). Convert with `Health360.Domain/Units` and never through a rounded display value.

Status: what's below is implemented and tested in `Health360.Domain` (`Body/`, `Physiology/`). Not yet specified: the LTHR estimate and the non-exercise VO₂max estimators (Nes et al., 2011; Jackson et al., 1990). They wait until their coefficients are checked against the papers, not secondary sources (see [Still to specify](#still-to-specify)).

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
1. **Lab thresholds**, when LT1 (or VT1) and LT2 (or VT2) heart rates are both measured and LT1 < LT2.
2. **LTHR (Friel)**, from a measured or test-derived LTHR. Without one, a measured LT2/VT2 is taken as LTHR (§2.8). Friel publishes zones for run and bike only, so other sports skip this basis.
3. **Heart rate reserve (Karvonen)**, when max HR and a measured resting HR are known and resting < max.
4. **% of max HR.**

Only measured values (any source tier) feed bases 1–3; estimated thresholds, LTHR and resting HR are ignored, so **the LTHR estimate never sets zones**. Max HR may be the Tanaka estimate, except when the heart-rate-lowering medication flag is set (PLAN.md §2.5). Then an estimated max HR is not used, and with no measured value there are no HR zones. Inputs that would give zones that don't ascend (LT1 ≥ LT2, resting ≥ max) skip that basis instead.

**Reading whole-percent ranges.** Both sources give bands as whole percentages ("85% to 89%", then "90% to 94%"). The app reads a band "a–b" as [a, b + 1), so each zone starts at its own first percentage and there are no gaps. So Friel's zone 5c ("more than 106%") starts at 107%. Every lower bound is computed as offset + percent × (base / 100).

### Lab thresholds: three zones

**Source:** Seiler, K. S. and Kjerland, G. Ø. (2006). Quantifying training intensity distribution in elite endurance athletes: is there evidence for an "optimal" distribution? *Scand J Med Sci Sports* 16(1):49–56 ([doi:10.1111/j.1600-0838.2004.00418.x](https://doi.org/10.1111/j.1600-0838.2004.00418.x)). VT1 and VT2 delineate three intensity zones.

| Zone | Starts at |
|---|---|
| 1 | 0 (below LT1/VT1) |
| 2 | LT1/VT1 |
| 3 | LT2/VT2 |

Golden value: LT1 140, LT2 165 → 0, 140, 165.

### LTHR: Friel

**Source:** Friel, J. *Joe Friel's Quick Guide to Setting Zones*, TrainingPeaks ([trainingpeaks.com](https://www.trainingpeaks.com/learn/articles/joe-friel-s-quick-guide-to-setting-zones/)), which gives % of LTHR for run and bike. Friel's own site revised the guide in September 2025 ([joefrieltraining.com](https://joefrieltraining.com/a-quick-guide-to-setting-zones/)) and now gives the table as an image, which we haven't checked. Re-check before changing anything here.

| Zone | Run (% of LTHR) | Bike (% of LTHR) |
|---|---|---|
| 1 | < 85 | < 81 |
| 2 | 85–89 | 81–89 |
| 3 | 90–94 | 90–93 |
| 4 | 95–99 | 94–99 |
| 5a | 100–102 | 100–102 |
| 5b | 103–106 | 103–106 |
| 5c | > 106 | > 106 |

Lower bounds used (%): run 0, 85, 90, 95, 100, 103, 107; bike 0, 81, 90, 94, 100, 103, 107. Golden values: run LTHR 170 → 0, 144.5, 153, 161.5, 170, 175.1, 181.9; bike LTHR 160 → 0, 129.6, 144, 150.4, 160, 164.8, 171.2.

### Heart rate reserve and % of max HR: Garber et al. (2011)

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

## What the 1.1 exit criterion proves

Step 1.1 proves that entering a lab RMR changes the physiology profile's maintenance estimate at once, and that deleting it falls back to the next value or the estimate. It also proves that a measured max HR replaces the Tanaka estimate, and that the zones derived from max HR follow it (`HeartRateZoneRules.Derive`). Nutrition targets built on this come in step 1.3 and have their own integration test (PLAN.md §3).

## Still to specify

These are in PLAN.md §2.8 but not implemented, because their coefficients couldn't be checked against the original papers yet:
- **Non-exercise VO₂max, Nes et al. (2011)**, *Med Sci Sports Exerc* 43(11):2024–2030 ([doi:10.1249/MSS.0b013e31821d3f6f](https://doi.org/10.1249/MSS.0b013e31821d3f6f)). The full text is paywalled. The equations and the PA-index scoring need checking against the paper.
- **Non-exercise VO₂max, Jackson et al. (1990)**, *Med Sci Sports Exerc* 22(6):863–870 ([doi:10.1249/00005768-199012000-00021](https://doi.org/10.1249/00005768-199012000-00021)). The full text is paywalled. Secondary sources disagree on the %fat model's fat coefficient (0.552 or 0.522), and the paper reports more than one fit (for example, a men-only %fat model), so the model and its coefficients must come from the paper itself.
- **LTHR estimate (about 90% of max HR).** No primary source found yet for the 90% figure. Until there is one it stays unimplemented. Zones don't depend on it, because the LTHR estimate never sets zones.
