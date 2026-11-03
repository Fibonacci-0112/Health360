-- Phase 1, step 2 (continued): training locations, programs, the schedule, personal records and the e1RM/PR
-- columns on set_logs (PLAN.md §2.3, §2.6, §3; docs/calculations.md, e1RM and Personal records).
--
--   training_locations  (H) a gym or home setup: equipment codes, the plates it has (each in its own unit) and
--                           the bar weight as entered, with bar_weight_kg recomputed.
--   programs            (U) a dated block of training, optionally at one location.
--   scheduled_workouts  (U) a template planned on a weekday of a program (weekly) or on one date.
--   personal_records    (U) a set that beat the profile's earlier best for an exercise: e1RM, heaviest weight,
--                           most reps at a weight, or single-set volume. One row per record, so the rows are
--                           the history.
--   set_logs            e1rm_formula (the client asks for an estimate), e1rm_kg (recomputed by trigger) and is_pr.
--
-- Access as in 20261103000001_workouts.sql: U rows follow the 'workouts' grant and children carry profile_id with
-- composite foreign keys to their parent; H rows are read and written by members of their household.

-------------------------------------------------------------------------------
-- training_locations (H)
-------------------------------------------------------------------------------

-- Plates a location has: an array of {"weight": > 0, "unit": "kg" | "lb", "count": 1–100} objects, each weight
-- in the unit printed on the plate, so a gym with both kinds lists both. No other keys, at most 40 entries.
create function private.is_plate_inventory(value jsonb) returns boolean
language sql immutable set search_path = '' as $$
  select jsonb_typeof(value) = 'array'
    and jsonb_array_length(value) <= 40
    and not exists (
      select 1 from jsonb_array_elements(value) e
      -- CASE, not OR, so the casts only see values already known to be numbers.
      where case
        when jsonb_typeof(e) <> 'object' then true
        when exists (select 1 from jsonb_object_keys(e) k where k not in ('weight', 'unit', 'count')) then true
        when jsonb_typeof(e -> 'weight') is distinct from 'number' or jsonb_typeof(e -> 'unit') is distinct from 'string'
          or jsonb_typeof(e -> 'count') is distinct from 'number' then true
        else (e ->> 'weight')::numeric <= 0 or (e ->> 'weight')::numeric > 1000
          or (e ->> 'unit') not in ('kg', 'lb')
          or (e ->> 'count')::numeric not between 1 and 100
          or (e ->> 'count')::numeric <> trunc((e ->> 'count')::numeric)
      end
    )
$$;

create table public.training_locations (
  id uuid primary key default gen_random_uuid(),
  household_id uuid not null references public.households (id) on delete cascade,
  name text not null check (char_length(btrim(name)) between 1 and 120),
  -- Equipment codes, the same vocabulary as exercises.equipment.
  equipment jsonb not null default '[]'::jsonb check (private.is_code_array(equipment)),
  plate_inventory jsonb not null default '[]'::jsonb check (private.is_plate_inventory(plate_inventory)),
  -- The bar as entered. bar_weight_kg is recomputed by trigger; what clients send is ignored.
  bar_weight double precision check (bar_weight >= 0 and bar_weight <= 100),
  bar_unit text check (bar_unit in ('kg', 'lb')),
  bar_weight_kg double precision,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint training_locations_bar_pair check ((bar_weight is null) = (bar_unit is null))
);
create index training_locations_household_idx on public.training_locations (household_id);
create trigger training_locations_updated_at before update on public.training_locations
  for each row execute function private.set_updated_at();

create function private.training_locations_canonical() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.bar_weight_kg := private.convert_unit(new.bar_weight, new.bar_unit, 'kg');
  return new;
end $$;

create trigger training_locations_canonical before insert or update on public.training_locations
  for each row execute function private.training_locations_canonical();

-- Like a custom exercise, a location stays in its household: members' programs point at it. SQLSTATE class 23, so
-- uploads quarantine.
create function private.training_locations_fixed_household() returns trigger
language plpgsql set search_path = '' as $$
begin
  if new.household_id is distinct from old.household_id then
    raise exception 'a training location cannot move to another household' using errcode = '23514';
  end if;
  return new;
end $$;

create trigger training_locations_fixed_household before update of household_id on public.training_locations
  for each row execute function private.training_locations_fixed_household();

-------------------------------------------------------------------------------
-- programs, scheduled_workouts (U)
-------------------------------------------------------------------------------

