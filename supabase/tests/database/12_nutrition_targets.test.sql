-- Nutrition targets (nutrition_targets): one row per profile with an id client and server both derive, the protein
-- ratio's canonical value computed on write, the owner's alone to change, read by others only through a 'nutrition'
-- grant. Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(24);

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

-- Saves targets the way the app uploads them: an upsert on the derived id with every column. The canonical ratio
-- sent is wrong on purpose; the trigger replaces it.
create function pg_temp.save(profile uuid, method text, ratio double precision, unit text, manual_kcal double precision)
returns void language sql as $$
  insert into nutrition_targets (id, profile_id, method, energy_adjustment_kcal, protein_basis, protein_ratio,
    protein_ratio_unit, protein_ratio_g_per_kg, fat_percent, manual_energy_kcal)
  values (private.nutrition_target_id(profile), profile, method, -500, 'body_weight', ratio, unit, 99, 30, manual_kcal)
  on conflict (id) do update set method = excluded.method, protein_ratio = excluded.protein_ratio,
    protein_ratio_unit = excluded.protein_ratio_unit, manual_energy_kcal = excluded.manual_energy_kcal;
$$;
grant execute on function pg_temp.save(uuid, text, double precision, text, double precision) to authenticated;

-- The golden value Health360.LocalDb's NutritionTargetStoreTests checks too.
select is(private.nutrition_target_id('0190a0b0-0000-7000-8000-000000000001'),
  '9f85d5f3-b85c-8b18-aa4b-9ea16e5769d0'::uuid, 'the targets id is the UUIDv8 the client derives');
select is(private.protein_ratio_g_per_kg(0.8, 'g_per_lb'), 0.8 / 0.45359237::double precision,
  'g/lb converts with the exact lb factor, as ProteinRatio.ToGPerKg does');

set local role authenticated;
select pg_temp.login('a');

-------- The owner saves and changes targets
select lives_ok(format($$ select pg_temp.save(%L, 'formula', 1.6, 'g_per_kg', null) $$, current_setting('t.a')),
  'A can save formula targets');
select is((select protein_ratio_g_per_kg from nutrition_targets), 1.6::double precision,
  'the canonical ratio comes from the trigger, not the client');
select lives_ok(format($$ select pg_temp.save(%L, 'manual', 0.8, 'g_per_lb', 2400) $$, current_setting('t.a')),
  'saving again (from another device, say) upserts the same row');
select is((select count(*) from nutrition_targets), 1::bigint, 'one row per profile');
select is((select protein_ratio_g_per_kg from nutrition_targets), 0.8 / 0.45359237::double precision,
  'an update recomputes the canonical ratio');
select is((select updated_at from nutrition_targets) is not null, true, 'targets have timestamps');

-------- Constraints
select throws_ok(
  format($$ insert into nutrition_targets (id, profile_id, method, protein_basis, protein_ratio, protein_ratio_unit,
    protein_ratio_g_per_kg, fat_percent) values (gen_random_uuid(), %L, 'formula', 'body_weight', 1.6, 'g_per_kg', 1.6, 30) $$,
    current_setting('t.a')),
  '23514', null, 'the id must be the derived one');
select throws_ok(format($$ select pg_temp.save(%L, 'manual', 1.6, 'g_per_kg', null) $$, current_setting('t.a')),
  '23514', null, 'a manual target needs an energy target');
select throws_ok(format($$ select pg_temp.save(%L, 'adaptive', 1.6, 'g_per_kg', null) $$, current_setting('t.a')),
  '23514', null, 'adaptive targets are not accepted yet');
select throws_ok(format($$ select pg_temp.save(%L, 'formula', 1.6, 'g_per_oz', null) $$, current_setting('t.a')),
  '23502', null, 'an unknown ratio unit leaves no canonical ratio, so it is refused');
select throws_ok(format($$ select pg_temp.save(%L, 'formula', 11, 'g_per_kg', null) $$, current_setting('t.a')),
  '23514', null, 'a ratio above 10 g/kg is refused');
select throws_ok(format($$ select pg_temp.save(%L, 'formula', 0, 'g_per_kg', null) $$, current_setting('t.a')),
  '23514', null, 'a ratio of 0 is refused');
select throws_ok(
  format($$ update nutrition_targets set fat_percent = 100 where profile_id = %L $$, current_setting('t.a')),
  '23514', null, 'fat must be under 100% of energy');

-------- Others
select pg_temp.login('b');
select is((select count(*) from nutrition_targets), 0::bigint, 'B cannot see A''s targets');
select is(pg_temp.affected($$ update nutrition_targets set fat_percent = 40 $$), 0::bigint, 'B cannot change A''s targets');
select is(pg_temp.affected($$ delete from nutrition_targets $$), 0::bigint, 'B cannot delete A''s targets');
select throws_ok(format($$ select pg_temp.save(%L, 'formula', 1.6, 'g_per_kg', null) $$, current_setting('t.a')),
  '42501', null, 'B cannot write targets for A');

select pg_temp.login('c');
select is((select count(*) from nutrition_targets), 0::bigint, 'a household member sees no targets without a grant');
select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'nutrition');
select pg_temp.login('c');
select is((select count(*) from nutrition_targets), 1::bigint, 'a nutrition grant lets a member read the targets');
select is(pg_temp.affected($$ update nutrition_targets set fat_percent = 40 $$), 0::bigint,
  'a read grant does not let C change them');

select pg_temp.login('a');
select is(pg_temp.affected($$ delete from nutrition_targets $$), 1::bigint, 'A can delete A''s targets');

reset role;
set local role anon;
select throws_ok($$ select * from nutrition_targets $$, '42501', null, 'anon cannot read targets');

select * from finish();
rollback;
