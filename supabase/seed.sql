-- Local development seed. Runs after migrations on `supabase db reset`.
insert into public.reference_datasets (code, version, attribution)
values ('health360-units', '1', 'Exact conversion factors: NIST SP 811')
on conflict (code) do nothing;
