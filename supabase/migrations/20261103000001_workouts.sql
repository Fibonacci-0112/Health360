-- Phase 1, step 2: exercises, workout templates, sessions and sets (PLAN.md §2.3, §2.9, §3).
--
--   exercises          (G/H) the exercise library: system rows (household_id null, written by tools/importers
--                            with the service role) and a household's own custom exercises.
--   workout_templates  (U)   a named routine.
--   template_exercises (U)   one exercise in a template, in order, with its set scheme, rest and progression rule.
--   workout_sessions   (U)   one workout as performed: strength, cardio or both, with its heart-rate summary.
--   session_exercises  (U)   one exercise in a session, in order.
--   set_logs           (U)   one set as entered: reps, load (weight + weight_unit + weight_kg), RPE/RIR, duration,
--                            distance (distance + distance_unit + distance_m).
--
-- Access: U rows follow the 'workouts' sharing category. The owner reads and writes; a household member reads
-- only with a 'workouts' grant. Grants are read-only. Every child row carries profile_id, and composite foreign
-- keys keep it equal to its parent's, so RLS never needs a join. A custom exercise is read and written by members
-- of its household; system exercises are read-only for clients.
--
-- Not here yet (PLAN.md §2.3 and step 2's deliverables): training_locations, programs, scheduled_workouts,
-- personal_records and set_logs.e1rm_kg / is_pr (with the PR logic), and the exercise import itself.

-------------------------------------------------------------------------------
-- exercises (G system rows, H custom rows)
-------------------------------------------------------------------------------

-- A JSON array of distinct short codes, e.g. ["barbell"] or ["quadriceps", "glutes"].
create function private.is_code_array(value jsonb) returns boolean
language sql immutable set search_path = '' as $$
  select jsonb_typeof(value) = 'array'
    and jsonb_array_length(value) <= 20
    and not exists (
      select 1 from jsonb_array_elements(value) e
      where jsonb_typeof(e) <> 'string' or (e #>> '{}') !~ '^[a-z][a-z0-9_]{0,39}$'
    )
    and (select count(distinct e) from jsonb_array_elements(value) e) = jsonb_array_length(value)
$$;

create table public.exercises (
  id uuid primary key default gen_random_uuid(),
  -- Null for a system exercise; the household that owns a custom one otherwise.
  household_id uuid references public.households (id) on delete cascade,
  -- The importer's stable key for a system exercise (e.g. the free-exercise-db id). Null for custom ones.
  external_id text check (char_length(external_id) between 1 and 120),
  name text not null check (char_length(btrim(name)) between 1 and 120),
  -- What a set of it records.
  tracking_type text not null check (tracking_type in ('weight_reps', 'reps', 'duration', 'distance_duration')),
  equipment jsonb not null default '[]'::jsonb check (private.is_code_array(equipment)),
  primary_muscles jsonb not null default '[]'::jsonb check (private.is_code_array(primary_muscles)),
  secondary_muscles jsonb not null default '[]'::jsonb check (private.is_code_array(secondary_muscles)),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint exercises_external_id_system check (external_id is null or household_id is null),
  unique (external_id)
);
create index exercises_household_idx on public.exercises (household_id);
create trigger exercises_updated_at before update on public.exercises
  for each row execute function private.set_updated_at();

-- A custom exercise stays in its household: moving it would hide it from the members whose templates and sessions
-- use it. RLS can't compare old and new rows, so a trigger does. SQLSTATE class 23, so uploads quarantine.
create function private.exercises_fixed_household() returns trigger
language plpgsql set search_path = '' as $$
begin
  if new.household_id is distinct from old.household_id then
    raise exception 'an exercise cannot move to another household' using errcode = '23514';
  end if;
  return new;
end $$;

create trigger exercises_fixed_household before update of household_id on public.exercises
  for each row execute function private.exercises_fixed_household();

-------------------------------------------------------------------------------
-- workout_templates, template_exercises (U)
-------------------------------------------------------------------------------

create table public.workout_templates (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  name text not null check (char_length(btrim(name)) between 1 and 120),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  -- Lets children reference a template of the same profile only.
  unique (id, profile_id)
);
create index workout_templates_profile_idx on public.workout_templates (profile_id);
create trigger workout_templates_updated_at before update on public.workout_templates
  for each row execute function private.set_updated_at();

-- set_scheme: the prescription as the app writes it, e.g. {"sets": 3, "min_reps": 8, "max_reps": 12} with an
-- optional load as entered ({"load": 60, "load_unit": "kg"}). progression: the rule and its settings, e.g.
-- {"kind": "double", "increment": 2.5, "increment_unit": "kg"} (Health360.Domain/Training/Progression). Both are
-- objects; their fields are validated by the app until the logger fixes them.
create table public.template_exercises (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  template_id uuid not null,
  -- An exercise in use can't be deleted (the upload is quarantined), so a template never loses one silently.
  exercise_id uuid not null references public.exercises (id),
  position integer not null check (position >= 0),
  set_scheme jsonb not null default '{}'::jsonb check (jsonb_typeof(set_scheme) = 'object'),
  rest_seconds integer check (rest_seconds between 0 and 3600),
  progression jsonb check (jsonb_typeof(progression) = 'object'),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  foreign key (template_id, profile_id) references public.workout_templates (id, profile_id) on delete cascade
);
create index template_exercises_template_idx on public.template_exercises (template_id, position);
create index template_exercises_profile_idx on public.template_exercises (profile_id);
create index template_exercises_exercise_idx on public.template_exercises (exercise_id);
create trigger template_exercises_updated_at before update on public.template_exercises
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- workout_sessions, session_exercises, set_logs (U)
-------------------------------------------------------------------------------

-- 1 to 10 zones of non-negative seconds. (A check constraint can't hold a subquery, hence a function.)
create function private.is_zone_seconds(value jsonb) returns boolean
language sql immutable set search_path = '' as $$
  select jsonb_typeof(value) = 'array'
    and jsonb_array_length(value) between 1 and 10
    and not exists (
      select 1 from jsonb_array_elements(value) e
      where jsonb_typeof(e) <> 'number' or (e #>> '{}')::numeric < 0
    )
$$;

create table public.workout_sessions (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  -- The template it started from. Deleting the template keeps the session.
  template_id uuid,
  kind text not null check (kind in ('strength', 'cardio', 'mixed')),
  -- The sport for cardio, matching zone_sets.sport so the session's zones can be found.
  sport text check (sport in ('run', 'bike', 'swim', 'row', 'other')),
  started_at timestamptz not null,
  -- Null while the session is in progress.
  ended_at timestamptz,
  local_date date not null,
  -- Heart-rate summary in bpm, as entered or from a device.
  avg_hr double precision check (avg_hr > 0 and avg_hr < 'Infinity'),
  max_hr double precision check (max_hr > 0 and max_hr < 'Infinity'),
  -- Seconds in each zone, zone 1 first, from a watch summary (§2.9): a JSON array of non-negative numbers.
  zone_seconds jsonb,
  -- The snapshot Health360.Domain computes (§2.9): kcal range, fat/carb split, aerobic/anaerobic minutes, inputs
  -- and method version. Stored, not recomputed, so editing zones later leaves past sessions unchanged.
  energy_estimate jsonb check (jsonb_typeof(energy_estimate) = 'object'),
  -- Session RPE on the 0–10 scale.
  rpe double precision check (rpe between 0 and 10),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, profile_id),
  constraint workout_sessions_ended check (ended_at is null or ended_at >= started_at),
  constraint workout_sessions_hr check (avg_hr is null or max_hr is null or avg_hr <= max_hr),
  constraint workout_sessions_zone_seconds check (zone_seconds is null or private.is_zone_seconds(zone_seconds)),
  foreign key (template_id, profile_id) references public.workout_templates (id, profile_id) on delete set null (template_id)
);
create index workout_sessions_profile_idx on public.workout_sessions (profile_id, started_at);
create index workout_sessions_template_idx on public.workout_sessions (template_id);
create trigger workout_sessions_updated_at before update on public.workout_sessions
  for each row execute function private.set_updated_at();

create table public.session_exercises (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  session_id uuid not null,
  exercise_id uuid not null references public.exercises (id),
  position integer not null check (position >= 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, profile_id),
  foreign key (session_id, profile_id) references public.workout_sessions (id, profile_id) on delete cascade
);
create index session_exercises_session_idx on public.session_exercises (session_id, position);
create index session_exercises_profile_idx on public.session_exercises (profile_id);
create index session_exercises_exercise_idx on public.session_exercises (exercise_id);
create trigger session_exercises_updated_at before update on public.session_exercises
  for each row execute function private.set_updated_at();

create table public.set_logs (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  session_exercise_id uuid not null,
  position integer not null check (position >= 0),
  set_type text not null default 'working' check (set_type in ('warmup', 'working', 'drop', 'backoff', 'failure')),
  reps integer check (reps >= 0),
  -- The load as entered, in a lifting unit. weight_kg is recomputed by trigger; what clients send is ignored.
  weight double precision check (weight >= 0 and weight < 'Infinity'),
  weight_unit text check (weight_unit in ('kg', 'lb')),
  weight_kg double precision,
  rpe double precision check (rpe between 0 and 10),
  rir integer check (rir between 0 and 20),
  duration_seconds double precision check (duration_seconds >= 0 and duration_seconds < 'Infinity'),
  -- The distance as entered. distance_m is recomputed by trigger.
  distance double precision check (distance >= 0 and distance < 'Infinity'),
  distance_unit text check (distance_unit in ('m', 'km', 'mi', 'yd')),
  distance_m double precision,
  -- When the set was ticked off; null while it is only planned.
  completed_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint set_logs_weight_pair check ((weight is null) = (weight_unit is null)),
  constraint set_logs_distance_pair check ((distance is null) = (distance_unit is null)),
  foreign key (session_exercise_id, profile_id) references public.session_exercises (id, profile_id) on delete cascade
);
create index set_logs_session_exercise_idx on public.set_logs (session_exercise_id, position);
create index set_logs_profile_idx on public.set_logs (profile_id);

-- Canonical values through the same conversion as measurements (private.convert_unit, mirrored from Domain).
create function private.set_logs_canonical() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.weight_kg := private.convert_unit(new.weight, new.weight_unit, 'kg');
  new.distance_m := private.convert_unit(new.distance, new.distance_unit, 'm');
  return new;
end $$;

create trigger set_logs_canonical before insert or update on public.set_logs
  for each row execute function private.set_logs_canonical();
create trigger set_logs_updated_at before update on public.set_logs
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- Grants and row level security
-------------------------------------------------------------------------------

-- The check constraints call these with the caller's rights (00_rls_coverage checks the grants).
revoke all on function private.is_code_array(jsonb), private.is_zone_seconds(jsonb) from public, anon;
grant execute on function private.is_code_array(jsonb), private.is_zone_seconds(jsonb) to authenticated;
-- The importer writes system exercises as service_role, which bypasses RLS but not function privileges.
grant usage on schema private to service_role;
grant execute on function private.is_code_array(jsonb), private.is_zone_seconds(jsonb) to service_role;
revoke all on function private.set_logs_canonical(), private.exercises_fixed_household() from public, anon;

alter table public.exercises enable row level security;
alter table public.workout_templates enable row level security;
alter table public.template_exercises enable row level security;
alter table public.workout_sessions enable row level security;
alter table public.session_exercises enable row level security;
alter table public.set_logs enable row level security;

revoke all on public.exercises, public.workout_templates, public.template_exercises, public.workout_sessions,
  public.session_exercises, public.set_logs from anon;

-- System rows are read by everyone signed in; custom rows by their household. Clients write custom rows only,
-- and can't move one to another household or turn it into a system row.
create policy exercises_select on public.exercises for select to authenticated
  using (household_id is null or household_id in (select private.my_household_ids()));
create policy exercises_insert on public.exercises for insert to authenticated
  with check (household_id in (select private.my_household_ids()) and external_id is null);
create policy exercises_update on public.exercises for update to authenticated
  using (household_id in (select private.my_household_ids()))
  with check (household_id in (select private.my_household_ids()) and external_id is null);
create policy exercises_delete on public.exercises for delete to authenticated
  using (household_id in (select private.my_household_ids()));

-- The U tables. A row may only use an exercise its writer can see (the foreign key alone would accept another
-- household's custom exercise id).
create policy workout_templates_select on public.workout_templates for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy workout_templates_insert on public.workout_templates for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy workout_templates_update on public.workout_templates for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy workout_templates_delete on public.workout_templates for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy template_exercises_select on public.template_exercises for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy template_exercises_insert on public.template_exercises for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()) and exercise_id in (select e.id from public.exercises e));
create policy template_exercises_update on public.template_exercises for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()) and exercise_id in (select e.id from public.exercises e));
create policy template_exercises_delete on public.template_exercises for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy workout_sessions_select on public.workout_sessions for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy workout_sessions_insert on public.workout_sessions for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy workout_sessions_update on public.workout_sessions for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy workout_sessions_delete on public.workout_sessions for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy session_exercises_select on public.session_exercises for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy session_exercises_insert on public.session_exercises for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()) and exercise_id in (select e.id from public.exercises e));
create policy session_exercises_update on public.session_exercises for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()) and exercise_id in (select e.id from public.exercises e));
create policy session_exercises_delete on public.session_exercises for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy set_logs_select on public.set_logs for select to authenticated
  using (profile_id in (select private.readable_profile_ids('workouts')));
create policy set_logs_insert on public.set_logs for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy set_logs_update on public.set_logs for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy set_logs_delete on public.set_logs for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-------------------------------------------------------------------------------
-- PowerSync: system exercises to everyone (reference), custom ones to their household, the rest to the owner (me).
-------------------------------------------------------------------------------

alter publication powersync add table
  public.exercises, public.workout_templates, public.template_exercises, public.workout_sessions,
  public.session_exercises, public.set_logs;
