# Review - Smart Director Restart phase 4 (2 of 2): the command line door

Reviewer seat: opened by the Delivery Lead (session number 150). Review of `git diff d8fdaafed HEAD` on branch
`smart-restart/p4-command-line`, head `6e4424d07`, in the worktree
`D:/ReposFred/devthrottle-smart-restart-p4-cli-review`. This file is written to that worktree and not committed.

## SCOPE - what I read, what I ran, what I could not reach

Read in full, from the diff: `SmartRestartCommandDtos.cs` (new), `SmartRestartWire.cs` (new),
`smart_restart_ops.py` (new), `test_smart_restart.py` (new), `SmartRestartCommandLineTests.cs` (new), and the
changes to `ControlApiHost.cs`, `DirectorSmartShutdown.cs`, `ISmartShutdown.cs`, `GatewayEndpoints.cs`,
`SessionKeyGuard.cs`, `SessionKeyGuardTests.cs`, `cli.py`, `test_axi_step_6c_help_and_errors.py` and
`docs/cli-reference.md`.

Read for context, not changed by this work: the Developer mandate, the proof, `mission.md` (sections 3, 5.1,
5.3 items 11 and 12, 5.4, 7), `DirectorSmartShutdown.cs` and `DirectorWayUp.cs` in full (the engine and the
history the door hands to), `DirectorCommandRouter.cs`, `MapDirectorFailure`, `TryResolveOwnedDirector`,
`AuthMiddleware.cs` (how the guard is reached and why a session-key scope refusal is terminal),
`DirectorLauncherRestartStep.cs`, the restart-request endpoints, `axi_cli.py` and the `gateway.py` helpers
(`field`, `path_segment`, `GatewayError`), the Director resolver (`_resolve_director_id`, `_my_director`),
the first phase 4 review (the dev reports half, a different workstream), and every `TrySendAsync` call site
in the Gateway (to answer "can any other route forward a caller-controlled verb to the tunnel").

Ran, all in the foreground, every count read from the summary line:

- The mandate's check, `dotnet test src/CcDirector.Gateway.UnitTests --filter
  "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`: **0 failed, 624 passed** - exactly the proof's count.
- The whole guard class, filter `SessionKeyGuard`: **0 failed, 294 passed** - the widening of the allow list
  broke no other guard case.
- The screens on the same engine file, `dotnet test src/CcDirector.Avalonia.Tests --filter
  "FullyQualifiedName~SmartRestart"`: **0 failed, 145 passed**.
- The whole `CcDirector.Gateway.UnitTests` project: **0 failed, 6767 passed, 8 skipped** (3 minutes 26
  seconds). This closes the proof's own caveat that its whole-project runs were on the earlier base: on the
  final base the whole project is green.
- The command line suite, in a throwaway virtual environment with the tool's declared floor (Python 3.11,
  click 8.5.0), because the system Python's click 8.1.8 is below that floor: `tools/cc-devthrottle/tests`
  **3442 passed, 3 skipped, 0 failed** - exactly the proof's count. On the system Python, 6 of the 24 new
  tests fail with "stderr not separately captured", which is the click-8.1 environment problem the proof
  discloses, not a defect in this change.

What I could not reach:

- No real Director, Gateway or launcher was driven, as the mandate forbids. The three Gateway routes have
  never served a request in my presence either, so every route-level statement below is a reading of the
  code, not a run.
- I did not mutate any tracked file, so the Developer's revert proofs were not re-run. Instead I read each
  new test and asked whether it can fail; the refusal tests assert exit codes, exact stderr sentences and
  that nothing was watched afterwards, so each goes red if its guard is removed.
- `Gateway.Tests` (hosted), `Core.Tests` and the other Python toolbelts were not run; the diff touches none
  of them.
- The "before" counts were not re-measured on untouched `origin/main`; I verified the arithmetic instead
  (624 minus the 18 new C# cases is the 606 baseline; 3442 minus the 24 new tests and the 17 self-enumerating
  usage-error cases is 3401).

## What I verified, so the findings below are the whole list

- **A session cannot reach the start.** The refusal sits inside the authentication itself
  (`AuthMiddleware.AuthenticateSession`), and a scope refusal is terminal there - it does not fall through to
  the cookie path or to any other credential. The refusal is checked before the allow list, is matched as
  one literal three-segment shape with the segments lower-cased, and the trailing-slash, case, sub-path,
  wrong-verb and wrong-surface neighbours are all refused (the 294-case guard class). No Gateway route
  forwards a caller-controlled verb down the tunnel: I enumerated every `TrySendAsync` call site and every
  verb is a fixed literal at its route, so the only door to `smart-restart/start` is the one guarded route.
  The direct restart route stays refused (existing test at `SessionKeyGuardTests` line 308). Tenant scoping
  is `TryResolveOwnedDirector`, the same resolver every other `/directors` route uses: another account's
  Director is a not-found, exactly as for the restart route this door claims to match.
