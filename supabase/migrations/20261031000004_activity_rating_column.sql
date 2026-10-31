-- The NASA/JSC physical activity rating (PA-R, 0–7) that the Jackson et al. (1990) VO₂max estimate takes
-- (docs/calculations.md). The app scores the NHANES Appendix E questionnaire and stores only the code;
-- null means the questions haven't been answered, and then nothing is estimated from it.
--
-- A column of its own, like takes_hr_lowering_medication, so an upload carries only this column and the last
-- write to it wins without touching another device's change to a different setting.

alter table public.profiles
  add column activity_rating smallint,
  add constraint profiles_activity_rating_range check (activity_rating between 0 and 7);

comment on column public.profiles.activity_rating is
  'NASA/JSC physical activity rating (PA-R), 0–7, from the NHANES 1999–2004 Cardiovascular Fitness Procedures '
  'Manual, Appendix E questionnaire. Null until answered. Not the onboarding activity_level.';

-- Owners edit their own fields only (profiles_update policy); this column is one of them.
grant update (activity_rating) on public.profiles to authenticated;
