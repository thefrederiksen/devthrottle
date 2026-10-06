# Teams v1 - a removed person's Director says so; a suggested Director name follows a move

devthrottle_internal#2311, live proof findings **F3** and **F4** (pull request #3581, `docs/proof/teams-2311-live/README.md`,
steps 5 and 6). Proven with unit tests, headless Avalonia tests and one real-socket test of the Director's tunnel
client. No Director window and no rig were started (the brief forbids it: on Windows a Director cannot be kept off the
owner's machine by environment, #3581 F8).

## F3 - what the Gateway sends, and what changed

**Before:** a key revoked for any reason was answered with one body, and the reason stayed in the database
(`device_credentials.RevokedReason`):

```
401 {"error":"device credential revoked","code":"device_credential_revoked"}
```

So the Director could not tell "removed from the team" from any other revoke. The Tech Lead ruled a Gateway change.

**After** - the F3 reason answer. Exactly one revoke gains one field:

```
401 {"error":"device credential revoked","code":"device_credential_revoked","reason":"team_member_removed"}
```

- Sent only when the key's row was revoked with reason exactly `team_member_removed` (what
  `TeamMemberAccessRevoker` writes when a person is removed from a team).
- Every other revoke answers today's body byte for byte: a move (`director_moved_to_another_team`), a role that can no
  longer run sessions, a near-miss spelling, and a key judged revoked only by its binding with no reason on its row.
- A request carrying two revoked keys with different reasons answers today's body: it cannot speak for both.
- The caller holds that very key, so the reason says nothing about anyone else.

Where: `DeviceRegistry.Judge` carries the row's `RevokedReason` on the revoked resolution; `AuthMiddleware` notes it
and writes the reason only for that one value. Pinned by `RevokedKeyReasonTests` (Gateway.UnitTests), which compares
whole bodies, not substrings.

**No deploy order is needed.** An older Gateway sends no reason; the Director then shows the plain "Key revoked" words,
never "removed from the team".

## F3 - what the Director does now

The root cause of "Connecting..." for good was two things:

1. The tunnel client already stopped dialing on a 401, but it called it "subscription required"; and
2. the main window mapped that state to *Unknown*, which the status box resolves to **"Connecting..."**.

Now:

- The tunnel client reads the 401's body from the very negotiate SignalR refused (`TunnelRefusalRecorder`, a handler in
  SignalR's own pipeline - SignalR's exception carries the status code but not the body; it is cleared before every
  attempt, so a body always belongs to the attempt that read it).
- It stops dialing ONLY on positive evidence that the GATEWAY revoked the key (review rounds 1 and 2, RM-F1 and RM-F4):
  the body must be the Gateway's revoke answer in full - BOTH `error: device credential revoked` AND
  `code: device_credential_revoked`. A 401 with no body, a proxy's HTML page, a code with no error, the generic
  `error: missing or invalid token` (which any hop in front of the Gateway can send) or any other JSON is retried
  exactly as before ("Connecting..."), with one log line saying so.
- The status box, in the place it said "Connecting...", says in red:
  - **"Removed from Team B"** - only on `reason: team_member_removed`; the tooltip: "This Director was removed from
    Team B: you are no longer a member of that team, so the Gateway revoked this Director's key and it has stopped
    trying to connect. To use it again, set it up again: click the Gateway status, sign in, and choose another team or
    set it up for yourself."
  - **"Key revoked"** for any other revoke (or an older Gateway). There is no longer a "Key not accepted" kind
    (RM-F4): an unknown key is answered with the generic "missing or invalid token", which is not proof, so it retries.
  - The team is named ONLY when the team file was saved for the very key the Gateway refused (RM-F6). The team file now
    carries a one-way fingerprint of the key it was saved with (never the key). A team file left behind by a move whose
    team save failed - or one saved before this existed - names no team: "Removed from the team", "removed from its
    team".
- Clicking it opens the Gateway connection panel on the choice step, where the Director is set up again (sign in, the
  team question, a new key).
- A 402 keeps today's "subscription required" path, unchanged.
- A network failure (a 503 from a proxy, the Gateway down) is still retried and still reads "Connecting...".

