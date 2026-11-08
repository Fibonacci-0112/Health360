-- Catalogue food search (PLAN.md step 1.3, "Catalogue search").
--
-- Catalogue foods (household_id null: the USDA FoodData Central import) are read online, never synced, so the app
-- searches them through this function over PostgREST (POST /rest/v1/rpc/search_catalogue_foods) with the user's own
-- session. It runs as the caller (security invoker), so the foods_select policy applies as for any read; it only
-- ever returns catalogue rows. A household that uses a result copies it into the household (FoodCopy.IdFor,
-- private.foods_copy), and the copy syncs.
--
-- Matching: every word of the query must appear in the name or the brand, case-insensitively, as a substring, so
-- "chick brea" finds "Chicken, breast". LIKE wildcards in the query match only themselves. Ranking, best first:
-- the name starts with the whole query, then the name starts with the first word, then shorter names (fewer
-- unrelated words), then name and id for a stable order. The 100-character limit bounds the number of words. 13,510 imported rows scan in a few milliseconds, so there
-- is no search index yet; one comes with the Open Food Facts import, if its size needs it.

create function public.search_catalogue_foods(query text, max_results integer default 20)
returns table (
  id uuid,
  source text,
  name text,
  brand text,
  basis text,
  energy_100 double precision,
  protein_100 double precision,
  carbohydrate_100 double precision,
  fat_100 double precision,
  nutrients_100 jsonb,
  portions jsonb
)
language sql stable security invoker set search_path = '' as $$
  with q as (
    select lower(btrim(coalesce(query, ''))) as whole,
           array(
             select replace(replace(replace(w, '\', '\\'), '%', '\%'), '_', '\_')
             from unnest(regexp_split_to_array(lower(btrim(coalesce(query, ''))), '\s+')) as w
             where w <> ''
           ) as words
  )
  select f.id, f.source, f.name, f.brand, f.basis, f.energy_100, f.protein_100, f.carbohydrate_100, f.fat_100,
         f.nutrients_100, f.portions
  from public.foods f, q
  where f.household_id is null
    and char_length(q.whole) between 2 and 100
    and cardinality(q.words) >= 1
    and not exists (
      select 1 from unnest(q.words) as w
      where not (lower(f.name) like '%' || w || '%' escape '\'
                 or lower(coalesce(f.brand, '')) like '%' || w || '%' escape '\')
    )
  order by
    (lower(f.name) like replace(replace(replace(q.whole, '\', '\\'), '%', '\%'), '_', '\_') || '%' escape '\') desc,
    (lower(f.name) like q.words[1] || '%' escape '\') desc,
    char_length(f.name),
    f.name,
    f.id
  limit greatest(1, least(coalesce(max_results, 20), 50))
$$;

revoke all on function public.search_catalogue_foods(text, integer) from public, anon;
grant execute on function public.search_catalogue_foods(text, integer) to authenticated;
