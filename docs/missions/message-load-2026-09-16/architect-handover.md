# Message Load - Architect handover (18 September 2026)

Written by the Architect session on devthrottle-mac-mini (session 8fdb130e / a386711a) for the session
that takes the seat on SOREN_NORTH. Everything below is on main; nothing lives only in a conversation.

## The mission, in one line

Stop sessions interrupting each other with messages: sending is rare and gated, and a message is a
queued record the receiving session reads at a safe moment, in the manner of Kun Chen's firstmate.

## Status: FINISHED and released

- Slice 1 (the inbox and the gate): main `a8fa8041`, pull request 2970, 17 September.
- The rest (doorbell, replies, row line, restore as a Director act, the words): main `ddc78ac4`,
  pull request 3016, 17 September; post-merge fix `7b6afbdc`, pull request 3041 (a Director id
  belongs to the credential that first registered it).
- Release v2.6.0 tagged at `dc3a6fa0`, release workflow run 35323423705 succeeded; the hosted
  Gateway deployed from main (the deploys' outage gate failed on slow slot swaps while production
  served the code; no rollback). The director-restart skill draft published as version 3.
- The Mission record `7e6c9c04` is marked finished on the Gateway.
- The record: this folder. Read `brief.md` (rulings), `handoff.md` (every ruling and every seat's
  section, in order), `report.html` (the owner's report), `inspection-1.md` to `inspection-17.md`.

## The owner's rulings that bind any follow-up

1. No agent ever types into another session, by any route; only the owner interrupts.
2. A session may message only the session that started it and the sessions it started.
3. A doorbell (any agent-origin send) does not end an armed snooze; the owner's own work does.
4. The spawn owner pin stays; restore is the Director's act.
5. Send NO fleet messages from any seat of this mission. Report through files and `session raise`.
6. Never mention any AI assistant or vendor in anything that reaches GitHub.

## Open follow-ups, with the exact next action for each

1. **Issue 3058 - the hosted Gateway accepts this Mac Director's snapshots 40 times slower since
   23:00 on 17 September** (median 250 ms before; 4 to 30 s after; the Director's freshness window is
   20 s, so the Gateway marks it stale and the stream reconnects). The first deploy carrying this
   mission's Gateway code went live at 22:14 local, so it may be the mission's. The Director-side
   evidence is in the issue. **Next action:** read the hosted Gateway's log for the push handler
   from 03:00 UTC on 18 September (the Azure App Service devthrottle-gw; the deploy run-book in
   devthrottle_internal names where its logs are), find the per-session cost in the roster push
   path (`GatewayHost` push handling, `DisplayFold`, `FleetInboxLineFold`, `TurnEndWatcher`,
   `FleetDoorbell.OnSettled`), and fix it forward, or prove it is not this mission's by rolling the
   Gateway to the commit before `1fd50e19` on the staging slot and measuring.
2. **Issue 3017** - main's Postgres collation census does not list the Fleet Manager columns; the
   parked proof is red. Next action: whoever owns the Fleet Manager mission lists them.
3. **Issue 3000** - the submit verifier's nudges can submit the owner's half-typed words after any
   product send. Next action: no nudges for agent-origin sends, or nudge only the text just typed.
4. **Issue 2998** - `GatewayDatabase.Dispose` clears every SQLite pool; parallel tests dispose each
   other's handles. Reproduction in `sqlite-pool-race-repro.cs.txt`. Next action: clear only its
   own pool.
5. **Inspection 12's findings 2 and 3** were to be filed as issues by the post-merge fix seat; check
   `gh issue list --search "inspection 12"` and file any that are missing from `inspection-12.md`.
6. **The Mac Director (devthrottle-mac-mini) is on 2.1.0** with 2.5.0 staged and held because
   sessions are running. It does not ring doorbells; messages to sessions there wait. Next action:
   the owner closes its sessions or restarts it; then it fetches 2.6.0.
7. **Not measured:** the messages-per-day baseline. The count is in the Gateway's `activity_events`
   table (`route='fleet-message'`). Next action, optional: query it for the week before and after
   17 September and add the numbers to `report.html`.

## What NOT to do

- Do not reopen the design: the rulings above are the owner's, given on 16 and 17 September.
- Do not spawn workers with `--controlled-by <another id>`; the Gateway refuses it.
- Do not run a hosted Gateway deploy or a release for a follow-up without the owner's word; both are
  chosen acts (see `.github/workflows/deploy-hosted-gateway.yml` and the release-manager skill).
