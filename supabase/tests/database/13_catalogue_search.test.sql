-- Catalogue food search (public.search_catalogue_foods): every word must match the name or brand, catalogue rows
-- only, ranked by how the name starts, callable by signed-in users only. Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(13);

insert into auth.users (id, email) values ('00000000-0000-0000-0000-00000000000a', 'a@example.com');
select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);
select set_config('t.ha', (select household_id::text from household_members where profile_id = current_setting('t.a')::uuid), true);

-- Catalogue rows, as the importer writes them.
insert into foods (id, source, external_id, name, brand, basis, energy_100, protein_100, fat_100, portions) values
  ('50000000-0000-0000-0000-000000000001', 'usda_fdc', '1', 'Chicken, broilers or fryers, breast, meat only, cooked, roasted', null, 'g', 165, 31, 3.6,
   '[{"name": "1 cup, chopped or diced", "amount": 140}]'),
  ('50000000-0000-0000-0000-000000000002', 'usda_fdc', '2', 'Chicken breast', null, 'g', 120, 22.5, 2.6, '[]'),
  ('50000000-0000-0000-0000-000000000003', 'usda_fdc', '3', 'Soup, chicken noodle', null, 'g', 25, 1.5, 0.7, '[]'),
  ('50000000-0000-0000-0000-000000000004', 'usda_fdc', '4', 'Milk, 100% whole', 'Dairy Co', 'ml', 61, 3.2, 3.3, '[]'),
  ('50000000-0000-0000-0000-000000000005', 'usda_fdc', '5', 'Milk, 2% fat', null, 'ml', 50, 3.3, 2, '[]');

set local role authenticated;
select set_config('request.jwt.claims', json_build_object(
  'sub', '00000000-0000-0000-0000-00000000000a', 'role', 'authenticated')::text, true);

-- A household food with a matching name: never a search result.
insert into foods (id, household_id, source, name, basis, energy_100)
  values ('50000000-0000-0000-0000-0000000000aa', current_setting('t.ha')::uuid, 'custom', 'Chicken breast, my marinade', 'g', 150);

select is(
  array(select name from search_catalogue_foods('chicken breast')),
  array['Chicken breast', 'Chicken, broilers or fryers, breast, meat only, cooked, roasted'],
  'every word must match; a name starting with the whole query ranks first, then one starting with the first word');
select is((select count(*) from search_catalogue_foods('chicken')), 3::bigint,
  'one word matches anywhere in the name');
select is(
  (select name from search_catalogue_foods('chicken') order by 1 desc limit 1), 'Soup, chicken noodle',
  'a word in the middle of a name matches');
select is((select count(*) from search_catalogue_foods('CHICK BREA')), 2::bigint,
  'matching ignores case and takes the start of words');
select is((select count(*) from search_catalogue_foods('chicken marinade')), 0::bigint,
  'household foods are never returned');
select is(
  (select portions from search_catalogue_foods('roasted')), '[{"name": "1 cup, chopped or diced", "amount": 140}]'::jsonb,
  'portions and values come back with the food');
select is((select count(*) from search_catalogue_foods('dairy')), 1::bigint, 'the brand is searched too');
select is(array(select name from search_catalogue_foods('100%')), array['Milk, 100% whole'],
  'a % in the query is a literal percent sign, not a wildcard');
select is((select count(*) from search_catalogue_foods('2_')), 0::bigint, 'an underscore is literal too');
select is((select count(*) from search_catalogue_foods('c')), 0::bigint, 'a one-character query returns nothing');
select is((select count(*) from search_catalogue_foods('chicken', 1)), 1::bigint, 'max_results limits the results');
select is((select count(*) from search_catalogue_foods('milk', 500)), 2::bigint, 'a limit over 50 is capped, not refused');

reset role;
set local role anon;
select throws_ok($$ select * from search_catalogue_foods('chicken') $$, '42501', null,
  'anon cannot search the catalogue');

select * from finish();
rollback;
