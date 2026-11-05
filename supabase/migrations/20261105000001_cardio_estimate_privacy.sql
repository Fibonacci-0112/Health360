-- Keep physiology inputs and their provenance out of workout rows, which are readable with a workouts grant.
create table public.cardio_estimates (
  id uuid primary key,
  profile_id uuid not null references public.profiles (id) on delete cascade,
  estimate jsonb not null check (jsonb_typeof(estimate) = 'object'),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  foreign key (id, profile_id) references public.workout_sessions (id, profile_id) on delete cascade
);
create index cardio_estimates_profile_idx on public.cardio_estimates (profile_id);
create trigger cardio_estimates_updated_at before update on public.cardio_estimates
  for each row execute function private.set_updated_at();

-- Move existing full snapshots before replacing the workout-visible payload.
insert into public.cardio_estimates (id, profile_id, estimate, created_at, updated_at)
select id, profile_id, energy_estimate, created_at, updated_at
from public.workout_sessions
where kind = 'cardio' and energy_estimate is not null and jsonb_typeof(energy_estimate) = 'object';

alter table public.cardio_estimates enable row level security;
revoke all on public.cardio_estimates from anon;

-- Full estimates are private to the profile owner; workouts grants expose only the summary above.
create policy cardio_estimates_select on public.cardio_estimates for select to authenticated
  using (profile_id in (select private.my_profile_ids()));
create policy cardio_estimates_insert on public.cardio_estimates for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy cardio_estimates_update on public.cardio_estimates for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy cardio_estimates_delete on public.cardio_estimates for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create function private.cardio_workout_summary(value jsonb) returns jsonb
language sql immutable set search_path = '' as $$
  select jsonb_build_object(
    'version', value -> 'version',
    'entry', jsonb_build_object(
      'activity_code', value -> 'entry' -> 'activity_code',
      'duration_seconds', value -> 'entry' -> 'duration_seconds',
      'average_hr', value -> 'entry' -> 'average_hr',
      'max_hr', value -> 'entry' -> 'max_hr',
      'rpe', value -> 'entry' -> 'rpe',
      'intervals', value -> 'entry' -> 'intervals',
      'distance', value -> 'entry' -> 'distance',
      'distance_unit', value -> 'entry' -> 'distance_unit'
    ),
    'activity', jsonb_build_object(
      'code', value -> 'activity' -> 'code',
      'name', value -> 'activity' -> 'name',
      'sport', value -> 'activity' -> 'sport',
      'mets', value -> 'activity' -> 'mets'
    ),
    'method', replace(value ->> 'method', ' (VO₂max)', ''),
    'kcal', jsonb_build_object('low', value -> 'kcal' -> 'low', 'high', value -> 'kcal' -> 'high'),
    'aerobic_seconds', value -> 'aerobic_seconds',
    'anaerobic_seconds', value -> 'anaerobic_seconds',
    'distance_metres', value -> 'distance_metres'
  )
$$;
revoke all on function private.cardio_workout_summary(jsonb) from public, anon;
grant execute on function private.cardio_workout_summary(jsonb) to authenticated;

update public.workout_sessions
set energy_estimate = private.cardio_workout_summary(energy_estimate)
where kind = 'cardio' and energy_estimate is not null and jsonb_typeof(energy_estimate) = 'object';

create function private.cardio_estimate_privacy() returns trigger
language plpgsql set search_path = '' as $$
declare
  summary jsonb;
begin
  if new.kind <> 'cardio' or new.energy_estimate is null then
    return new;
  end if;

  summary := private.cardio_workout_summary(new.energy_estimate);
  if new.energy_estimate <> summary then
    if new.energy_estimate ? 'physiology' or new.energy_estimate ? 'zones' then
      insert into public.cardio_estimates (id, profile_id, estimate, created_at, updated_at)
      values (new.id, new.profile_id, new.energy_estimate, new.created_at, new.updated_at)
      on conflict (id) do update
        set estimate = excluded.estimate, updated_at = excluded.updated_at;
    end if;
    update public.workout_sessions set energy_estimate = summary where id = new.id and profile_id = new.profile_id;
  end if;
  return new;
end $$;
revoke all on function private.cardio_estimate_privacy() from public, anon;
create trigger cardio_estimate_privacy after insert or update of energy_estimate on public.workout_sessions
  for each row execute function private.cardio_estimate_privacy();

alter publication powersync add table public.cardio_estimates;
