-- Guards that hold for every table, now and in every later migration (PLAN.md §2.5, §4).
begin;
create extension if not exists pgtap with schema extensions;
select plan(7);

select is_empty(
  $$ select c.relname::text from pg_class c join pg_namespace n on n.oid = c.relnamespace
     where n.nspname = 'public' and c.relkind in ('r', 'p') and not c.relrowsecurity $$,
  'every public table has RLS enabled'
);

-- Server-only (S) tables: RLS on, no policies, no grants, never synced. Keep this list short.
create temp table server_only(relname text);
insert into server_only values ('household_invites');

select is_empty(
  $$ select c.relname::text from pg_class c join pg_namespace n on n.oid = c.relnamespace
     where n.nspname = 'public' and c.relkind in ('r', 'p')
       and not exists (select 1 from pg_policy p where p.polrelid = c.oid)
       and c.relname not in (select relname from server_only) $$,
  'every public table except server-only ones has at least one policy'
);

select is_empty(
  $$ select table_name::text from information_schema.role_table_grants
     where grantee = 'anon' and table_schema = 'public' $$,
  'anon has no privileges on any public table'
);

select is_empty(
  $$ select c.relname::text from pg_class c join pg_namespace n on n.oid = c.relnamespace
     where n.nspname = 'public' and c.relkind = 'r'
       and c.relname not in (select relname from server_only)
       and c.relname not in (select tablename from pg_publication_tables where pubname = 'powersync') $$,
  'every synced table is in the powersync publication'
);

select is_empty(
  $$ select tablename::text from pg_publication_tables
     where pubname = 'powersync' and tablename in (select relname from server_only) $$,
  'no server-only table is replicated to PowerSync'
);

-- Functions in `private` run inside triggers and policies on behalf of the signed-in user. A
-- missing EXECUTE grant fails every request in production, but behavioural tests can miss it: PL/pgSQL
-- checks EXECUTE once per transaction, and test setup (as superuser) has usually run it already.
select is_empty(
  $$ select p.oid::regprocedure::text from pg_proc p join pg_namespace n on n.oid = p.pronamespace
     where n.nspname = 'private' and p.prorettype <> 'trigger'::regtype
       and not has_function_privilege('authenticated', p.oid, 'execute') $$,
  'every private helper is executable by authenticated'
);

select is_empty(
  $$ select p.oid::regprocedure::text from pg_proc p join pg_namespace n on n.oid = p.pronamespace
     where n.nspname = 'private' and has_function_privilege('anon', p.oid, 'execute') $$,
  'no private function is executable by anon'
);

select * from finish();
rollback;
