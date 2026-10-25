-- Phase 0 review fixes, as a forward migration so databases that already ran
-- 20261025000001_identity_households_rls.sql get them too.

-- household_members.user_id is copied from the profile's owner on insert. Keep the copy right when a
-- profile changes hands (a managed profile gets a new manager, or its own login), so the household
-- stream follows the current owner, as RLS already does.
create function private.profiles_refresh_memberships() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  update public.household_members m set user_id = coalesce(new.user_id, new.managed_by)
  where m.profile_id = new.id;
  return null;
end $$;
revoke all on function private.profiles_refresh_memberships() from public, anon;

create trigger profiles_refresh_memberships after update of user_id, managed_by on public.profiles
  for each row when (old.user_id is distinct from new.user_id or old.managed_by is distinct from new.managed_by)
  execute function private.profiles_refresh_memberships();

-- Repair copies that went stale before the trigger existed.
update public.household_members m set user_id = coalesce(p.user_id, p.managed_by)
from public.profiles p
where p.id = m.profile_id and m.user_id is distinct from coalesce(p.user_id, p.managed_by);

-- Sharing grants are read-only for now: write access comes with the on-demand shared_profile stream,
-- together with the policies that enforce it. Write includes read, so existing grants keep what
-- they can actually do today.
update public.sharing_grants set access = 'read' where access <> 'read';
alter table public.sharing_grants drop constraint sharing_grants_access_check;
alter table public.sharing_grants add constraint sharing_grants_access_check check (access = 'read');

-- The owner profile itself must belong to the grant's household, not just another of the user's
-- profiles.
drop policy sharing_grants_write on public.sharing_grants;
create policy sharing_grants_write on public.sharing_grants for all to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (
    profile_id in (select private.my_profile_ids())
    and exists (
      select 1 from public.household_members m
      where m.household_id = sharing_grants.household_id and m.profile_id = sharing_grants.profile_id
    )
    and (grantee_profile_id is null or exists (
      select 1 from public.household_members m
      where m.household_id = sharing_grants.household_id and m.profile_id = sharing_grants.grantee_profile_id
    ))
  );

-- Consents are a record: a new decision is a new row (the latest per kind wins), and past rows can be
-- neither changed nor erased.
drop policy consents_update on public.consents;
revoke update, delete on public.consents from authenticated;
