# Phase 7 run playbook - press go, in this order

Written 2026-09-07 by session `634b3b07` so that the run can be driven by whoever holds the seat
when the Manager says go. Companion to `RIG-phase-7.md` (where things are) and `MANDATE-phase-7.md`
(what the report must contain). Every step names the artefact it leaves; the report is assembled
from those and nothing else.

Preconditions, all of them:

- Phases 1 to 6 merged to origin/main. `git fetch origin && git merge-base --is-ancestor origin/main HEAD`
  in `D:\ReposFred\devthrottle-restart-p7` says yes after a rebase of `restart-phase-7`.
- The rig REBUILT from that tree: `restart-qa-rig.ps1 build -Force`, then `down`, then `up`. Every
  binary and both web shells stamped with the new commit. A rig built against a tree that no longer
  exists proves nothing.
- The Manager has said go. Do not rehearse the cycle before that.

Environment for every command below, from a session on DevThrottle_1 (never on the rig):

```
set CC_GATEWAY_URL=http://127.0.0.1:7911
set CC_GATEWAY_SESSION_KEY=<root>\config\director\gateway-token.txt contents
set CC_SESSION_ID=
```

Run two (production) is the same steps with `CC_GATEWAY_URL=https://gateway.devthrottle.com`, a
credential of that Gateway, machine `SOREN_NORTH`, Director `DevThrottle_1`, driven from SORENLAPTOP
- initiated by the Architect, never by this seat.

## 0. Re-seed after the rebuild

The rig `down`/`up` around the rebuild destroys the seeded sessions (that is what a restart does; it
is why the epic exists). Re-seed from `rig-seeds\` exactly as in `RIG-phase-7.md`: missions may
survive on the rig Gateway (check `cc-devthrottle mission list`), seats do not. After every spawn
read `promptDeliveryUnresolved`; re-send parked prompts through `POST /sessions/{id}/prompt`. Wait
until every agent seat has its file on disk and the standalone is `Held`.

Artefact: `attachments/seeding-<timestamp>.txt` (the session list with the flags).

## 1. Put a seat mid-turn, then take the before picture

Tell Worker B to run its tests again (its seed says how). Confirm it is `Working`. Then:

```
python scripts\restart-qa\before-picture.py --gateway %CC_GATEWAY_URL% --key %CC_GATEWAY_SESSION_KEY% --director <rig director id> --out docs\missions\restart-a-director-2026-09-06\attachments --label before
```

Artefact: `attachments/before-picture.md` and `.json`. Note the time.

## 2. The negative cases that must fail BEFORE the owner is asked

In this order, each one recorded expected / observed / artefact:

1. Session-key probes from inside a rig seat (`session-key-probes.ps1`, now with `-RequestRoute`
   set to Phase 6's request route): direct restart 403; control 400; request route creates a request
   (or answers whatever Phase 6 defines).
2. Ask twice: a second request while one is pending is refused. Record both answers.
3. A machine that cannot be restarted: point the request at a machine with no launcher stream (a
   made-up machine, or the rig with its launcher stopped by `Set-NamedEvent` on its shutdown signal
   and restarted afterwards) - refused before any approval exists, naming the Phase 1 reason in
   Phase 1's words.
4. Let an approval expire: create a request, accept nothing, wait past the expiry Phase 6 defines
   (thirty minutes as specified), then try to accept - refused. This one costs half an hour; start
   it early and let it run alongside step 5.
5. `onlyIfEmpty` with sessions live: `POST /machines/SOREN_NORTH/director/restart` with
   `{"onlyIfEmpty": true}` and the shared token - 409 naming the live session count. ONLY on a tree
   carrying Phase 2 AND after the Phase 1 capability query says the launcher declares the verb; on
   an older launcher the flag is ignored and the rig Director is restarted for real.
6. Corrupt a registration while sessions are live (issue 2730): write junk into a copy of the
   Director's registration file under `<root>\instances\default\config\director\instances\` (keep
   the original bytes to restore), ask for a restart with `onlyIfEmpty`, show it REFUSED, restore
   the file, show the Director still holds every session.

Artefacts: `attachments/negative-cases/*.json`, one per case, each with the raw response.

## 3. The blocked drain - the first time anything has refused to stop

With the wedge in place, a session asks for the restart in its own words (the Rig Alpha Architect is
the natural asker; message it to ask through the Phase 6 route, or have the probe seat do it). The
machine scrutinises; record what the Cockpit shows on the rig (`http://127.0.0.1:7911/c/`, Bearer
cookie or header) as the owner's screen. Accept once, with the shared token, standing in for the
owner - say so in the report.

Then touch nothing. Poll the workspace on the rig Gateway (`/gateway/workspaces/...` as Phase 3
defines) and the Director log. Expect, and assert each:

- the drain reaches every other seat and closes them leaf-first, each recorded `drained` or
  `covered` with its document on disk;
- the wedge is recorded with its true state and reason - `unreachable` after the ninety-minute
  deadline, or whatever Phase 4 records for a seat its prompt route refused to deliver to (watch
  what `SendAsync` returning false becomes; it is a third state and where it lands is a finding);
- the restart does NOT happen: the Director pid and registration are unchanged, the launcher log
  shows no `RestartAsync`;
- `directorOutcome` is `not-restarted`, the owner is told.

Then the recovery, BEFORE clearing the wedge: ask for the restore-without-restart Phase 5 defines,
explicitly. Expect every `drained`/`covered` seat back, doing its own work (demand the unique fact:
D-4117..9, M-2261, W-7703, W-7704, B-5150, W-8812, S-3391), the wedge untouched and still running,
`seatOutcome.scope: all`, no seat duplicated (count the sessions).

If restore-without-restart is missing, STOP and say so to the Manager - it is a Phase 5 defect, not
something to do by hand.

Artefacts: the workspace document as JSON, every handover copied into `attachments/handovers/blocked-run/`,
the Director and launcher log excerpts, the session lists before and after.

## 4. Clear the wedge, run the cycle to green

Flag the wedge for deletion (`cc-devthrottle session done <id>` with the shared token, or the
Cockpit) and confirm it is gone. Re-run step 1 (a seat mid-turn, a fresh before picture).

A session asks. Scrutiny. Accept once. Touch nothing. Record:

- the drain (every handover, the index, the secret sweep WITH its control - Phase 4 records the
  proof count; copy it);
- the gap: last heartbeat of the old Director, launcher `RestartAsync`, first registration and
  first stream Hello of the new one - from the logs, to the second;
- the restore: `promptDeliveryUnresolved` false on every restored seat, and each one answering the
  demand for its unique fact from its own document - quote the turn;
- the after picture: `before-picture.py --label after`, diffed seat by seat against the before.

Artefacts: everything above under `attachments/`.

## 5. What was lost, and what was not exercised

Walk the before picture against the after picture and list every difference: Worker B's test run
died mid-turn (expected - say what its handover said about it), scratchpad files, background jobs,
anything a handover admitted it could not carry. Then list what this run did not exercise, starting
with the owner's own accept on production (run two) and the ninety-minute deadline if the wedge was
recorded another way.

## 6. Land it

Report written into `docs/missions/restart-a-director-2026-09-06/phase-7-qa-report.md`, attachments
beside it, RIG-phase-7.md and the seeds and this playbook copied in as the mission's record. Rebase,
gate, a different-family review of the report, merge, then ONE message to the Manager with the path.
