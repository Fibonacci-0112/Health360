-- Workouts: exercises (system and household custom), templates, sessions and sets. U rows follow the 'workouts'
-- grant, children keep their parent's profile, and set loads and distances get canonical values. Runs as
-- `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(44);

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.com'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.com'),
  ('00000000-0000-0000-0000-00000000000c', 'c@example.com');

select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);
select set_config('t.b', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000b'), true);
select set_config('t.c', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000c'), true);
select set_config('t.ha', (select household_id::text from household_members where profile_id = current_setting('t.a')::uuid), true);
select set_config('t.hb', (select household_id::text from household_members where profile_id = current_setting('t.b')::uuid), true);
-- C is a member of A's household; B is not.
insert into household_members (household_id, profile_id, role)
  values (current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'adult');

-- A system exercise, as the importer writes it.
insert into exercises (id, external_id, name, tracking_type, equipment, primary_muscles)
  values ('60000000-0000-7000-8000-000000000001', 'Barbell_Squat', 'Barbell squat', 'weight_reps',
          '["barbell"]', '["quadriceps"]');

create function pg_temp.login(who text) returns void language sql as $$
  select set_config('request.jwt.claims', json_build_object(
    'sub', '00000000-0000-0000-0000-00000000000' || who, 'role', 'authenticated')::text, true);
$$;
grant execute on function pg_temp.login(text) to authenticated;

create function pg_temp.affected(q text) returns bigint language plpgsql as $$
declare n bigint;
begin
  execute q;
  get diagnostics n = row_count;
  return n;
end $$;
grant execute on function pg_temp.affected(text) to authenticated;

set local role authenticated;

-------- Exercises
select pg_temp.login('a');
select is((select count(*) from exercises), 1::bigint, 'everyone signed in sees system exercises');
select is(pg_temp.affected($$ update exercises set name = 'x' $$), 0::bigint, 'clients cannot change system exercises');
select is(pg_temp.affected($$ delete from exercises $$), 0::bigint, 'clients cannot delete system exercises');
select throws_ok(
  $$ insert into exercises (name, tracking_type) values ('Mine', 'reps') $$,
  '42501', null, 'clients cannot add system exercises');
select lives_ok(
  format($$ insert into exercises (id, household_id, name, tracking_type, equipment)
            values ('60000000-0000-7000-8000-000000000002', %L, 'Band pull-apart', 'reps', '["band"]') $$,
         current_setting('t.ha')),
  'A can add a custom exercise to their household');
select throws_ok(
  format($$ insert into exercises (household_id, name, tracking_type, external_id) values (%L, 'x', 'reps', 'x') $$,
         current_setting('t.ha')),
  '42501', null, 'a custom exercise has no importer key');
select throws_ok(
  format($$ insert into exercises (household_id, name, tracking_type) values (%L, 'x', 'reps') $$, current_setting('t.hb')),
  '42501', null, 'A cannot add an exercise to another household');
select throws_ok(
  format($$ insert into exercises (household_id, name, tracking_type, equipment) values (%L, 'x', 'reps', '["Bar bell"]') $$,
         current_setting('t.ha')),
  '23514', null, 'equipment must be an array of codes');
select throws_ok(
  format($$ insert into exercises (household_id, name, tracking_type, primary_muscles) values (%L, 'x', 'reps', '["a", "a"]') $$,
         current_setting('t.ha')),
  '23514', null, 'muscle codes are distinct');
select throws_ok(
  format($$ insert into exercises (household_id, name, tracking_type) values (%L, 'x', 'swimming') $$, current_setting('t.ha')),
  '23514', null, 'the tracking type must be a known one');
select throws_ok(
  format($$ update exercises set household_id = %L where household_id is not null $$, current_setting('t.hb')),
  '23514', null, 'a custom exercise cannot move to another household');
select is(
  (select count(*) from exercises where household_id = current_setting('t.ha')::uuid), 1::bigint,
  'the custom exercise is still there');

select pg_temp.login('c');
select is((select count(*) from exercises), 2::bigint, 'a household member sees its custom exercises');
select pg_temp.login('b');
select is((select count(*) from exercises), 1::bigint, 'a stranger sees only system exercises');

-------- The owner's template, session and sets
select pg_temp.login('a');
select lives_ok(
  format($$ insert into workout_templates (id, profile_id, name) values ('61000000-0000-7000-8000-000000000001', %L, 'Legs') $$,
         current_setting('t.a')),
  'A can add a template');
select lives_ok(
  format($$ insert into template_exercises (id, profile_id, template_id, exercise_id, position, set_scheme, rest_seconds, progression)
            values ('62000000-0000-7000-8000-000000000001', %L, '61000000-0000-7000-8000-000000000001',
                    '60000000-0000-7000-8000-000000000001', 0, '{"sets": 3, "min_reps": 8, "max_reps": 12}', 120,
                    '{"kind": "double", "increment": 2.5, "increment_unit": "kg"}') $$,
         current_setting('t.a')),
  'A can add an exercise to the template');
select throws_ok(
  format($$ insert into template_exercises (profile_id, template_id, exercise_id, position, set_scheme)
            values (%L, '61000000-0000-7000-8000-000000000001', '60000000-0000-7000-8000-000000000001', 1, '[3]') $$,
         current_setting('t.a')),
  '23514', null, 'a set scheme is an object');

select lives_ok(
  format($$ insert into workout_sessions (id, profile_id, template_id, kind, started_at, local_date, avg_hr, max_hr, zone_seconds)
            values ('63000000-0000-7000-8000-000000000001', %L, '61000000-0000-7000-8000-000000000001', 'strength',
                    '2026-11-02T17:00:00Z', '2026-11-02', 120, 165, '[600, 1200, 300, 0, 0]') $$,
         current_setting('t.a')),
  'A can start a session from the template');
insert into workout_sessions (id, profile_id, kind, started_at, ended_at, local_date, energy_estimate)
  values ('63000000-0000-7000-8000-000000000002', current_setting('t.a')::uuid, 'cardio',
          '2026-11-02T18:00:00Z', '2026-11-02T18:30:00Z', '2026-11-02',
          '{"version":1,"entry":{"activity_code":"12020","duration_seconds":1800,"average_hr":150,"zone_seconds":[1800]},
            "activity":{"code":"12020","name":"Jogging","sport":"Run","mets":7.5},"method":"Keytel (VO₂max), average HR",
            "physiology":{"weight_kg":80},"zones":{"inputs":[{"value":160}]},"notes":["Private input"],"kcal":{"low":10,"high":20}}');
select is((select estimate -> 'physiology' ->> 'weight_kg' from cardio_estimates where id = '63000000-0000-7000-8000-000000000002'),
  '80', 'a legacy full estimate is preserved in private storage');
select is((select count(*) from cardio_estimates where id = '63000000-0000-7000-8000-000000000002'),
  1::bigint, 'A can read the private cardio estimate');
select throws_ok(
  format($$ insert into workout_sessions (profile_id, kind, started_at, ended_at, local_date)
            values (%L, 'cardio', '2026-11-02T17:00:00Z', '2026-11-02T16:00:00Z', '2026-11-02') $$, current_setting('t.a')),
  '23514', null, 'a session cannot end before it starts');
select throws_ok(
  format($$ insert into workout_sessions (profile_id, kind, started_at, local_date, zone_seconds)
            values (%L, 'cardio', '2026-11-02T17:00:00Z', '2026-11-02', '[60, -1]') $$, current_setting('t.a')),
  '23514', null, 'time in zone cannot be negative');
select lives_ok(
  format($$ insert into session_exercises (id, profile_id, session_id, exercise_id, position)
            values ('64000000-0000-7000-8000-000000000001', %L, '63000000-0000-7000-8000-000000000001',
                    '60000000-0000-7000-8000-000000000001', 0) $$, current_setting('t.a')),
  'A can add an exercise to the session');
select lives_ok(
  format($$ insert into set_logs (id, profile_id, session_exercise_id, position, reps, weight, weight_unit, weight_kg, rpe)
            values ('65000000-0000-7000-8000-000000000001', %L, '64000000-0000-7000-8000-000000000001', 0, 8, 225, 'lb', 1, 8) $$,
         current_setting('t.a')),
  'A can log a set in pounds');
select is((select weight_kg from set_logs where id = '65000000-0000-7000-8000-000000000001'),
  225 * 0.45359237::double precision, 'weight_kg is recomputed from the entered load');
select is((select (weight, weight_unit) from set_logs where id = '65000000-0000-7000-8000-000000000001'),
  row(225::double precision, 'lb'::text), 'the load is kept as entered');
select lives_ok(
  format($$ insert into set_logs (profile_id, session_exercise_id, position, duration_seconds, distance, distance_unit)
            values (%L, '64000000-0000-7000-8000-000000000001', 1, 300, 1, 'mi') $$, current_setting('t.a')),
  'A can log a distance in miles');
select is((select distance_m from set_logs where position = 1), 1609.344::double precision, 'distance_m is in metres');
select throws_ok(
  format($$ insert into set_logs (profile_id, session_exercise_id, position, weight) values (%L, '64000000-0000-7000-8000-000000000001', 2, 50) $$,
         current_setting('t.a')),
  '23514', null, 'a load needs its unit');
select throws_ok(
  format($$ insert into set_logs (profile_id, session_exercise_id, position, weight, weight_unit)
            values (%L, '64000000-0000-7000-8000-000000000001', 2, 50, 'g') $$, current_setting('t.a')),
  '23514', null, 'a load is in kg or lb');

-------- Children keep their parent's profile; exercises must be visible
select pg_temp.login('b');
select lives_ok(
  format($$ insert into workout_sessions (id, profile_id, kind, started_at, local_date)
            values ('63000000-0000-7000-8000-0000000000b1', %L, 'strength', now(), '2026-11-02') $$, current_setting('t.b')),
  'B can start their own session');
select throws_ok(
  format($$ insert into session_exercises (profile_id, session_id, exercise_id, position)
            values (%L, '63000000-0000-7000-8000-000000000001', '60000000-0000-7000-8000-000000000001', 1) $$,
         current_setting('t.b')),
  '23503', null, 'B cannot attach an exercise to A''s session');
select throws_ok(
  format($$ insert into session_exercises (profile_id, session_id, exercise_id, position)
            values (%L, '63000000-0000-7000-8000-0000000000b1', '60000000-0000-7000-8000-000000000002', 0) $$,
         current_setting('t.b')),
  '42501', null, 'B cannot use another household''s custom exercise');

-------- A stranger and a household member
select is((select count(*) from workout_sessions), 1::bigint, 'B sees only their own session');
select is((select count(*) from set_logs) + (select count(*) from workout_templates), 0::bigint,
  'B sees none of A''s templates or sets');
select is(pg_temp.affected($$ delete from set_logs $$), 0::bigint, 'B cannot delete A''s sets');

select pg_temp.login('c');
select is((select count(*) from workout_sessions), 0::bigint, 'workouts are not shared without a grant');
select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'workouts');
select pg_temp.login('c');
select is(
  (select count(*) from workout_templates) + (select count(*) from template_exercises) + (select count(*) from workout_sessions)
    + (select count(*) from session_exercises) + (select count(*) from set_logs),
  7::bigint, 'a workouts grant shows A''s template, sessions and sets');
select is((select count(*) from cardio_estimates), 0::bigint, 'a workouts-only grant cannot read full cardio estimates');
select is((select energy_estimate ? 'physiology' from workout_sessions where id = '63000000-0000-7000-8000-000000000002'),
  false, 'a workouts grant sees only the redacted cardio summary');
select is((select energy_estimate ? 'zones' from workout_sessions where id = '63000000-0000-7000-8000-000000000002'),
  false, 'the cardio summary does not expose zone provenance');
select is(pg_temp.affected($$ update set_logs set reps = 1 $$), 0::bigint, 'the grant is read-only');

-------- A member of two households still can't move a custom exercise between them
reset role;
insert into household_members (household_id, profile_id, role)
  values (current_setting('t.hb')::uuid, current_setting('t.a')::uuid, 'adult');
set local role authenticated;
select pg_temp.login('a');
select throws_ok(
  format($$ update exercises set household_id = %L where id = '60000000-0000-7000-8000-000000000002' $$, current_setting('t.hb')),
  '23514', null, 'a custom exercise stays in its household, even for a member of both');

-------- The importer
reset role;
set local role service_role;
select lives_ok(
  $$ insert into exercises (external_id, name, tracking_type, equipment, primary_muscles)
     values ('Push_Up', 'Push-up', 'reps', '["body_only"]', '["chest"]') $$,
  'service_role can import a system exercise');

select * from finish();
rollback;
