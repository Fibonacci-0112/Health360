-- Training locations, programs, the schedule, personal records and set_logs' e1RM. Locations follow their
-- household; the U rows follow the 'workouts' grant and keep their parent's profile; e1rm_kg is recomputed.
-- Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(43);

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

insert into exercises (id, external_id, name, tracking_type, equipment, primary_muscles)
  values ('60000000-0000-7000-8000-000000000001', 'Barbell_Squat', 'Barbell squat', 'weight_reps',
          '["barbell"]', '["quadriceps"]'),
         ('60000000-0000-7000-8000-000000000002', 'Bench_Press', 'Bench press', 'weight_reps', '["barbell"]', '["chest"]');

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

-------- Training locations (H)
select pg_temp.login('a');
select lives_ok(
  format($$ insert into training_locations (id, household_id, name, equipment, plate_inventory, bar_weight, bar_unit, bar_weight_kg)
            values ('70000000-0000-7000-8000-000000000001', %L, 'Garage', '["barbell", "dumbbell"]',
                    '[{"weight": 45, "unit": "lb", "count": 4}, {"weight": 1.25, "unit": "kg", "count": 2}]', 45, 'lb', 1) $$,
         current_setting('t.ha')),
  'A can add a location with plates in both units');
select is((select bar_weight_kg from training_locations), 45 * 0.45359237::double precision,
  'bar_weight_kg is recomputed from the entered bar');
select throws_ok(
  format($$ insert into training_locations (household_id, name, plate_inventory) values (%L, 'x', '[{"weight": 20, "unit": "st", "count": 2}]') $$,
         current_setting('t.ha')),
  '23514', null, 'a plate is in kg or lb');
select throws_ok(
  format($$ insert into training_locations (household_id, name, plate_inventory) values (%L, 'x', '[{"weight": "20", "unit": "kg", "count": 2}]') $$,
         current_setting('t.ha')),
  '23514', null, 'a plate weight is a number');
select throws_ok(
  format($$ insert into training_locations (household_id, name, plate_inventory) values (%L, 'x', '[{"weight": 20, "unit": "kg", "count": 1.5}]') $$,
         current_setting('t.ha')),
  '23514', null, 'a plate count is a whole number');
select throws_ok(
  format($$ insert into training_locations (household_id, name, plate_inventory) values (%L, 'x', '[{"weight": 20, "unit": "kg", "count": 2, "colour": "red"}]') $$,
         current_setting('t.ha')),
  '23514', null, 'a plate has no other fields');
select throws_ok(
  format($$ insert into training_locations (household_id, name, plate_inventory) values (%L, 'x', '[20]') $$, current_setting('t.ha')),
  '23514', null, 'a plate is an object');
select throws_ok(
  format($$ insert into training_locations (household_id, name, bar_weight) values (%L, 'x', 20) $$, current_setting('t.ha')),
  '23514', null, 'a bar weight needs its unit');
select throws_ok(
  format($$ insert into training_locations (household_id, name) values (%L, 'x') $$, current_setting('t.hb')),
  '42501', null, 'A cannot add a location to another household');

select pg_temp.login('c');
select is((select count(*) from training_locations), 1::bigint, 'a household member sees the location');
select pg_temp.login('b');
select is((select count(*) from training_locations), 0::bigint, 'a stranger does not');
select is(pg_temp.affected($$ delete from training_locations $$), 0::bigint, 'a stranger cannot delete it');

-------- Programs and the schedule (U)
select pg_temp.login('a');
insert into workout_templates (id, profile_id, name)
  values ('61000000-0000-7000-8000-000000000001', current_setting('t.a')::uuid, 'Legs');
select lives_ok(
  format($$ insert into programs (id, profile_id, name, location_id, starts_on, ends_on)
            values ('71000000-0000-7000-8000-000000000001', %L, 'Base', '70000000-0000-7000-8000-000000000001',
                    '2026-11-02', '2026-12-27') $$, current_setting('t.a')),
  'A can add a program at a household location');
select throws_ok(
  format($$ insert into programs (profile_id, name, starts_on, ends_on) values (%L, 'x', '2026-11-02', '2026-11-01') $$,
         current_setting('t.a')),
  '23514', null, 'a program cannot end before it starts');
select lives_ok(
  format($$ insert into scheduled_workouts (id, profile_id, program_id, template_id, weekday)
            values ('72000000-0000-7000-8000-000000000001', %L, '71000000-0000-7000-8000-000000000001',
                    '61000000-0000-7000-8000-000000000001', 1) $$, current_setting('t.a')),
  'A can schedule the template every Monday of the program');
select lives_ok(
  format($$ insert into scheduled_workouts (id, profile_id, template_id, scheduled_on)
            values ('72000000-0000-7000-8000-000000000002', %L, '61000000-0000-7000-8000-000000000001', '2026-11-05') $$,
         current_setting('t.a')),
  'A can schedule the template on one date');
