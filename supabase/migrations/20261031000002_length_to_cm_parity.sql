-- profiles.height_cm parity with Health360.Domain.
--
-- private.length_to_cm (20261025000001) computed value × 2.54, but the app computes canonical height
-- with Conversion.Convert(value, in, cm) = value × 2.54 × 100 / (100 × 1). The two differ in the last
-- bit for many entries (65.6 in: 166.624 vs 166.62399999999997), so a client's height_cm changed when
-- the server recomputed it. Delegate to private.convert_unit, which mirrors Domain operation for
-- operation and which CanonicalUnitParityTests checks bit for bit.
--
-- create or replace keeps the signature and the EXECUTE grant to authenticated.
create or replace function private.length_to_cm(value double precision, unit text) returns double precision
language sql immutable set search_path = '' as $$
  select private.convert_unit(value, unit, 'cm')
$$;

-- Recompute heights the old formula stored. Only rows whose value changes are touched; the
-- profiles_canonical trigger computes the same value, and updated_at moves so devices sync the fix.
update public.profiles
set height_cm = private.length_to_cm(height, height_unit)
where height_cm is distinct from private.length_to_cm(height, height_unit);
