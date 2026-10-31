-- Phase 1, step 1.1: body metrics, vitals and the physiology profile (PLAN.md §2.3, §2.8).
--
--   metric_definitions (G)  what can be measured: code, sharing category, entry units, canonical unit.
--   measurement_groups (U)  one sitting: a DEXA scan, a lab visit, a field test, or values entered together.
--   measurements       (U)  one value, stored as entered (value + unit) plus value_canonical.
--   zone_sets          (U)  heart-rate, power or pace zones per sport, effective from a date.
--
-- Access: the owner reads and writes; a household member reads only with a sharing grant for the row's
-- category ('body' or 'physiology'). Grants are read-only, so nobody else can change these rows.
--
-- Not here yet (PLAN.md §2.8): the "newest per metric and source tier" marker for the sync window, and
-- exercise_test_stages (Phase 3). measurement_groups.report_path exists, but nothing writes it yet; the
-- lab-reports bucket and its policies arrive with the upload flow.

-------------------------------------------------------------------------------
-- Unit conversion, mirrored from Health360.Domain/Units.
-------------------------------------------------------------------------------

-- Each ratio unit as (dimension, num, den) against its dimension's base unit, exactly as Unit.cs
-- defines it: value_in_base = value × num / den. Codes are the database codes (Unit.Code). Literals
-- are cast to double precision before any arithmetic, so 2.54 × 12 is the same double C# computes,
-- not an exact numeric rounded afterwards.
create function private.unit_ratio(unit text, out dimension text, out num double precision, out den double precision)
language sql immutable set search_path = '' as $$
  select r.dimension, r.num, r.den from (values
    ('kg', 'mass', 1::double precision, 1::double precision),
    ('g', 'mass', 1::double precision, 1000::double precision),
    ('lb', 'mass', 0.45359237::double precision, 1::double precision),
    ('oz', 'mass', 28.349523125::double precision, 1000::double precision),
    ('m', 'length', 1::double precision, 1::double precision),
    ('cm', 'length', 1::double precision, 100::double precision),
    ('km', 'length', 1000::double precision, 1::double precision),
    ('in', 'length', 2.54::double precision, 100::double precision),
    ('ft', 'length', 2.54::double precision * 12, 100::double precision),
    ('yd', 'length', 2.54::double precision * 36, 100::double precision),
    ('mi', 'length', 1609.344::double precision, 1::double precision),
    ('ml', 'volume', 1::double precision, 1::double precision),
    ('l', 'volume', 1000::double precision, 1::double precision),
    ('fl_oz', 'volume', 29.5735295625::double precision, 1::double precision),
    ('cup', 'volume', 29.5735295625::double precision * 8, 1::double precision),
    ('tbsp', 'volume', 29.5735295625::double precision, 2::double precision),
    ('tsp', 'volume', 29.5735295625::double precision, 6::double precision),
    ('kcal', 'energy', 1::double precision, 1::double precision),
    ('kJ', 'energy', 1::double precision, 4.184::double precision),
    ('mmol/L', 'glucose', 1::double precision, 1::double precision),
    ('mg/dL', 'glucose', 1::double precision, 18.0156::double precision)
  ) as r (code, dimension, num, den)
  where r.code = unit
$$;

-- Converts between two unit codes; null when they can't be converted. Mirrors, operation for operation:
--   * Conversion.Convert: the same unit returns the value unchanged; temperature is affine;
--     otherwise value × a.num × b.den / (a.den × b.num), evaluated left to right.
--   * Pace.ToSecondsPerKm / FromSecondsPerKm for s_per_mi ↔ s_per_km.
-- Fixed scientific units (bpm, W, ml/kg/min, ...) convert only to themselves.
-- tests/Health360.Server.Tests/CanonicalUnitParityTests.cs checks it bit for bit against Domain.
create function private.convert_unit(value double precision, from_unit text, to_unit text) returns double precision
language plpgsql immutable set search_path = '' as $$
declare
  a record;
  b record;
