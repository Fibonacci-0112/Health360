-- Nutrition: nutrients (reference), foods (catalogue and household), recipes and ingredients (household), and food
-- log entries, which follow the 'nutrition' grant. Amounts as entered get canonical g or ml. Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(48);

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.com'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.com'),
  ('00000000-0000-0000-0000-00000000000c', 'c@example.com');

select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);
select set_config('t.b', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000b'), true);
select set_config('t.c', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000c'), true);
select set_config('t.ha', (select household_id::text from household_members where profile_id = current_setting('t.a')::uuid), true);
select set_config('t.hb', (select household_id::text from household_members where profile_id = current_setting('t.b')::uuid), true);
-- C is a member of A's household and of B's; B is not a member of A's.
insert into household_members (household_id, profile_id, role) values
  (current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'adult'),
  (current_setting('t.hb')::uuid, current_setting('t.c')::uuid, 'adult');

-- A catalogue food, as the importer writes it.
insert into foods (id, source, external_id, name, basis, energy_100, protein_100, carbohydrate_100, fat_100, nutrients_100, portions)
  values ('70000000-0000-7000-8000-000000000001', 'usda_fdc', '173904', 'Oats', 'g', 379, 13.2, 67.7, 6.5,
          '{"fiber": 10.1, "iron": 4.25}', '[{"name": "1 cup", "amount": 81}]');

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

-------- Nutrients
select pg_temp.login('a');
select is((select count(*) from nutrients), 16::bigint, 'everyone signed in sees the nutrient catalogue');
select is((select unit from nutrients where code = 'vitamin_d'), 'ug', 'each nutrient has its fixed unit');
select throws_ok($$ insert into nutrients (code, unit, position) values ('zinc', 'mg', 99) $$,
  '42501', null, 'clients cannot add nutrients');
select throws_ok($$ update nutrients set unit = 'g' $$, '42501', null, 'clients cannot change nutrients');

-------- Foods
select is((select count(*) from foods), 1::bigint, 'everyone signed in sees catalogue foods');
select is(pg_temp.affected($$ update foods set name = 'x' $$), 0::bigint, 'clients cannot change catalogue foods');
select is(pg_temp.affected($$ delete from foods $$), 0::bigint, 'clients cannot delete catalogue foods');
select throws_ok($$ insert into foods (source, name) values ('usda_fdc', 'Mine') $$,
  '42501', null, 'clients cannot add catalogue foods');
select lives_ok(
  format($$ insert into foods (id, household_id, source, name, basis, energy_100, protein_100, carbohydrate_100, fat_100, portions)
            values ('70000000-0000-7000-8000-000000000002', %L, 'custom', 'Sourdough', 'g', 250, 9, 48, 1.5,
                    '[{"name": "1 slice", "amount": 60}]') $$, current_setting('t.ha')),
  'A can add a food to their household');
select lives_ok(
  format($$ insert into foods (id, household_id, source, name, basis, energy_100) values
            ('70000000-0000-7000-8000-000000000003', %L, 'label_scan', 'Oat milk', 'ml', 46) $$, current_setting('t.ha')),
  'A can add a scanned drink listed per 100 ml');
select throws_ok(
  format($$ insert into foods (household_id, source, name) values (%L, 'usda_fdc', 'x') $$, current_setting('t.ha')),
  '23514', null, 'a household food is not a catalogue source');
select throws_ok(
  format($$ insert into foods (household_id, source, external_id, name) values (%L, 'custom', '1', 'x') $$, current_setting('t.ha')),
  '23514', null, 'a household food has no importer key');
select throws_ok(
  format($$ insert into foods (household_id, source, name) values (%L, 'custom', 'x') $$, current_setting('t.hb')),
  '42501', null, 'A cannot add a food to another household');
select throws_ok(
  format($$ insert into foods (household_id, source, name, protein_100) values (%L, 'custom', 'x', 101) $$, current_setting('t.ha')),
  '23514', null, '100 g of food holds at most 100 g of protein');
select throws_ok(
  format($$ insert into foods (household_id, source, name, nutrients_100) values (%L, 'custom', 'x', '{"protein": 5}') $$,
         current_setting('t.ha')),
  '23514', null, 'macronutrients have their own columns, not map entries');
select throws_ok(
  format($$ insert into foods (household_id, source, name, nutrients_100) values (%L, 'custom', 'x', '{"iron": -1}') $$,
         current_setting('t.ha')),
  '23514', null, 'nutrient amounts are non-negative');
select throws_ok(
  format($$ insert into foods (household_id, source, name, nutrients_100) values (%L, 'custom', 'x', '{"Iron": 1}') $$,
         current_setting('t.ha')),
  '23514', null, 'nutrient keys are codes');
select throws_ok(
  format($$ insert into foods (household_id, source, name, portions) values (%L, 'custom', 'x', '[{"name": "1 slice", "amount": 0}]') $$,
         current_setting('t.ha')),
  '23514', null, 'a portion holds a positive amount');
select throws_ok(
  format($$ insert into foods (household_id, source, name, portions)
            values (%L, 'custom', 'x', '[{"name": "a", "amount": 1}, {"name": "a", "amount": 2}]') $$, current_setting('t.ha')),
  '23514', null, 'portion names are distinct');
select throws_ok(
  format($$ insert into foods (household_id, source, name, barcode) values (%L, 'custom', 'x', '12-34') $$, current_setting('t.ha')),
  '23514', null, 'a barcode is 8 to 14 digits');
select throws_ok(
  format($$ update foods set household_id = %L where household_id is not null $$, current_setting('t.hb')),
  '23514', null, 'a household food cannot move to another household');
select throws_ok(
  $$ update foods set basis = 'ml' where id = '70000000-0000-7000-8000-000000000002' $$,
  '23514', null, 'a food''s basis cannot change');

select pg_temp.login('c');
select is((select count(*) from foods), 3::bigint, 'a household member sees its foods');
select pg_temp.login('b');
select is((select count(*) from foods), 1::bigint, 'a stranger sees only catalogue foods');

-------- Recipes
select pg_temp.login('a');
select lives_ok(
  format($$ insert into recipes (id, household_id, name, servings, cooked_weight, cooked_weight_unit)
            values ('71000000-0000-7000-8000-000000000001', %L, 'Porridge', 2, 1, 'lb') $$, current_setting('t.ha')),
  'A can add a recipe');
select is((select cooked_weight_g from recipes), 453.59237::double precision, 'the cooked weight gets canonical grams');
select lives_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 0, '70000000-0000-7000-8000-000000000001', 2, 'oz', 'g', 0) $$,
         current_setting('t.ha')),
  'an ingredient may use a catalogue food');
