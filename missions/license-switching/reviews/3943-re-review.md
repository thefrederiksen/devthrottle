# Re-review of pull request 3943

Round 1 is `3943-review.md`; the Developer's answers are `3943-answers.md`. This round is for the code at
`efd7988f3` (the worktree is at `8c4a0192e`, which adds two documents and no code). 13 October 2026.

## Verdict: approve

The one must fix of round 1 is fixed and I could not break it. All ten round 1 findings are answered in the code
and held by tests that name a presence. There are 7 new findings: 2 should fix and 5 notes. Neither should fix can
leak anything or cross an account; both are about the Licenses log recording the wrong end of a sign-in in an
uncommon case. Whether they go in before the merge or straight after is the Tech Lead's call.

## What I ran, and what I did not

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` at `efd7988f3` | 709 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` at `efd7988f3` | 39 passed, 0 failed |
| Flood reproduction, out of tree (below) | the limit holds |

**The flood reproduction.** A scratch console project outside the repository, referencing the built
`CcDirector.Gateway.dll`, builds the real `LicenseApprovalRelay` with a sender that never answers and its log sent
to a scratch folder. Output:

- 8 requests taken: 8 in flight, 8 commands held.
- 1,000 more for the same account, each with its own identifier and an address of 4,096 characters: all 1,000 threw
  `LicenseApprovalsBusyException`; in flight stayed 8; commands sent stayed 8; the heap grew by 8,880 bytes.
- 20 with an address of two million characters: all 20 threw `ArgumentException`; nothing was taken.
- While the first account was full, a request of a second account was taken (9 in flight).
- After the 8 were released, in flight was 0 and a new request of the first account was taken.
- 1,041 log lines, none holding the address.

**Not run, so not claimed:** `.\scripts\test-database.ps1` (the PostgreSQL test of finding 7 is judged by reading
it); any mutation of the product code; the revert table in the proof file, which the brief said to leave. The two
should fix findings below are read from the code on both sides and were not reproduced.

## Round 1 findings

1. **The limit did not bound - FIXED.** `LicenseApprovalRelay.cs:104-137` checks the identifier, the account and
   the address on the call, and takes a place under one lock with a kept count. A refused request has no entry, no
   task and no command. The reproduction above confirms it, and refused requests do not use up places.
2. **The 15 minute rule - FIXED as ruled, with two gaps (new findings 1 and 2).** The Director is asked again with
   the same identifier and deadline; a report counts only when accepted after the 15 minutes
   (`LicenseSignInService.cs:301`); a renewal is judged by the stored sign-in time having changed (`:309-311`). The
   Director on main keeps a finished result on disk and answers a repeat from it
   (`LicenseSignInCoordinator.cs:80`, `:93`), so a repeat cannot run a finished sign-in twice, and one sent while
   the first is running waits and gets the first one's result. Asking stops when the row ends; a pass that is
   still asking makes the next minute skip that account only (`:235`).
3. **A relay that throws - FIXED.** `LicenseApprovalRelay.cs:196-209` answers `approval-failed` and logs the type
   only. The test throws an exception whose message holds the address and checks the log has neither.
4. **Unchecked text in log lines - FIXED.** The relay, `Start` and `LicenseApproverStore.Set` all log after the
   check; names refuse control, format and separator characters. One small exception is new finding 3.
5. **The host's own log lines - FIXED.** The hosted test first shows the router's lines are in the capture, then
   that no line holds the address, the sealed approval or the key, across an approved relay and a dropped tunnel.
6. **Shared token and raised session key - FIXED**, over real HTTP, and the table now gives the right code for a
   session key.
7. **PostgreSQL - FIXED by reading.** The test is in `Gateway.DatabaseTests` and covers every statement of
   `LicenseSignInLog`. I did not run it.
8. **Approver store - nothing to change.**
9. **Wrong scope - FIXED.** `Accept_CalledWhileAnotherAccountsScopeIsInEffect_...` is the case I could not build.
10. **Two mismatches - FIXED.** The wait is the sooner of 3 minutes and the deadline (`:275-278`).

## New findings

1. **SHOULD FIX - an answer of "I have no record" is recorded as how the sign-in ended.**
   `LicenseSignInService.cs:284-291`. When asked again, a Director that finds no stored result and is past the
   deadline answers `failed` / `expired` with `Replayed` false (`LicenseSignInCoordinator.cs:97-99`). The Gateway
   ends the row with that. But the Director keeps a result for 24 hours only
   (`LicenseContract.SignInResultRetention`, `LicenseSignInResultStore.cs` `IsCurrent`). A sign-in that worked,
   whose answer was lost, on a laptop then closed for more than a day, comes back as `expired`: the log says
   failed while the machine holds the account. The report that would show the truth is never looked at, because
   step 2 runs only when step 1 gave no answer. Suggested: when the answer is not a replay and the command had
   gone out before (`SendCount` above 0), settle from the later report instead. For a Director that truly never
   saw it, the report gives the same `failed`.

2. **SHOULD FIX - asking again can start the sign-in, when the machine's clock is more than five minutes slow.**
   `LicenseSignInService.cs:270-281`. The Director judges the deadline by its own clock
   (`LicenseSignInCoordinator.cs:97`). The Gateway asks 15 minutes after the ask, 5 minutes after the deadline, by
   its own clock. A Director more than five minutes behind that never received the first command sees a deadline
   still ahead and starts the sign-in, a quarter of an hour after the owner clicked. The 60 second wait then ends
   with no answer, the same pass falls through to the report (`:298-314`) and can end the row `failed` while the
   sign-in is running. The contract says clocks may be minutes apart, and the Gateway already keeps each machine's
   measured difference (`ClockSkewSeconds`). The new text in `contract.md` 8.4 and `cockpit-api.md` 7.5 ("asking
   again can start nothing") is true only for clocks within five minutes. Suggested: do not ask a machine whose
   measured difference is near the margin, and do not settle from a report in a pass whose ask timed out.

3. **NOTE - the identifier shape accepts a trailing line break.** `LicenseMessages.cs:404` is
   `^[0-9a-f]{32}$`, and in .NET `$` also matches before a final line break; the scratch run printed `True` for 32
   characters plus one. It is on main and shared with the Director, which uses the identifier as a file name. Here
   it puts one line break into the relay's lines (`LicenseApprovalRelay.cs:125`, `:131`, `:138`). Nothing can be
   forged with it. `\z` closes it. The answers say a refusal line names nothing of the request; the line at `:131`
   names the identifier.

4. **NOTE - every refused request writes one log line.** `LicenseApprovalRelay.cs:131`, `:161`. The run above
   wrote 1,020 refusal lines for 1,020 refusals. The limit holds memory; it does not hold the log.

5. **NOTE - a repeat of an identifier in flight is dropped without an answer, whoever sent it.**
   `LicenseApprovalRelay.cs:123-127`. A second Director of the same account using the same identifier gets a call
   that succeeds and no result. Identifiers are random, so only a faulty Director does this. Unchanged from round 1.

6. **NOTE - three lines of `LicenseSignInService` still log an exception's message** (`:170`, `:344`, `:399`),
   under a comment at `:213` that says the type only, everywhere. No address or approval is in reach of this class.

7. **NOTE - nothing ends a row whose machine never comes back, and asking has no upper count.** Documented in
   `cockpit-api.md` 7.5 as intended. The row is read every minute for good, and "Add a spare" for that machine and
   account stays refused.
