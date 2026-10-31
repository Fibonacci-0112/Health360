-- Drop profiles.physiology_prefs.
--
-- It was a jsonb bag for physiology settings. A jsonb object is uploaded whole, so two devices changing
-- different keys overwrite each other (see 20261031000003_hr_medication_column.sql). Its only key, the
-- heart-rate-lowering medication flag, moved to profiles.takes_hr_lowering_medication, and pins live in
-- metric_pins. Nothing in the app reads or writes the column any more.
--
-- Dropping the column also drops its column-level UPDATE grant. A client that still uploads an update
-- naming physiology_prefs fails permanently and lands in failed_uploads, so ship this with (or after) the
-- app version whose AppSchema no longer has the column.

alter table public.profiles
  drop column physiology_prefs;
