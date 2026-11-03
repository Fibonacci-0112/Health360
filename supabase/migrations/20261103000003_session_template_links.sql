-- Phase 1, step 2 (continued): which template row planned each exercise of a workout (PLAN.md §3,
-- docs/calculations.md, Progression).
--
--   session_exercises.template_exercise_id   the template_exercises row the logger planned this entry from; null
--                                            for an exercise added by hand or a workout without a template.
--
-- Progression replays a template row's own history through this link, so a template that lists an exercise twice
-- (heavy sets, then back-off sets) keeps two histories, and reordering the template or removing an entry during a
-- workout doesn't swap them.
--
-- No foreign key, on purpose. A workout logged offline uploads later, and its template row may have been deleted
-- on another device meanwhile; a foreign key would then refuse the upload for good and quarantine the workout. The
-- link is only a hint for progression: a dangling id matches no row, and the history simply isn't counted. The row's
-- own policies already keep it to its profile, and reads only ever join the link within one profile.

alter table public.session_exercises add column template_exercise_id uuid;

create index session_exercises_template_exercise_idx on public.session_exercises (template_exercise_id);
