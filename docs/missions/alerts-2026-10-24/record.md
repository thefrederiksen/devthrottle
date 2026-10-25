# Alerts - mission record

Delivery Lead record, 25 October 2026. Everything below is on origin/main.

## What landed

| Phase | Pull request | Merged as | Deployed |
|---|---|---|---|
| 1 - stopgap on the restart card | #4470 | `7e084fcf6` | Yes, run 40184469283 |
| 3 - shared bell, tray and opened alert in client-core | #4471 | `87f7b1452` | (in the second deploy) |
| 2 - Gateway: alert sources, `alert_marks`, four routes | #4472 | `6b0decd48` | (in the second deploy) |
| 4 - mounted in both apps, old panels removed, QA run | #4473 | `d895bfc70` | Yes, run 40204809366 |

Phases 2 and 3 ran in parallel in separate worktrees. Each phase was reviewed by a separate review
session before it merged; the reviews are in `reviews/`. The first reviewer seat in the order refused to
start on every attempt that day, so every review ran on the second.

## How each phase was accepted

The Delivery Lead ran each phase's check in a worktree of its own, never the Developer's, before
merging:

- Phase 1: typecheck; client-core 186, mobile 38, Cockpit 131 test files.
- Phase 3: the same on its head, and again on Phase 3 merged onto main with Phase 1 (both change the
  restart panel).
- Phase 2: the three Alert filters of section 7 (85 unit, 12 host-bound), then `scripts/test-database.ps1`
  against a real PostgreSQL. That run found the collation finding below. After the answers: 94 unit,
  13 host-bound, the migration test passing on PostgreSQL including a sub-microsecond round trip.
- Phase 4: typecheck; client-core 187, mobile 39, Cockpit 132 test files; Gateway Alert, push and
  restart filters (171 unit, 13 host-bound); the database Alert test (1 executed, passed); and the roster
  guard run both ways by the Delivery Lead: PASS on the branch, FAIL with origin/main's `Home.tsx` and
  `SessionRoster.tsx` put back.

## Findings and what was done with them

- **Delivery Lead, Phase 2 - collation proof.** The new `alert_marks.AlertId` key was missing from
  `PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres`.
  Accepted and fixed by the Developer.
- **Review, Phase 2, Finding 1 - a restart alert never read as seen on PostgreSQL** (sub-microsecond
  ticks lost in `seen_version`). Accepted and fixed, with a PostgreSQL test.
- **Review, Phase 2, Observation 1 - a Director's own credential could clear the owner's alerts.**
  Delivery Lead decision: the three write routes (seen, clear one, clear all) follow the owner-device rule
  the message request routes use. Done, with a test per route.
- **Review, Phase 4, Observations 1, 3, 5.** Delivery Lead decision: fix all three - a stale clear-refused
  sentence is dropped once the alert no longer needs an answer; the bell is in every phone app bar, not
  only the session list (section 5.4); the QA host's comment now matches that it is committed. Done.
- Every other observation was answered in the review files' Answers sections, or needed no change.
- **The id example in section 5.2** said `restart:3f9c...`; the code, the push payloads and the client all
  use the full kind word. The document is corrected in this record's pull request.

## Found on main, not caused by this mission

- `npm run lint` fails with five errors on main (`FactoryRecordsTab.tsx`, `fixtures.ts`, `sw.test.ts`,
  `Recorder.tsx`, `VoiceMode.tsx`): four are "Definition for rule ... was not found".
- Three PostgreSQL proofs fail on main: `AddLicenseAlarmsPostgresTests`, `ArchiveFactoriesPostgresTests`,
  and the collation proof (for `cron_failure_notices`). `scripts/test-local.ps1 -Parked` does not run the
  DatabaseTests project at all, so a green `-Parked` run says nothing about them.
- Ten `MacAppNameAndPlaceTests` in `cc-director-setup-engine.Tests` fail on main (reported by the Phase 2
  Developer).
- The Cockpit service worker handles only the secret transfer push; a message request push reaching a
  Cockpit subscription closes its notification as a count of 0 (Phase 4 review, Observation 4).

## The deploys

- First deploy (Phase 1): live, health 200. Longest outage 65.8 s against a 120 s budget - within budget,
  but a normal deploy is 4 to 7 s.
- Second deploy (Phase 4): the new image went live and health is 200, but the watch job failed the run:
  longest outage 135.7 s, over the 120 s budget. The workflow states it is not a rollback signal; the
  service answered in under 0.2 s afterwards. Two long outages on one day are worth a look on their own.

## Proof on the hosted Gateway

- The served phone bundle contains the Alerts tray, its empty state and the `/gateway/alerts` client,
  and no longer contains the old restart panel. The Cockpit is built into the same image.
- `GET /gateway/alerts` without credentials answers 401.
- **Not proved:** the signed-in flow on the hosted Gateway from the phone (section 7's last line). No
  browser profile on this machine is signed in to the hosted app, and signing in is the owner's job.

## QA

`qa/` holds the local QA run: 40 screenshots at phone and desktop width covering every state section 7
lists, the overflow fixture at 360 px and in a 280 px popover, the guard's before and after output, and
the run log.
