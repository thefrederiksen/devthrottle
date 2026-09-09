# Worker B - the route, the fold, the refusal, the allow list and the audit

Items 2, 3, 4, 5 and 6 of Phase A's seven. Written for the Manager. Nothing here is committed - the
Manager commits.

## What is built, and where

| Item | Where |
|---|---|
| 2. `POST /sessions/{sid}/stop` | `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` - one shared `StopSessionAsync` handler with the two doors onto it |
| 2. `DELETE /sessions/{sid}` becomes a thin forward | same place, immediately below the POST |
| 2. THE FOLD | `src/CcDirector.Gateway/Api/SessionStopFold.cs` (new) |
| 3. The refusal | `SessionStopFold.ReasonMissing`, returned as 400 from the POST door only |
| 4. `notOnFleet` is a success | in `StopSessionAsync`, answered 200, never through `SessionUnavailable` |
| 5. The allow list | `src/CcDirector.Gateway/Util/SessionKeyGuard.cs` |
| 6. The audit record | `GovernanceAuditEventType.Stopped` (new) + `RecordStopInTheAuditTrail` in the route |

Tests: `src/CcDirector.Gateway.UnitTests/SessionStopFoldTests.cs` (new, 26 cases, no server),
`src/CcDirector.Gateway.Tests/SessionStopEndpointTests.cs` (new, 16 cases, the real route through
`GatewayEndpoints.Map`), plus four cases added to `SessionKeyGuardTests`.

I did not touch `SessionCommandExecutor`, `Session`, or anything under `src/CcDirector.Core/Git/`.

## The three decisions I had to make that the brief did not settle

**1. An answer this Gateway cannot fold honestly is REFUSED (502), not guessed at.**
The Gateway and the Director do not deploy together - the Gateway ships in a container image and each
Director updates itself on its own machine - so during a rollout a Gateway carrying this mission can be
handed an answer from a Director that predates it and reports only the original `killed` / `removed`
pair. There is no honest headline for that: all five assert either that a process was ended or that
none was running, and an older Director said neither. So `SessionStopFold.DirectorAnswerProblem`
returns a sentence and the route answers 502 with it, naming the older Director and saying to update it.

**The cost, stated plainly, because it is real.** During that window a stop through EITHER door answers
a failure for a session on a machine that has not updated - including `DELETE /sessions/{sid}`, which
shipped clients call and which succeeds against such a Director today. The stop still reaches the
Director and still ends the session; what is refused is the REPORT. I took that side because reporting
is the entire point of this verb and the mission's own words are that claiming something no machine
established is the worse mistake. **If the Architect wants the other side, it is one method.**

**2. A session whose Director has gone quiet is NOT `notOnFleet`.**
`LocateSessionAsync` finds nothing both when the session does not exist and when its owning Director
has simply stopped pushing. Answering `notOnFleet` for the second would print "nothing in this account
carries the id ..." about a session that IS in the account. So the handler asks
`TryLocateIgnoringFreshness` and, when the account knows the session but its Director has not reported,
returns the existing retryable 503 (`SessionUnavailable`) - Ruling 3's "the owning Director could not be
reached" failure. `notOnFleet` is answered only when nothing in the tenant carries the identifier.

**3. The route resolves the tenant itself instead of using `LocateSessionForRequestAsync`.**
That helper collapses "no tenant is bound to this request" and "no such session" into the same pair of
nulls. This route must tell them apart, or an unauthenticated-tenant request would be told that nothing
in the account carries that identifier when nothing ever looked in an account at all. It resolves the
tenant explicitly (403 when there is none), exactly as `GET /sessions/{sid}` does.

## The consequence you asked me to say out loud

**`OutcomeLedgerReporter` will now count stops as interventions.** Verified in the code, not assumed:
`src/CcDirector.Gateway/Governance/OutcomeLedgerReporter.cs:81` counts EVERY row in the `intervention`
category per session, with no filter on event type, and feeds it to `InterventionCount` (line 190). A
`stopped` row is in that category, so from this change on, a stopped session's intervention count goes
up by one in that report. It is defensible - a session that had to be stopped did require an
intervention - but it is a real change to an existing report and somebody should decide whether it is
wanted, rather than discovering it in a number that moved.

## What I watched fail on purpose, and what the red said

Every test below was watched going red with the symptom it claims to catch, then the mutation was
reverted and the suite re-run green. Three rounds; the failing set was exactly the expected set each
time, with nothing else disturbed.

**Round one - the fold and the allow list (`Gateway.UnitTests`), 10 failures:**

| What I broke | What went red, and what it said |
|---|---|
| The unknown worktree case folded as the CLEAN case | `A_worktree_whose_state_could_not_be_determined_gets_its_own_sentence_and_never_the_clean_one` - the two sentences differ |
| `ShortIdFor` truncates everything to eight characters, not only a GUID | `A_typed_name_is_never_truncated` - expected `Stop a session - Worker`, actual **`Stop a s`**: literally the corrupted answer the amended brief warned about. Also `A_guid_written_another_way_...({...})` - actual `{9c41e7a` |
| `DirectorAnswerProblem` always returns null | `A_missing_answer_is_refused`, `An_answer_with_no_verdict_is_refused_rather_than_guessed_at`, `A_stop_that_names_no_process_is_refused` - all `Assert.NotNull() Failure: Value is null` |
| `"stop"` taken back out of the allow list | `The_stop_that_carries_a_reason_is_allowed_...` and `The_action_side_of_the_agent_route_set_is_allowed(POST .../stop)` |
| The bare `DELETE /sessions/{sid}` added to the allow list | `The_bare_delete_stays_refused_however_it_is_written` AND the pre-existing `The_account_surface_is_refused(DELETE /sessions/{id})` - a second, older test also guards that refusal |

