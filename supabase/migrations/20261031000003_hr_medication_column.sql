-- The heart-rate-lowering medication flag gets its own column (PLAN.md §2.3, §2.5).
--
-- It lived in profiles.physiology_prefs (jsonb). Clients upload an update as the columns it changed, and
-- the last write to each column wins, so two settings in one jsonb object overwrite each other: a device
-- that changed one key while another device changed a different key would send back the whole object.
-- Separate columns (and, for the pins, a table of their own when they arrive) need no merge logic.
--
-- physiology_prefs stays for now, unused; it is dropped once no app version writes it.

alter table public.profiles
  add column takes_hr_lowering_medication boolean not null default false;

comment on column public.profiles.takes_hr_lowering_medication is
  'The user takes medication that lowers heart rate (a beta blocker, for example). Nothing is then derived from '
  'age-predicted max HR.';

-- Carry over any flag already written to the old key, and remove the key.
update public.profiles
set takes_hr_lowering_medication = coalesce(physiology_prefs -> 'heartRateLoweringMedication' = 'true'::jsonb, false),
    physiology_prefs = physiology_prefs - 'heartRateLoweringMedication'
where physiology_prefs ? 'heartRateLoweringMedication';

-- Owners edit their own fields only (profiles_update policy); this column is one of them.
grant update (takes_hr_lowering_medication) on public.profiles to authenticated;