begin
  if value is null or from_unit is null or to_unit is null then
    return null;
  end if;
  if from_unit = to_unit then
    return value;
  end if;
  if from_unit = 'C' and to_unit = 'F' then
    return (value * 9 / 5) + 32;
  end if;
  if from_unit = 'F' and to_unit = 'C' then
    return (value - 32) * 5 / 9;
  end if;
  if from_unit = 's_per_mi' and to_unit = 's_per_km' then
    return value * 1000 / 1609.344::double precision;
  end if;
  if from_unit = 's_per_km' and to_unit = 's_per_mi' then
    return value * 1609.344::double precision / 1000;
  end if;

  select * into a from private.unit_ratio(from_unit);
  select * into b from private.unit_ratio(to_unit);
  if a.dimension is null or b.dimension is null or a.dimension <> b.dimension then
    return null;
  end if;
  return value * a.num * b.den / (a.den * b.num);
end $$;

-------------------------------------------------------------------------------
-- metric_definitions (G)
-------------------------------------------------------------------------------

-- valid_min and valid_max (canonical units, inclusive) are definitional limits only: a percentage is
-- 0–100, a mass or a rate is never negative. They are not plausibility or clinical ranges; the app
-- has no cited source for those yet, so none are set.
create table public.metric_definitions (
  id uuid primary key default gen_random_uuid(),
  code text not null unique check (code ~ '^[a-z][a-z0-9_]{0,47}$'),
  -- The sharing category a measurement of this metric falls under (sharing_grants.category).
  category text not null check (category in ('body', 'physiology')),
  -- Units a value may be entered in, as unit codes. The canonical unit is always one of them.
  units jsonb not null check (jsonb_typeof(units) = 'array' and jsonb_array_length(units) > 0),
  canonical_unit text not null,
  valid_min double precision,
  valid_max double precision,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint metric_definitions_canonical_listed check (units ? canonical_unit),
  constraint metric_definitions_range check (valid_min is null or valid_max is null or valid_min <= valid_max)
);
create trigger metric_definitions_updated_at before update on public.metric_definitions
  for each row execute function private.set_updated_at();

