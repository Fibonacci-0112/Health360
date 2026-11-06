# Step 2 acceptance and verification

The workout/cardio features described in PLAN.md step 1.2 are implemented. Device acceptance remains distinct from automated local persistence tests.

## Automated coverage

- Domain: progression and e1RM golden fixtures; bounded mixed-unit plate loading; Keytel/MET energy paths; revised RQ conversion fixtures; medication and missing-input paths; threshold bands and refusal to divide straddling zones/average-HR intervals.
- Local database: a 60-minute strength session reopened halfway, continued and uploaded; completed-set swap refusal and owner checks; program/location/schedule edits; volume conversion; backdated snapshot persistence; local-only drafts and clearing account data.
- Shared UI: create/edit locations and programs, weekly scheduling/calendar, cardio preview/save, persistent intermediate text, rest deadline restoration/cancellation on profile change, and empty/single/constant/date-spaced chart states.
- Full core checks use the commands in AGENTS.md. Integration tests gated by environment variables are skipped unless the disposable Supabase/PowerSync stack is running.

## Android and Windows device acceptance (not yet recorded)

1. Sync an exercise library and create a location, template with progression and a superset, and a weekly program. Use a second device signed into the same owner and an unrelated user's device.
2. Go offline, start a workout, accept/modify sets and use the plate calculator. After roughly 30 minutes, kill the app process while a field has focus. Relaunch offline, confirm every acknowledged field/set and rest deadline remains, and finish after 60 minutes.
3. Reconnect. Verify all sets, entered units, e1RM/records and finished-session energy reach the second owner device. Confirm no workout or private estimate appears for the unrelated user. Compare the uploaded values with the local entries.
4. Start rest, background and kill/relaunch the app. Verify the generic notification at the saved deadline, restart/+stop cancellation, and cancellation when changing accounts. Android: test notification permission denied and exact-alarm access disabled as well as enabled. Check Windows as the shipped unpackaged app. Delivery under OS restrictions is not guaranteed; the saved in-app deadline remains authoritative.
5. Start an unfinished cardio form including zone times, elevation and an incomplete numeric value; kill/relaunch offline and confirm the text remains. Change the effective zone set on the other owner device; sync and check that stale zone times clear with an explanation.
6. Log a backdated cardio session with zone time, one with average HR, and one with effort only. Check session/weekly saved totals. Edit current HR zones, weight and unit preferences; past snapshots must remain byte-for-byte unchanged apart from display conversions. Try a skipped/repeated daylight-saving local time and a future ending time; both must be rejected.

The native target builds and these physical-device checks must be recorded before claiming the step's kill-and-sync exit criterion is complete. A SQLite reopen fixture alone does not establish OS lifecycle, permission or notification behavior.
