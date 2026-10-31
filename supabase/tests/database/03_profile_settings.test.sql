-- The medication flag column: default, owner-only writes, and that it is independent of physiology_prefs.
begin;
create extension if not exists pgtap with schema extensions;
select plan(7);

insert into auth.users (id, email, raw_user_meta_data) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.com', '{}'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.com', '{}');
select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);
select set_config('t.b', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000b'), true);

create function pg_temp.login(who text) returns void language sql as $$
  select set_config('request.jwt.claims', json_build_object(
    'sub', case who when 'a' then '00000000-0000-0000-0000-00000000000a' else '00000000-0000-0000-0000-00000000000b' end,
    'role', 'authenticated')::text, true);
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

select is(
  (select takes_hr_lowering_medication from profiles where id = current_setting('t.a')::uuid), false,
  'the flag is off for a new profile'
);

set local role authenticated;
select pg_temp.login('a');
select is(
  pg_temp.affected($$ update profiles set takes_hr_lowering_medication = true $$), 1::bigint,
  'A can set the flag on their own profile'
);
-- Two devices each sending only the column they changed: neither undoes the other.
update profiles set unit_prefs = '{"load":"lb"}';
select is(
  (select (takes_hr_lowering_medication, unit_prefs) from profiles),
  (true, '{"load":"lb"}'::jsonb),
  'a write to another column leaves the flag as it was'
);
select is((select physiology_prefs from profiles), '{}'::jsonb, 'the flag is not kept in physiology_prefs');

select pg_temp.login('b');
select is(
  pg_temp.affected(format($$ update profiles set takes_hr_lowering_medication = true where id = %L $$, current_setting('t.a'))),
  0::bigint, 'B cannot set A''s flag'
);
select is((select takes_hr_lowering_medication from profiles), false, 'B''s own flag is untouched');

reset role;
set local role anon;
select throws_ok(
  $$ update profiles set takes_hr_lowering_medication = true $$,
  '42501', null, 'anon cannot set the flag'
);

select * from finish();
rollback;
