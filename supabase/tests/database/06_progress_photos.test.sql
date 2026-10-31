-- Progress photos: the row and its files are the owner's alone, read by a household member only through a
-- 'photos' grant, and the paths are fixed by the row id. Runs as `authenticated`.
begin;
create extension if not exists pgtap with schema extensions;
select plan(31);

insert into auth.users (id, email) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.com'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.com'),
  ('00000000-0000-0000-0000-00000000000c', 'c@example.com');

select set_config('t.a', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000a'), true);
select set_config('t.b', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000b'), true);
select set_config('t.c', (select id::text from profiles where user_id = '00000000-0000-0000-0000-00000000000c'), true);
select set_config('t.ha', (select household_id::text from household_members where profile_id = current_setting('t.a')::uuid), true);
-- C is a member of A's household; B is not.
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

-- A photo row for `profile` with the paths the app derives from the id.
create function pg_temp.photo(profile uuid, photo uuid) returns void language sql as $$
  insert into progress_photos (id, profile_id, pose, taken_at, local_date, storage_path, thumb_path)
  values (photo, profile, 'front', '2026-10-30T07:00:00Z', '2026-10-30',
          profile::text || '/' || photo::text || '.jpg', profile::text || '/' || photo::text || '_thumb.jpg');
$$;
grant execute on function pg_temp.photo(uuid, uuid) to authenticated;

create function pg_temp.object(name text) returns void language sql as $$
  insert into storage.objects (bucket_id, name) values ('progress-photos', name);
$$;
grant execute on function pg_temp.object(text) to authenticated;

select is((select public from storage.buckets where id = 'progress-photos'), false, 'the bucket is private');
select is((select allowed_mime_types from storage.buckets where id = 'progress-photos'), array['image/jpeg'],
  'the bucket takes only JPEG (re-encoded on the device without EXIF)');

-- Recent Supabase Storage refuses SQL deletes on storage.objects unless this is set (the app deletes through the
-- Storage API, which runs the same policies). Set it so these tests check the policies, not that guard.
select set_config('storage.allow_delete_query', 'true', true);

set local role authenticated;

-------- The owner
select pg_temp.login('a');
select lives_ok(
  format($$ select pg_temp.photo(%L, '50000000-0000-7000-8000-000000000001') $$, current_setting('t.a')),
  'A can add a photo of their own profile');
select throws_ok(
  format($$ insert into progress_photos (id, profile_id, pose, taken_at, local_date, storage_path, thumb_path)
            values ('50000000-0000-7000-8000-000000000002', %L, 'front', now(), '2026-10-30',
                    %L || '/someone-else.jpg', %L || '/50000000-0000-7000-8000-000000000002_thumb.jpg') $$,
         current_setting('t.a'), current_setting('t.a'), current_setting('t.a')),
  '23514', null, 'the photo path must be derived from the row id');
select throws_ok(
  format($$ insert into progress_photos (id, profile_id, pose, taken_at, local_date, storage_path, thumb_path)
            values ('50000000-0000-7000-8000-000000000003', %L, 'front', now(), '2026-10-30',
                    %L || '/50000000-0000-7000-8000-000000000003.jpg', %L || '/50000000-0000-7000-8000-000000000003_thumb.jpg') $$,
         current_setting('t.a'), current_setting('t.b'), current_setting('t.b')),
  '23514', null, 'a row cannot point at another profile''s files');
select throws_ok(
  format($$ insert into progress_photos (id, profile_id, pose, taken_at, local_date, storage_path, thumb_path)
            values ('50000000-0000-7000-8000-000000000004', %L, 'sideways', now(), '2026-10-30',
                    %L || '/50000000-0000-7000-8000-000000000004.jpg', %L || '/50000000-0000-7000-8000-000000000004_thumb.jpg') $$,
         current_setting('t.a'), current_setting('t.a'), current_setting('t.a')),
  '23514', null, 'the pose must be a known one');
select is(pg_temp.affected($$ update progress_photos set pose = 'side_left' $$), 1::bigint, 'A can change the pose');

select lives_ok(
  format($$ select pg_temp.object(%L || '/50000000-0000-7000-8000-000000000001.jpg') $$, current_setting('t.a')),
  'A can upload the photo file into their own folder');
select lives_ok(
  format($$ select pg_temp.object(%L || '/50000000-0000-7000-8000-000000000001_thumb.jpg') $$, current_setting('t.a')),
  'A can upload the thumbnail');
select lives_ok(
  format($$ select pg_temp.object(%L || '/50000000-0000-7000-8000-0000000000ff.jpg') $$, current_setting('t.a')),
  'a file may upload before its row (a photo taken offline)');
select throws_ok(
  format($$ select pg_temp.object(%L || '/notes.txt') $$, current_setting('t.a')),
  '42501', null, 'only photo and thumbnail names are accepted');
select throws_ok(
  format($$ select pg_temp.object(%L || '/x/50000000-0000-7000-8000-000000000001.jpg') $$, current_setting('t.a')),
  '42501', null, 'no nested folders');
select throws_ok(
  format($$ select pg_temp.object(%L || '/50000000-0000-7000-8000-000000000009.jpg') $$, current_setting('t.b')),
  '42501', null, 'A cannot upload into another profile''s folder');
select is((select count(*) from storage.objects where bucket_id = 'progress-photos'), 3::bigint, 'A sees their files');

-------- A stranger
select pg_temp.login('b');
select is((select count(*) from progress_photos), 0::bigint, 'B sees none of A''s photos');
select is((select count(*) from storage.objects where bucket_id = 'progress-photos'), 0::bigint,
  'B sees none of A''s files (the Storage API answers 403)');
select is(pg_temp.affected($$ delete from progress_photos $$), 0::bigint, 'B cannot delete A''s photos');
select is(pg_temp.affected($$ delete from storage.objects $$), 0::bigint, 'B cannot delete A''s files');
select throws_ok(
  format($$ select pg_temp.photo(%L, '50000000-0000-7000-8000-000000000005') $$, current_setting('t.a')),
  '42501', null, 'B cannot add a photo to A''s profile');

-------- A household member: nothing by default, not even with a body grant
select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'body');
select pg_temp.login('c');
select is((select count(*) from progress_photos), 0::bigint, 'photos are not shared by default, nor by a body grant');
select is((select count(*) from storage.objects where bucket_id = 'progress-photos'), 0::bigint,
  'nor are their files');

-------- With a photos grant: read only
select pg_temp.login('a');
insert into sharing_grants (profile_id, household_id, grantee_profile_id, category)
  values (current_setting('t.a')::uuid, current_setting('t.ha')::uuid, current_setting('t.c')::uuid, 'photos');
select pg_temp.login('c');
select is((select count(*) from progress_photos), 1::bigint, 'a photos grant reveals the photo');
select is((select count(*) from storage.objects where bucket_id = 'progress-photos'), 3::bigint,
  'and its files');
select is(pg_temp.affected($$ update progress_photos set pose = 'back' $$), 0::bigint, 'C cannot change it');
select is(pg_temp.affected($$ delete from progress_photos $$), 0::bigint, 'C cannot delete it');
select is(pg_temp.affected($$ update storage.objects set metadata = '{}' $$), 0::bigint, 'C cannot change the files');
select is(pg_temp.affected($$ delete from storage.objects $$), 0::bigint, 'C cannot delete the files');
select throws_ok(
  format($$ select pg_temp.object(%L || '/50000000-0000-7000-8000-000000000006.jpg') $$, current_setting('t.a')),
  '42501', null, 'C cannot upload into A''s folder');

-------- The owner deletes
select pg_temp.login('a');
select is(pg_temp.affected($$ delete from progress_photos $$), 1::bigint, 'A can delete their photo row');
select is(pg_temp.affected(
  format($$ delete from storage.objects where name like %L $$, current_setting('t.a') || '/%')), 3::bigint,
  'A can delete their files (the app does this through the Storage API)');

-------- anon
reset role;
set local role anon;
select throws_ok($$ select count(*) from progress_photos $$, '42501', null, 'anon cannot read progress_photos');

select * from finish();
rollback;
