-- Pins: the value a user chose for one metric, overriding the resolver's order (PLAN.md §2.8,
-- docs/calculations.md "Resolver").
--
--   metric_pins (U)  one row per profile and metric: a specific measurement, or the estimate.
--
-- Pins are a table, not keys in profiles.physiology_prefs: a jsonb object is uploaded whole, so a pin set on
-- one device would overwrite a pin another device set for a different metric. Here each metric is its own
-- row, so two devices only conflict when they pin the same metric, and then the last write wins.
--
-- The row id is derived from (profile_id, metric_code) (private.metric_pin_id), so every device writes the
-- same row for a metric: an offline pin on two devices upserts one row instead of the second being rejected
-- as a duplicate. The client computes the same id (Health360.Domain UuidV8.FromName, LocalDb MetricPins.IdFor).
--
-- A pin is kept when its measurement is deleted: the resolver then reports it as unavailable and falls back
-- to the next value, and the app says so instead of silently dropping the user's choice. measurement_id is
-- therefore not a foreign key, and isn't proof of anything: the resolver looks it up among the profile's own
-- measurements of the pinned metric, so a pin to anything else is simply unavailable. Any query that follows
-- it (the future sync window keeps pinned measurements at any age) must still filter measurements by
-- profile_id.

-- A UUIDv8 (RFC 9562 §5.8) from the first 128 bits of SHA-256('health360/metric_pins/<profile id>/<metric>'),
-- with the version and variant bits set. Health360.Domain's UuidV8.FromName computes the same.
create function private.metric_pin_id(profile_id uuid, metric_code text) returns uuid
language plpgsql immutable set search_path = '' as $$
declare
  h bytea := substring(
    pg_catalog.sha256(convert_to('health360/metric_pins/' || profile_id::text || '/' || metric_code, 'UTF8')) from 1 for 16);
begin
  h := set_byte(h, 6, (get_byte(h, 6) & 15) | 128); -- version 8
  h := set_byte(h, 8, (get_byte(h, 8) & 63) | 128); -- RFC 9562 variant
  return encode(h, 'hex')::uuid;
end $$;

create table public.metric_pins (
  id uuid primary key,
  profile_id uuid not null references public.profiles (id) on delete cascade,
  metric_code text not null references public.metric_definitions (code),
  -- Copied from metric_definitions by trigger, so RLS can pick the sharing category without a join, as for
  -- measurements: a pin is shared exactly as the values it chooses between.
  category text not null check (category in ('body', 'physiology')),
  -- 'measurement': use measurement_id. 'estimate': use the estimate even when measured values exist.
  target text not null check (target in ('measurement', 'estimate')),
  measurement_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (profile_id, metric_code),
  constraint metric_pins_target check ((target = 'measurement') = (measurement_id is not null)),
  constraint metric_pins_id check (id = private.metric_pin_id(profile_id, metric_code))
);
create index metric_pins_measurement_idx on public.metric_pins (measurement_id);

-- Runs as the caller, like measurements_canonical. SQLSTATE class 23, so the upload connector quarantines.
create function private.metric_pins_category() returns trigger
language plpgsql set search_path = '' as $$
declare
  d public.metric_definitions;
begin
  select * into d from public.metric_definitions where code = new.metric_code;
  if not found then
    raise exception 'unknown metric %', new.metric_code using errcode = '23503';
  end if;
  new.category := d.category;
  return new;
end $$;

create trigger metric_pins_category before insert or update on public.metric_pins
  for each row execute function private.metric_pins_category();
create trigger metric_pins_updated_at before update on public.metric_pins
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- Grants and row level security
-------------------------------------------------------------------------------

-- The id check runs with the caller's rights (00_rls_coverage checks the grants).
revoke all on function private.metric_pin_id(uuid, text) from public, anon;
grant execute on function private.metric_pin_id(uuid, text) to authenticated;
revoke all on function private.metric_pins_category() from public, anon;

alter table public.metric_pins enable row level security;
revoke all on public.metric_pins from anon;

-- The owner reads and writes; a household member reads with a grant for the pinned metric's category. Grants
-- are read-only, so nobody else can change a pin.
create policy metric_pins_select on public.metric_pins for select to authenticated
  using (
    (category = 'body' and profile_id in (select private.readable_profile_ids('body')))
    or (category = 'physiology' and profile_id in (select private.readable_profile_ids('physiology')))
  );
create policy metric_pins_insert on public.metric_pins for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy metric_pins_update on public.metric_pins for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy metric_pins_delete on public.metric_pins for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-- PowerSync: to the owner (the me stream).
alter publication powersync add table public.metric_pins;
