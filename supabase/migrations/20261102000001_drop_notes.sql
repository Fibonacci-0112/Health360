-- Drop the Phase 0 throwaway `notes` table (docs/phase0b.md, "Carried into Phase 1").
--
-- It existed only to prove offline sync on every target. Body measurements now carry the sync tests and
-- nothing in the app reads or writes notes. Sharing grants for the 'notes' category go with it, and the
-- category is no longer accepted.
--
-- A device that still has a queued notes write uploads it to a table that no longer exists. PostgREST
-- answers PGRST205, which the upload connector treats as permanent: the write lands in failed_uploads
-- instead of blocking the queue. Ship this with (or after) the app version whose AppSchema has no notes.

alter publication powersync drop table public.notes;

drop table public.notes;

delete from public.sharing_grants where category = 'notes';

alter table public.sharing_grants
  drop constraint sharing_grants_category_check,
  add constraint sharing_grants_category_check check (category in (
    'body', 'photos', 'physiology', 'workouts', 'nutrition', 'goals', 'labs', 'supplements', 'mental'
  ));
