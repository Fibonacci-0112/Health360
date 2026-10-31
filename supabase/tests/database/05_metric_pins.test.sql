-- Pins (metric_pins): one row per profile and metric with an id both client and server derive, the owner's
-- alone to change, read by others only through a grant for the metric's category. Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(35);

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

create function pg_temp.affected(q text) returns bigint language plpgsql as $$
declare n bigint;
begin
  execute q;
  get diagnostics n = row_count;
  return n;
end $$;
grant execute on function pg_temp.affected(text) to authenticated;

-- Pins `metric` of `profile` the way the app uploads it: an upsert on the derived id. The category sent is
-- wrong on purpose; the trigger replaces it.
create function pg_temp.pin(profile uuid, metric text, measurement uuid) returns void language sql as $$
  insert into metric_pins (id, profile_id, metric_code, category, target, measurement_id)
  values (private.metric_pin_id(profile, metric), profile, metric, 'body',
          case when measurement is null then 'estimate' else 'measurement' end, measurement)
  on conflict (id) do update set target = excluded.target, measurement_id = excluded.measurement_id;
$$;
grant execute on function pg_temp.pin(uuid, text, uuid) to authenticated;

-- The derived id is the only uniqueness. A second unique index made two devices' simultaneous uploads of a new
-- pin fail with 23505 instead of upserting: ON CONFLICT (id) resolves conflicts on the primary key only.
select is(
  (select count(*) from pg_index where indrelid = 'public.metric_pins'::regclass and indisunique),
  1::bigint, 'the primary key is the only unique index on metric_pins');
select is(
  (select count(*) from pg_index i join pg_attribute a on a.attrelid = i.indrelid and a.attnum = i.indkey[0]
   where i.indrelid = 'public.metric_pins'::regclass and a.attname = 'profile_id'),
  1::bigint, 'pins are still found by profile through an index');

-- The golden value Health360.Domain's UuidV8Tests and LocalDb's MetricPinsTests check too.
select is(private.metric_pin_id('0190a0b0-0000-7000-8000-000000000001', 'max_hr'),
  '6a8b1fc1-d36f-8c03-ba62-7ced888bd0da'::uuid, 'the pin id is the UUIDv8 the client derives');

set local role authenticated;
select pg_temp.login('a');
select is(private.metric_pin_id('0190a0b0-0000-7000-8000-000000000001', 'max_hr'),
  '6a8b1fc1-d36f-8c03-ba62-7ced888bd0da'::uuid, 'authenticated can derive it (the id check runs as the caller)');

insert into measurements (id, profile_id, metric_code, category, value, unit, value_canonical, measured_at, local_date, source)
values
  ('40000000-0000-0000-0000-000000000001', current_setting('t.a')::uuid, 'max_hr', 'physiology', 192, 'bpm', 192,
   '2025-03-01T07:00:00Z', '2025-03-01', 'lab'),
  ('40000000-0000-0000-0000-000000000002', current_setting('t.a')::uuid, 'max_hr', 'physiology', 188, 'bpm', 188,
   '2026-10-01T07:00:00Z', '2026-10-01', 'field_test'),
  ('40000000-0000-0000-0000-000000000003', current_setting('t.a')::uuid, 'body_fat_pct', 'body', 18, '%', 18,
   '2026-10-01T07:00:00Z', '2026-10-01', 'manual');

-------- The owner pins, re-pins and unpins
select lives_ok(
  format($$ select pg_temp.pin(%L, 'max_hr', '40000000-0000-0000-0000-000000000001') $$, current_setting('t.a')),
  'A can pin an older lab value');
select is((select category from metric_pins where metric_code = 'max_hr'), 'physiology',
  'the category comes from the metric, not the client');
select lives_ok(
  format($$ select pg_temp.pin(%L, 'max_hr', null) $$, current_setting('t.a')),
  'pinning the same metric again (from another device, say) upserts the same row');
select results_eq(
  $$ select count(*), min(target), bool_and(measurement_id is null) from metric_pins where metric_code = 'max_hr' $$,
  $$ values (1::bigint, 'estimate'::text, true) $$,
  'one row per metric, now pinned to the estimate');
select lives_ok(
  format($$ select pg_temp.pin(%L, 'body_fat_pct', '40000000-0000-0000-0000-000000000003') $$, current_setting('t.a')),
  'A can pin a body-fat value');
select is((select category from metric_pins where metric_code = 'body_fat_pct'), 'body', 'a body metric''s pin is a body row');

select throws_ok(
  format($$ insert into metric_pins (id, profile_id, metric_code, category, target)
            values (gen_random_uuid(), %L, 'rmr', 'physiology', 'estimate') $$, current_setting('t.a')),
  '23514', null, 'an id other than the derived one is rejected');
