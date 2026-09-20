# Phase 1, task 1 - raised sessions in the Gateway - the Developer's proof

Issue #3177. Branch `fleet-manager-improvement/p1-gateway`. Written 2026-09-20 by the Developer seat.

## The command, and what happened to it

The mandate's check is one command, run in the worktree:

    .\scripts\test-local.ps1 -Parked

**It could not finish in the foreground.** The tool this seat runs commands through allows ten minutes; the
parked Gateway host suite alone took one hour and sixteen minutes that night. At the ten-minute mark the tool
moved the run to the background by itself. I did not ask for that, and I did not start it that way. I let it
finish rather than end a running process, and read its result files. Say so plainly to whoever reads this: the
full run below was NOT watched in the foreground from start to end.

That full run was made on the first commit of this branch, BEFORE the fixes it led to and BEFORE the rebase
onto main. It has not been repeated in full since, because a repeat is another hour and more in the
background. What WAS repeated afterwards, in the foreground, on the rebased branch, is listed second.

### Run 1 - the full `-Parked` run, first commit, before the rebase (backgrounded by the tool)

| Suite | Passed | Failed | Total | What failed |
| --- | --- | --- | --- | --- |
| CcDirector.Core.UnitTests | 762 | 2 | 764 | MINE - fixed, see below |
| CcDirector.Avalonia.Tests | 554 | 0 | 554 | |
| CcDirector.Engine.Tests | 63 | 0 | 63 | |
| CcDirector.HostedAgent.Tests | 88 | 0 | 88 | |
| CcDirector.Launcher.Tests | 195 | 2 | 197 | not mine - see "Red that is not this change" |
| CcDirector.Terminal.Avalonia.Tests | 30 | 0 | 30 | |
| CcDirector.Reclaim.Tests | 241 | 0 | 241 | |
| cc-director-setup.Tests | 25 | 0 | 25 | |
| cc-director-setup-engine.Tests | 619 | 0 | 619 | |
| CcDirector.Gateway.Tests (parked) | 2698 | 6 | 2712 (8 skipped) | 2 MINE - fixed; 4 not mine |
| CcDirector.Core.Tests (parked) | 3780 | 1 | 3788 (7 skipped) | not mine - the test host crashed |
| CcDirector.Gateway.UnitTests (parked) | 6497 | 11 | 6510 (2 skipped) | MINE - fixed, see below |

What was mine in that run, and what I did:

- **Eleven Gateway unit tests and two PostgreSQL proofs** pin which migration is the newest and how many
  follow the one under test. This change adds a migration, so every one of them had to move. They now name
  `AddRaisedSessions` as the newest for both providers. The chain test confirmed the useful thing on the way:
  the new migration's Designer differs from the one before it by exactly its own table and two indexes.
  Two of the PostgreSQL proofs were already behind main before this change (they still named the migration
  before last as the newest); they are brought up to date here.
- **Two Core unit tests** (`RetiredMessagingWordsTests`) forbid any text an agent reads from naming
  `cc-devthrottle session prompt`, on the stated premise that the Gateway refuses it to every session key.
  This task changes that premise. The Fleet Manager's skill and conduct are now named exemptions, with the
  reason, and the test's own sentences say "every session key the owner has not raised". Every other text is
  still held to the ban. **This was an Architect's ruling (17 September); the Tech Lead should confirm the
  exemption is the right answer.**

### Run 2 - after the fixes and after the rebase onto main (`f20bbd33b`), all in the foreground

| Command | Result |
| --- | --- |
| `.\scripts\test-local.ps1` (the default gate) | 8 of 9 suites green: Core.UnitTests 764, Avalonia 646, Engine 63, HostedAgent 88, Terminal 30, Reclaim 323, setup 25, setup-engine 619. Launcher 206 of 208 - the same two tests as run 1. |
| `dotnet test src/CcDirector.Gateway.UnitTests` (the whole parked suite, with a build) | 6,626 passed, 0 failed, 8 skipped |
| `dotnet test src/CcDirector.Gateway.Tests --filter` - `RaisedSessionHostTests` and every host test class for the routes this change touches (Fleet Manager routes, placement, hand over, page, walkthrough, judged-stop answer, fleet messages, session keys, governance audit) | 132 passed, 0 failed |
| `.\scripts\test-local.ps1 -Parked -Filter` - the six host tests that failed in run 1, my host tests, and `PostgresProviderProofTests`, against the throwaway PostgreSQL (made BEFORE the rebase) | 40 of 44. Both PostgreSQL proofs green, the provider census green, my 19 green. The 4 red are the tunnel and voice tests below. |