**One correction to the brief.** It pointed the person at Settings, Team tab, "Choose another team...". That cannot
rescue a removed Director: the move route acts only on an ACTIVE key of the Director
(`HostedEnrollmentEndpoint`, `ActiveKeysOfDirector`), so with the key revoked it answers 404 "This account has no
Director set up with that id, or its key is no longer active, so it cannot be moved. Set the Director up again." The
message therefore sends the person to set the Director up again, which works with a revoked key.

## F4 - where "was it the suggestion" comes from

It was not stored before. Now setup records it, in the Director's own storage home beside its team file:
`<home>/config/director/director-name-suggestion.json` = `{ "teamId": "...", "machineName": "...", "name": "<computer> - <team>" }`,
written ONLY when the Director was set up for a TEAM and named exactly the suggestion (D1 left as suggested, or one
team taken without asking). A typed name writes no record (and removes an old one). Choosing Personal writes no record
even with its suggested `<computer> - Personal` name (review round 1, RM-F2).

The record carries the team it was suggested for, and a move follows it ONLY when it leaves that very team (review
round 2, RM-F5). So a record that outlives its team - say the move to Personal could not remove it - never renames the
Director on a later move. A record with no team (written before this) counts as no record.

On a move (`DirectorTeamMover`, after the Gateway's yes):

| The Director's state | What the move does |
|---|---|
| Record present for the team being left, name still equals it | renamed to `<same computer> - <new team>`, record updated |
| Record present, person renamed it since | name kept, record removed |
| No record (typed name, Personal, or set up before this existed) | name kept |
| Moving to Personal | name kept, record removed - a personal Director never carries a record |
| Record present for another team than the one being left, or with no team | name kept |

The rename runs before the team is saved (saving the team redraws the title bar, which then shows the new name and
chip together) and before the connection is re-applied (whose Hello reads the name from the instance registry, so the
Fleet Map shows the new name). A rename that fails does not undo the move; the person is told the move finished and to
rename by hand. If the rename succeeds but the new record cannot be saved (review round 1, RM-F3), the old record is
removed - never left beside the new name - and the person is told the name changed but will not change on later moves;
if even the removal fails it is logged, and the stale record no longer matches the name, so the next move keeps it.

Every failure of a move is reported (RM-F7): when the name step went wrong AND a later step fails too (saving the team,
saving the key, re-applying the connection), the message says both - the later failure, then what happened to the
name.

Personal Directors and Directors on a Gateway without Teams: unchanged. A personal Director is never renamed by a move.

## Tests

| Finding | Test (project) | Proves |
|---|---|---|
| F3 | `RevokedKeyReasonTests` (Gateway.UnitTests) - 10 | the reason only for team removal; every other revoke byte-for-byte today's body; two keys; unknown key unchanged |
| F3 | `RemovedDirectorTunnelTests` (Avalonia.Tests) - 11 | the REAL `GatewayStreamClient` over a real socket: removal -> `KeyRefused`, names the team, no further negotiate in 7 seconds; other revoke -> plain revoke; 503 -> still Connecting and retried; the main window's status-box inputs say "Removed from Team B"; an EMPTY 401 and an HTML 401 in front of a real SignalR hub -> retried, never `KeyRefused`, and connected on the next attempt (RM-F1); a generic "missing or invalid token" 401 and a code-only revoke object in front of the hub -> retried and connected (RM-F4); a Team A file saved for another key does not name Team A in the removal message, and the team saved for this key is named (RM-F6) |
| F3 | `GatewayKeyRefusalTests` (17), `KeyRefusedStatusBoxTests` (Core.UnitTests) | body classification (only the Gateway's revoke answer in full is a refusal; eleven other bodies are not), the words, red visual, "Connecting..." kept while dialing |
| F3 | `TeamNameForKeyTests` (Core.UnitTests) - 4 | the team is named only for the key it was saved with; a file from another key, from no key, or none names no team; the key itself is never written (RM-F6) |
| F4 | `DirectorNameFollowsMoveTests` (Core.UnitTests) - 25 | suggested name follows; typed name never changes; no record keeps the name; Personal is never recorded and a move to Personal drops the record; Personal set up with its suggestion then moved to a team keeps its name; a record that cannot be saved after the rename removes the old one, the next move keeps the name, and the move says "will not change on later moves"; rename before team save and reconnect; a failed rename still finishes the move; a record for another team than the one being left keeps the name; Team A record, move to Personal with the removal failing, then to Team B: name unchanged (RM-F5); a record with no team is no record; the name failure is reported beside a failed team save, key save or re-apply (RM-F7) |
| F4 | `HostedTeamSetupTests` (Avalonia.Tests) - 3 new, 4 updated | setup records the suggestion only for a team's suggested name; never for Personal (RM-F2) |
| F4 | `DirectorTeamScreensTests` (Avalonia.Tests) - 1 new | the Settings Team panel passes its name step to the move |

## Red checks

`red-checks-round2.txt` (review round 2): six mutations, all red, `git status` empty after every restore - the round 1
classifier rule (a code-only body or "missing or invalid token" stops the Director) makes three classifier cases and
both new tunnel recovery tests red; dropping the leaving-team check makes the three RM-F5 tests red; ignoring the key
fingerprint makes two store tests and the RM-F6 tunnel test red; a move that drops the name note from a later failure
makes the three RM-F7 tests red.

`red-checks-round1.txt` (review round 1): four mutations, all red - the round 0 status-only rule (every 401 stops)
makes both recovery tests red; recording Personal makes the setup test and two rule/store tests red; dropping the
removal of the old record after a failed save makes the RM-F3 test red. (One first attempt at the RM-F3 mutation did
not compile and was replaced; both runs are in the file.)

`red-checks.txt` (round 0): ten mutations, each putting one piece of the old or a wrong behaviour back in a throwaway worktree
cut from the commit, running the tests that must catch it, and restoring the file in a `finally`. **All ten red.**
`git status` was empty after every restore. Highlights: putting today's 401 handling back makes the three tunnel
tests red; putting today's status-box mapping back makes the status-box test red (it reads "Connecting..."); making the
Gateway send the reason for every revoke makes five body tests red; a move that never renames, and one that renames a
typed name, are each red.

## Gates

Round 2 (head after the second review's fixes): see `gate-round2.txt` - green, 10 suites, every one `outcome=Completed`.
Round 2 changed no Gateway code. The parked Gateway.Tests was built (it compiles against the renamed constructor
parameter), and the parked Core.Tests filtered to the status box and the credential store (which saves a team) passed 45.

Round 1 (head after the review fixes): see `gate-round1.txt`. The Gateway was not changed in round 1, so Gateway.UnitTests
was not re-run. Round 0 below.

- Default local gate (`.\scripts\test-local.ps1`): **green**, 10 suites, 3,713 tests, every one `outcome=Completed`
  (`gate-default.txt`).
- Gateway.UnitTests in full: **9,418 passed, 0 failed, 14 skipped** (`gateway-unittests-full.txt`).
- Gateway.Tests filtered to the classes that assert the revoked 401 body: **20 passed, 0 failed**; Core.Tests filtered
  to the status box presenter and resolver: **35 passed** (`parked-filtered.txt`).

## What this does not prove

- Nothing ran live. The words were not seen on a real Director window; no screenshot was taken (none of these tests
  renders the main window). The status-box words are proven from the main window's own input builder through the
  presenter.
- After a removal on a LIVE tunnel, SignalR's automatic reconnect tries four times (0, 2, 5, 10 seconds) before the
  supervise loop dials fresh and reads the 401; so "Connecting..." can show for about 20 seconds before "Removed from
  <team>". Not measured live.
- The Director's other polls (account status, session colours) still meet the 401 on their own schedules; only the
  tunnel stops. They were already logged as failures and change nothing on screen here.
- A 402 ("subscription required") still maps to "Connecting..." in the status box - the same mapping defect, in a path
  this change leaves alone on purpose. Reported, not fixed.
- That the Fleet Map shows the new name rests on the existing Hello, which reads the name from the instance registry
  on every reseed (devthrottle_internal#1176), and on the test that the rename happens before the re-apply.