select throws_ok(
  format($$ insert into scheduled_workouts (profile_id, template_id, weekday) values (%L, '61000000-0000-7000-8000-000000000001', 1) $$,
         current_setting('t.a')),
  '23514', null, 'a weekly entry belongs to a program');
select throws_ok(
  format($$ insert into scheduled_workouts (profile_id, program_id, template_id, weekday, scheduled_on)
            values (%L, '71000000-0000-7000-8000-000000000001', '61000000-0000-7000-8000-000000000001', 1, '2026-11-05') $$,
         current_setting('t.a')),
  '23514', null, 'an entry is weekly or dated, not both');
select throws_ok(
  format($$ insert into scheduled_workouts (profile_id, program_id, template_id, weekday)
            values (%L, '71000000-0000-7000-8000-000000000001', '61000000-0000-7000-8000-000000000001', 8) $$,
         current_setting('t.a')),
  '23514', null, 'weekdays run 1 to 7');

select pg_temp.login('b');
select throws_ok(
  format($$ insert into programs (profile_id, name, location_id, starts_on)
            values (%L, 'x', '70000000-0000-7000-8000-000000000001', '2026-11-02') $$, current_setting('t.b')),
  '42501', null, 'B cannot use another household''s location');
select throws_ok(
  format($$ insert into scheduled_workouts (profile_id, template_id, scheduled_on)
            values (%L, '61000000-0000-7000-8000-000000000001', '2026-11-05') $$, current_setting('t.b')),
  '23503', null, 'B cannot schedule A''s template');
select is((select count(*) from programs) + (select count(*) from scheduled_workouts), 0::bigint,
  'B sees none of A''s programs or schedule');

-------- set_logs: e1RM recomputed, only for completed working sets
select pg_temp.login('a');
insert into workout_sessions (id, profile_id, kind, started_at, local_date)
  values ('63000000-0000-7000-8000-000000000001', current_setting('t.a')::uuid, 'strength', '2026-11-02T17:00:00Z', '2026-11-02');
insert into session_exercises (id, profile_id, session_id, exercise_id, position)
  values ('64000000-0000-7000-8000-000000000001', current_setting('t.a')::uuid, '63000000-0000-7000-8000-000000000001',
          '60000000-0000-7000-8000-000000000001', 0);
select lives_ok(
  format($$ insert into set_logs (id, profile_id, session_exercise_id, position, reps, weight, weight_unit, e1rm_formula, e1rm_kg,
                                  completed_at, is_pr)
            values ('65000000-0000-7000-8000-000000000001', %L, '64000000-0000-7000-8000-000000000001', 0, 5, 100, 'kg',
                    'epley', 1, '2026-11-02T17:10:00Z', true) $$, current_setting('t.a')),
  'A can log a completed set with an Epley estimate');
select is((select e1rm_kg from set_logs where id = '65000000-0000-7000-8000-000000000001'),
  100 * (1 + 5::double precision / 30), 'e1rm_kg is recomputed with Epley');
insert into set_logs (id, profile_id, session_exercise_id, position, reps, weight, weight_unit, e1rm_formula, completed_at)
  values ('65000000-0000-7000-8000-000000000002', current_setting('t.a')::uuid, '64000000-0000-7000-8000-000000000001', 1, 8,
          225, 'lb', 'brzycki', '2026-11-02T17:15:00Z');
select is((select e1rm_kg from set_logs where id = '65000000-0000-7000-8000-000000000002'),
  225 * 0.45359237::double precision * 36 / 29, 'Brzycki estimates from canonical kg');
insert into set_logs (id, profile_id, session_exercise_id, position, reps, weight, weight_unit, e1rm_formula)
  values ('65000000-0000-7000-8000-000000000003', current_setting('t.a')::uuid, '64000000-0000-7000-8000-000000000001', 2, 5,
          100, 'kg', 'epley');
select is((select (e1rm_kg, e1rm_formula) from set_logs where id = '65000000-0000-7000-8000-000000000003'),
  row(null::double precision, null::text), 'a set still planned has no estimate');
update set_logs set completed_at = '2026-11-02T17:20:00Z', e1rm_formula = 'epley', reps = 12
  where id = '65000000-0000-7000-8000-000000000003';
select is((select e1rm_kg from set_logs where id = '65000000-0000-7000-8000-000000000003'), null::double precision,
  'nothing is estimated above 10 reps');
update set_logs set reps = 1, e1rm_formula = 'brzycki' where id = '65000000-0000-7000-8000-000000000003';
select is((select e1rm_kg from set_logs where id = '65000000-0000-7000-8000-000000000003'), 100::double precision,
  'a single is its own 1RM');
update set_logs set set_type = 'warmup', e1rm_formula = 'epley' where id = '65000000-0000-7000-8000-000000000003';
select is((select e1rm_kg from set_logs where id = '65000000-0000-7000-8000-000000000003'), null::double precision,
  'a warm-up is not estimated');

