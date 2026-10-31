-- Drop metric_pins' unique (profile_id, metric_code).
--
-- It duplicates what the primary key already guarantees: metric_pins_id checks id = private.metric_pin_id(
-- profile_id, metric_code), so a profile and metric have exactly one possible id, and the primary key allows one
-- row with it.
--
-- It also made concurrent first pins fail. Clients upload a pin as INSERT ... ON CONFLICT (id) DO UPDATE (the
-- PostgREST upsert). ON CONFLICT only resolves conflicts on the index it names. When two devices' uploads of a new
-- pin run at the same moment, the second can pass its check on id before the first has written its index
-- entries; it then waits on the first in this other unique index and, once the first commits, fails with 23505.
-- That upload is quarantined instead of becoming an update. Released together in rounds, six clients upserting
-- the same new pin failed this way in about one round in seven; without this index, none did.

alter table public.metric_pins
  drop constraint metric_pins_profile_id_metric_code_key;