select throws_ok(
  format($$ insert into metric_pins (id, profile_id, metric_code, category, target)
            values (private.metric_pin_id(%L, 'rmr'), %L, 'rmr', 'physiology', 'measurement') $$,
    current_setting('t.a'), current_setting('t.a')),
  '23514', null, 'a pin to a measurement needs the measurement');
select throws_ok(
  format($$ insert into metric_pins (id, profile_id, metric_code, category, target, measurement_id)
            values (private.metric_pin_id(%L, 'rmr'), %L, 'rmr', 'physiology', 'estimate', gen_random_uuid()) $$,
    current_setting('t.a'), current_setting('t.a')),
  '23514', null, 'a pin to the estimate names no measurement');
select throws_ok(
  format($$ insert into metric_pins (id, profile_id, metric_code, category, target)
            values (private.metric_pin_id(%L, 'rmr'), %L, 'rmr', 'physiology', 'lab') $$,
    current_setting('t.a'), current_setting('t.a')),
  '23514', null, 'an unknown target is rejected');
select throws_ok(
  format($$ select pg_temp.pin(%L, 'not_a_metric', null) $$, current_setting('t.a')),
  '23503', null, 'an unknown metric is rejected');

-- Deleting the pinned measurement keeps the pin, so the app can say the choice is no longer available.
select pg_temp.pin(current_setting('t.a')::uuid, 'max_hr', '40000000-0000-0000-0000-000000000001');
delete from measurements where id = '40000000-0000-0000-0000-000000000001';
select is((select measurement_id from metric_pins where metric_code = 'max_hr'), '40000000-0000-0000-0000-000000000001'::uuid,
  'deleting the pinned measurement keeps the pin');
select is((select updated_at from metric_pins where metric_code = 'max_hr') is not null, true, 'pins have timestamps');

-------- B (another household): sees nothing of A's pins, changes nothing, can't pin in A's profile
select pg_temp.login('b');
select is((select count(*) from metric_pins), 0::bigint, 'B cannot see A''s pins');
select is(pg_temp.affected($$ update metric_pins set target = 'estimate', measurement_id = null $$), 0::bigint,
  'B cannot change A''s pins');
select is(pg_temp.affected($$ delete from metric_pins $$), 0::bigint, 'B cannot delete A''s pins');
select throws_ok(
  format($$ select pg_temp.pin(%L, 'rmr', null) $$, current_setting('t.a')),
  '42501', null, 'B cannot pin a value in A''s profile');
select throws_ok(
  format($$ select pg_temp.pin(%L, 'max_hr', null) $$, current_setting('t.a')),
  '42501', null, 'B cannot overwrite A''s pin by upserting its id');
select lives_ok(
  format($$ select pg_temp.pin(%L, 'max_hr', null) $$, current_setting('t.b')),
  'B can pin in B''s own profile');
select is((select count(*) from metric_pins), 1::bigint, 'B sees only B''s own pin');

select pg_temp.login('a');
select results_eq(
  $$ select metric_code, target from metric_pins order by metric_code $$,
  $$ values ('body_fat_pct'::text, 'measurement'::text), ('max_hr', 'measurement') $$,
  'A''s pins are as A left them, and A cannot see B''s');

-------- C (A's household): nothing without a grant, then only the granted category, and never writes
select pg_temp.login('c');
select is((select count(*) from metric_pins), 0::bigint, 'a household member sees no pins without a grant');

select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'body');

select pg_temp.login('c');
select results_eq($$ select metric_code from metric_pins $$, $$ values ('body_fat_pct'::text) $$,
  'a body grant reveals body pins only');
select is(pg_temp.affected($$ update metric_pins set target = 'estimate', measurement_id = null $$), 0::bigint,
  'a read grant does not let C change a pin');
select is(pg_temp.affected($$ delete from metric_pins $$), 0::bigint, 'a read grant does not let C unpin');

select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'physiology');
select pg_temp.login('c');
select is((select count(*) from metric_pins), 2::bigint, 'a physiology grant adds physiology pins');
select throws_ok(
  format($$ select pg_temp.pin(%L, 'rmr', null) $$, current_setting('t.a')),
  '42501', null, 'a read grant does not let C pin in A''s profile');

-------- Unpinning
select pg_temp.login('a');
select is(pg_temp.affected($$ delete from metric_pins where metric_code = 'max_hr' $$), 1::bigint, 'A can unpin');
select is((select count(*) from metric_pins), 1::bigint, 'the other pin stays');

-------- anon
reset role;
set local role anon;
select throws_ok($$ select * from metric_pins $$, '42501', null, 'anon cannot read pins');
select throws_ok($$ select private.metric_pin_id(gen_random_uuid(), 'max_hr') $$, '42501', null,
  'anon cannot call the id function');

select * from finish();
rollback;
