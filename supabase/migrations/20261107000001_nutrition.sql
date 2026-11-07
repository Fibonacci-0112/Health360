-- Phase 1, step 3: the nutrition schema (PLAN.md §2.3, §3; docs/calculations.md, Nutrition amounts and recipes).
--
--   nutrients          (G)   what can be recorded: code, fixed unit (kcal, g, mg, ug) and display order.
--   foods              (G/H) the food catalogue: system rows (household_id null, written by tools/importers with the
--                            service role: USDA FoodData Central, Open Food Facts) and a household's own foods,
--                            among them its copies of catalogue foods it uses (copied_from). Values are per 100 g
--                            or 100 ml (basis), with named portions.
--   recipes            (H)   a household's recipe: servings and the cooked weight as entered.
--   recipe_ingredients (H)   one food in a recipe, in order, with its amount as entered and in g or ml.
--   food_log_entries   (U)   one thing eaten: a snapshot of its name and nutrients for the amount eaten, so the log
--                            works offline and never changes when a food is corrected later.
--
-- Access: system foods and nutrients are read by everyone signed in and written only by the server. A household's
-- foods, recipes and ingredients are read and written by its members. Log entries follow the 'nutrition' sharing
-- category: the owner reads and writes; a household member reads only with a 'nutrition' grant.
--
-- System foods are in the publication (every synced table must be) but no stream selects them: they are searched
-- online through the API (PLAN.md §2.3). A log entry keeps its own snapshot of what it needs. A recipe uses only its
-- household's foods: a catalogue food is copied into the household the first time it is used, and the copy syncs to
-- every member's devices, so the recipe can be totalled offline anywhere.
--
-- Not here yet (step 3's later deliverables): saved_meals, nutrition_targets, label_scans and its bucket, nutrient
-- reference intakes (nutrient_dri), water, and food costs.

-------------------------------------------------------------------------------
-- Validators
-------------------------------------------------------------------------------

-- Nutrient amounts keyed by nutrient code: a JSON object of non-negative numbers, at most 200 entries. The four
-- macronutrients have their own columns, so they can't appear here too. Codes aren't checked against nutrients, so
-- a row written by a newer app with a nutrient this database hasn't seeded still uploads.
create function private.is_nutrient_map(value jsonb) returns boolean
language sql immutable set search_path = '' as $$
  select jsonb_typeof(value) = 'object'
    and (select count(*) from jsonb_object_keys(value)) <= 200
    and not exists (
      select 1 from jsonb_each(value) e
      where e.key !~ '^[a-z][a-z0-9_]{0,39}$'
        or e.key in ('energy', 'protein', 'carbohydrate', 'fat')
        or jsonb_typeof(e.value) <> 'number'
        or (e.value #>> '{}')::numeric < 0
    )
$$;

-- Distinct nutrient codes, as a JSON array of at most 200 (private.is_code_array allows only 20).
create function private.is_nutrient_codes(value jsonb) returns boolean
language sql immutable set search_path = '' as $$
  select jsonb_typeof(value) = 'array'
    and jsonb_array_length(value) <= 200
    and not exists (
      select 1 from jsonb_array_elements(value) e
      where jsonb_typeof(e) <> 'string' or (e #>> '{}') !~ '^[a-z][a-z0-9_]{0,39}$'
    )
    and (select count(distinct e) from jsonb_array_elements(value) e) = jsonb_array_length(value)
$$;

-- Named measures of a food: an array of {"name": 1–80 characters, "amount": > 0} objects, amount in g or ml of the
-- food's basis. No other keys, at most 50, names distinct.
create function private.is_food_portions(value jsonb) returns boolean
language sql immutable set search_path = '' as $$
  select jsonb_typeof(value) = 'array'
    and jsonb_array_length(value) <= 50
    and not exists (
      select 1 from jsonb_array_elements(value) e
      -- CASE, not OR, so the casts only see values already known to be numbers and strings.
      where case
        when jsonb_typeof(e) <> 'object' then true
        when exists (select 1 from jsonb_object_keys(e) k where k not in ('name', 'amount')) then true
        when jsonb_typeof(e -> 'name') is distinct from 'string' or jsonb_typeof(e -> 'amount') is distinct from 'number' then true
        else char_length(btrim(e ->> 'name')) not between 1 and 80
          or (e ->> 'amount')::numeric <= 0 or (e ->> 'amount')::numeric > 100000
      end
    )
    and (select count(distinct e ->> 'name') from jsonb_array_elements(value) e) = jsonb_array_length(value)
$$;

-------------------------------------------------------------------------------
-- nutrients (G)
-------------------------------------------------------------------------------

create table public.nutrients (
  id uuid primary key default gen_random_uuid(),
  code text not null unique check (code ~ '^[a-z][a-z0-9_]{0,39}$'),
  -- Every amount of the nutrient is in this unit; amounts have no unit column of their own.
  unit text not null check (unit in ('kcal', 'g', 'mg', 'ug')),
  position integer not null check (position >= 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);
create trigger nutrients_updated_at before update on public.nutrients
  for each row execute function private.set_updated_at();

-- The Nutrition Facts label set, in display order, as Health360.Domain/Nutrition/Nutrient.cs lists it. The USDA
-- import adds the rest.
insert into public.nutrients (code, unit, position) values
  ('energy', 'kcal', 0),
  ('fat', 'g', 1),
  ('saturated_fat', 'g', 2),
  ('trans_fat', 'g', 3),
  ('cholesterol', 'mg', 4),
  ('sodium', 'mg', 5),
  ('carbohydrate', 'g', 6),
  ('fiber', 'g', 7),
  ('sugars', 'g', 8),
  ('added_sugars', 'g', 9),
  ('sugar_alcohols', 'g', 10),
  ('protein', 'g', 11),
  ('vitamin_d', 'ug', 12),
  ('calcium', 'mg', 13),
  ('iron', 'mg', 14),
  ('potassium', 'mg', 15);

-------------------------------------------------------------------------------
-- foods (G system rows, H household rows)
-------------------------------------------------------------------------------

-- The id of a household's copy of a catalogue food: a UUIDv8 (RFC 9562 §5.8) from the first 128 bits of
-- SHA-256('health360/food_copies/<household id>/<catalogue food id>'), as private.metric_pin_id builds pins' ids.
-- Every device computes the same id (Health360.Domain FoodCopy.IdFor), so two members copying the same food offline
-- upsert one row instead of the second being rejected as a duplicate.
create function private.food_copy_id(household_id uuid, food_id uuid) returns uuid
language plpgsql immutable set search_path = '' as $$
declare
  h bytea := substring(
    pg_catalog.sha256(convert_to('health360/food_copies/' || household_id::text || '/' || food_id::text, 'UTF8')) from 1 for 16);
begin
  h := set_byte(h, 6, (get_byte(h, 6) & 15) | 128); -- version 8
  h := set_byte(h, 8, (get_byte(h, 8) & 63) | 128); -- RFC 9562 variant
  return encode(h, 'hex')::uuid;
end $$;

create table public.foods (
  id uuid primary key default gen_random_uuid(),
  -- Null for a catalogue food; the household that owns it otherwise.
  household_id uuid references public.households (id) on delete cascade,
  -- Where it came from. Catalogue rows are imported; household rows are entered or scanned.
  source text not null check (source in ('usda_fdc', 'open_food_facts', 'custom', 'label_scan')),
  -- The importer's stable key within its source (an FDC id, a barcode). Null for household rows.
  external_id text check (char_length(external_id) between 1 and 120),
  name text not null check (char_length(btrim(name)) between 1 and 200),
  brand text check (char_length(btrim(brand)) between 1 and 120),
  -- GTIN-8 to GTIN-14 digits, for barcode lookup.
  barcode text check (barcode ~ '^[0-9]{8,14}$'),
  -- What the values are per: 100 g or 100 ml.
  basis text not null default 'g' check (basis in ('g', 'ml')),
  -- Macronutrients per 100 of basis; null when unknown, which is not 0.
  energy_100 double precision check (energy_100 >= 0 and energy_100 < 'Infinity'),
  protein_100 double precision check (protein_100 >= 0 and protein_100 < 'Infinity'),
  carbohydrate_100 double precision check (carbohydrate_100 >= 0 and carbohydrate_100 < 'Infinity'),
  fat_100 double precision check (fat_100 >= 0 and fat_100 < 'Infinity'),
  -- Every other nutrient per 100 of basis, by code.
  nutrients_100 jsonb not null default '{}'::jsonb check (private.is_nutrient_map(nutrients_100)),
  portions jsonb not null default '[]'::jsonb check (private.is_food_portions(portions)),
  -- The values were checked against the label or the source (a confirmed scan, a reviewed custom food).
  verified boolean not null default false,
  -- For a household's copy, the catalogue food it was copied from; its source is that food's. Not a foreign key:
  -- it records where the values came from, and the copy stands alone if the catalogue row is ever removed.
  copied_from uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  -- Catalogue rows have a catalogue source; household rows are entered or scanned, or are copies keeping their
  -- catalogue food's source.
  constraint foods_catalogue_source check (
    case when household_id is null then source in ('usda_fdc', 'open_food_facts') and copied_from is null
         else (source in ('usda_fdc', 'open_food_facts')) = (copied_from is not null) end
  ),
  constraint foods_copy_id check (copied_from is null or id = private.food_copy_id(household_id, copied_from)),
  constraint foods_external_id_catalogue check (external_id is null or household_id is null),
  -- 100 g of food can't hold more than 100 g of one macronutrient (definitional, not a plausibility range).
  constraint foods_mass_macros check (
    basis <> 'g' or (coalesce(protein_100, 0) <= 100 and coalesce(carbohydrate_100, 0) <= 100 and coalesce(fat_100, 0) <= 100)
  ),
  unique (source, external_id)
);
create index foods_household_idx on public.foods (household_id);
create index foods_barcode_idx on public.foods (barcode) where barcode is not null;
create trigger foods_updated_at before update on public.foods
  for each row execute function private.set_updated_at();

-- A household's food stays in its household: members' recipes use it. Its basis is fixed too: recipe ingredients
-- store their amounts in it, and values per 100 ml mean something else per 100 g, so a food sold the other way is a
-- new food. Where it came from is fixed: its source, and for a copy the catalogue food. SQLSTATE class 23, so uploads
-- quarantine.
create function private.foods_fixed() returns trigger
language plpgsql set search_path = '' as $$
begin
  if new.household_id is distinct from old.household_id then
    raise exception 'a food cannot move to another household' using errcode = '23514';
  end if;
  if new.basis is distinct from old.basis then
    raise exception 'a food''s basis cannot change' using errcode = '23514';
  end if;
  if new.copied_from is distinct from old.copied_from then
    raise exception 'a copied food cannot change what it was copied from' using errcode = '23514';
  end if;
  if new.source is distinct from old.source then
    raise exception 'a food''s source cannot change' using errcode = '23514';
  end if;
  return new;
end $$;

create trigger foods_fixed before update of household_id, basis, copied_from, source on public.foods
  for each row execute function private.foods_fixed();

-- A new copy takes its values from the catalogue row, whatever the client sent, so a household food that says it
-- came from USDA FDC or Open Food Facts holds what that source published. The household may edit it afterwards (add
-- a portion, say). Reads the catalogue with the writer's rights; every signed-in user can read it. SQLSTATE 23503
-- when there is no such catalogue food, as for a broken foreign key, so the upload quarantines.
--
-- Uploads send an insert as INSERT ... ON CONFLICT (id) DO UPDATE. When the copy already exists (another member's
-- device copied the same food first), that insert must not touch it: the household may have edited it since. So the
-- insert leaves a transaction-local marker, foods_copy_conflict skips the conflict update it turns into, and the
-- existing row stays exactly as committed, however the two uploads interleave. An insert that goes ahead clears the
-- marker (foods_copy_inserted), so later updates in the same transaction are unaffected.
create function private.foods_copy() returns trigger
language plpgsql set search_path = '' as $$
declare
  original public.foods;
begin
  if new.copied_from is null then
    return new;
  end if;
  select * into original from public.foods f where f.id = new.copied_from and f.household_id is null;
  if not found then
    raise exception 'no catalogue food %', new.copied_from using errcode = '23503';
  end if;
  perform set_config('health360.food_copy_insert', new.id::text, true);
  new.source := original.source;
  new.name := original.name;
  new.brand := original.brand;
  new.barcode := original.barcode;
  new.basis := original.basis;
  new.energy_100 := original.energy_100;
  new.protein_100 := original.protein_100;
  new.carbohydrate_100 := original.carbohydrate_100;
  new.fat_100 := original.fat_100;
  new.nutrients_100 := original.nutrients_100;
  new.portions := original.portions;
  new.verified := original.verified;
  return new;
end $$;

create trigger foods_copy before insert on public.foods
  for each row execute function private.foods_copy();

create function private.foods_copy_inserted() returns trigger
language plpgsql set search_path = '' as $$
begin
  perform set_config('health360.food_copy_insert', '', true);
  return null;
end $$;

create trigger foods_copy_inserted after insert on public.foods
  for each row when (new.copied_from is not null) execute function private.foods_copy_inserted();

-- Runs before foods_fixed and foods_updated_at (triggers fire in name order). Returning null skips the update.
create function private.foods_copy_conflict() returns trigger
language plpgsql set search_path = '' as $$
begin
  if new.copied_from is not null and current_setting('health360.food_copy_insert', true) = new.id::text then
    perform set_config('health360.food_copy_insert', '', true);
    return null;
  end if;
  return new;
end $$;

create trigger foods_copy_conflict before update on public.foods
  for each row execute function private.foods_copy_conflict();

-------------------------------------------------------------------------------
-- recipes, recipe_ingredients (H)
-------------------------------------------------------------------------------

create table public.recipes (
  id uuid primary key default gen_random_uuid(),
  household_id uuid not null references public.households (id) on delete cascade,
  name text not null check (char_length(btrim(name)) between 1 and 200),
  servings double precision not null check (servings > 0 and servings <= 1000),
  -- The finished dish's weight as entered, when it was weighed. cooked_weight_g is recomputed by trigger.
  cooked_weight double precision check (cooked_weight > 0 and cooked_weight < 'Infinity'),
  cooked_weight_unit text check (cooked_weight_unit in ('g', 'kg', 'oz', 'lb')),
  cooked_weight_g double precision,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, household_id),
  constraint recipes_cooked_weight_pair check ((cooked_weight is null) = (cooked_weight_unit is null))
);
create index recipes_household_idx on public.recipes (household_id);
create trigger recipes_updated_at before update on public.recipes
  for each row execute function private.set_updated_at();

create function private.recipes_canonical() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.cooked_weight_g := private.convert_unit(new.cooked_weight, new.cooked_weight_unit, 'g');
  return new;
end $$;

create trigger recipes_canonical before insert or update on public.recipes
  for each row execute function private.recipes_canonical();

create function private.recipes_fixed_household() returns trigger
language plpgsql set search_path = '' as $$
begin
  if new.household_id is distinct from old.household_id then
    raise exception 'a recipe cannot move to another household' using errcode = '23514';
  end if;
  return new;
end $$;

create trigger recipes_fixed_household before update of household_id on public.recipes
  for each row execute function private.recipes_fixed_household();

-- An amount of food as entered: a mass or volume unit, or a number of the food's named portions ('portion', with
-- portion_name). base_amount is in the food's basis (g or ml), copied onto the row as basis. The server checks basis
-- against the food and recomputes base_amount: through private.convert_unit for a unit, and as count × the food's
-- portion of that name for portions. The ingredient keeps that amount if the food's portions are edited later.
create table public.recipe_ingredients (
  id uuid primary key default gen_random_uuid(),
  household_id uuid not null,
  recipe_id uuid not null,
  position integer not null check (position >= 0),
  -- A food of the recipe's own household, a copy for a catalogue food (the policies check it). A food in use can't
  -- be deleted (the upload is quarantined), so a recipe never loses one silently.
  food_id uuid not null references public.foods (id),
  quantity double precision not null check (quantity >= 0 and quantity < 'Infinity'),
  quantity_unit text not null check (quantity_unit in ('g', 'kg', 'oz', 'lb', 'ml', 'l', 'fl_oz', 'cup', 'tbsp', 'tsp', 'portion')),
  portion_name text check (char_length(btrim(portion_name)) between 1 and 80),
  basis text not null check (basis in ('g', 'ml')),
  base_amount double precision not null check (base_amount >= 0 and base_amount < 'Infinity'),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint recipe_ingredients_portion check ((quantity_unit = 'portion') = (portion_name is not null)),
  foreign key (recipe_id, household_id) references public.recipes (id, household_id) on delete cascade
);
create index recipe_ingredients_recipe_idx on public.recipe_ingredients (recipe_id, position);
create index recipe_ingredients_household_idx on public.recipe_ingredients (household_id);
create index recipe_ingredients_food_idx on public.recipe_ingredients (food_id);
create trigger recipe_ingredients_updated_at before update on public.recipe_ingredients
  for each row execute function private.set_updated_at();

-- Canonical amount through the same conversion as every other quantity (private.convert_unit, mirrored from
-- Domain's FoodAmount.ToBase). A unit of the wrong dimension (cups of a per-100 g food) has no conversion: it is
-- refused with a class 23 SQLSTATE, so the upload quarantines instead of storing a wrong amount.
create function private.food_amount_base(quantity double precision, quantity_unit text, basis text, client_base double precision)
returns double precision
language plpgsql immutable set search_path = '' as $$
declare
  converted double precision;
begin
  if quantity is null or quantity_unit is null or quantity_unit in ('portion', 'serving') then
    return client_base;
  end if;
  converted := private.convert_unit(quantity, quantity_unit, basis);
  if converted is null then
    raise exception '% does not measure a food listed per 100 %', quantity_unit, coalesce(basis, '?') using errcode = '23514';
  end if;
  return converted;
end $$;

-- Reads the food with the writer's rights: the policies only accept a food the writer can see, and an unknown one
-- is left to the foreign key. The basis must be the food's, and a portion must be one of its portions (class 23).
create function private.recipe_ingredients_canonical() returns trigger
language plpgsql set search_path = '' as $$
declare
  food record;
  prior public.recipe_ingredients;
  portion_amount double precision;
begin
  -- A write that leaves the amount as entered alone keeps the stored amount, so a later change to the food's portions
  -- never moves it or makes the write fail: an edit (a new position, say), and also a replayed insert, which uploads
  -- send as INSERT ... ON CONFLICT DO UPDATE and which runs this trigger as an insert before the conflict is found.
  if tg_op = 'UPDATE' then
    prior := old;
  else
    select * into prior from public.recipe_ingredients r where r.id = new.id;
  end if;
  if prior.id is not null and new.food_id = prior.food_id and new.quantity = prior.quantity
    and new.quantity_unit = prior.quantity_unit and new.portion_name is not distinct from prior.portion_name
    and new.basis = prior.basis then
    new.base_amount := prior.base_amount;
    return new;
  end if;
  select f.basis, f.portions into food from public.foods f where f.id = new.food_id;
  if found then
    if new.basis is distinct from food.basis then
      raise exception 'the ingredient''s basis % is not its food''s, %', new.basis, food.basis using errcode = '23514';
    end if;
    if new.quantity_unit = 'portion' then
      select (e ->> 'amount')::double precision into portion_amount
      from jsonb_array_elements(food.portions) e where e ->> 'name' = new.portion_name;
      if portion_amount is null then
        raise exception 'the food has no portion named %', new.portion_name using errcode = '23514';
      end if;
      new.base_amount := new.quantity * portion_amount;
      return new;
    end if;
  end if;
  new.base_amount := private.food_amount_base(new.quantity, new.quantity_unit, new.basis, new.base_amount);
  return new;
end $$;

create trigger recipe_ingredients_canonical before insert or update on public.recipe_ingredients
  for each row execute function private.recipe_ingredients_canonical();

-------------------------------------------------------------------------------
-- food_log_entries (U)
-------------------------------------------------------------------------------

create table public.food_log_entries (
  id uuid primary key default gen_random_uuid(),
  profile_id uuid not null references public.profiles (id) on delete cascade,
  logged_at timestamptz not null,
  local_date date not null,
  meal text not null check (meal in ('breakfast', 'lunch', 'dinner', 'snack')),
  entry_method text not null check (entry_method in ('search', 'barcode', 'label_scan', 'recipe', 'quick_add', 'copy', 'manual')),
  -- Where it came from. No foreign keys: a catalogue food isn't synced and a household food or recipe may be deleted
  -- later, and an entry logged offline must still upload. The snapshot below stands alone.
  food_id uuid,
  recipe_id uuid,
  name text not null check (char_length(btrim(name)) between 1 and 200),
  brand text check (char_length(btrim(brand)) between 1 and 120),
  -- The amount as entered, as for an ingredient, or a number of a recipe's servings ('serving'). Null for a quick
  -- add of calories or macros without a food.
  quantity double precision check (quantity >= 0 and quantity < 'Infinity'),
  quantity_unit text check (quantity_unit in ('g', 'kg', 'oz', 'lb', 'ml', 'l', 'fl_oz', 'cup', 'tbsp', 'tsp', 'portion', 'serving')),
  portion_name text check (char_length(btrim(portion_name)) between 1 and 80),
  basis text check (basis in ('g', 'ml')),
  base_amount double precision check (base_amount >= 0 and base_amount < 'Infinity'),
  -- Nutrients in the amount eaten, computed by the app when it was logged. Null when unknown, which is not 0.
  energy double precision check (energy >= 0 and energy < 'Infinity'),
  protein double precision check (protein >= 0 and protein < 'Infinity'),
  carbohydrate double precision check (carbohydrate >= 0 and carbohydrate < 'Infinity'),
  fat double precision check (fat >= 0 and fat < 'Infinity'),
  nutrients jsonb not null default '{}'::jsonb check (private.is_nutrient_map(nutrients)),
  -- Nutrients whose amount is only a lower bound, because some ingredients didn't list them (NutrientTotal.Incomplete).
  incomplete_nutrients jsonb not null default '[]'::jsonb check (private.is_nutrient_codes(incomplete_nutrients)),
  -- An estimate rather than a measured amount (a meal photo, a guess), shown with an estimate badge.
  is_estimate boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint food_log_entries_quantity_pair check ((quantity is null) = (quantity_unit is null)),
  constraint food_log_entries_portion check ((quantity_unit is not distinct from 'portion') = (portion_name is not null)),
  constraint food_log_entries_serving check (quantity_unit is distinct from 'serving' or recipe_id is not null),
  constraint food_log_entries_base_pair check ((basis is null) = (base_amount is null)),
  constraint food_log_entries_unit_basis check (quantity_unit is null or quantity_unit = 'serving' or basis is not null)
);
create index food_log_entries_profile_idx on public.food_log_entries (profile_id, local_date);
create trigger food_log_entries_updated_at before update on public.food_log_entries
  for each row execute function private.set_updated_at();

create function private.food_log_entries_canonical() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.base_amount := private.food_amount_base(new.quantity, new.quantity_unit, new.basis, new.base_amount);
  return new;
end $$;

create trigger food_log_entries_canonical before insert or update on public.food_log_entries
  for each row execute function private.food_log_entries_canonical();

-------------------------------------------------------------------------------
-- Grants and row level security
-------------------------------------------------------------------------------

-- The check constraints and triggers call these with the caller's rights (00_rls_coverage checks the grants). The
-- importer writes catalogue foods as service_role, which bypasses RLS but not function privileges.
revoke all on function private.is_nutrient_map(jsonb), private.is_nutrient_codes(jsonb), private.is_food_portions(jsonb),
  private.food_amount_base(double precision, text, text, double precision), private.food_copy_id(uuid, uuid) from public, anon;
grant execute on function private.is_nutrient_map(jsonb), private.is_nutrient_codes(jsonb), private.is_food_portions(jsonb),
  private.food_amount_base(double precision, text, text, double precision), private.food_copy_id(uuid, uuid)
  to authenticated, service_role;
revoke all on function private.foods_fixed(), private.foods_copy(), private.foods_copy_inserted(), private.foods_copy_conflict(),
  private.recipes_canonical(), private.recipes_fixed_household(),
  private.recipe_ingredients_canonical(), private.food_log_entries_canonical() from public, anon;

alter table public.nutrients enable row level security;
alter table public.foods enable row level security;
alter table public.recipes enable row level security;
alter table public.recipe_ingredients enable row level security;
alter table public.food_log_entries enable row level security;

revoke all on public.nutrients, public.foods, public.recipes, public.recipe_ingredients, public.food_log_entries from anon;

revoke insert, update, delete on public.nutrients from authenticated;
create policy nutrients_select on public.nutrients for select to authenticated
  using (true);

-- Catalogue rows are read by everyone signed in; household rows by their household. Clients write household rows
-- only (foods_catalogue_source keeps them out of the catalogue's sources), and can't move one to another household.
create policy foods_select on public.foods for select to authenticated
  using (household_id is null or household_id in (select private.my_household_ids()));
create policy foods_insert on public.foods for insert to authenticated
  with check (household_id in (select private.my_household_ids()));
create policy foods_update on public.foods for update to authenticated
  using (household_id in (select private.my_household_ids()))
  with check (household_id in (select private.my_household_ids()));
create policy foods_delete on public.foods for delete to authenticated
  using (household_id in (select private.my_household_ids()));

create policy recipes_select on public.recipes for select to authenticated
  using (household_id in (select private.my_household_ids()));
create policy recipes_insert on public.recipes for insert to authenticated
  with check (household_id in (select private.my_household_ids()));
create policy recipes_update on public.recipes for update to authenticated
  using (household_id in (select private.my_household_ids()))
  with check (household_id in (select private.my_household_ids()));
create policy recipes_delete on public.recipes for delete to authenticated
  using (household_id in (select private.my_household_ids()));

-- An ingredient may only use a food of its recipe's household. Any food its writer can see would let a member of two
-- households put one household's food in the other's recipe, where that household's other members can't read it,
-- and where the foreign key would stop its own household deleting it. A catalogue food isn't synced, so the
-- household's members could not total the recipe offline: it is used through the household's copy.
create policy recipe_ingredients_select on public.recipe_ingredients for select to authenticated
  using (household_id in (select private.my_household_ids()));
create policy recipe_ingredients_insert on public.recipe_ingredients for insert to authenticated
  with check (household_id in (select private.my_household_ids())
    and food_id in (select f.id from public.foods f where f.household_id = recipe_ingredients.household_id));
create policy recipe_ingredients_update on public.recipe_ingredients for update to authenticated
  using (household_id in (select private.my_household_ids()))
  with check (household_id in (select private.my_household_ids())
    and food_id in (select f.id from public.foods f where f.household_id = recipe_ingredients.household_id));
create policy recipe_ingredients_delete on public.recipe_ingredients for delete to authenticated
  using (household_id in (select private.my_household_ids()));

create policy food_log_entries_select on public.food_log_entries for select to authenticated
  using (profile_id in (select private.readable_profile_ids('nutrition')));
create policy food_log_entries_insert on public.food_log_entries for insert to authenticated
  with check (profile_id in (select private.my_profile_ids()));
create policy food_log_entries_update on public.food_log_entries for update to authenticated
  using (profile_id in (select private.my_profile_ids()))
  with check (profile_id in (select private.my_profile_ids()));
create policy food_log_entries_delete on public.food_log_entries for delete to authenticated
  using (profile_id in (select private.my_profile_ids()));

-------------------------------------------------------------------------------
-- PowerSync: nutrients to everyone (reference), household foods and recipes to their household, the log to the
-- owner (me). Catalogue foods are published but selected by no stream.
-------------------------------------------------------------------------------

alter publication powersync add table
  public.nutrients, public.foods, public.recipes, public.recipe_ingredients, public.food_log_entries;