select is((select base_amount from recipe_ingredients where position = 0), 56.69904625::double precision,
  'the server recomputes the amount in grams, whatever the client sent');
select lives_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, portion_name, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 1, '70000000-0000-7000-8000-000000000002', 1.5, 'portion', '1 slice', 'g', 0) $$,
         current_setting('t.ha')),
  'an ingredient may be measured in portions');
select is((select base_amount from recipe_ingredients where position = 1), 90::double precision,
  'the server computes a portion amount from the food: 1.5 × 60 g, whatever the client sent');
select throws_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, portion_name, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 2, '70000000-0000-7000-8000-000000000002', 1, 'portion', '1 loaf', 'g', 800) $$,
         current_setting('t.ha')),
  '23514', null, 'a portion must be one of the food''s');
select throws_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 2, '70000000-0000-7000-8000-000000000001', 1, 'cup', 'ml', 237) $$,
         current_setting('t.ha')),
  '23514', null, 'an ingredient''s basis must be its food''s');
select throws_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 2, '70000000-0000-7000-8000-000000000001', 1, 'cup', 'g', 81) $$,
         current_setting('t.ha')),
  '23514', null, 'a volume unit cannot measure a food listed per 100 g');
select throws_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 2, '70000000-0000-7000-8000-000000000001', 1, 'portion', 'g', 81) $$,
         current_setting('t.ha')),
  '23514', null, 'a portion names which one');