- **The two reads opened to a session key are a deliberate, disclosed widening, and the reasoning holds.**
  Both are inside the GET/HEAD block as two literal shapes; the history is folded from the very workspace
  records `GET /gateway/workspaces` already serves this key, and the progress rows carry the same facts the
  roster serves. Nothing wider opened: a sub-path, a DELETE, a POST to the history and a history by id are
  all refused. Confirming or deleting the six lines is the Delivery Lead's admission decision, as the proof
  says.
- **It is the same engine, not a second one.** The start calls `CreateSmartShutdown()` - the same factory the
  File menu path uses - with purpose Restart; the time rule is read from `SmartShutdownTimes.Allowed` and
  `RefusalFor` (one list, one sentence, in both the throwing and the refusing caller); the two-thirds point
  and the limit are computed by the engine and carried on its snapshot; the record is the drain's, with the
  same driven-by note as the window's run; the history is `IDirectorWayUp.ReadHistoryAsync`, scoped to this
  machine and this Director's name, so it cannot show another Director's or another account's records.
- **The wrong Director cannot be acted on by accident.** One Director is named or the command refuses; a
  name matching nothing, a name matching two, and a Director that is registered but not running each refuse
  with the reason (the resolver's own sentences, and the Gateway's 502/404 through `MapDirectorFailure`).
  There is no form that acts on more than one.
- **The outcome is told apart.** A refusal is the Gateway's or the engine's own words and exits 1; a start
  taken and not watched exits 3 with a sentence saying nothing here knows how it ended; a Director that goes
  silent is reported as exactly that and claims neither success nor failure; a time outside 5, 10, 15, 30,
  60 is refused by name by the Director and nothing is touched; a history that could not be read is an error
  carrying the reason, never `count: 0`; a run that ended carrying no result is reported as neither running
  nor finished.

## Findings

### 1. (Medium) The three Gateway routes are the one seam of this change with no test anywhere

`src/CcDirector.Gateway/Api/GatewayEndpoints.cs` lines 3908, 3934 and 3948.