create table public.programs (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  name text not null check (char_length(btrim(name)) between 1 and 120),
  -- Where it is trained; deleting the location keeps the program.
  location_id uuid references public.training_locations (id) on delete set null,
  starts_on date not null,
  -- Null for an open-ended program.
  ends_on date,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, profile_id),
  constraint programs_dates check (ends_on is null or ends_on >= starts_on)
);
create index programs_profile_idx on public.programs (profile_id, starts_on);
create index programs_location_idx on public.programs (location_id);
create trigger programs_updated_at before update on public.programs
  for each row execute function private.set_updated_at();

-- Either weekly (weekday, ISO 1 = Monday … 7 = Sunday, within its program's dates) or on one date. A dated entry
-- records the session that did it.
create table public.scheduled_workouts (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  program_id uuid,
  template_id uuid not null,
  weekday integer check (weekday between 1 and 7),
  scheduled_on date,
  session_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint scheduled_workouts_when check ((weekday is null) <> (scheduled_on is null)),
  constraint scheduled_workouts_weekly_program check (weekday is null or program_id is not null),
  constraint scheduled_workouts_session_dated check (session_id is null or scheduled_on is not null),
  foreign key (program_id, profile_id) references public.programs (id, profile_id) on delete cascade,
  foreign key (template_id, profile_id) references public.workout_templates (id, profile_id) on delete cascade,
  foreign key (session_id, profile_id) references public.workout_sessions (id, profile_id) on delete set null (session_id)
);
create index scheduled_workouts_profile_idx on public.scheduled_workouts (profile_id, scheduled_on);
create index scheduled_workouts_program_idx on public.scheduled_workouts (program_id);
create index scheduled_workouts_template_idx on public.scheduled_workouts (template_id);
create index scheduled_workouts_session_idx on public.scheduled_workouts (session_id);
create trigger scheduled_workouts_updated_at before update on public.scheduled_workouts
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- set_logs: e1RM and PR flag
-------------------------------------------------------------------------------

-- Estimated one-rep max in kg, exactly as Health360.Domain/Training/OneRepMax computes it from canonical kg (same
-- operations in the same order, so the value syncs back unchanged): a single is its own 1RM, nothing above 10 reps.
-- Null when there is nothing to estimate from.
create function private.e1rm_kg(weight_kg double precision, reps integer, formula text) returns double precision
language sql immutable set search_path = '' as $$
  select case
    when formula is null then null
    when weight_kg is null or not (weight_kg > 0) or weight_kg = 'Infinity' or reps is null or reps < 1 or reps > 10 then null
    when reps = 1 then weight_kg
    when formula = 'epley' then weight_kg * (1 + reps::double precision / 30)
    when formula = 'brzycki' then weight_kg * 36 / (37 - reps)
  end
$$;

alter table public.set_logs
  -- The formula the client asked for; null when the set gives no estimate (see the trigger).
  add column e1rm_formula text check (e1rm_formula in ('epley', 'brzycki')),
  -- Recomputed by trigger from weight_kg, reps and e1rm_formula; what clients send is ignored.
  add column e1rm_kg double precision,
  -- Set by the client when the set made a personal_records row.
  add column is_pr boolean not null default false,
  add constraint set_logs_e1rm_pair check ((e1rm_kg is null) = (e1rm_formula is null)),
  -- Lets personal_records reference a set of the same profile only.
  add constraint set_logs_id_profile_key unique (id, profile_id);

-- Canonical values, and an estimate only for a completed set taken to fatigue: not a warm-up, not one still planned.
create or replace function private.set_logs_canonical() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.weight_kg := private.convert_unit(new.weight, new.weight_unit, 'kg');
  new.distance_m := private.convert_unit(new.distance, new.distance_unit, 'm');
  new.e1rm_kg := case
    when new.completed_at is null or new.set_type = 'warmup' then null
    else private.e1rm_kg(new.weight_kg, new.reps, new.e1rm_formula)
  end;
  if new.e1rm_kg is null then
    new.e1rm_formula := null;
  end if;
  return new;
end $$;

-------------------------------------------------------------------------------
-- personal_records (U)
-------------------------------------------------------------------------------

create table public.personal_records (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  exercise_id uuid not null references public.exercises (id),
  -- e1rm, weight and volume (weight × reps) are in kg; reps is a count, at weight_kg.
  kind text not null check (kind in ('e1rm', 'weight', 'reps', 'volume')),
  value double precision not null check (value > 0 and value < 'Infinity'),
  -- The load of the set, in kg.
  weight_kg double precision not null check (weight_kg > 0 and weight_kg < 'Infinity'),
  reps integer not null check (reps >= 1),
  -- For an e1rm record, the formula it was estimated with.
  e1rm_formula text check (e1rm_formula in ('epley', 'brzycki')),
  -- The set that made it. Deleting the set deletes the record; the app works out the earlier best again.
  set_log_id uuid not null,
  achieved_at timestamptz not null,
  local_date date not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint personal_records_formula check ((kind = 'e1rm') = (e1rm_formula is not null)),
  foreign key (set_log_id, profile_id) references public.set_logs (id, profile_id) on delete cascade
);
create index personal_records_profile_idx on public.personal_records (profile_id, exercise_id, kind);
create index personal_records_exercise_idx on public.personal_records (exercise_id);
create index personal_records_set_idx on public.personal_records (set_log_id);
create trigger personal_records_updated_at before update on public.personal_records
  for each row execute function private.set_updated_at();

-- A record is of the exercise its set was logged under: the trigger copies it from the set (as measurements copy
-- their category from the metric), so the exercise's record history can't hold another exercise's sets. A set the
-- writer can't see leaves the value as sent, and the foreign key or RLS rejects the row.
create function private.personal_records_exercise() returns trigger
language plpgsql set search_path = '' as $$
declare
  set_exercise uuid;
begin
  select se.exercise_id into set_exercise
  from public.set_logs s join public.session_exercises se on se.id = s.session_exercise_id and se.profile_id = s.profile_id
  where s.id = new.set_log_id and s.profile_id = new.profile_id;
  if set_exercise is not null then
    new.exercise_id := set_exercise;
  end if;
  return new;
end $$;

create trigger personal_records_exercise before insert or update of exercise_id, set_log_id on public.personal_records
  for each row execute function private.personal_records_exercise();

-------------------------------------------------------------------------------
-- Grants and row level security
-------------------------------------------------------------------------------

revoke all on function private.is_plate_inventory(jsonb), private.e1rm_kg(double precision, integer, text) from public, anon;
grant execute on function private.is_plate_inventory(jsonb), private.e1rm_kg(double precision, integer, text) to authenticated;
revoke all on function private.training_locations_canonical(), private.training_locations_fixed_household(),
  private.personal_records_exercise() from public, anon;

alter table public.training_locations enable row level security;
alter table public.programs enable row level security;
alter table public.scheduled_workouts enable row level security;
alter table public.personal_records enable row level security;

revoke all on public.training_locations, public.programs, public.scheduled_workouts, public.personal_records from anon;

create policy training_locations_select on public.training_locations for select to authenticated
  using (household_id in (select private.my_household_ids()));
create policy training_locations_insert on public.training_locations for insert to authenticated
  with check (household_id in (select private.my_household_ids()));
create policy training_locations_update on public.training_locations for update to authenticated
  using (household_id in (select private.my_household_ids()))
  with check (household_id in (select private.my_household_ids()));
create policy training_locations_delete on public.training_locations for delete to authenticated
  using (household_id in (select private.my_household_ids()));

-- A program may only point at a location its writer can see.
create policy programs_select on public.programs for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy programs_insert on public.programs for insert to authenticated
  with check (profile_id in (select private.my_profile_ids())
    and (location_id is null or location_id in (select l.id from public.training_locations l)));
create policy programs_update on public.programs for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids())
    and (location_id is null or location_id in (select l.id from public.training_locations l)));
create policy programs_delete on public.programs for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy scheduled_workouts_select on public.scheduled_workouts for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy scheduled_workouts_insert on public.scheduled_workouts for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy scheduled_workouts_update on public.scheduled_workouts for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy scheduled_workouts_delete on public.scheduled_workouts for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy personal_records_select on public.personal_records for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy personal_records_insert on public.personal_records for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()) and exercise_id in (select e.id from public.exercises e));
create policy personal_records_update on public.personal_records for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()) and exercise_id in (select e.id from public.exercises e));
create policy personal_records_delete on public.personal_records for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-------------------------------------------------------------------------------
-- PowerSync: locations to their household, the rest to the owner (me).
-------------------------------------------------------------------------------

alter publication powersync add table
  public.training_locations, public.programs, public.scheduled_workouts, public.personal_records;
