# Factory Inbox phase 2, Task D - review of commit 480e8e3bb (branch factory-inbox/guard)

Reviewer: a separate review session, 5 November 2026. Worktree D:/ReposFred/devthrottle-fi-guard, read only.
Reviewed: `git diff e39837ea8 480e8e3bb` (the two phase 1 commits beneath it were reviewed before and were not re-read
except where this commit calls into them).

## Verdict

No finding proves harm. The commit does what MISSION.md section 5 and the Task D section of phase-2-plan.md ask, and the
two claims the Tech Lead asked me to check by hand both held:

1. Every start door a session key can reach is in the list, nothing in the list is a non-door, and neither a Boss, a worker
   nor an ordinary session is refused anywhere. The one place is the authentication middleware's session-key step, keyed on
   the caller's own history row, never on the request.
2. The stuck alert is computed from the inbox's own tables with an injected time, raises for both mission cases (not
   received in 15 minutes; bundle not decided in 2 hours), stays silent for a received or decided inbox, and every word
   the client shows is written on the Gateway.

Three observations follow that are not defects. Nothing here should hold the merge.

## Scope - what I read

- D:/ReposFred/_briefs/factory-inbox-2026-11-04/MISSION.md (section 5 "Nobody picks it up", "The Secretary", section 7).
- phase-2-plan.md, including the "Decided during the phase" list added today, which rules that Task D merges first and
  Task C then extends the one refusal to the Secretary recorded on `factory_inbox_wakes` for an undefined factory.
- CLAUDE.md in the repository (rule 7, the test standard, the review rule).
- The whole diff: `SecretaryStartLimit.cs`, `AuthMiddleware.cs`, `GatewayHost.cs` (both wiring points),
  `FactoryInboxStuckAlertSource.cs`, `AlertDtos.cs`, the three test files, and the four client-core alert files.
- Around the diff, from the same commit: `SessionKeyGuard.cs` in full (every allowed write for a session key, raised or
  not), `SessionHistoryStore.FactoryOf` and the history purge, `FactoryInboxStore` (claim, bundle and close writes),
  `FactoryInboxWords.States`, `FactoryInboxEndpoints.Seat`, `FactoryDefinitionEndpoints` (who may publish or restore),
  `FleetManagerPlacementService.SaveAsync` and `SetMarkByOwnerAsync`, `FleetManagerHandOverEndpoints`,
  `SettingsEndpoints` (`PUT /gateway/fleet-manager`), the needs-manager route, the Mail Desk folder, the Rules folder,
  `IAlertSource`, `AlertsService.Clear`, `AlertEndpoints`, `DevReportSessionLiveness`, `MachineEndpoints` and
  `GatewayEndpoints` route maps for the listed doors.
- The Task C worktree (D:/ReposFred/devthrottle-fi-wake, two commits stacked on 480e8e3bb) only to confirm that a message
  waiting for a computer keeps the state `sent` (the waiting words live on the wake row), so the alert's `sent` filter
  still sees it.

## Scope - what I ran

Out of tree, in a `git archive 480e8e3bb` copy under the session scratchpad. The worktree was not edited, built or run.

| Run | Result |
|---|---|
| The three new test classes (door tests through the real middleware and session key registry, the pure limit tests, the alert source over the real store) | 137 passed, 0 failed, 24 seconds |
| Sensitivity: in the copy, the door list switched off (`IsAStartDoor` matches no verb), door tests rerun | 51 of 69 red, 18 green - exactly the commit's claim. The 18 green are the Boss / worker / ordinary rows and the "no history read on an ordinary request" fact, which must stay green |

The copy was then left mutated; it is scratch and nothing reads it.

## Scope - what I could not reach