**Round two - the route (`Gateway.Tests`), 6 failures:**

| What I broke | What went red |
|---|---|
| `notOnFleet` routed through `SessionUnavailable` | `A_session_that_is_not_on_this_fleet_is_a_success_not_a_404` - expected OK, actual **NotFound**; and `A_second_stop_straight_after_the_first_succeeds_quietly`, same. That is the exact failure Ruling 3 exists to prevent, reproduced |
| The reason requirement dropped | all three `A_stop_with_no_reason_is_refused_...` cases (null, empty, whitespace) - expected BadRequest, actual OK |
| The DELETE door answering `{ killed: true }` on its own again | `The_delete_door_reaches_the_same_stop_...` - verdict expected `stopped`, actual `""` |

**Round three - the audit (`Gateway.Tests`), 4 failures:**

| What I broke | What went red |
|---|---|
| The stop served but never recorded | `A_stop_appends_one_intervention_row_carrying_who_asked_and_why`, `An_already_stopped_session_is_recorded_too`, `The_delete_door_...` - `Assert.Single() Failure: The collection was empty` |
| A row written for `notOnFleet` | `Not_on_this_fleet_writes_no_audit_row` - `Assert.Empty() Failure: Collection was not empty` |

## What is NOT proven

- **The Director's answer is a stub in every test I wrote.** These prove what the Gateway does WITH an
  answer and prove nothing about whether that answer is true. Seat 1 carries that half.
- **The actor is `unknown` in the route tests**, because the harness authenticates nothing. The four
  actor shapes (session key, device key, machine token, nothing) are covered in the fold's own tests
  instead. No route test drives a real session key through `AuthMiddleware`.
- **No client is exercised.** The claim that `DELETE /sessions/{sid}` may safely grow its response body
  rests on the Manager's reading of `killSession` in `packages/client-core/src/api/client.ts` and of
  `phone/CcDirectorClient/Voice/GatewayClient.cs` - both read the status code only. I did not re-verify
  it and no test of mine covers a client.
- **No hosted / multi-tenant path is exercised.** Every test runs self-host, where the tenant always
  resolves to Local. The 403-when-no-tenant branch is written and reasoned but not tested.
- **The audit write is best-effort at the end of a stop.** If the append throws, the stop still answers
  success and the failure is logged loudly - the session is already ended by then, and answering an
  error would be a lie about the one fact this verb reports. That means a database fault produces a
  stop with no audit row, which is a governance gap that only the log records.

## The state of the gate when I finished

`.\scripts\test-local.ps1` is **not clean**, and neither reason is this work:

1. **`CcDirector.Gateway.UnitTests` is OVER THE 120-SECOND BUDGET (3 minutes 6 seconds).**
   **Measured, not assumed:** re-running that suite with EVERY test from this mission excluded
   (`SessionStopFoldTests`, `SessionKeyGuardTests`, `SessionCommandExecutorTests`) still takes
   **3 minutes 16 seconds**. This mission's 26 fold tests account for **0.136 seconds** of the suite's
   704 seconds of summed test time, and none of the twenty slowest tests belongs to it - the top two are
   `StatsStoreReopensAfterAnUnreachableStoreTests` at 25 seconds each. The breach predates the branch.
   Somebody should decide whether that is this mission's problem to carry; I do not think it is.
2. **`PythonToolsHealAndShimTests.InstallAsync_FailedVenvRebuild_LeavesNoManagedShim` failed in BOTH
   gate runs** and **passes when run on its own**, twice (once as the whole class, 13 of 13; once as
   that single test). So it is load-sensitive rather than a one-off flake, and somebody should look at
   it - but it is not this mission's. I read it: it runs entirely inside its own temporary layout
   (`_layout`), staging a fake release with a stub python, and touches neither the repository's
   `tools/cc-devthrottle` sources nor anything else this branch changes. Three Workers were building on
   this machine throughout.

Also seen once and gone: `SessionCommandExecutorTests.Kill_CleanWorktree_ReportsFalse` failed in the
first gate run and passed on the very next full run of the suite - Worker A was editing that file while
the gate was reading it.

**The second gate run did not finish**: the harness killed it for low memory partway through
`Gateway.UnitTests` (three Workers building at once), so its verdict block was never printed. Every
other suite in it passed. The `Gateway.UnitTests` figure quoted above comes from running that suite
directly, twice, to completion: **4,221 tests, 0 failures, 3 minutes 19 seconds.**

`Gateway.Tests` is PARKED and did not run in the default gate; my 16 route tests in it were run
directly and pass. The whole parked suite still needs `-Parked`.

## One thing the command line needs to know

The 400 body is `{ "error": "<the sentence>" }` - the shape every other Gateway refusal uses. The
sentence names the REASON as what is missing and deliberately names no flag; Worker C prints it and
adds `--reason`.
