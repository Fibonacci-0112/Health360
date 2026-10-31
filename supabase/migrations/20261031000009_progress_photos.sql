-- Phase 1, step 1.1: progress photos (PLAN.md §2.3, §2.5, §3).
--
--   progress_photos (U)          one photo's metadata: pose, when it was taken, and where its files are.
--   storage bucket progress-photos  the photo and its thumbnail, private, under the owner's profile id.
--
-- Access: the owner reads and writes. Photos are never shared by default: a household member reads them only
-- with a sharing grant for the 'photos' category (a 'body' grant does not reveal them). Grants are read-only.
-- The same rule applies to the row and to its files, so a member without a grant gets no row and a 403 on
-- the file.
--
-- Files are read through short-lived signed URLs (PLAN.md §2.3). The app strips EXIF and location metadata
-- on the device before upload (§2.5); the server cannot check that, so it accepts only JPEG.
--
-- The paths are fixed by the row id: '<profile id>/<id>.jpg' and '<profile id>/<id>_thumb.jpg'. A row can't
-- point at another profile's files or at any other object, and the delete flow (row first through the upload
-- queue, then both files through the Storage API, PLAN.md §3) always knows which files to remove.
--
-- A file may upload before its row (a photo taken offline), so the bucket's policies depend only on the
-- folder, never on the row existing. Orphaned files are removed by the server cleanup job (PLAN.md §3, step 5).
-- Never delete storage.objects rows in SQL: that leaves the file behind.

create table public.progress_photos (
  id uuid primary key,
  profile_id uuid not null references public.profiles (id) on delete cascade,
  pose text not null check (pose in ('front', 'back', 'side_left', 'side_right', 'other')),
  taken_at timestamptz not null,
  -- The day in the profile's time zone when it was taken, for the comparison view.
  local_date date not null,
  storage_path text not null,
  thumb_path text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint progress_photos_storage_path check (storage_path = profile_id::text || '/' || id::text || '.jpg'),
  constraint progress_photos_thumb_path check (thumb_path = profile_id::text || '/' || id::text || '_thumb.jpg')
);
create index progress_photos_profile_idx on public.progress_photos (profile_id, taken_at);
create trigger progress_photos_updated_at before update on public.progress_photos
  for each row execute function private.set_updated_at();

alter table public.progress_photos enable row level security;
revoke all on public.progress_photos from anon;

create policy progress_photos_select on public.progress_photos for select to authenticated
  using (profile_id in (select private.readable_profile_ids('photos')));
create policy progress_photos_insert on public.progress_photos for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy progress_photos_update on public.progress_photos for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy progress_photos_delete on public.progress_photos for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-- PowerSync: to the owner (the me stream). Only the metadata; files follow their own cache rule.
alter publication powersync add table public.progress_photos;

-------------------------------------------------------------------------------
-- Storage
-------------------------------------------------------------------------------

-- 10 MiB per file. The app resizes before upload, so a real photo is far smaller.
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('progress-photos', 'progress-photos', false, 10485760, array['image/jpeg']);

-- The first path segment is the profile id, compared as text so a malformed name never raises a cast error.
-- The full name must also be one the row could point at, so nothing else (other types, nested folders) can be
-- stored in the bucket.
create function private.progress_photo_object_profile(name text) returns text
language sql immutable set search_path = '' as $$
  select case
    when name ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(_thumb)?\.jpg$'
    then split_part(name, '/', 1)
  end
$$;
revoke all on function private.progress_photo_object_profile(text) from public, anon;
grant execute on function private.progress_photo_object_profile(text) to authenticated;

create policy progress_photos_objects_select on storage.objects for select to authenticated
  using (
    bucket_id = 'progress-photos'
    and private.progress_photo_object_profile(name) in (select p::text from private.readable_profile_ids('photos') p)
  );
create policy progress_photos_objects_insert on storage.objects for insert to authenticated
  with check (
    bucket_id = 'progress-photos'
    and private.progress_photo_object_profile(name) in (select p::text from private.my_profile_ids() p)
  );
create policy progress_photos_objects_update on storage.objects for update to authenticated
  using (
    bucket_id = 'progress-photos'
    and private.progress_photo_object_profile(name) in (select p::text from private.my_profile_ids() p)
  )
  with check (
    bucket_id = 'progress-photos'
    and private.progress_photo_object_profile(name) in (select p::text from private.my_profile_ids() p)
  );
create policy progress_photos_objects_delete on storage.objects for delete to authenticated
  using (
    bucket_id = 'progress-photos'
    and private.progress_photo_object_profile(name) in (select p::text from private.my_profile_ids() p)
  );