- The GatewayHost wiring itself (`SessionSeatOf` reading `_sessionHistory.FactoryOf` under the key's tenant scope, and the
  alert source's three delegates) has no test through a running Gateway; the door tests wire their own reader. I read the
  wiring against the phase 1 `FactoryOf` delegate beside it and it is the same shape. Task C's end-to-end test is where
  the real Secretary start meets this refusal.
- I did not run test-local or test-local -Parked; the Tech Lead reports both green bar 11 failures main also has.
- An undefined factory's Secretary has no seat on its history row and so is NOT refused by this commit. That is decided
  work for Task C after it rebases (phase-2-plan.md, "Decided during the phase"), not a Task D defect; I note it so the
  Delivery Lead holds Task C to it.

## The door sweep

I walked every write a session key may make in `SessionKeyGuard.IsAllowed` (including the raised-only Fleet Manager
routes) and asked of each: does the route start a session or launch a program?

Listed as doors, and they are: the generic spawn on a machine and on a Director (which is also the owner's Boss start
door, `SpawnFactory.TryStartTheBoss`, reached only when a session in no factory names a factory in that same spawn);
machine launch; factory seat start; handover create; workspace restore; schedule create, update and run now; trigger
create, update and resume; browser start and sign-in; Fleet Manager start, restart and move. All seventeen are mapped
routes and all seventeen are allowed by the guard (the limit tests prove the second half).

Checked and correctly NOT doors: Director restart request (nothing restarts until the owner accepts); browser profile
create, stop, rename, delete; schedule and trigger pause, unlink, delete; `PUT /gateway/fleet-manager` (sets which session
is the Fleet Manager, starts none); `PUT /gateway/fleet-manager/placement` (saves agent and machine, starts none);
Fleet Manager hand-over (changes who controls a running session); needs-manager (raises a flag, starts none); workspace
create and update; skills and workflows publish (a workflow RUN is a separate route the guard never allows a session
key); Mail Desk channels and rules (no session start anywhere in that folder); Session Rules (they type into a session
at turn end; they never start one); factory definition publish, restore, import and create (publish and restore are
refused to any seat but the Boss by the route itself, so a Secretary cannot plant a schedule that way; create and import
are refused to any factory's own session); factory briefs, memory, records, activity, goal numbers; every factory inbox
route; every read.

The Secretary still reaches its own routes: proved by the door tests for its inbox list and bundle and the roster read,
with zero history reads on those requests.

## Observations (not findings; none proves harm)

1. **A session with no history row is now answered 409 at seventeen doors, not one.** Before this commit only the generic
   spawn refused a not-yet-known session; now `POST /cron/jobs`, `POST /triggers`, browser start and the rest do too, with
   "try again in a moment". The row is written from the Director's first push, seconds after start, and the history purge
   only removes rows with a recorded ending, so a running session never loses its row and the refusal is always
   transient. The commit says this plainly. Worth one line in the release notes, nothing more.

2. **The inherited-seat reading differs from the inbox routes, in the safe direction.** `IsASecretary` refuses any
   session whose row carries the seat `secretary`, started or inherited, while the phase 1 inbox routes count only a
   session started as the Secretary. A broader refusal is the right side to err on, and a Secretary cannot spawn or hand
   over, so an inherited `secretary` row cannot arise from it in the first place.

3. **A Secretary whose computer disconnects mid-claim raises no alert until an ending is recorded.** The extra
   "Secretary ended" reason relies on `DevReportSessionLiveness`, which reads an absent session with no ending as busy,
   not ended. The two mission cases (not received in 15 minutes, bundle not decided in 2 hours) are unaffected; this
   third reason is the commit's own addition and simply does not cover that one shape. If it matters, it is new work in
   words, not a fix here.

## Rule 7 (the client renders, the Gateway rules)

The client gains a type for the kind, a fixture, one `switch` arm that lists `reasons[1..]` as sentences, and a null
answer for "which session asked". It writes no sentence, derives no state, and has no button; the title, summary, status
word, tone and every reason sentence come from the Gateway. The one convention it relies on - the first reason is also the
summary - is stated in the Gateway's `Fold` and is a layout choice (do not print one sentence twice), not a ruling.

## Answers (the Developer of Task D, 5 November 2026)

1. Accepted as is. The 409 at every start door is deliberate, so an unknown session can never pass as a non-Secretary; the commit message says so, and the line belongs in the release notes when the mission ships.
2. Accepted as is. Refusing any session whose row carries the Secretary seat is the safe reading, and no change is made.
3. Accepted as is. The two mission cases are covered; a Secretary whose computer disconnects without a recorded ending is left uncovered by this commit, and covering it is new work for the Delivery Lead to decide, not a change here.
