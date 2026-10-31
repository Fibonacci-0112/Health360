-- A vs B, member vs non-member, and sharing grants, on the Phase 0 tables.
begin;
create extension if not exists pgtap with schema extensions;
select plan(33);

-- Users A and B each get a profile and a personal household from the sign-up trigger.
-- C is a member of A's household; B is not.
insert into auth.users (id, email, raw_user_meta_data) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.com', '{"display_name":"Alex"}'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.com', '{}'),
  ('00000000-0000-0000-0000-00000000000c', 'c@example.com', '{}');

select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);
select set_config('t.b', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000b'), true);
select set_config('t.c', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000c'), true);
select set_config('t.ha', (select household_id::text from household_members where profile_id = current_setting('t.a')::uuid), true);

insert into household_members (household_id, profile_id, role)
  values (current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'adult');
insert into reference_datasets (code, version) values ('pgtap-fixture', '1');



select is((select display_name from profiles where id = current_setting('t.a')::uuid), 'Alex', 'sign-up uses display_name metadata');
select is((select display_name from profiles where id = current_setting('t.b')::uuid), 'b', 'sign-up falls back to the email name');
select is(
  (select user_id from household_members where profile_id = current_setting('t.c')::uuid and household_id = current_setting('t.ha')::uuid),
  '00000000-0000-0000-0000-00000000000c'::uuid,
  'membership copies user_id for the sync stream'
);

create function pg_temp.login(who text) returns void language sql as $$
  select set_config('request.jwt.claims', json_build_object(
    'sub', case who when 'a' then '00000000-0000-0000-0000-00000000000a'
                    when 'b' then '00000000-0000-0000-0000-00000000000b'
                    else '00000000-0000-0000-0000-00000000000c' end,
    'role', 'authenticated')::text, true);
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

-------- A
set local role authenticated;
select pg_temp.login('a');

select results_eq('select id from profiles', format('values (%L::uuid)', current_setting('t.a')), 'A sees only their own profile');
select results_eq('select id from households', format('values (%L::uuid)', current_setting('t.ha')), 'A sees only their own household');
select is((select count(*) from household_members), 2::bigint, 'A sees both members of their household');

select lives_ok(
  format($$ insert into notes (id, profile_id, body, local_date)
            values ('10000000-0000-0000-0000-000000000001', %L, 'A private note', '2026-10-25') $$, current_setting('t.a')),
  'A can write a note for their own profile'
);
select throws_ok(
  format($$ insert into notes (profile_id, body, local_date) values (%L, 'forged', '2026-10-25') $$, current_setting('t.b')),
  '42501', null, 'A cannot write a note into B''s profile'
);
select throws_ok($$ update profiles set user_id = '00000000-0000-0000-0000-00000000000b' $$,
  '42501', null, 'A cannot change the login a profile belongs to');
select throws_ok($$ insert into profiles (user_id) values ('00000000-0000-0000-0000-00000000000a') $$,
  '42501', null, 'clients cannot create profiles');
select throws_ok($$ select * from household_invites $$, '42501', null, 'household_invites is server-only');
select throws_ok($$ insert into household_members (household_id, profile_id, role)
                    select household_id, profile_id, 'owner' from household_members limit 1 $$,
  '42501', null, 'clients cannot add household members');

-- A client sends its own canonical value; the trigger recomputes it, so the two can't drift.
update profiles set height = 70, height_unit = 'in', height_cm = 1 where id = current_setting('t.a')::uuid;
select is((select height_cm from profiles where id = current_setting('t.a')::uuid), 177.8::double precision,
  'the database recomputes canonical height from the value as entered');
-- 65.6 × 2.54 is 166.624, but Domain computes 65.6 × 2.54 × 100 / 100 (Conversion.Convert); the server
-- must store the same double.
update profiles set height = 65.6, height_unit = 'in' where id = current_setting('t.a')::uuid;
select is((select height_cm from profiles where id = current_setting('t.a')::uuid), 166.62399999999997::double precision,
  'canonical height matches Domain bit for bit');

select is((select count(*) from reference_datasets where code = 'pgtap-fixture'), 1::bigint, 'reference data is readable');

select lives_ok(
  format($$ insert into consents (id, profile_id, kind, version, granted)
            values ('20000000-0000-0000-0000-000000000001', %L, 'tos', '1', true) $$, current_setting('t.a')),
  'A can record a consent decision'
);
select throws_ok($$ update consents set granted = false $$, '42501', null, 'past consent decisions cannot be changed');
select throws_ok($$ delete from consents $$, '42501', null, 'past consent decisions cannot be erased');
select throws_ok($$ insert into reference_datasets (code, version) values ('x', '1') $$,
  '42501', null, 'clients cannot write reference data');

-------- B (another household)
select pg_temp.login('b');
select is((select count(*) from notes), 0::bigint, 'B cannot see A''s note');
select is((select count(*) from profiles where id = current_setting('t.a')::uuid), 0::bigint, 'B cannot see A''s profile');
select is(
  pg_temp.affected($$ update notes set body = 'hijacked' $$),
  0::bigint, 'B cannot update A''s note'
);
select is(
  pg_temp.affected($$ update profiles set display_name = 'hijacked' where id = current_setting('t.a')::uuid $$),
  0::bigint, 'B cannot update A''s profile'
);

-------- C (A's household, no grant yet)
select pg_temp.login('c');
select is((select count(*) from notes), 0::bigint, 'a household member cannot see A''s note without a grant');

-------- A shares notes with C only, then a household-wide grant
select pg_temp.login('a');
select lives_ok(
  format($$ insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
            values (%L, %L, %L, 'notes') $$, current_setting('t.a'), current_setting('t.ha'), current_setting('t.c')),
  'A can share notes with a member of their household'
);
select throws_ok(
  format($$ insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
            values (%L, %L, %L, 'notes') $$, current_setting('t.a'), current_setting('t.ha'), current_setting('t.b')),
  '42501', null, 'A cannot share with someone outside the household'
);
-- Managed profiles, set up as superuser (only the server creates them).
reset role;
-- M: a profile A manages, which is a member of B's household (A's own profile is not).
select set_config('t.hb', (select household_id::text from household_members where profile_id = current_setting('t.b')::uuid), true);
insert into profiles (id, managed_by, display_name)
  values ('30000000-0000-0000-0000-00000000000d', '00000000-0000-0000-0000-00000000000a', 'Kid');
select set_config('t.m', '30000000-0000-0000-0000-00000000000d', true);
insert into household_members (household_id, profile_id, role)
  values (current_setting('t.hb')::uuid, current_setting('t.m')::uuid, 'teen');

-- Handing M to C must move M's membership (and so the household stream) to C.
update profiles set managed_by = '00000000-0000-0000-0000-00000000000c' where id = current_setting('t.m')::uuid;
select is(
  (select user_id from household_members where profile_id = current_setting('t.m')::uuid),
  '00000000-0000-0000-0000-00000000000c'::uuid,
  'a new manager takes over the copied user_id on existing memberships'
);
update profiles set managed_by = '00000000-0000-0000-0000-00000000000a' where id = current_setting('t.m')::uuid;
set local role authenticated;
select pg_temp.login('a');

select throws_ok(
  format($$ insert into sharing_grants (profile_id, household_id, category)
            values (%L, %L, 'notes') $$, current_setting('t.a'), current_setting('t.hb')),
  '42501', null, 'a grant must name a household its owner profile belongs to, not just another of the user''s profiles'
);
select throws_ok(
  format($$ insert into sharing_grants (profile_id, household_id, grantee_profile_id, category, access)
            values (%L, %L, %L, 'notes', 'write') $$, current_setting('t.a'), current_setting('t.ha'), current_setting('t.c')),
  '23514', null, 'write grants are not accepted until something enforces them'
);

select pg_temp.login('c');
select is((select count(*) from notes), 1::bigint, 'C sees A''s note once it is shared');
select is(
  pg_temp.affected($$ update notes set body = 'edited by C' $$),
  0::bigint, 'a read grant does not let C edit A''s note'
);

select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, null, 'notes');
select pg_temp.login('b');
select is((select count(*) from notes), 0::bigint, 'a household-wide grant still excludes non-members');

-------- anon
reset role;
set local role anon;
select throws_ok($$ select * from notes $$, '42501', null, 'anon cannot read anything');

select * from finish();
rollback;
