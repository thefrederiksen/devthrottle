# Step 3 proof: the 26 October replay sees the silence

Mission "Licenses - Loud When Stranded", step 3. The test class is
`src/CcDirector.Gateway.UnitTests/Licenses/LicenseStrandedReplayTests.cs`. It was written before steps 1 and 2, on a
branch cut from main at `aa1c515af`, with no change to product code. Its positive cases are meant to fail on that code,
and do. After steps 1 and 2 merged, the same test, unchanged, passes on main.

There are two runs, both kept in this folder:
- **The control, on `aa1c515af`** (`control-run-aa1c515af.txt`): 13 tests, 9 passed, 4 failed. It is kept as it was.
- **The passing run, on main at `88f7f9019`** (`passing-run-main.txt`): 13 tests, 13 passed. That commit is step 2
  (pull request 4528), on top of step 1 (`2349a1ea0`, pull request 4525). The branch was rebased onto it with no
  change to the test.

## The command

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~LicenseStrandedReplayTests" --logger "console;verbosity=normal"
```

Run from the repository root on `aa1c515af` plus this one test file. The full output is
`control-run-aa1c515af.txt` beside this file: **13 tests, 9 passed, 4 failed**. The 4 failures are exactly the four
positive loud cases, and each fails on the same assertion: no email, no push and no banner line.

## What the test rebuilds

Usage reports go into the real license store on a throwaway SQLite file, as Directors would send them. Then one Gateway
minute runs: the real readiness check (`LicenseReadinessService`), then the real switch check (`LicenseSwitchService`).
Both share one license alarm (`LicenseAlarmService`), one recorder of owner emails and one recorder of pushes. The banner
is read the way the Cockpit and the phone read it, through `GET /gateway/licenses/alarms` (`LicenseEndpoints.Alarms`).
No verdict is built by hand.

- **26 October, 13:23 UTC.** The dev account is at 100% of its weekly window (it resets 29 October 21:00). The
  centerconsulting and duksrevo accounts are at 97% weekly. soren.frederiksen is at 0.5%, so it is the only account
  with room. devlinux holds a soren.frederiksen sign-in. SOREN_NORTH and the Mac hold only centerconsulting and
  duksrevo. SORENLAPTOP last reported on 23 October 02:43.
- **25 October, 18:00 UTC.** The same machines and sign-ins, with the dev account at 80% of its WEEKLY window and 20%
  of its five-hour window. The other accounts read as they would have then (the reasons are in the class comment), so
  soren.frederiksen is again the only account with room.
- **The approver.** soren.frederiksen's approver is a browser on SOREN_NORTH whose last check failed, as it failed every
  day from 19 October. So the readiness check cannot fix the gap by signing the machine in. This is why the gap stood
  on the day.
- **SORENLAPTOP** is the worst case, as the Tech Lead ruled: dev active, only centerconsulting and duksrevo spares. Its
  report is three days old, so what it holds is not current and it is not stranded by this rule.

Every check matches what a person would read: the machine's name and the account addresses (all `example.com`). No
check depends on a sentence's wording, because steps 1 and 2 have not written theirs yet. Each surface is matched so
that one machine's words cannot answer for another's (review finding 2):
- **Banner:** by the entry's own `machineName` and `accountEmail` fields (the machine, and the account it is stuck on),
  with the needed account looked for in that entry's own text.
- **Email:** line by line. An alarm email gives each alarm its own line, or for one alarm its own paragraph.
- **Push:** by its machine field.

## Each case, in plain words

| Case | What it asserts | On `aa1c515af` |
|---|---|---|
| `OutOfWeek_26October_DevlinuxIsSwitchedToTheAccountWithRoom` | devlinux is sent exactly one switch, from dev to soren.frederiksen. | PASS. This part worked on the day. It guards the replay: if it failed, the picture would be wrong, not the Gateway. |
| `OutOfWeek_26October_AMachineThatCannotSwitch_IsToldByEmailPushAndBanner` (SOREN_NORTH, Mac) | The owner gets an email line naming the machine, dev and soren.frederiksen. There is a push about the machine, and a banner entry about the machine on dev whose text names soren.frederiksen. No automatic sign-in was started. Every push and every banner entry is about SOREN_NORTH or the Mac, never SORENLAPTOP. | **FAIL x2**: "emails sent: 0 ... pushes sent: 0 ... banner lines: []". |
| `EightyOfWeek_25October_AMachineReadyForNoAccountWithRoom_RaisesTheWarning` (SOREN_NORTH, Mac) | The same three surfaces and the same rule about naming only those two machines, for the 80% weekly picture. | **FAIL x2**: the same. |
| `FiveHourAtEightyFive_25October_TheAlarmThatExistsToday_IsHeard` (SOREN_NORTH, Mac) | The 25 October picture with the dev account's FIVE-HOUR window at 85%, which today's alarm covers. The same three surfaces name the machine and both accounts. | PASS. This proves the recorders and the banner read can hear an alarm. |
| `OutOfWeek_26October_WithTheSignInHeld_EveryMachineIsSwitched_AndNothingIsLoud` | Negative control: the 26 October picture with SOREN_NORTH and the Mac each holding a soren.frederiksen sign-in. All three live machines are switched to soren.frederiksen. There is no email, no push and no banner line, including about the stale SORENLAPTOP. | PASS. |
| `EightyOfWeek_25October_WithTheSignInHeld_NothingIsLoud` | Negative control: the 25 October picture with the sign-in held. No switch, no email, no push, no banner line. | PASS. |
| `UsageAlone_AtEightyOrNinetySevenOfEitherWindow_WithEveryMachineReady_RaisesNothing` (dev five-hour/weekly at 80/50, 20/80, 97/50, 20/97) | Boundary for issue #4249 ("usage limits are not to be policed or flagged"). Every machine, SORENLAPTOP included, holds a sign-in for the account with room. There is no email, no push and no banner line, whatever the usage. | PASS x4. |

## Why each failure is the defect and not the test

- **Not a crash and not a setup error.** All four failures come from the one assertion message in
  `AssertToldLoudly`, and the run reaches it. Before that line, the same test has checked that no automatic sign-in was
  started, which is the 26 October fact. In the 26 October case, the devlinux switch on the same picture passes, so the
  picture loaded and the minute ran.
- **The instrument can hear.** The five-hour 85% case uses the same recorders, the same banner read and the same
  picture, and it passes on this code. So "emails sent: 0, pushes sent: 0, banner lines: []" is the Gateway saying
  nothing, not a recorder that cannot record.
- **It is the silence the mission describes.** With SOREN_NORTH and the Mac on a full account and ready for no account
  with room, the engine leaves them behind with a log row and no event (mission section 5, root cause 4). The 80%
  alarm looks only at the five-hour window, so the weekly 80% says nothing (root cause 3). The negative control shows
  that the same minute, given the missing sign-in, switches both machines quietly. The only difference between the two
  is the stranded state the test is about.

## A finding for steps 1 and 2

On this code, today's five-hour alarm (`not-ready-five-hour`) also raises a line, a push and an email for **SORENLAPTOP**
when its report is three days old ("SORENLAPTOP is not ready to switch off dev@example.com, which is at 80% of its 5-hour
window ..."). The instrument check above does not assert anything about the stale machine. The replay's positive cases
do: a push or a banner entry about SORENLAPTOP fails them. So if step 1 or step 2 raises its alarm through a path that
includes stale machines, these tests will fail on it.

## What a green run proves, and what it does not (review finding 1)

The cases match no alarm kind, so they cannot tell step 1's alarm from step 2's. Step 1 alarms when the account is
tired, at 97% or more. Step 2 alarms when either window is at 80% or more. 100% is above 80%, so once step 2 is built,
the 26 October cases pass with or without step 1.

- **A green run after both steps proves** that the Gateway is loud about a stranded machine at 80% and at 100%.
- **A green run does not prove** that step 1's alarm in particular exists.

The evidence for step 1 is its own test class (`LicenseStrandedAlarmTests`, first opened as pull request 4493 and merged as pull request 4525) and its run of this
replay, made before step 2 existed. On 26 October I ran this revised replay on step 1's branch
(`origin/loud-switch-1-stranded-event`) in a throwaway worktree, since removed: 13 tests, 11 passed, 2 failed. The two
26 October loud cases pass; the two 25 October warning cases fail and are left for step 2.

## The passing run on main

On 26 October, after steps 1 and 2 merged, this branch was rebased onto main at `88f7f9019` with no change to the test,
and the command above was run again. The full output is `passing-run-main.txt` beside this file: **13 tests, 13
passed**. The four cases that failed in the control now pass, and the negative controls, the boundary and the
instrument check still pass.

As the section above says, the green 26 October cases prove that the Gateway is loud about a machine stranded on a full
account. They do not prove that step 1's alarm in particular is the one that spoke. Step 1's own proof is its test
class, `LicenseStrandedAlarmTests`, and its run of this replay before step 2 existed: 11 passed, 2 failed.
