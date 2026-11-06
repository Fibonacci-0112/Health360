-- Version-2 cardio summaries keep elevation, end time, RPE-only intensity and fuel on workout rows, while physiology, zones,
-- zone times and notes stay in the owner-only cardio_estimates. Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(17);

insert into auth.users (id, email) values ('00000000-0000-0000-0000-00000000000a', 'a@example.com');
select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);

create function pg_temp.login(who text) returns void language sql as $$
  select set_config('request.jwt.claims', json_build_object(
    'sub', '00000000-0000-0000-0000-00000000000' || who, 'role', 'authenticated')::text, true);
$$;
grant execute on function pg_temp.login(text) to authenticated;

set local role authenticated;
select pg_temp.login('a');

-- A full version-2 snapshot, as an older client may still upload it on the workout row.
insert into workout_sessions (id, profile_id, kind, started_at, ended_at, local_date, energy_estimate)
  values ('64000000-0000-7000-8000-000000000001', current_setting('t.a')::uuid, 'cardio',
          '2026-11-05T18:00:00Z', '2026-11-05T18:30:00Z', '2026-11-05',
          '{"version":2,
            "entry":{"activity_code":"12030","duration_seconds":1800,"average_hr":150,"max_hr":null,"zone_seconds":[600,1200],
                     "rpe":5,"intervals":false,"distance":5,"distance_unit":"km","elevation":120,"elevation_unit":"ft",
                     "ended_at":"2026-11-05T18:30:00+00:00"},
            "activity":{"code":"12030","name":"Running","sport":0,"mets":8.5},
            "physiology":{"weight_kg":80},"zones":{"inputs":[{"value":160}]},"notes":["Private input"],
            "method":"Keytel (VO₂max), average HR","kcal":{"low":300,"high":400},
            "aerobic_seconds":1500,"anaerobic_seconds":300,"distance_metres":5000,
            "intensity":{"low_seconds":600,"moderate_seconds":900,"high_seconds":300,"method":"Measured LT1/LT2 HR"},
            "fuel":{"fat_kcal":{"low":100,"high":150},"carb_kcal":{"low":200,"high":250},
                    "fat_grams":{"low":11,"high":17},"carb_grams":{"low":50,"high":62},"method":"Romijn 1993 population proxy"},
            "elevation_metres":36.576}');

select is((select energy_estimate -> 'intensity' from workout_sessions where id = '64000000-0000-7000-8000-000000000001'),
  'null'::jsonb, 'the summary drops intensity from measured thresholds');
select is((select energy_estimate -> 'fuel' from workout_sessions where id = '64000000-0000-7000-8000-000000000001'),
  '{"fat_kcal":{"low":100,"high":150},"carb_kcal":{"low":200,"high":250},"fat_grams":{"low":11,"high":17},
    "carb_grams":{"low":50,"high":62},"method":"Romijn 1993 population proxy"}'::jsonb,
  'the summary keeps fuel ranges');
select is((select (energy_estimate ->> 'elevation_metres')::numeric from workout_sessions where id = '64000000-0000-7000-8000-000000000001'),
  36.576, 'the summary keeps canonical elevation');
select is((select (energy_estimate -> 'entry') - array['activity_code', 'duration_seconds', 'average_hr', 'max_hr', 'rpe',
                  'intervals', 'distance', 'distance_unit']
           from workout_sessions where id = '64000000-0000-7000-8000-000000000001'),
  '{"elevation":120,"elevation_unit":"ft","ended_at":"2026-11-05T18:30:00+00:00"}'::jsonb,
  'the summary keeps the new entry fields and drops zone times');
select is((select energy_estimate ->> 'method' from workout_sessions where id = '64000000-0000-7000-8000-000000000001'),
  'Keytel, average HR', 'the summary method omits VO₂max provenance');
select ok((select not (energy_estimate ?| array['physiology', 'zones', 'notes']) from workout_sessions
           where id = '64000000-0000-7000-8000-000000000001'),
  'the summary omits physiology, zones and notes');