-- Unit codes: Health360.Domain's Unit.Code ('kg', 'lb', 'cm', 'in', 'kcal', 'kJ', 'mmol/L', 'mg/dL',
-- 'C', 'F'), pace in seconds per km or mile ('s_per_km', 's_per_mi', as Pace stores it), and fixed
-- scientific units (PLAN.md §2.6): 'bpm', 'W', 'ms', 'mmHg', '%', 'ml/kg/min', 'L/min', 'g/cm2',
-- 'kg/m2', 'steps', and 'sd' for T- and Z-scores. RMR is energy per day.
insert into public.metric_definitions (code, category, units, canonical_unit, valid_min, valid_max) values
  -- Body (scale, tape, vitals)
  ('weight', 'body', '["kg", "lb"]', 'kg', 0, null),
  ('body_fat_pct', 'body', '["%"]', '%', 0, 100),
  ('fat_mass', 'body', '["kg", "lb", "g"]', 'kg', 0, null),
  ('lean_mass', 'body', '["kg", "lb", "g"]', 'kg', 0, null),
  ('circ_neck', 'body', '["cm", "in"]', 'cm', 0, null),
  -- Navy method sites (docs/calculations.md): abdomen II at the navel (men), abdomen I at the narrowest
  -- point (women).
  ('circ_waist_navel', 'body', '["cm", "in"]', 'cm', 0, null),
  ('circ_waist_narrowest', 'body', '["cm", "in"]', 'cm', 0, null),
  ('circ_hip', 'body', '["cm", "in"]', 'cm', 0, null),
  ('circ_chest', 'body', '["cm", "in"]', 'cm', 0, null),
  ('circ_upper_arm', 'body', '["cm", "in"]', 'cm', 0, null),
  ('circ_thigh', 'body', '["cm", "in"]', 'cm', 0, null),
  ('circ_calf', 'body', '["cm", "in"]', 'cm', 0, null),
  ('bp_systolic', 'body', '["mmHg"]', 'mmHg', 0, null),
  ('bp_diastolic', 'body', '["mmHg"]', 'mmHg', 0, null),
  ('spo2', 'body', '["%"]', '%', 0, 100),
  ('glucose', 'body', '["mmol/L", "mg/dL"]', 'mmol/L', 0, null),
  ('body_temperature', 'body', '["C", "F"]', 'C', null, null),
  ('steps', 'body', '["steps"]', 'steps', 0, null),
  ('hrv_rmssd', 'body', '["ms"]', 'ms', 0, null),
  -- Physiology profile (§2.8)
  ('resting_hr', 'physiology', '["bpm"]', 'bpm', 0, null),
  ('max_hr', 'physiology', '["bpm"]', 'bpm', 0, null),
  ('lthr', 'physiology', '["bpm"]', 'bpm', 0, null),
  ('rmr', 'physiology', '["kcal", "kJ"]', 'kcal', 0, null),
  ('vo2max', 'physiology', '["ml/kg/min"]', 'ml/kg/min', 0, null),
  ('vo2max_absolute', 'physiology', '["L/min"]', 'L/min', 0, null),
  ('lt1_hr', 'physiology', '["bpm"]', 'bpm', 0, null),
  ('lt2_hr', 'physiology', '["bpm"]', 'bpm', 0, null),
  ('vt1_hr', 'physiology', '["bpm"]', 'bpm', 0, null),
  ('vt2_hr', 'physiology', '["bpm"]', 'bpm', 0, null),
  ('lt1_power', 'physiology', '["W"]', 'W', 0, null),
  ('lt2_power', 'physiology', '["W"]', 'W', 0, null),
  ('vt1_power', 'physiology', '["W"]', 'W', 0, null),
  ('vt2_power', 'physiology', '["W"]', 'W', 0, null),
  ('lt1_pace', 'physiology', '["s_per_km", "s_per_mi"]', 's_per_km', 0, null),
  ('lt2_pace', 'physiology', '["s_per_km", "s_per_mi"]', 's_per_km', 0, null),
  ('vt1_pace', 'physiology', '["s_per_km", "s_per_mi"]', 's_per_km', 0, null),
  ('vt2_pace', 'physiology', '["s_per_km", "s_per_mi"]', 's_per_km', 0, null),
  ('ftp', 'physiology', '["W"]', 'W', 0, null),
  ('threshold_pace', 'physiology', '["s_per_km", "s_per_mi"]', 's_per_km', 0, null),
  -- DEXA (shown as the report states it; §2.5). Whole-body fat %, fat and lean mass are the body rows above.
  ('fat_mass_arms', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('fat_mass_legs', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('fat_mass_trunk', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('lean_mass_arms', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('lean_mass_legs', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('lean_mass_trunk', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('bone_mineral_content', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('bmd', 'physiology', '["g/cm2"]', 'g/cm2', 0, null),
  ('bmd_t_score', 'physiology', '["sd"]', 'sd', null, null),
  ('bmd_z_score', 'physiology', '["sd"]', 'sd', null, null),
  ('vat_mass', 'physiology', '["kg", "lb", "g"]', 'kg', 0, null),
  ('android_fat_pct', 'physiology', '["%"]', '%', 0, 100),
  ('gynoid_fat_pct', 'physiology', '["%"]', '%', 0, 100),
  ('almi', 'physiology', '["kg/m2"]', 'kg/m2', 0, null);

-------------------------------------------------------------------------------
-- measurement_groups (U)
-------------------------------------------------------------------------------

create table public.measurement_groups (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  kind text not null check (kind in ('manual', 'dexa', 'rmr', 'metabolic_test', 'lactate_test', 'field_test')),
  measured_at timestamptz not null,
  local_date date not null,
  provider text check (char_length(provider) <= 120),
  -- The scanner or device make: Hologic and GE Lunar DEXA results differ (§2.8).
  device_make text check (char_length(device_make) <= 80),
  protocol text check (char_length(protocol) <= 200),
  -- The report file in the private lab-reports bucket. Nothing writes it yet. Its first path segment
  -- must be the owner's profile id, as Storage policies will require (PLAN.md §2.3).
  report_path text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  -- Lets measurements and zone sets reference a group of the same profile only.
  unique (id, profile_id),
  constraint measurement_groups_report_path check (
    report_path is null or (
      char_length(report_path) <= 512
      and starts_with(report_path, profile_id::text || '/')
      and report_path !~ '(^|/)\.\.?(/|$)'
    )
  )
);
create index measurement_groups_profile_idx on public.measurement_groups (profile_id, measured_at);
create trigger measurement_groups_updated_at before update on public.measurement_groups
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- measurements (U)
-------------------------------------------------------------------------------

create table public.measurements (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  metric_code text not null references public.metric_definitions (code),
  -- Copied from metric_definitions by trigger, so RLS can pick the sharing category without a join.
  category text not null check (category in ('body', 'physiology')),
  value double precision not null check (value > '-Infinity' and value < 'Infinity'),
  unit text not null,
  -- Recomputed by trigger from value and unit, in the metric's canonical unit; what clients send is ignored.
  value_canonical double precision not null,
  measured_at timestamptz not null,
  local_date date not null,
  -- Deleting a group (one scan or test) deletes its values.
  group_id uuid,
  source text not null check (source in ('lab', 'field_test', 'device', 'manual')),
  -- How it was measured, e.g. 'dexa', 'navy', 'smart_scale', 'cooper'. Each method is its own trend series.
  method text check (method ~ '^[a-z][a-z0-9_]{0,39}$'),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  foreign key (group_id, profile_id) references public.measurement_groups (id, profile_id) on delete cascade
);
create index measurements_profile_metric_idx on public.measurements (profile_id, metric_code, measured_at);
create index measurements_group_idx on public.measurements (group_id);

-- Runs as the caller, so it sees metric_definitions through its select policy. Errors use SQLSTATE
-- class 23, which the upload connector treats as permanent and quarantines.
create function private.measurements_canonical() returns trigger
language plpgsql set search_path = '' as $$
declare
  d public.metric_definitions;
begin
  select * into d from public.metric_definitions where code = new.metric_code;
  if not found then
    raise exception 'unknown metric %', new.metric_code using errcode = '23503';
  end if;
  if not (d.units ? new.unit) then
    raise exception 'unit % is not allowed for %', new.unit, new.metric_code using errcode = '23514';
  end if;

  new.category := d.category;
  new.value_canonical := private.convert_unit(new.value, new.unit, d.canonical_unit);
  if new.value_canonical is null then
    raise exception 'cannot convert % to %', new.unit, d.canonical_unit using errcode = '23514';
  end if;
  if new.value_canonical < d.valid_min or new.value_canonical > d.valid_max then
    raise exception 'value out of range for %', new.metric_code using errcode = '23514';
  end if;
  return new;
end $$;

create trigger measurements_canonical before insert or update on public.measurements
  for each row execute function private.measurements_canonical();
create trigger measurements_updated_at before update on public.measurements
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- zone_sets (U)
-------------------------------------------------------------------------------

-- boundaries: the lower bound of each zone, zone 1 first, as entered in `unit`. A value below the first
-- bound is below zone 1; the last zone has no upper bound. Intensity rises zone by zone, so the numbers
-- ascend for hr (bpm) and power (W) and descend for pace (seconds per km or mile; faster is smaller).
create table public.zone_sets (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  sport text not null check (sport in ('all', 'run', 'bike', 'swim', 'row', 'other')),
  metric text not null check (metric in ('hr', 'power', 'pace')),
  basis text not null check (basis in ('custom', 'lab', 'lthr', 'hrr', 'max_hr')),
  boundaries jsonb not null,
  unit text not null,
  -- Recomputed by trigger in bpm, W or s_per_km; what clients send is ignored.
  boundaries_canonical jsonb not null,
  effective_from date not null,
  -- The lab test the zones came from, when basis is 'lab'. Deleting the test keeps the zones.
  group_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  foreign key (group_id, profile_id) references public.measurement_groups (id, profile_id) on delete set null (group_id)
);
create index zone_sets_profile_idx on public.zone_sets (profile_id, sport, metric, effective_from);
create index zone_sets_group_idx on public.zone_sets (group_id);

create function private.zone_sets_canonical() returns trigger
language plpgsql set search_path = '' as $$
declare
  canonical_unit text := case new.metric when 'hr' then 'bpm' when 'power' then 'W' when 'pace' then 's_per_km' end;
  element jsonb;
  bound double precision;
  previous double precision;
  converted jsonb := '[]'::jsonb;
begin
  if not (
    (new.metric = 'hr' and new.unit = 'bpm')
    or (new.metric = 'power' and new.unit = 'W')
    or (new.metric = 'pace' and new.unit in ('s_per_km', 's_per_mi'))
  ) then
    raise exception 'unit % is not allowed for % zones', new.unit, new.metric using errcode = '23514';
  end if;
  if jsonb_typeof(new.boundaries) is distinct from 'array' then
    raise exception 'boundaries must be an array' using errcode = '23514';
  end if;
  if jsonb_array_length(new.boundaries) not between 2 and 10 then
    raise exception 'boundaries must list 2 to 10 zones' using errcode = '23514';
  end if;

  for element in select e from jsonb_array_elements(new.boundaries) as e loop
    if jsonb_typeof(element) <> 'number' then
      raise exception 'boundaries must be numbers' using errcode = '23514';
    end if;
    bound := private.convert_unit((element #>> '{}')::double precision, new.unit, canonical_unit);
    if bound is null or not (bound >= 0 and bound < 'Infinity') or (new.metric = 'pace' and bound = 0) then
      raise exception 'boundaries must be finite and not negative' using errcode = '23514';
    end if;
    if previous is not null and (
      (new.metric = 'pace' and bound >= previous) or (new.metric <> 'pace' and bound <= previous)
    ) then
      raise exception 'boundaries must rise in intensity zone by zone' using errcode = '23514';
    end if;
    previous := bound;
    converted := converted || to_jsonb(bound);
  end loop;

  new.boundaries_canonical := converted;
  return new;
end $$;

create trigger zone_sets_canonical before insert or update on public.zone_sets
  for each row execute function private.zone_sets_canonical();
create trigger zone_sets_updated_at before update on public.zone_sets
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- Grants and row level security
-------------------------------------------------------------------------------

-- The triggers call these with the caller's rights (00_rls_coverage checks the grants).
revoke all on function private.unit_ratio(text), private.convert_unit(double precision, text, text) from public, anon;
grant execute on function private.unit_ratio(text), private.convert_unit(double precision, text, text) to authenticated;
revoke all on function private.measurements_canonical(), private.zone_sets_canonical() from public, anon;

alter table public.metric_definitions enable row level security;
alter table public.measurement_groups enable row level security;
alter table public.measurements enable row level security;
alter table public.zone_sets enable row level security;

revoke all on public.metric_definitions, public.measurement_groups, public.measurements, public.zone_sets from anon;

revoke insert, update, delete on public.metric_definitions from authenticated;
create policy metric_definitions_select on public.metric_definitions for select to authenticated
  using (true);

-- Each policy compares profile_id with an uncorrelated set (one helper call per statement, not per row).
-- A group of kind 'manual' falls under 'body'; every test or scan kind under 'physiology'. Its
-- measurements are still checked by their own metric's category.
create policy measurement_groups_select on public.measurement_groups for select to authenticated
  using (
    (kind = 'manual' and profile_id in (select private.readable_profile_ids('body')))
    or (kind <> 'manual' and profile_id in (select private.readable_profile_ids('physiology')))
  );
create policy measurement_groups_insert on public.measurement_groups for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy measurement_groups_update on public.measurement_groups for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy measurement_groups_delete on public.measurement_groups for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy measurements_select on public.measurements for select to authenticated
  using (
    (category = 'body' and profile_id in (select private.readable_profile_ids('body')))
    or (category = 'physiology' and profile_id in (select private.readable_profile_ids('physiology')))
  );
create policy measurements_insert on public.measurements for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy measurements_update on public.measurements for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy measurements_delete on public.measurements for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

create policy zone_sets_select on public.zone_sets for select to authenticated
  using (profile_id in (select private.readable_profile_ids('physiology')));
create policy zone_sets_insert on public.zone_sets for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy zone_sets_update on public.zone_sets for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy zone_sets_delete on public.zone_sets for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-------------------------------------------------------------------------------
-- PowerSync: metric_definitions goes to everyone (reference stream), the rest to their owner (me).
-------------------------------------------------------------------------------

alter publication powersync add table
  public.metric_definitions, public.measurement_groups, public.measurements, public.zone_sets;
