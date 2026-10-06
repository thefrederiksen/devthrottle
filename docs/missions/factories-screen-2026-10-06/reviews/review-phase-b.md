# Review: phase B Gateway views

**Verdict: CHANGES REQUESTED**

Reviewed commit `944be91766e02ad8f077b957b35db986f0843d64` against its merge base with `origin/main`, `c4f8a481c45a8a017382e365aa77d386f8e90514` (`git diff origin/main...HEAD`).

## Findings

### [P1] Count every recent factory failure, including failures written by non-seats

Locations: `src/CcDirector.Gateway/Factory/FactoriesScreenFold.cs:331`, `src/CcDirector.Gateway.UnitTests/Factory/FactoriesScreenFoldTests.cs:99`

`Status` requires a failed activity row's `FactoryAgent` to be a registry seat before it can make the factory `FAILING`. That is the inverse of the plan's build-time decision: **any** failed activity row the factory wrote in the last 24 hours counts. The decision calls out ClickFunnels' Facebook Reader, Badge Runner and Sales Sender precisely because they are runner scripts rather than seats and their failures must still fail the factory. With this fold, one of those runners can write a current `failed` row and the list will show `RUNNING` when nothing is waiting. The new test `Status_AFailureByANonSeat_IsNotFailing` locks in the wrong behavior rather than exposing it.

Remove the seat-membership condition from the factory-level failed-row check while continuing to use the registry, not activity names, as the source of Seats-tab rows.

### [P1] A factory with no enabled schedule is reported as RUNNING instead of PAUSED

Locations: `src/CcDirector.Gateway/Factory/FactoriesScreenFold.cs:350`, `src/CcDirector.Gateway/Factory/FactoriesScreenFold.cs:352`, `src/CcDirector.Gateway.UnitTests/Factory/FactoriesScreenFoldTests.cs:148`

The `switches > 0` guard makes an empty schedule-and-trigger set skip `PAUSED` and fall through to `RUNNING`. The plan's added decision explicitly settles this case: a factory with no enabled schedule at all is `PAUSED`, naming Tallyhand because all three of its registered seats have no schedules. Once no failure or waiting item outranks it, the shipped list will therefore tell the owner Tallyhand is running even though nothing in it runs. The new `Status_NothingScheduled_IsRunningNotPaused` test again asserts the opposite of the governing decision.

Treat the absence of any enabled seat schedule as `PAUSED`; add the decided Tallyhand-shaped case as the regression proof.

## Scope and evidence

Read:

- The complete `origin/main...HEAD` change inventory and every changed production surface: the new contracts, folds, schedule wording, three read endpoints, host wiring, explicit-tenant schedule read, existing Factory Agents fold/route compatibility changes, activity outcome and command change, and command reference.
- `CLAUDE.md`, the mission, phase B brief, plan including decisions 5-7 and the decisions added during the build, the version 4 design report, and the phase C and phase D consumer briefs.
- The new fold, route, switch, registry and activity tests, plus the existing Factory Agents fold and the cron scheduling implementation that defines the stored cron and time-zone semantics.
- The live read-only schedule inventory: 133 jobs total, with all 27 schedule identifiers named by the ten mission manifests present. Their recurring cron shapes and `America/Toronto` time zone were checked against the schedule-wording branches and focused tests. No schedule was changed.

Ran:

- Targeted Gateway unit tests for `FactoriesScreenFoldTests`, `FactoryAgentsSwitchTests`, and `FactoryActivityRecordTests`: **82 passed, 0 failed**. This includes positive enumeration of all three new routes under the per-account `factoryAgents.enabled` gate, tenant isolation of activity rows, registry-only seat output, status ordering, and cron/time-zone wording. Two of those passing tests are the incorrect expectations cited above.
- The existing `FactoryAgentsFoldTests`: **26 passed, 0 failed**, covering the old Factory Agents screens that remain on main.
- `tools/cc-devthrottle/tests/test_factory_ops.py` in an isolated environment at the command's declared Click 8.2.1 and Typer 0.16.1 floors: **12 passed, 0 failed**.
- `git diff --check origin/main...HEAD`: exit 0.