select throws_ok(
  format($$ delete from foods where id = '70000000-0000-7000-8000-000000000002' $$),
  '23503', null, 'a food in a recipe cannot be deleted');

select pg_temp.login('c');
select is((select count(*) from recipe_ingredients), 2::bigint, 'a household member sees its recipes'' ingredients');
insert into foods (id, household_id, source, name, basis)
  values ('70000000-0000-7000-8000-000000000004', current_setting('t.hb')::uuid, 'custom', 'B''s granola', 'g');
select throws_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 9, '70000000-0000-7000-8000-000000000004', 50, 'g', 'g', 50) $$,
         current_setting('t.ha')),
  '42501', null, 'a member of two households cannot put one''s food in the other''s recipe');
select pg_temp.login('b');
select is((select count(*) from recipes) + (select count(*) from recipe_ingredients), 0::bigint, 'a stranger sees no recipes');
select throws_ok(
  format($$ insert into recipe_ingredients (household_id, recipe_id, position, food_id, quantity, quantity_unit, basis, base_amount)
            values (%L, '71000000-0000-7000-8000-000000000001', 9, '70000000-0000-7000-8000-000000000001', 1, 'g', 'g', 1) $$,
         current_setting('t.ha')),
  '42501', null, 'a stranger cannot add to another household''s recipe');

-------- Food log
select pg_temp.login('a');
select lives_ok(
  format($$ insert into food_log_entries (id, profile_id, logged_at, local_date, meal, entry_method, food_id, name,
              quantity, quantity_unit, basis, base_amount, energy, protein, carbohydrate, fat, nutrients)
            values ('72000000-0000-7000-8000-000000000001', %L, '2026-11-07T08:00:00Z', '2026-11-07', 'breakfast', 'search',
                    '70000000-0000-7000-8000-000000000001', 'Oats', 2.5, 'oz', 'g', 0, 268.7, 9.36, 47.99, 4.61,
                    '{"fiber": 7.16}') $$, current_setting('t.a')),
  'A can log a food');
select is((select base_amount from food_log_entries where id = '72000000-0000-7000-8000-000000000001'),
  70.8738078125::double precision, 'the logged amount gets canonical grams');
select lives_ok(
  format($$ insert into food_log_entries (profile_id, logged_at, local_date, meal, entry_method, recipe_id, name,
              quantity, quantity_unit, energy, nutrients, incomplete_nutrients)
            values (%L, '2026-11-07T12:00:00Z', '2026-11-07', 'lunch', 'recipe', '71000000-0000-7000-8000-000000000001',
                    'Porridge', 1, 'serving', 299.08, '{"fiber": 5.6}', '["fiber"]') $$, current_setting('t.a')),
  'A can log a recipe serving, with a lower-bound nutrient marked incomplete');
select lives_ok(
  format($$ insert into food_log_entries (profile_id, logged_at, local_date, meal, entry_method, name, energy, is_estimate)
            values (%L, '2026-11-07T15:00:00Z', '2026-11-07', 'snack', 'quick_add', 'Coffee shop muffin', 420, true) $$,
         current_setting('t.a')),
  'A can quick-add an estimate without a food');
select throws_ok(
  format($$ insert into food_log_entries (profile_id, logged_at, local_date, meal, entry_method, name, quantity, quantity_unit)
            values (%L, now(), '2026-11-07', 'snack', 'search', 'x', 1, 'serving') $$, current_setting('t.a')),
  '23514', null, 'servings belong to a recipe');
select throws_ok(
  format($$ insert into food_log_entries (profile_id, logged_at, local_date, meal, entry_method, name)
            values (%L, now(), '2026-11-07', 'snack', 'search', 'x') $$, current_setting('t.b')),
  '42501', null, 'A cannot log for another profile');

select pg_temp.login('c');
select is((select count(*) from food_log_entries), 0::bigint, 'a household member sees no log without a grant');
select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'nutrition');
select pg_temp.login('c');
select is((select count(*) from food_log_entries), 3::bigint, 'a nutrition grant lets a member read the log');
select is(pg_temp.affected($$ update food_log_entries set name = 'x' $$), 0::bigint, 'a grant is read-only');

select * from finish();
rollback;
