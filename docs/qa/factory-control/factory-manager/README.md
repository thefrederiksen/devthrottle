# Factory Manager - QA report

Factory Control, brief 7 (mission step 5): the one standing session over all of an account's factories. The design is
in [DESIGN.md](DESIGN.md). This report shows it working end to end against a local Gateway, and lists the tests.

Taken on 11 October 2026 from a local Gateway built from this branch, by `rig/shoot.py`, run in the foreground:

    python docs/qa/factory-control/factory-manager/rig/shoot.py docs/qa/factory-control/factory-manager

## What each screenshot shows

| File | What it proves |
|---|---|
| `1-factories-with-the-factory-manager-card.png` | The Factories tab opens with the **Factory Manager** card above the factories. It is not started yet. It asks for the computer it runs on (only computers with a running Director are offered) and a folder there. The **name check** flags two seats of WarmForward: `chief`, whose role says CEO, and `writer`, named "Nora Hale", which is not a role word of that factory. Tallyhand's seats, named Boss and Scout, pass. Every word on the card is the Gateway's (`FactoryManagerEndpoints.CardOf`, `FactoryNameCheck`). |
| `2-talk-started-the-factory-manager.png` | **Talk to the Factory Manager**, pressed with the computer and folder filled in. The Gateway started a session named `Factory Manager - talk with the owner` on that computer, in that folder. The session is in no factory, has no controller and no parent: it is the owner's own top-level session. The Cockpit moved to it. The page shows the first prompt exactly as the Director received it (`talk-first-prompt.txt`, `talk-create.json`): every factory with its boss, the commands to read first, and the five rules of conduct. |
| `3-card-while-the-factory-manager-runs.png` | The card again: "Running on SOREN_NORTH", the saved computer and folder, and **Open the Factory Manager**. Pressing it opened the same session and started nothing (`open-again-navigated-to.txt`; the stand-in Director received no second create). |
| `4-factory-with-no-boss-offers-handle-the-issue.png` | WarmForward has no boss, and its open problem now offers **Handle the issue**. Brief 4's screenshot `5-factory-with-no-boss.png` showed this button unavailable ("the Factory Manager, which is not built yet"); that is what this step replaces. |
| `5-handle-the-issue-went-to-the-factory-manager.png` | Handle the issue on that problem. The Gateway started a Factory Manager session named `Factory Manager - WarmForward (QA) - handle the issue (qa-warmforward - writer)`, briefed on the problem in exactly the words a boss gets (`FactoryTalkSeed.ProblemBrief`): the run, the schedule, what went wrong, when and in which shift, the session to read, and `cc-devthrottle run resolve <run> "<one line: what fixed it>"`. It is told it stands in for the missing boss (`handle-first-prompt.txt`, `handle-create.json`). The new session is now the bound Factory Manager. |
| `6-command-line-binding-name-check-and-resolve-rights.png` | The real `cc-devthrottle` from this tree, run AS sessions with the session keys the stand-in Director registered (`command-line-transcript.txt`). `factory manager show` prints the binding. `factory name-check` prints the same two findings. The FIRST Factory Manager session, no longer the bound one and in no factory, is **refused** when it tries to resolve WarmForward's problem. The bound Factory Manager **resolves it**, and the Gateway records "resolved by: the Factory Manager (session ...)" with the reason. |
| `7-resolved-by-the-factory-manager-gone.png` | WarmForward's page afterwards: the problem is gone. `gateway-problems-after.json` shows it `resolved`, with the resolver and the reason kept. |

## How the rig differs from the real thing

Said plainly, so nobody reads more into the pictures than they show:

- **The problem was seeded straight into `cron_runs`**, not produced by a real scheduled run. The folds and routes
  that read it are the real ones.
- **The two devices were enrolled by writing `device_credentials` rows**, as in brief 4's rig: a browser key for the
  Cockpit and a workstation key for the Director. Authentication was ON, so the owner-device check on both talks
  really ran.
- **The Director is a stand-in** (`rig/standin-director`). It dials the Gateway's Director stream as a real one does,
  answers the `create` command, and writes down exactly what it was sent. It never starts a real coding session. Unlike
  brief 4's stand-in, it also mints a session key for each session and registers its hash the way a real Director
  does, so the command-line step goes through the real session-key check and the real routes.