What breaks, for whom: the guard is tested as a pure function, the host dispatch on a real `ControlApiHost`,
the wire translation over what the real engine raised, and the command line against a stubbed Gateway - so a
route mapped one word off (`/directors/{id}/smart-restarts`, say), a broken relay, a wrong status code or a
miswritten tenant resolution in these three handlers ships green through every suite this phase ran. I ran
the whole `CcDirector.Gateway.UnitTests` project on this base: 6767 passed, and not one of them reaches these
routes. The hosted `Gateway.Tests` project has no smart-restart test either. This is exactly the failure the
guard file itself records from history (`IsCatalogueWrite`: "a test written from the guard tests the guard
against itself... both were consistently wrong and green"): the guard test pins the refused shape, and
nothing proves a route is mapped at that shape. The Owner would meet it in the one situation this door exists
for - the window is broken, he reaches for the command line, and the route is not where the guard, the
contract and the documentation all say it is.

Why it must change, or be accepted out loud: the sibling half of this same phase (dev report inheritance)
added hosted route tests for its new route and the first reviewer ran them; the pattern exists in this
repository. At minimum the start route needs one hosted test that a session key receives the 403 with the
named sentence through the real authentication pipeline, and one that a device key reaches the relay. The
Developer discloses this gap and asks the Delivery Lead whether phase 5 covers it; my reading is that a
route test is cheaper and more repeatable than a rig run, and that "phase 5 will catch it" should not be the
only line between this door and a route that is not mapped.

### 2. (Medium) The documented success exit code is racy, and on the normal path it is unreachable

`tools/cc-devthrottle/src/smart_restart_ops.py` (`_ended`, exit 0 on outcome `RestartAccepted`),
`docs/cli-reference.md` ("Exit 0 means the launcher accepted the restart"), and the mechanism:
`src/CcDirector.ControlApi/Restart/DirectorLauncherRestartStep.cs` line 47 - "A successful ask may never
return to this process" - and the launcher stopping this very Director as part of accepting.

What breaks, for whom: exit 0 requires one progress poll to land in the window between the engine recording
`RestartAccepted` and the launcher stopping the process. The engine's own contract says that window may be
zero length. So the usual end of a run that WORKED is the Director going silent and the command exiting 3 -
the same code as a Director that died mid-run - and the same successful run can end 0 or 3 depending on
timing. A person reading the output is told the truth either way; a script or an owner following the
documentation is told to expect a 0 that the normal flow will not produce, and cannot distinguish success
from death by exit code, which is the one thing the mandate asked the exit codes to deliver.

Why it must change, or be accepted out loud: the Developer discloses it and declines to have the client rule
on what a silence means, which is the right instinct under critical rule 7. But the documentation currently
promises the racy outcome, not the likely one. Either the phase 5 real run measures which code a successful
run actually ends with and the documentation is corrected to say so ("a successful restart usually exits 3;
exit 0 means the final state happened to be read before the launcher stopped the Director"), or the command
asks the Gateway - which holds the restart record and the Director registry, and is the side allowed to rule -
instead of leaving the watcher to guess. Silence is a decision the Delivery Lead should make on the record,
not a documentation claim that the normal flow contradicts.

### 3. (Low) `restart-history --count` does not apply to `--json`

`tools/cc-devthrottle/src/smart_restart_ops.py` line 335: the `--json` branch returns before the `--count`
narrowing at line 348, so `restart-history --json --count 1` returns every record.

What breaks, for whom: an agent composing flags by the repository's own standard. The command line standard
in this repository says every filter applies to `--json` too, and the tool's own `session list` narrows its
bare array under `--json` ("a filter narrows the same bare array; it never changes its shape"). The help text
discloses it ("Output raw JSON: every record, every seat"), so it is not silent - but an agent that asks for
one record in JavaScript Object Notation gets them all, and nothing errors.

Why it must change: narrow the entries in the `--json` path exactly as the plain path does, keeping the
shape; that is the house pattern one file away in `session_ops.py`.

### 4. (Low) A malformed answer one level down is silently degraded rather than refused

`tools/cc-devthrottle/src/smart_restart_ops.py` line 348 (`entries` that is not a list becomes an empty
history; non-dict entries are dropped) and line 177 (`_rows`: `sessions` that is not a list becomes no
rows).

What breaks, for whom: the command's own principle - "a history that could not be read is an error naming
why, never an empty list" - is enforced at the top level (a non-object answer is refused) but not one level
down. A future Gateway that renames or reshapes `entries` makes this command print `count: 0`, the
Director's "no records" message or a blank line, and exit 0 - the exact lie this command was built never to
tell, on the surface the mandate singles out ("a history that could not be read... never an empty list").
Every test stays green, because every test feeds well-formed shapes.

Why it must change: refuse an `entries` that is not a list and a `sessions` that is not a list, the same
way the object-level check refuses, with the "this is not a shape I can read" sentence. That is the
no-fallback law applied to the one place this file still has a fallback.

### 5. (Low) `--machine` without `--director` is silently ignored on a command that empties a Director

`tools/cc-devthrottle/src/smart_restart_ops.py` lines 79-85: `machine` is only consulted when `director` is
given; otherwise the command resolves to "this session's own Director" and the flag is dropped.

What breaks, for whom: `director smart-restart --machine OTHER_BOX` does not act on OTHER_BOX. Today the harm
is contained - a session key is refused at the start route, and a terminal outside a session has no
`CC_DIRECTOR_ID` so the command refuses with "Name the Director" - but this is the one command where acting
on a Director the caller did not name is the harm the mandate names first, and the flag that appears to name
where is accepted and ignored rather than refused. The history commands inherit the same silence
harmlessly, which is why the fix belongs in the start path or in a shared usage error, not in the resolver
every other command shares.

Why it must change: refuse `--machine` without `--director` as a usage error on the start command, so the
locator flag either narrows a name or is rejected - never silently "here".

### 6. (Low) The history is capped at 25 records and reports the cap as the Director's whole history

`src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs` line 30 (`MostRecentRecordsRead = 25`, a phase 3
constant chosen for start-up reading) and line 299 (`Take(MostRecentRecordsRead)`); `WayUpWords.HistoryRead`
("This Director has {count} restart records"); the wire contract comment ("EVERY restart record THIS
DIRECTOR WROTE", `SmartRestartCommandDtos.cs`); the command's `count: N of N total` line.

What breaks, for whom: the mission's own stated habit is a restart most mornings; 25 records is under four
weeks of it. Past that, the command silently hides the older records, prints `count: 25 of 25 total`, and
the Director's own message states the read count as the Director's total - an absence presented as a
complete answer, on the door whose documentation says "every restart record" and "nothing is deleted".

Why it must change, or be accepted out loud: the cap is right for start-up, where the way up only needs
the newest; this door's promise is broader. Either say the cap when it bites (the count line or the message
naming that older records exist and are not being read), or read past it on this door. The phase 3 design
note ("older ones stay on the Gateway and stay readable there") is true of the Gateway, not of this command.

## Verdict

No finding above blocks the merge. The authorization is sound and I could not find a way round it: the
refusal is inside the authentication, terminal, and matched as one literal shape; the tunnel has no other
door; the tenant boundary is the same one the restart route trusts. The engine is the same one the window
calls, and I found no second copy of any rule. Findings 1 and 2 are the two that should be decided by the
Delivery Lead on the record rather than carried silently: finding 1 is the only untested seam of the change
and finding 2 is a documentation promise the normal flow contradicts. Findings 3 to 6 are small, local and
independent of each other, and each is a sentence or a refusal away from closed. The proof is honest: every
gap I verified independently, it had already named, and both of its counts reproduce exactly - including
the whole project on the final base, which its own caveat left unmeasured until now.

END OF REVIEW