**Not repeated after the rebase:** the rest of `CcDirector.Gateway.Tests` (about 2,550 tests) and
`CcDirector.Core.Tests`. Main's five commits since touched `GatewayEndpoints.cs` and `GatewayHost.cs`; the
rebase was clean, but a clean rebase is not a test run.

## Run 3 - after the review, on the commit that carries the skill fix (all in the foreground)

Added 20 September 2026 by the Developer seat that fixed the review's finding 1. The change since run
2 is two rows of one shipped skill file and two new mission documents - no code. The three suites
below are the ones that can decide that change, and they ran on this worktree with the fix in the
working tree, each built from source, each watched from start to end.

| Command | Result |
| --- | --- |
| `dotnet test src\CcDirector.Core.UnitTests --filter "FullyQualifiedName~Skills|FullyQualifiedName~RetiredMessagingWords"` | 11 passed, 0 failed, 4 seconds. The eleven are named below. |
| `dotnet test src\CcDirector.Gateway.UnitTests` (the whole parked suite, with a build) | 6,626 passed, 0 failed, 8 skipped, 5 minutes 22 seconds |
| `dotnet test src\CcDirector.Gateway.Tests --filter "FullyQualifiedName~RaisedSessionHostTests"` | 19 passed, 0 failed, 40 seconds |

The eleven guard tests, listed because the point of running them is WHICH ones ran, not the count:
`BuiltInSkillsHaveOneSourceTests` (both - the repository copy equals the shipped body, and a copy
still carries its frontmatter), `RetiredMessagingWordsTests` (all five - the typing-command sweep
over every text an agent reads, the tree scan, the named surfaces and the exemptions, and the inbox
wording), and `ShippedSkillsTeachOwnershipTests` (all four, including both of its cases for the Fleet
Manager - the skill and the workflow conduct). `BuiltInSkillsHaveOneSourceTests` is the one the
one-source rule names: there is no `.claude/skills/fleet-manager/SKILL.md` in this repository, so it
passes by there being nothing to disagree with the shipped file, which is the state that rule prefers.

**What run 3 does NOT cover, and it is the same gap run 2 left.** This is not
`.\scripts\test-local.ps1 -Parked`. The roughly 2,550 remaining Gateway host tests and the whole Core
suite have still not run against the five main commits this branch was rebased onto. That is the
review's finding 2, it is unpaid, and it is carried as a work item by the Delivery Lead - see
`reviews/phase-1-task-1-answers.md`.

### Red that is not this change

- **`LauncherDeclaredCapabilitiesTests`, two tests, every run.** The branch has zero difference from main
  anywhere under `src/CcDirector.Launcher` and `src/CcDirector.Launcher.Tests`, so this is main's code failing
  on this machine. The tests expect that no restart signal is armed; I did not establish why one reads as
  armed here.
- **`TunnelExplicitRouteProofTests` and the two voice sweep tests, four per run, a different four each time.**
  Each reads "the last command the Gateway sent" and finds a background verb (`screen-grid`,
  `set-resolved-role`) there instead of its own. This change sends no Director command. I did NOT run them on
  main to prove they are red there; the evidence is only that the set differs between two runs of the same
  binary and that the code is not mine.
- **`CcDirector.Core.Tests`: the test host crashed** in `RepositoryRegistryConcurrencyTests` (a temporary
  folder gone under a reader thread). Not code this change touches. Seven tests never ran because of it.

## The revert proof for the guard widening

Committed first. Then in `AuthMiddleware.AuthenticateSession` the consultation of the raised list was switched
off (`if (false && ...)`), the host test project was REBUILT, and `RaisedSessionHostTests` was run:
**6 of 19 red** - typing with a raised key, the owner-only Fleet Manager routes, the record of raised actions,
lowering taking the grant away, raised following the mark, and the other-account control. The message test
stayed green, correctly: the message waiver is a different code path. Then `git checkout` restored the file,
`git status` and `git diff HEAD` were both empty, and the run was repeated WITH a build (never `--no-build`):
**19 of 19 green.**

## What the tests prove, in plain terms

Through a booted hosted Gateway, over HTTP, through the real middleware, with real minted session keys:

