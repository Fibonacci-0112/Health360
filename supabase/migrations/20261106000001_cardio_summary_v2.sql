-- Version-2 cardio summaries add elevation, end time, intensity minutes and fuel ranges. Extend the workout-visible
-- allow-list so the privacy trigger keeps them, while physiology, zones, zone times and notes stay private.
-- Intensity minutes are kept only when derived from RPE alone (see below).

-- An absent range stays JSON null rather than becoming {"low":null,"high":null}, which clients reject.
create function private.cardio_summary_range(value jsonb) returns jsonb
language sql immutable set search_path = '' as $$
  select case when jsonb_typeof(value) = 'object'
    then jsonb_build_object('low', value -> 'low', 'high', value -> 'high') end
$$;
revoke all on function private.cardio_summary_range(jsonb) from public, anon;
grant execute on function private.cardio_summary_range(jsonb) to authenticated;

create or replace function private.cardio_workout_summary(value jsonb) returns jsonb
language sql immutable set search_path = '' as $$
  select jsonb_build_object(
    'version', value -> 'version',
    'entry', jsonb_build_object(
      'activity_code', value -> 'entry' -> 'activity_code',
      'duration_seconds', value -> 'entry' -> 'duration_seconds',
      'average_hr', value -> 'entry' -> 'average_hr',
      'max_hr', value -> 'entry' -> 'max_hr',
      'rpe', value -> 'entry' -> 'rpe',
      'intervals', value -> 'entry' -> 'intervals',
      'distance', value -> 'entry' -> 'distance',
      'distance_unit', value -> 'entry' -> 'distance_unit',
      'elevation', value -> 'entry' -> 'elevation',
      'elevation_unit', value -> 'entry' -> 'elevation_unit',
      'ended_at', value -> 'entry' -> 'ended_at'
    ),
    'activity', jsonb_build_object(
      'code', value -> 'activity' -> 'code',
      'name', value -> 'activity' -> 'name',
      'sport', value -> 'activity' -> 'sport',
      'mets', value -> 'activity' -> 'mets'
    ),
    'method', replace(value ->> 'method', ' (VO₂max)', ''),
    'kcal', private.cardio_summary_range(value -> 'kcal'),
    'aerobic_seconds', value -> 'aerobic_seconds',
    'anaerobic_seconds', value -> 'anaerobic_seconds',
    'distance_metres', value -> 'distance_metres',
    -- Intensity is shared only when it follows from shared fields alone: RPE categories with no HR input. Otherwise its
    -- presence or method can reveal measured thresholds or the heart-rate-lowering-medication path.
    'intensity', case when jsonb_typeof(value -> 'intensity') = 'object'
      and value -> 'intensity' ->> 'method' = 'RPE categories'
      and jsonb_typeof(coalesce(value -> 'entry' -> 'average_hr', 'null')) = 'null'
      and jsonb_typeof(coalesce(value -> 'entry' -> 'zone_seconds', 'null')) = 'null' then jsonb_build_object(
      'low_seconds', value -> 'intensity' -> 'low_seconds',
      'moderate_seconds', value -> 'intensity' -> 'moderate_seconds',
      'high_seconds', value -> 'intensity' -> 'high_seconds',
      'method', value -> 'intensity' -> 'method'
    ) end,
    'fuel', case when jsonb_typeof(value -> 'fuel') = 'object' then jsonb_build_object(
      'fat_kcal', private.cardio_summary_range(value -> 'fuel' -> 'fat_kcal'),
      'carb_kcal', private.cardio_summary_range(value -> 'fuel' -> 'carb_kcal'),
      'fat_grams', private.cardio_summary_range(value -> 'fuel' -> 'fat_grams'),
      'carb_grams', private.cardio_summary_range(value -> 'fuel' -> 'carb_grams'),
      'method', value -> 'fuel' -> 'method'
    ) end,
    'elevation_metres', value -> 'elevation_metres'
  )
$$;

-- Rebuild summaries from the private snapshots, restoring fields the version-1 allow-list removed.
update public.workout_sessions s
set energy_estimate = private.cardio_workout_summary(c.estimate)
from public.cardio_estimates c
where c.id = s.id and c.profile_id = s.profile_id and s.kind = 'cardio'
  and jsonb_typeof(c.estimate) = 'object'
  and s.energy_estimate is distinct from private.cardio_workout_summary(c.estimate);