- **Screenshots 2 and 5 are not the Cockpit's session page.** That page draws a live terminal, and the stand-in has no
  session behind it (brief 4 hit the same limit). So each draws what the Gateway sent, next to the session the
  Gateway lists and the address the Cockpit moved to. The rig checks the address names that session.
- **Screenshot 6 is the transcript drawn on a page**, not a terminal capture.
- The branch has the run results work (pull request 3799) and brief 4's Handle the issue merged into it locally,
  because this step builds on both.

## Tests

| Command | Result |
|---|---|
| `dotnet test src\CcDirector.Gateway.UnitTests` | 10,565 passed, 0 failed |
| `.\scripts\test-local.ps1` | GREEN, 10 suites, 4,294 tests, every TRX outcome Completed. It flags the parked suites as a coverage gap; Gateway.UnitTests was run in full on its own (above). `-Parked` (Gateway.Tests and Core.Tests, which need Docker) was not run. |
| `python -m pytest tests` in `tools\cc-devthrottle` | 3,926 passed, 3 skipped |
| `npx vitest run` in `apps\cockpit` | 1,033 passed (after the error-display fix; the first run caught a missing `error-reported-by` note) |
| `npx vitest run` in `packages\client-core` | 1,988 passed |
| `npx tsc --noEmit` in both | clean |

The new tests:

- **Binding:** `FactoryTalkEndpointTests.Binding_*`. Set, show and clear round-trip. A missing or bad `sessionId` is
  400 and changes nothing. With the switch off the answer is 404. A start binds the new session
  (`TalkToFactoryManager_WithAPlacement_*`, `Handle_AFactoryWithNoBoss_StartsAFactoryManagerTalk_*`).
- **Resolve rights:** `CronRunResultEndpointsTests.Resolve_ByTheBoundFactoryManager_IsAllowed_InAnyFactory_AndKeepsTheReason`.
  Refused: `Resolve_BySessionThatIsNotTheBoundFactoryManager_IsStillRefused`,
  `Resolve_ByTheFactoryManagerSession_WhenNoneIsBound_IsRefused`. A missing reason is still refused:
  `Resolve_ByTheBoundFactoryManager_WithNoReason_IsRefused`.
- **Boss-less hand-over:** `Handle_AFactoryWithNoBoss_StartsAFactoryManagerTalk_BriefedOnTheProblem_AndBindsIt`,
  `Handle_AFactoryWithNoBoss_AndNoPlacementSaved_Is409WithASentence_AndNothingIsStarted`, and the fold's
  `Card_AFactoryWithNoBoss_OffersHandleTheIssue_WithTheFactoryManager` and `Route_*`.
- **The talk:**
  - `TalkToFactoryManager_*`: it starts a top-level session, opens the running one with 200, and starts a new one
    when the old one has exited;
  - refusals: half a placement is 400; no Director on the computer is 409, with nothing saved; a session key, a
    Director key or the machine token is 403; the switch off is 404; a session key never reaches the route.
- **Name check:** `FactoryNameCheckTests`:
  - CEO is flagged in an id, a name or a role, as a whole word only;
  - Boss passes;
  - a person-style name ("Quillon Varrick", a name on no list) is flagged;
  - a role word passes;
  - an archived factory is left out;
  - the seed's text is checked too.
- **Session-key guard:** `SessionKeyGuardTests`:
  - allowed: GET and PUT on the binding, and GET on the name check;
  - refused: the talk, the card, and every other verb.
- **Command line:** `tests/test_factory_manager_ops.py`, with 13 tests covering:
  - show, set (by name and by this session) and clear;
  - the switch off, said in words;
  - name-check output, with `count: 0`;
  - an answer with no list is an error, never a pass;
  - `--json` keeps the Gateway's shape;
  - the action entries.
- **Client:** four `factoriesScreenClient.test.ts` tests and four `FactoryManagerCard.test.tsx` tests.

## Revert proof

Each rule was broken on purpose, the tests that guard it were run, and the source was restored. The restore is in a
`finally` block, and the tree was checked clean and rebuilt afterwards.

| Broken on purpose | Tests that went red |
|---|---|
| The bound Factory Manager check never matches | 2 resolve tests |
| A factory with no boss hands over to nobody | 4 (two fold, two Handle the issue) |
| `SaysCeo` always false | 6 name-check tests |
| Any name passes the role-word rule | 2 name-check tests |
| A start does not bind the new session | 3 talk tests |

After the restore, the same tests passed: 83 of 83.