Could not reach:

- `FactoryRegistryRouteTests` compiled but did not execute because the repository's per-user Gateway test lock was held by live process `53752`, owned by session `6e74d41b-f4c1-4d9b-9e29-79c9949d7525` in `D:\ReposFred\devthrottle-teams-2307`. I stopped only this review's queued test process and do not count the integration assertions as executed. Consequently, the source and unit evidence covers route mapping, the feature gate, tenant plumbing, and owner/session-key decisions, but not a request through the complete authentication middleware pipeline.
- I did not run the full local or parked suites, open an undeployed screen in a browser, or call the new endpoints on a live Gateway. Phase D has not built the client yet.

## Round 2

**Verdict: APPROVED**

Re-reviewed follow-up commit `c3e0aabbdf7097adc5a9fa182356799137aa07ae`, including the complete `origin/main...HEAD` change set at merge base `c4f8a481c45a8a017382e365aa77d386f8e90514`. Both round-one findings are resolved, and I found no new blocking issue.

### Resolved findings

- The factory-level status fold now treats any failed activity row written by that factory in the last 24 hours as `FAILING`; it no longer requires the row's agent name to be a registry seat. `Status_AFailureByANonSeat_IsFailing` proves the ClickFunnels-runner-shaped case, while its different-factory control preserves tenant/factory isolation.
- The fold now reports `PAUSED` when there is no enabled seat schedule and no unpaused trigger, including a factory with no schedules or triggers at all. `Status_NoScheduleAtAll_IsPaused_AndSaysSo` proves the Tallyhand-shaped case and the owner-facing reason.
- The follow-up also classifies the cron runner's actual terminal failure outcomes (`not-started`, `worklist-no-list`, `worklist-no-director`, and `worklist-unknown`) as failures while leaving its documented clean/contended outcomes running. The focused tests enumerate those values.

### Scope and evidence

Read:

- All three files changed by the follow-up and the complete phase B diff, not only the two repaired branches.
- The status precedence and reason strings, Seats registry filtering, per-factory activity filtering, schedule/trigger selection, cron outcome producers, route authentication/authorization, feature-gate behavior, and the compatibility surface for the old Factory Agents pages.
- The governing mission, brief, plan decisions, version 4 design report, and downstream phase C/D consumer briefs already used in round one.
- `origin/main` has advanced to `cfaecf391a6f37da1a0f82d2ce8790f60e449bea`, one commit beyond this branch. That commit adds Teams live-proof documentation only; it has no path overlap with this phase B diff, whose merge base remains `c4f8a481c45a8a017382e365aa77d386f8e90514`.

Ran:

- The two specifically named regression tests: **2 passed, 0 failed**.
- Targeted Gateway unit tests for `FactoriesScreenFoldTests`, `FactoryAgentsSwitchTests`, `FactoryActivityRecordTests`, and the existing `FactoryAgentsFoldTests`: **115 passed, 0 failed**. This positively enumerates the corrected status cases, status ordering, tenant/factory isolation, registry-only Seats rows, schedule wording, route registration, feature-gate metadata, and the old-screen compatibility fold.
- Authenticated `FactoryRegistryRouteTests` through the real Gateway host: **6 passed, 0 failed**. This closes round one's test-lock gap and covers owner access, session-key rejection, the off-switch `404`, and all three registry-backed routes through the middleware pipeline.
- `git diff --check origin/main...HEAD`: exit 0.
- The Python `factory_ops.py` file is unchanged from round one, so the isolated floor-version result remains applicable: **12 passed, 0 failed** with Click 8.2.1 and Typer 0.16.1.

Could not reach:

- I did not run the full local or parked suites, exercise the new endpoints against a deployed Gateway, or inspect a browser UI. Those surfaces are outside this focused Gateway review; the phase D client is not part of this diff.
