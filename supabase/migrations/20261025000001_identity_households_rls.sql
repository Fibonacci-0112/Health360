-- Phase 0: identity, households, sharing grants, RLS helpers, and the throwaway `notes` table.
--
-- Conventions every later migration copies (PLAN.md §2.3):
--   * ids are UUIDv7 generated on the client; the default only covers server-side inserts.
--   * every row carries profile_id and/or household_id, so RLS never needs a join.
--   * categories are text + CHECK, not enums.
--   * quantities are stored as entered (value + unit) plus a canonical column a trigger recomputes.
--   * RLS is on for every table; tests/database/00_rls_coverage.test.sql fails CI otherwise.
--
-- Access scopes: U = owner (+ household members a sharing grant allows), H = household members,
-- G = global reference data (read by any signed-in user), S = server only, never synced.

create schema if not exists private;
revoke all on schema private from public, anon, authenticated;
grant usage on schema private to authenticated;

-- Nothing in this app is readable without signing in.
alter default privileges in schema public revoke all on tables from anon;
alter default privileges in schema public revoke all on functions from anon;

-------------------------------------------------------------------------------
-- Shared trigger functions
-------------------------------------------------------------------------------

create function private.set_updated_at() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.updated_at := now();
  return new;
end $$;

-- Exact factors, mirrored from packages/domain/src/units/factors.ts. The client computes
-- canonical values too; recomputing here means the two can never drift (PLAN.md §2.6).
create function private.length_to_cm(value double precision, unit text) returns double precision
language sql immutable set search_path = '' as $$
  select case unit
    when 'cm' then value
    when 'm' then value * 100
    when 'in' then value * 2.54
    when 'ft' then value * 30.48
    else null
  end
$$;

-------------------------------------------------------------------------------
-- Identity (U / H)
-------------------------------------------------------------------------------

create table public.profiles (
  id uuid primary key default gen_random_uuid(),
  -- The login that owns this profile. Null for a managed (child) profile, which uses managed_by.
  user_id uuid unique references auth.users (id) on delete cascade,
  managed_by uuid references auth.users (id) on delete cascade,
  display_name text not null default '' check (char_length(display_name) <= 80),
  birth_date date,
  sex_for_calcs text check (sex_for_calcs in ('female', 'male')),
  height double precision check (height > 0),
  height_unit text check (height_unit in ('cm', 'in')),
  height_cm double precision,
  unit_prefs jsonb not null default '{}'::jsonb check (jsonb_typeof(unit_prefs) = 'object'),
  timezone text not null default 'UTC',
  currency text not null default 'USD' check (currency ~ '^[A-Z]{3}$'),
  activity_level text check (activity_level in ('sedentary', 'light', 'moderate', 'active', 'very_active')),
  physiology_prefs jsonb not null default '{}'::jsonb check (jsonb_typeof(physiology_prefs) = 'object'),
  onboarded_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint profiles_has_owner check (user_id is not null or managed_by is not null),
  constraint profiles_height_pair check ((height is null) = (height_unit is null))
);
create index profiles_managed_by_idx on public.profiles (managed_by);

create function private.profiles_canonical() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.height_cm := private.length_to_cm(new.height, new.height_unit);
  return new;
end $$;

create trigger profiles_canonical before insert or update on public.profiles
  for each row execute function private.profiles_canonical();
create trigger profiles_updated_at before update on public.profiles
  for each row execute function private.set_updated_at();