- A raised key's prompt, interrupt, escape, fan-out and judged-stop answer reach the route and get the SAME
  status and the SAME named code the owner's own device gets for the same request. The owner-only Fleet
  Manager routes (placement read, page, walkthrough read, restart, move) likewise. An unraised key, asked the
  same thing in the same test, gets 403 `session_key_out_of_scope` with today's sentence.
- A raised key is refused - byte for byte as an unraised key is - devices, account devices, logout, account
  email, trial, credits, Gateway shutdown, the walkthrough's three writes, and its own audit trail. It cannot
  raise another session, raise itself, or lower itself. None of those refusals leaves a record.
- Another account's session answers a raised key exactly as an unknown session does. A session id raised in
  account B gives account A's key for that id nothing.
- Raise and lower work from the owner's own device and are recorded; from a Director's key they answer 403
  `owner_only`; from any session key they are refused by the guard and the list is unchanged.
- Setting up the Fleet Manager from the owner's device raises it; when the mark moves the old one is lowered
  and the new one raised at that moment; when it clears nobody is raised - each step checked by a real request
  with the affected key, and by the stored record.
- **A session key that marks itself the Fleet Manager is marked and NOT raised** (see decisions).
- When a Director says over a real tunnel connection that the session is over, the entry is gone.
- The list survives a Gateway stopped and started again over the same files.
- Eight messages in a row from a raised sender to a session it is not related to are all queued - past the
  relationship rule, six an hour and ten minutes - the ninth, identical and unread, is dropped as a duplicate,
  and there are exactly eight records naming the sender. An unraised sender is refused as today.
- Every raised action leaves exactly one record, naming the acting session as actor, read back through the
  owner's query route; the prompt's words are not in it; the unraised key's attempts left none.

Unit tests pin the pure rules: the guard asked both ways for every row, the store (including the backstop that
a mark entry counts only while its session IS the mark, and that raised is carried to a successor and never
multiplied), the placement service calling the store with the right caller at the right moments, the message
rule's waiver flag, the roster values, and that a Director cannot push a forged raised stamp.

## What the tests do NOT cover

- **No real Director and no real agent.** No Director is on the tunnel, so "passed the guard" is proven by the
  route's own answer (409 screen unreadable, 502 not connected), not by text arriving in a session.
- **The mark is written in six places; two are proven end to end** (the mark route and the placement
  service's start, restart and move). `FleetManagerPromotionStore.Promote` and the Gateway's own clearing of
  the mark are covered only by the backstop rule, which makes a forgotten writer fail closed (not raised), and
  that rule is unit tested - but no test drives those writers.
- **PostgreSQL:** the migration is proven to apply and the collation census passes; the raised list's own
  reads and writes were exercised on SQLite only.
- **The desktop display push** does not carry the raised stamp (it does not carry the Fleet Manager pin
  either). Only the roster routes are stamped and tested.
- **The record of a message is written after the message is queued**, not before. A failure between the two
  would leave a queued message without its record. Typing and owner-route actions are recorded BEFORE they
  run, and that ordering is what the tests exercise.
- **The command line's help text** (`tools/cc-devthrottle/src/cli.py`, `session_ops.py`) still says typing is
  refused to every agent. It is outside this Gateway task, and no Python test ran.
- No web test and no Python test ran at all.
- Nothing here was deployed, so nothing is proven about the hosted Gateway.

## Decisions I made that the Tech Lead should see

1. **The mark alone never raises.** Any session key may call `PUT /gateway/fleet-manager` today
   (`fleet-manager set` with no argument marks the caller). Had the mark raised, any session could have raised
   itself in one call. So the mark raises only when the owner's own signed-in phone or browser set it. A
   session key moving the mark lowers the old Fleet Manager and raises nobody.
2. **A restart or move carries raised; it does not grant it.** A Fleet Manager the owner lowered by hand stays
   lowered across a restart.
3. **The walkthrough's writes are not granted** (answered, snoozed, close): they store that THE OWNER did it,
   which would be a false record from a session. Compact-and-continue with a message, and the whole-account
   broadcast, are left exactly as today; neither is in the mandate's list.
4. **The judged-stop answer route keeps its own second wall**: while an account's verdict colours are off, no
   session key, raised or not, may answer one.
5. **The word "raise" now means two things** in the product: this, and `session raise` (a hand up to the
   session that started you). The terminology skill may want a line.
