-- Nutrition targets (PLAN.md §2.3 and step 1.3; docs/calculations.md, "Nutrition targets").
--
--   nutrition_targets (U)  one row per profile: how its daily energy and macronutrient targets are set.
--
-- The row holds the settings, not the computed grams. A formula target is computed on the device from the physiology
-- profile every time it is shown (Health360.Domain NutritionTargets.Compute), so a lab RMR entered or deleted changes
-- it at once, and no stored number can go stale. A manual target is an explicit override that nothing recomputes.
-- The formula fields are kept while the method is manual, so switching back restores them.
--
-- Energy is stored in kcal, as food_log_entries stores it; the app converts from and to the user's kJ preference.
-- The protein ratio is stored as entered with its unit (g_per_kg or g_per_lb), plus protein_ratio_g_per_kg, which a
-- trigger computes exactly as Health360.Domain ProteinRatio.ToGPerKg does.
--
-- The row id is derived from the profile (private.nutrition_target_id), the same on every device, so targets set
-- offline on two devices end as one row and the last upload wins. Adaptive targets (method 'adaptive') come in
-- Phase 2 with the weekly check-in, and history of earlier targets with the goal wizard (step 4); a day is compared
-- with the current targets until then.

-- A UUIDv8 (RFC 9562 §5.8) from the first 128 bits of SHA-256('health360/nutrition_targets/<profile id>'), with the
-- version and variant bits set. Health360.Domain's UuidV8.FromName computes the same.
create function private.nutrition_target_id(profile_id uuid) returns uuid
language plpgsql immutable set search_path = '' as $$
declare
  h bytea := substring(
    pg_catalog.sha256(convert_to('health360/nutrition_targets/' || profile_id::text, 'UTF8')) from 1 for 16);
begin
  h := set_byte(h, 6, (get_byte(h, 6) & 15) | 128); -- version 8
  h := set_byte(h, 8, (get_byte(h, 8) & 63) | 128); -- RFC 9562 variant
  return encode(h, 'hex')::uuid;
end $$;

-- g per kg from a ratio as entered. Mirrors ProteinRatio.ToGPerKg: g/lb ÷ 0.45359237. Null for an unknown unit.
create function private.protein_ratio_g_per_kg(ratio double precision, unit text) returns double precision
language sql immutable set search_path = '' as $$
  select case unit
    when 'g_per_kg' then ratio
    when 'g_per_lb' then ratio / 0.45359237::double precision
  end
$$;

create table public.nutrition_targets (
  id uuid primary key,
  profile_id uuid not null references public.profiles (id) on delete cascade,
  method text not null check (method in ('formula', 'manual')),
  -- Formula: added to maintenance energy, kcal/day (negative for a deficit).
  energy_adjustment_kcal double precision not null default 0
    check (energy_adjustment_kcal >= -5000 and energy_adjustment_kcal <= 5000),
  protein_basis text not null check (protein_basis in ('body_weight', 'lean_mass')),
  protein_ratio double precision not null,
  protein_ratio_unit text not null check (protein_ratio_unit in ('g_per_kg', 'g_per_lb')),
  protein_ratio_g_per_kg double precision not null check (protein_ratio_g_per_kg > 0 and protein_ratio_g_per_kg <= 10),
  fat_percent double precision not null check (fat_percent > 0 and fat_percent < 100),
  -- Manual: the targets as typed. Energy is required for a manual target; any macro may be left unset.
  manual_energy_kcal double precision check (manual_energy_kcal > 0 and manual_energy_kcal <= 20000),
  manual_protein_g double precision check (manual_protein_g >= 0 and manual_protein_g <= 2000),
  manual_fat_g double precision check (manual_fat_g >= 0 and manual_fat_g <= 2000),
  manual_carbohydrate_g double precision check (manual_carbohydrate_g >= 0 and manual_carbohydrate_g <= 2000),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint nutrition_targets_manual check (method <> 'manual' or manual_energy_kcal is not null),
  constraint nutrition_targets_id check (id = private.nutrition_target_id(profile_id))
);
create index nutrition_targets_profile_idx on public.nutrition_targets (profile_id);

-- Runs as the caller. An unknown unit leaves the canonical column null, which fails its not-null check (class 23),
-- so the upload connector quarantines the row.
create function private.nutrition_targets_canonical() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.protein_ratio_g_per_kg := private.protein_ratio_g_per_kg(new.protein_ratio, new.protein_ratio_unit);
  return new;
end $$;

create trigger nutrition_targets_canonical before insert or update on public.nutrition_targets
  for each row execute function private.nutrition_targets_canonical();
create trigger nutrition_targets_updated_at before update on public.nutrition_targets
  for each row execute function private.set_updated_at();

-------------------------------------------------------------------------------
-- Grants and row level security
-------------------------------------------------------------------------------

-- The id check and the canonical value run with the caller's rights (00_rls_coverage checks the grants).
revoke all on function private.nutrition_target_id(uuid) from public, anon;
grant execute on function private.nutrition_target_id(uuid) to authenticated;
revoke all on function private.protein_ratio_g_per_kg(double precision, text) from public, anon;
grant execute on function private.protein_ratio_g_per_kg(double precision, text) to authenticated;
revoke all on function private.nutrition_targets_canonical() from public, anon;

alter table public.nutrition_targets enable row level security;
revoke all on public.nutrition_targets from anon;

-- The owner reads and writes; a household member reads with a 'nutrition' grant, as for the food log.
create policy nutrition_targets_select on public.nutrition_targets for select to authenticated
  using (profile_id in (select private.readable_profile_ids('nutrition')));
create policy nutrition_targets_insert on public.nutrition_targets for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy nutrition_targets_update on public.nutrition_targets for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy nutrition_targets_delete on public.nutrition_targets for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-- PowerSync: to the owner (the me stream).
alter publication powersync add table public.nutrition_targets;
