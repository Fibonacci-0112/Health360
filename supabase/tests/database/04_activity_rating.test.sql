-- The PA-R column: null by default, 0–7 only, owner-only writes.
begin;
create extension if not exists pgtap with schema extensions;
select plan(8);

insert into auth.users (id, email, raw_user_meta_data) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.com', '{}'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.com', '{}');
select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);

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
  (select activity_rating from profiles where id = current_setting('t.a')::uuid), null::smallint,
  'a new profile has no rating'
);

set local role authenticated;
select pg_temp.login('a');
select is(pg_temp.affected($$ update profiles set activity_rating = 7 $$), 1::bigint, 'A can set their own rating');
select is(pg_temp.affected($$ update profiles set activity_rating = 0 $$), 1::bigint, '0 is a rating');
select throws_ok($$ update profiles set activity_rating = 8 $$, '23514', null, 'a rating above 7 is rejected');
select throws_ok($$ update profiles set activity_rating = -1 $$, '23514', null, 'a negative rating is rejected');
select is(pg_temp.affected($$ update profiles set activity_rating = null $$), 1::bigint, 'A can clear their rating');

select pg_temp.login('b');
select is(
  pg_temp.affected(format($$ update profiles set activity_rating = 3 where id = %L $$, current_setting('t.a'))),
  0::bigint, 'B cannot set A''s rating'
);

reset role;
set local role anon;
select throws_ok($$ update profiles set activity_rating = 3 $$, '42501', null, 'anon cannot set a rating');

select * from finish();
rollback;
