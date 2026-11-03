-- session_exercises.template_exercise_id: the template row an entry was planned from. A plain link, so an entry
-- whose template row is gone (deleted on another device while the workout was offline) still uploads.
-- Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(4);

insert into auth.users (id, email) values ('00000000-0000-0000-0000-00000000000a', 'a@example.com');
select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);

insert into exercises (id, external_id, name, tracking_type)
  values ('60000000-0000-7000-8000-000000000001', 'Barbell_Squat', 'Barbell squat', 'weight_reps');

create function pg_temp.login(who text) returns void language sql as $$
  select set_config('request.jwt.claims', json_build_object(
    'sub', '00000000-0000-0000-0000-00000000000' || who, 'role', 'authenticated')::text, true);
$$;
grant execute on function pg_temp.login(text) to authenticated;

select has_column('public', 'session_exercises', 'template_exercise_id', 'session_exercises links its template row');
select is(
  (select count(*)::int from pg_constraint c join pg_attribute a on a.attrelid = c.conrelid and a.attnum = any (c.conkey)
   where c.conrelid = 'public.session_exercises'::regclass and c.contype = 'f' and a.attname = 'template_exercise_id'),
  0, 'the link has no foreign key, so a deleted template row cannot block an upload');

set local role authenticated;
select pg_temp.login('a');
insert into workout_templates (id, profile_id, name)
  values ('70000000-0000-7000-8000-000000000001', current_setting('t.a')::uuid, 'Legs');
insert into template_exercises (id, profile_id, template_id, exercise_id, position)
  values ('70000000-0000-7000-8000-000000000002', current_setting('t.a')::uuid, '70000000-0000-7000-8000-000000000001',
          '60000000-0000-7000-8000-000000000001', 0);
insert into workout_sessions (id, profile_id, template_id, kind, started_at, local_date)
  values ('70000000-0000-7000-8000-000000000003', current_setting('t.a')::uuid, '70000000-0000-7000-8000-000000000001',
          'strength', now(), current_date);

select lives_ok(
  format($$ insert into session_exercises (profile_id, session_id, exercise_id, position, template_exercise_id)
            values (%L, '70000000-0000-7000-8000-000000000003', '60000000-0000-7000-8000-000000000001', 0,
                    '70000000-0000-7000-8000-000000000002') $$, current_setting('t.a')),
  'an entry can name the template row it was planned from');
select lives_ok(
  format($$ insert into session_exercises (profile_id, session_id, exercise_id, position, template_exercise_id)
            values (%L, '70000000-0000-7000-8000-000000000003', '60000000-0000-7000-8000-000000000001', 1,
                    '70000000-0000-7000-8000-0000000000ff') $$, current_setting('t.a')),
  'an entry whose template row is gone still uploads');

select * from finish();
rollback;