create table public.households (
  id uuid primary key default gen_random_uuid(),
  name text not null check (char_length(name) between 1 and 80),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
create trigger households_updated_at before update on public.households
  for each row execute function private.set_updated_at();

create table public.household_members (
  id uuid primary key default gen_random_uuid(),
  household_id uuid not null references public.households (id) on delete cascade,
  profile_id uuid not null references public.profiles (id) on delete cascade,
  -- Copied from the profile by trigger so the sync stream can filter without a join.
  user_id uuid references auth.users (id) on delete cascade,
  role text not null check (role in ('owner', 'adult', 'teen')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (household_id, profile_id)
);
create index household_members_profile_idx on public.household_members (profile_id);
create index household_members_user_idx on public.household_members (user_id);

create function private.household_members_copy_user() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  select coalesce(p.user_id, p.managed_by) into new.user_id from public.profiles p where p.id = new.profile_id;
  return new;
end $$;

create trigger household_members_copy_user before insert or update of profile_id on public.household_members
  for each row execute function private.household_members_copy_user();

-- Keeps the copy right when a profile changes hands (a managed profile gets a new manager, or its
-- own login), so the household stream follows the current owner, as RLS already does.
create function private.profiles_refresh_memberships() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  update public.household_members m set user_id = coalesce(new.user_id, new.managed_by)
  where m.profile_id = new.id;
  return null;
end $$;

create trigger profiles_refresh_memberships after update of user_id, managed_by on public.profiles
  for each row when (old.user_id is distinct from new.user_id or old.managed_by is distinct from new.managed_by)
  execute function private.profiles_refresh_memberships();
create trigger household_members_updated_at before update on public.household_members
  for each row execute function private.set_updated_at();

-- S: invitations are created and redeemed only by the household-invite Edge Function.
create table public.household_invites (
  id uuid primary key default gen_random_uuid(),
  household_id uuid not null references public.households (id) on delete cascade,
  invited_by_profile_id uuid not null references public.profiles (id) on delete cascade,
  email text not null,
  role text not null check (role in ('adult', 'teen')),
  token_hash text not null unique,
  expires_at timestamptz not null,
  redeemed_at timestamptz,
  created_at timestamptz not null default now()
);

-- U: lets an owner share one category of their data with one household member, or with all of
-- them when grantee_profile_id is null. Read-only for now: write access comes with the on-demand
-- shared_profile stream, together with the policies that enforce it.
create table public.sharing_grants (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  household_id uuid not null references public.households (id) on delete cascade,
  grantee_profile_id uuid references public.profiles (id) on delete cascade,
  category text not null check (category in (
    'body', 'photos', 'physiology', 'workouts', 'nutrition', 'goals', 'labs', 'supplements', 'mental', 'notes'
  )),
  access text not null default 'read' check (access = 'read'),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint sharing_grants_not_self check (grantee_profile_id is distinct from profile_id)
);
create unique index sharing_grants_unique
  on public.sharing_grants (profile_id, household_id, coalesce(grantee_profile_id, '00000000-0000-0000-0000-000000000000'::uuid), category);
create trigger sharing_grants_updated_at before update on public.sharing_grants
  for each row execute function private.set_updated_at();

create table public.consents (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  kind text not null check (kind in ('tos', 'privacy', 'ai_processing', 'photo_ai', 'health_data')),
  version text not null,
  granted boolean not null,
  decided_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
create index consents_profile_idx on public.consents (profile_id);
create trigger consents_updated_at before update on public.consents
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- Reference (G)
-------------------------------------------------------------------------------

-- Which version of each imported reference dataset is loaded (USDA FDC, Compendium METs, ...).
-- Written by tools/importers with the service role; read by every signed-in client.
create table public.reference_datasets (
  id uuid primary key default gen_random_uuid(),
  code text not null unique,
  version text not null,
  attribution text,
  imported_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
create trigger reference_datasets_updated_at before update on public.reference_datasets
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- Phase 0 throwaway (U): proves offline sync on every target.
-------------------------------------------------------------------------------

create table public.notes (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  body text not null check (char_length(body) between 1 and 2000),
  local_date date not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
create index notes_profile_idx on public.notes (profile_id);
create trigger notes_updated_at before update on public.notes
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- RLS helpers. SECURITY DEFINER so policies on profiles and household_members don't recurse.
-------------------------------------------------------------------------------

create function private.my_profile_ids() returns setof uuid
language sql stable security definer set search_path = '' as $$
  select p.id from public.profiles p
  where p.user_id = (select auth.uid()) or p.managed_by = (select auth.uid())
$$;

create function private.my_household_ids() returns setof uuid
language sql stable security definer set search_path = '' as $$
  select m.household_id from public.household_members m
  where m.profile_id in (select private.my_profile_ids())
$$;

create function private.my_owned_household_ids() returns setof uuid
language sql stable security definer set search_path = '' as $$
  select m.household_id from public.household_members m
  where m.role = 'owner' and m.profile_id in (select private.my_profile_ids())
$$;

-- Profiles whose `category` rows I may read: my own, plus anyone who granted it to me (or to
-- everyone in a household we share). The owner must still be a member of that household.
create function private.readable_profile_ids(category text) returns setof uuid
language sql stable security definer set search_path = '' as $$
  select private.my_profile_ids()
  union
  select g.profile_id from public.sharing_grants g
  join public.household_members owner_m
    on owner_m.household_id = g.household_id and owner_m.profile_id = g.profile_id
  where g.category = readable_profile_ids.category
    and g.household_id in (select private.my_household_ids())
    and (g.grantee_profile_id is null or g.grantee_profile_id in (select private.my_profile_ids()))
$$;

-------------------------------------------------------------------------------
-- New sign-up: a profile and a personal household the user owns.
-------------------------------------------------------------------------------

create function private.handle_new_user() returns trigger
language plpgsql security definer set search_path = '' as $$
declare
  new_profile_id uuid;
  new_household_id uuid;
  name text := coalesce(nullif(new.raw_user_meta_data ->> 'display_name', ''), split_part(coalesce(new.email, ''), '@', 1));
begin
  insert into public.profiles (user_id, display_name) values (new.id, left(name, 80))
    returning id into new_profile_id;
  insert into public.households (name) values (left(coalesce(nullif(name, ''), 'My') || '''s household', 80))
    returning id into new_household_id;
  insert into public.household_members (household_id, profile_id, role)
    values (new_household_id, new_profile_id, 'owner');
  return new;
end $$;

create trigger on_auth_user_created after insert on auth.users
  for each row execute function private.handle_new_user();

-- Functions get EXECUTE for PUBLIC by default. Take it back, then grant only what signed-in users
-- need: the RLS helpers, and the helpers that triggers call with the caller's rights (a missing
-- grant there fails every write; tests/database/00_rls_coverage.test.sql checks it).
revoke all on all functions in schema private from public, anon;
grant execute on function
  private.my_profile_ids(), private.my_household_ids(), private.my_owned_household_ids(),
  private.readable_profile_ids(text), private.length_to_cm(double precision, text)
  to authenticated;

-------------------------------------------------------------------------------
-- Row level security
-------------------------------------------------------------------------------

alter table public.profiles enable row level security;
alter table public.households enable row level security;
alter table public.household_members enable row level security;
alter table public.household_invites enable row level security;
alter table public.sharing_grants enable row level security;
alter table public.consents enable row level security;
alter table public.reference_datasets enable row level security;
alter table public.notes enable row level security;

revoke all on all tables in schema public from anon;

-- profiles: created by the sign-up trigger, never by clients. Owners edit their own fields only;
-- identity columns (user_id, managed_by) and computed columns stay server-side.
revoke insert, update, delete on public.profiles from authenticated;
grant update (
  display_name, birth_date, sex_for_calcs, height, height_unit, height_cm, unit_prefs, timezone, currency,
  activity_level, physiology_prefs, onboarded_at
) on public.profiles to authenticated;

create policy profiles_select on public.profiles for select to authenticated
  using (id in (select private.my_profile_ids()));
create policy profiles_update on public.profiles for update to authenticated
  using (id in (select private.my_profile_ids()))
  with check (id in (select private.my_profile_ids()));

-- households: members read; owners rename. Creation and membership go through Edge Functions.
revoke insert, update, delete on public.households from authenticated;
grant update (name) on public.households to authenticated;

create policy households_select on public.households for select to authenticated
  using (id in (select private.my_household_ids()));
create policy households_update on public.households for update to authenticated
  using (id in (select private.my_owned_household_ids()))
  with check (id in (select private.my_owned_household_ids()));

revoke insert, update, delete on public.household_members from authenticated;
create policy household_members_select on public.household_members for select to authenticated
  using (household_id in (select private.my_household_ids()));

-- household_invites: S. RLS on with no policies, and no grants.
revoke all on public.household_invites from authenticated;

create policy sharing_grants_select on public.sharing_grants for select to authenticated
  using (
    profile_id in (select private.my_profile_ids())
    or grantee_profile_id in (select private.my_profile_ids())
    or (grantee_profile_id is null and household_id in (select private.my_household_ids()))
  );
create policy sharing_grants_write on public.sharing_grants for all to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (
    profile_id in (select private.my_profile_ids())
    -- The owner profile itself must belong to the household, not just another of the user's profiles.
    and exists (
      select 1 from public.household_members m
      where m.household_id = sharing_grants.household_id and m.profile_id = sharing_grants.profile_id
    )
    and (grantee_profile_id is null or exists (
      select 1 from public.household_members m
      where m.household_id = sharing_grants.household_id and m.profile_id = sharing_grants.grantee_profile_id
    ))
  );

create policy consents_select on public.consents for select to authenticated
  using (profile_id in (select private.my_profile_ids()));
create policy consents_insert on public.consents for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
-- Consents are a record: a new decision is a new row (the latest per kind wins), and past rows can be
-- neither changed nor erased.
revoke update, delete on public.consents from authenticated;

revoke insert, update, delete on public.reference_datasets from authenticated;
create policy reference_datasets_select on public.reference_datasets for select to authenticated
  using (true);

-- The U pattern later personal tables copy: read through readable_profile_ids(category),
-- write only rows for my own profiles.
create policy notes_select on public.notes for select to authenticated
  using (profile_id in (select private.readable_profile_ids('notes')));
create policy notes_insert on public.notes for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy notes_update on public.notes for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy notes_delete on public.notes for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-------------------------------------------------------------------------------
-- PowerSync logical replication. Server-only tables (household_invites) are left out.
-- The replication role is created per environment, not here: see docs/phase0.md.
-------------------------------------------------------------------------------

create publication powersync for table
  public.profiles, public.households, public.household_members, public.sharing_grants,
  public.consents, public.reference_datasets, public.notes;
