-- Body metrics and the physiology profile: server-side canonical values, and who can read or change
-- measurements, measurement groups and zone sets. Everything runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(55);

-- A and B each get a profile and household from the sign-up trigger; C joins A's household.
insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.com'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.com'),
  ('00000000-0000-0000-0000-00000000000c', 'c@example.com');

select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);
select set_config('t.b', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000b'), true);
select set_config('t.c', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000c'), true);
select set_config('t.ha', (select household_id::text from household_members where profile_id = current_setting('t.a')::uuid), true);
insert into household_members (household_id, profile_id, role)
  values (current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'adult');

create function pg_temp.login(who text) returns void language sql as $$
  select set_config('request.jwt.claims', json_build_object(
    'sub', '00000000-0000-0000-0000-00000000000' || who, 'role', 'authenticated')::text, true);
$$;
grant execute on function pg_temp.login(text) to authenticated;

-- Rows an UPDATE/DELETE actually touched; RLS makes blocked writes silently match nothing.
create function pg_temp.affected(q text) returns bigint language plpgsql as $$
declare n bigint;
begin
  execute q;
  get diagnostics n = row_count;
  return n;
end $$;
grant execute on function pg_temp.affected(text) to authenticated;

-- A measurement for A's profile with the given metric, value and unit; returns its canonical value.
create function pg_temp.measure(id uuid, metric text, value double precision, unit text,
  source text default 'manual', group_id uuid default null) returns double precision language sql as $$
  insert into measurements (id, profile_id, metric_code, category, value, unit, value_canonical, measured_at, local_date,
    group_id, source)
  values (id, current_setting('t.a')::uuid, metric, 'body', value, unit, -1, '2026-10-31T07:00:00Z', '2026-10-31',
    group_id, source)
  returning value_canonical;
$$;
grant execute on function pg_temp.measure(uuid, text, double precision, text, text, uuid) to authenticated;

set local role authenticated;

-------- Reference data
select pg_temp.login('a');
select is(
  (select canonical_unit from metric_definitions where code = 'weight'), 'kg', 'metric definitions are readable');
select throws_ok($$ insert into metric_definitions (code, category, units, canonical_unit) values ('x', 'body', '["kg"]', 'kg') $$,
  '42501', null, 'clients cannot add metric definitions');
select throws_ok($$ update metric_definitions set valid_max = 1 $$, '42501', null,
  'clients cannot change metric definitions');

-------- Canonical values, recomputed from value + unit with Health360.Domain's factors and order
-- Each client sends value_canonical -1 and category 'body'; the trigger replaces both.
select is(pg_temp.measure('40000000-0000-0000-0000-000000000001', 'weight', 180, 'lb'),
  180::double precision * 0.45359237::double precision, 'lb → kg, as entered stays 180 lb');
select is((select value from measurements where id = '40000000-0000-0000-0000-000000000001'), 180::double precision,
  'the value as entered is kept');
select is(pg_temp.measure('40000000-0000-0000-0000-000000000002', 'weight', 81.5, 'kg'), 81.5::double precision,
  'the canonical unit converts to itself unchanged');
select is(pg_temp.measure('40000000-0000-0000-0000-000000000003', 'circ_waist_navel', 34.5, 'in'),
  34.5::double precision * 2.54::double precision * 100 / (100 * 1), 'in → cm');
select is(pg_temp.measure('40000000-0000-0000-0000-000000000004', 'glucose', 99, 'mg/dL'),
  99::double precision * 1 * 1 / (18.0156::double precision * 1), 'mg/dL → mmol/L');
select is(pg_temp.measure('40000000-0000-0000-0000-000000000005', 'body_temperature', 98.6, 'F'),
  (98.6::double precision - 32) * 5 / 9, '°F → °C');
select is(pg_temp.measure('40000000-0000-0000-0000-000000000006', 'rmr', 7200, 'kJ', 'lab'),
  7200::double precision * 1 * 1 / (4.184::double precision * 1), 'kJ → kcal');
select is(pg_temp.measure('40000000-0000-0000-0000-000000000007', 'threshold_pace', 420, 's_per_mi', 'field_test'),
  420::double precision * 1000 / 1609.344::double precision, 'seconds per mile → seconds per km');
select is(pg_temp.measure('40000000-0000-0000-0000-000000000008', 'max_hr', 188, 'bpm', 'field_test'),
  188::double precision, 'a fixed unit is kept as is');
select is((select category from measurements where id = '40000000-0000-0000-0000-000000000008'), 'physiology',
  'the category comes from the metric, not the client');

update measurements set value = 200, value_canonical = 1 where id = '40000000-0000-0000-0000-000000000001';
select is((select value_canonical from measurements where id = '40000000-0000-0000-0000-000000000001'),
  200::double precision * 0.45359237::double precision, 'an edit recomputes the canonical value');

select throws_ok($$ select pg_temp.measure(gen_random_uuid(), 'max_hr', 188, 'kg') $$,
  '23514', null, 'a unit the metric does not allow is rejected');
select throws_ok($$ select pg_temp.measure(gen_random_uuid(), 'body_fat_pct', 120, '%') $$,
  '23514', null, 'a percentage above 100 is rejected');
select throws_ok($$ select pg_temp.measure(gen_random_uuid(), 'weight', -1, 'kg') $$,
  '23514', null, 'a negative mass is rejected');
select throws_ok($$ select pg_temp.measure(gen_random_uuid(), 'weight', 'NaN', 'kg') $$,
  '23514', null, 'NaN is rejected');
select throws_ok($$ select pg_temp.measure(gen_random_uuid(), 'weight', 'Infinity', 'kg') $$,
  '23514', null, 'infinity is rejected');
select throws_ok($$ select pg_temp.measure(gen_random_uuid(), 'not_a_metric', 1, 'kg') $$,
  '23503', null, 'an unknown metric is rejected');
select throws_ok($$ insert into measurements (profile_id, metric_code, category, value, unit, value_canonical, measured_at, local_date, source)
                    values (current_setting('t.a')::uuid, 'weight', 'body', 80, 'kg', 80, now(), current_date, 'guess') $$,
  '23514', null, 'an unknown source is rejected');

-------- Groups
select lives_ok(
  format($$ insert into measurement_groups (id, profile_id, kind, measured_at, local_date, provider, device_make)
            values ('50000000-0000-0000-0000-00000000000a', %L, 'dexa', '2026-10-30T09:00:00Z', '2026-10-30', 'Clinic', 'Hologic') $$,
    current_setting('t.a')),
  'A can record a DEXA scan'
);
select is(pg_temp.measure('40000000-0000-0000-0000-000000000009', 'bmd', 1.21, 'g/cm2', 'lab', '50000000-0000-0000-0000-00000000000a'),
  1.21::double precision, 'a DEXA value belongs to its scan');
select lives_ok(
  format($$ insert into measurement_groups (profile_id, kind, measured_at, local_date, report_path)
            values (%L, 'rmr', now(), current_date, %L) $$, current_setting('t.a'), current_setting('t.a') || '/rmr/report.pdf'),
  'a report path under the owner''s profile id is accepted'
);
select throws_ok(
  format($$ insert into measurement_groups (profile_id, kind, measured_at, local_date, report_path)
            values (%L, 'rmr', now(), current_date, %L) $$, current_setting('t.a'), current_setting('t.b') || '/report.pdf'),
  '23514', null, 'a report path under another profile is rejected'
);
select throws_ok(
  format($$ insert into measurement_groups (profile_id, kind, measured_at, local_date, report_path)
            values (%L, 'rmr', now(), current_date, %L) $$, current_setting('t.a'), current_setting('t.a') || '/../x.pdf'),
  '23514', null, 'a report path cannot climb out of the profile''s folder'
);

-------- Zone sets
select lives_ok(
  format($$ insert into zone_sets (id, profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from, group_id)
            values ('60000000-0000-0000-0000-00000000000a', %L, 'run', 'hr', 'lab', '[0, 130, 145, 160, 172]', 'bpm', '[]',
                    '2026-10-30', '50000000-0000-0000-0000-00000000000a') $$, current_setting('t.a')),
  'A can save heart-rate zones'
);
select is((select boundaries_canonical from zone_sets where id = '60000000-0000-0000-0000-00000000000a'),
  '[0, 130, 145, 160, 172]'::jsonb, 'bpm zones are their own canonical values');
-- Written from a session that prints doubles with 15 digits, as some clients do: the stored canonical
-- numbers must still parse back to the exact doubles.
set local extra_float_digits = 0;
select lives_ok(
  format($$ insert into zone_sets (id, profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from)
            values ('60000000-0000-0000-0000-00000000000b', %L, 'run', 'pace', 'custom', '[600, 540, 480]', 's_per_mi', '[]', '2026-10-30') $$,
    current_setting('t.a')),
  'A can save pace zones in seconds per mile'
);
reset extra_float_digits;
select results_eq(
  $$ select (b ->> 0)::double precision from zone_sets z, jsonb_array_elements(z.boundaries_canonical) with ordinality as e (b, n)
     where z.id = '60000000-0000-0000-0000-00000000000b' order by n $$,
  $$ values (600::double precision * 1000 / 1609.344::double precision),
            (540::double precision * 1000 / 1609.344::double precision),
            (480::double precision * 1000 / 1609.344::double precision) $$,
  'pace zones are recomputed in seconds per km, exactly');
select throws_ok(
  format($$ insert into zone_sets (profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from)
            values (%L, 'run', 'hr', 'custom', '[130, 120, 150]', 'bpm', '[]', '2026-10-30') $$, current_setting('t.a')),
  '23514', null, 'heart-rate zones must ascend');
select throws_ok(
  format($$ insert into zone_sets (profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from)
            values (%L, 'run', 'pace', 'custom', '[480, 540]', 's_per_km', '[]', '2026-10-30') $$, current_setting('t.a')),
  '23514', null, 'pace zones must get faster');
select throws_ok(
  format($$ insert into zone_sets (profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from)
            values (%L, 'bike', 'power', 'custom', '[150, 200]', 'bpm', '[]', '2026-10-30') $$, current_setting('t.a')),
  '23514', null, 'power zones must be in watts');
select throws_ok(
  format($$ insert into zone_sets (profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from)
            values (%L, 'run', 'hr', 'custom', '{"z1": 120}', 'bpm', '[]', '2026-10-30') $$, current_setting('t.a')),
  '23514', null, 'boundaries must be an array');

-------- B (another household): sees nothing of A's, changes nothing, can't write into A's profile
select pg_temp.login('b');
select is((select count(*) from measurements), 0::bigint, 'B cannot see A''s measurements');
select is((select count(*) from measurement_groups), 0::bigint, 'B cannot see A''s measurement groups');
select is((select count(*) from zone_sets), 0::bigint, 'B cannot see A''s zone sets');
select is(pg_temp.affected($$ update measurements set value = 1 $$), 0::bigint, 'B cannot edit A''s measurements');
select is(pg_temp.affected($$ delete from measurement_groups $$), 0::bigint, 'B cannot delete A''s groups');
select throws_ok(
  format($$ insert into measurements (profile_id, metric_code, category, value, unit, value_canonical, measured_at, local_date, source)
            values (%L, 'weight', 'body', 50, 'kg', 50, now(), current_date, 'manual') $$, current_setting('t.a')),
  '42501', null, 'B cannot write a measurement into A''s profile');
select throws_ok(
  format($$ insert into zone_sets (profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from)
            values (%L, 'all', 'hr', 'custom', '[100, 150]', 'bpm', '[]', current_date) $$, current_setting('t.a')),
  '42501', null, 'B cannot write zones into A''s profile');
-- B's own group, for the cross-profile check below.
insert into measurement_groups (id, profile_id, kind, measured_at, local_date)
  values ('50000000-0000-0000-0000-00000000000b', current_setting('t.b')::uuid, 'manual', now(), current_date);

select pg_temp.login('a');
select throws_ok(
  $$ select pg_temp.measure(gen_random_uuid(), 'weight', 80, 'kg', 'manual', '50000000-0000-0000-0000-00000000000b') $$,
  '23503', null, 'A cannot attach a measurement to B''s group');

-------- C (A's household): nothing without a grant, then only the granted category, and never writes
select pg_temp.login('c');
select is((select count(*) from measurements), 0::bigint, 'a household member sees no measurements without a grant');
select is((select count(*) from zone_sets), 0::bigint, 'a household member sees no zones without a grant');

select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'body');

select pg_temp.login('c');
select is((select count(*) from measurements where category = 'physiology'), 0::bigint,
  'a body grant does not reveal physiology values');
select is((select count(*) from measurements), (select count(*) from measurements where category = 'body'),
  'a body grant reveals body values only');
select ok((select count(*) from measurements) > 0, 'C sees A''s body values once shared');
select is((select count(*) from zone_sets), 0::bigint, 'a body grant does not reveal zones');
select is(pg_temp.affected($$ update measurements set value = 1 $$), 0::bigint, 'a read grant does not let C edit');
select is(pg_temp.affected($$ delete from measurements $$), 0::bigint, 'a read grant does not let C delete');

select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'physiology');
select pg_temp.login('c');
select is((select count(*) from zone_sets), 2::bigint, 'a physiology grant reveals zones');
select is(pg_temp.affected($$ delete from zone_sets $$), 0::bigint, 'a read grant does not let C delete zones');

-------- Deleting a scan deletes its values and detaches the zones built on it
select pg_temp.login('a');
delete from measurement_groups where id = '50000000-0000-0000-0000-00000000000a';
select is((select count(*) from measurements where id = '40000000-0000-0000-0000-000000000009'), 0::bigint,
  'deleting a scan deletes its values');
select is((select group_id from zone_sets where id = '60000000-0000-0000-0000-00000000000a'), null::uuid,
  'deleting a lab test keeps the zones taken from it');

-------- anon
reset role;
set local role anon;
select throws_ok($$ select * from measurements $$, '42501', null, 'anon cannot read measurements');

select * from finish();
rollback;