select is((select estimate -> 'fuel' -> 'fat_kcal' from cardio_estimates where id = '64000000-0000-7000-8000-000000000001'),
  '{"low":100,"high":150}'::jsonb, 'the full snapshot is kept privately');
select is(
  (select private.cardio_workout_summary(energy_estimate) = energy_estimate from workout_sessions
   where id = '64000000-0000-7000-8000-000000000001'),
  true, 'summarising a summary changes nothing');

-- The current client uploads the summary to the workout row and the full snapshot to cardio_estimates.
insert into workout_sessions (id, profile_id, kind, started_at, ended_at, local_date, energy_estimate)
  values ('64000000-0000-7000-8000-000000000002', current_setting('t.a')::uuid, 'cardio',
          '2026-11-05T19:00:00Z', '2026-11-05T19:20:00Z', '2026-11-05',
          '{"version":2,"entry":{"activity_code":"02054","duration_seconds":1200,"zone_seconds":null,"rpe":7,"intervals":false},
            "activity":{"code":"02054","name":"Strength","sport":2,"mets":5},"method":"Unavailable","kcal":null,
            "aerobic_seconds":null,"anaerobic_seconds":null,"distance_metres":null,"intensity":null,"fuel":null,
            "elevation_metres":null}');
insert into cardio_estimates (id, profile_id, estimate)
  values ('64000000-0000-7000-8000-000000000002', current_setting('t.a')::uuid,
          '{"version":2,"physiology":{"weight_kg":null},"notes":["Private"]}');

select is((select energy_estimate -> 'kcal' from workout_sessions where id = '64000000-0000-7000-8000-000000000002'),
  'null'::jsonb, 'a missing energy range stays null');
select is((select energy_estimate -> 'fuel' from workout_sessions where id = '64000000-0000-7000-8000-000000000002'),
  'null'::jsonb, 'a missing fuel estimate stays null');
select is((select energy_estimate -> 'intensity' from workout_sessions where id = '64000000-0000-7000-8000-000000000002'),
  'null'::jsonb, 'a missing intensity split stays null');
select is((select energy_estimate -> 'entry' ->> 'rpe' from workout_sessions where id = '64000000-0000-7000-8000-000000000002'),
  '7', 'the uploaded summary keeps its entry');
select is((select estimate -> 'notes' from cardio_estimates where id = '64000000-0000-7000-8000-000000000002'),
  '["Private"]'::jsonb, 'an uploaded summary does not replace the private snapshot');
select is(
  (select private.cardio_workout_summary('{"fuel":{"fat_kcal":null,"carb_kcal":{"low":1,"high":2},"method":"m"}}') -> 'fuel'),
  '{"fat_kcal":null,"carb_kcal":{"low":1,"high":2},"fat_grams":null,"carb_grams":null,"method":"m"}'::jsonb,
  'nested fuel ranges keep nulls as null');
select is(
  (select private.cardio_workout_summary('{"entry":{"rpe":5,"zone_seconds":null},
     "intensity":{"low_seconds":0,"moderate_seconds":1800,"high_seconds":0,"method":"RPE categories"}}') -> 'intensity'),
  '{"low_seconds":0,"moderate_seconds":1800,"high_seconds":0,"method":"RPE categories"}'::jsonb,
  'RPE intensity without HR inputs is shared');
select is(
  (select private.cardio_workout_summary('{"entry":{"average_hr":140,"rpe":5},
     "intensity":{"low_seconds":0,"moderate_seconds":1800,"high_seconds":0,"method":"RPE categories"}}') -> 'intensity'),
  'null'::jsonb, 'RPE intensity alongside HR, the medication path, is not shared');
select is(
  (select private.cardio_workout_summary('{"entry":{"rpe":5,"zone_seconds":[600,1200]},
     "intensity":{"low_seconds":0,"moderate_seconds":1800,"high_seconds":0,"method":"RPE categories"}}') -> 'intensity'),
  'null'::jsonb, 'RPE intensity alongside zone times is not shared');

select * from finish();
rollback;