-------- Personal records (U)
select lives_ok(
  format($$ insert into personal_records (id, profile_id, exercise_id, kind, value, weight_kg, reps, e1rm_formula, set_log_id,
                                          achieved_at, local_date)
            values ('73000000-0000-7000-8000-000000000001', %L, '60000000-0000-7000-8000-000000000001', 'e1rm', 116.67, 100, 5,
                    'epley', '65000000-0000-7000-8000-000000000001', '2026-11-02T17:10:00Z', '2026-11-02') $$,
         current_setting('t.a')),
  'A can record an e1RM PR');
select lives_ok(
  format($$ insert into personal_records (profile_id, exercise_id, kind, value, weight_kg, reps, set_log_id, achieved_at, local_date)
            values (%L, '60000000-0000-7000-8000-000000000001', 'volume', 500, 100, 5,
                    '65000000-0000-7000-8000-000000000001', '2026-11-02T17:10:00Z', '2026-11-02') $$,
         current_setting('t.a')),
  'A can record a volume PR');
select lives_ok(
  format($$ insert into personal_records (id, profile_id, exercise_id, kind, value, weight_kg, reps, set_log_id, achieved_at, local_date)
            values ('73000000-0000-7000-8000-000000000009', %L, '60000000-0000-7000-8000-000000000002', 'weight', 100, 100, 5,
                    '65000000-0000-7000-8000-000000000001', '2026-11-02T17:10:00Z', '2026-11-02') $$,
         current_setting('t.a')),
  'a record sent with another exercise is accepted');
select is((select exercise_id from personal_records where id = '73000000-0000-7000-8000-000000000009'),
  '60000000-0000-7000-8000-000000000001'::uuid, 'but it is filed under the exercise its set was logged under');
delete from personal_records where id = '73000000-0000-7000-8000-000000000009';
select throws_ok(
  format($$ insert into personal_records (profile_id, exercise_id, kind, value, weight_kg, reps, set_log_id, achieved_at, local_date)
            values (%L, '60000000-0000-7000-8000-000000000001', 'e1rm', 116.67, 100, 5,
                    '65000000-0000-7000-8000-000000000001', '2026-11-02T17:10:00Z', '2026-11-02') $$,
         current_setting('t.a')),
  '23514', null, 'an e1RM record names its formula');
select throws_ok(
  format($$ insert into personal_records (profile_id, exercise_id, kind, value, weight_kg, reps, set_log_id, achieved_at, local_date)
            values (%L, '60000000-0000-7000-8000-000000000001', 'weight', 0, 100, 5,
                    '65000000-0000-7000-8000-000000000001', '2026-11-02T17:10:00Z', '2026-11-02') $$,
         current_setting('t.a')),
  '23514', null, 'a record is positive');

select pg_temp.login('b');
select throws_ok(
  format($$ insert into personal_records (profile_id, exercise_id, kind, value, weight_kg, reps, set_log_id, achieved_at, local_date)
            values (%L, '60000000-0000-7000-8000-000000000001', 'weight', 100, 100, 5,
                    '65000000-0000-7000-8000-000000000001', '2026-11-02T17:10:00Z', '2026-11-02') $$,
         current_setting('t.b')),
  '23503', null, 'B cannot record a PR on A''s set');
select is((select count(*) from personal_records), 0::bigint, 'B sees none of A''s records');

-------- The workouts grant shows the U rows read-only
select pg_temp.login('c');
select is((select count(*) from programs) + (select count(*) from personal_records), 0::bigint,
  'records and programs are not shared without a grant');
select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'workouts');
select pg_temp.login('c');
select is(
  (select count(*) from programs) + (select count(*) from scheduled_workouts) + (select count(*) from personal_records),
  5::bigint, 'a workouts grant shows A''s program, schedule and records');
select is(pg_temp.affected($$ update personal_records set value = 1 $$), 0::bigint, 'the grant is read-only');

-------- Deleting the set deletes its records; deleting the location keeps the program
select pg_temp.login('a');
delete from set_logs where id = '65000000-0000-7000-8000-000000000001';
select is((select count(*) from personal_records), 0::bigint, 'a deleted set takes its records with it');
delete from training_locations where id = '70000000-0000-7000-8000-000000000001';
select is((select location_id from programs), null::uuid, 'a deleted location leaves the program without one');

-------- A location stays in its household
reset role;
insert into training_locations (id, household_id, name)
  values ('70000000-0000-7000-8000-000000000002', current_setting('t.ha')::uuid, 'Gym');
insert into household_members (household_id, profile_id, role)
  values (current_setting('t.hb')::uuid, current_setting('t.a')::uuid, 'adult');
set local role authenticated;
select pg_temp.login('a');
select throws_ok(
  format($$ update training_locations set household_id = %L $$, current_setting('t.hb')),
  '23514', null, 'a location cannot move to another household');

select * from finish();
rollback;
