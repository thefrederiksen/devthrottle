# QA report - one real Director restarted start to finish, and what it cost to find out

Phase 7 of issue 2719 (issue 2726). Mission "Restart a Director without losing the fleet".

**Status: SKELETON - the run has not happened yet.** Every section below carries the heading the run
must fill and, where the fact is already known, the fact. A section that says NOT YET RUN is exactly
that; nothing here is reconstructed from what should have occurred. Phases 1 to 6 are unmerged as of
2026-09-07 14:30Z; the rig stands and is seeded ahead of them.

---

## The opening sentence

While cataloguing this defect class across two thousand catch sites, we built a fresh instance of it
into the drain we were writing to fix it. Phase 4's review found that an unknown or missing
drain-report state fell into the permissive `drained` branch - it would have closed a session on an
answer nobody understood. That is not a near-miss in old code. It is new code, written on
2026-09-07, by a seat that had already been sent the finding.

Knowing the class, naming it, and hunting it across the codebase did not stop us reproducing it in
the thing we were building. That is the honest state of how well any of this transfers.

## The lead finding - the class itself

**A binary that should have been a trinary, with the missing third state being "I do not know",
silently folded into whichever of the two is permissive.**

Six witnesses, three altitudes (the code, the process, the instrument):

| | Witness | The two states it had | The missing third | Folded into | Cost |
|---|---|---|---|---|---|
| 1 | The locator (issue 2730) | running / not-running | could not read the registration | not-running, which `Start` reads as permission to start a Director | a corrupt file starts a second Director on a live instance home; SHIPPED |
| 2 | The safety sweep | found / clean | never ran | clean | every seat was required to report even a nothing |
| 3 | The mutation harness | killed / survived | did not build | survived | voided every mutation claim on the mission at once |
| 4 | The capability handshake | capable / too old | did not answer | (named as three states before it shipped) | would have condemned a merely unreachable machine |
| 5 | The workspace outcome | restarted-and-restored / blocked | the Director-versus-seats distinction | (one word carrying two facts) | a recovery with no restart had no name at all |
| 6 | A second mutation harness | killed / survived | did not build | KILLED - the opposite mechanics, a non-zero exit read as a kill | the evidence looked stronger than it was |

In five of the six the fold went the permissive way, where permissive includes flattering your own
evidence.

**What the mandated re-runs found.** A mutant weakening an acknowledgement check from "the flag is
TRUE" to "the flag is PRESENT" survived. A launcher answering `onlyIfEmpty:false` - stating plainly
that it had NOT guarded the restart - would have been accepted as a guarantee. The epic's central
promise was reading its own refusal as consent. It was invisible to every test and every review, and
only a mutation run under a compile gate found it. That is the reason every mutation claim on the
mission was voided and re-run, and the justification for the cost of doing so.

**The correct handling was usually already in the file** - three times in one day, adjacent to the
wrong one, by the same author:

- `ReadSessionCount` refuses to let an unreadable roster read as idle, one method from the locator
  resolution that folds unreadable into `NotRunning` (issue 2730).
- `SaveClaudeJson` declines to write when it cannot read, eleven lines below `SaveSettingsJson`,
  which overwrites and destroys the hooks (issue 2735). Its guard was written for a DIFFERENT reason
  ("Don't create .claude.json if it doesn't exist"), so the author reasoned about the ABSENT case in
  one method and about the UNREADABLE case in neither.
- One seat's mutation harness had three verdicts while another seat's, written the same day, had two.

But not as the general case: of nine defects, exactly one had a clean correct sibling in the same
file. The pattern is real and striking where it occurs, and it is not the explanation.

**Coverage is thinnest exactly where the damage is greatest - as a mechanism, not a coincidence.**
What is cheap to assert is what gets asserted, and side effects are what is expensive, so coverage is
pushed off the dangerous paths by the same force that makes them dangerous. Verified three times for
three:

- Of the locator's two consumers, the TESTED one was `StopAsync` (a wrong answer costs nothing) and
  the UNTESTED one was `Start`, which launches a process. No test called `Start` on any branch; that
  is why issue 2730 survived.
- Not one test on origin/main names `ClaudeConfigDialog` or `SaveSettingsJson` - the path that
  destroys the hooks carrying the fleet preamble (issue 2735).
- The mutation harnesses were verifying themselves, and nobody tests those at all.

Two sharpenings, both worse than the original claim:

- **The coverage collapses onto the pure functions.** `AutomationBrowserService` has exactly one
  behavioural test on main, on `AccountUrl` - a pure string builder. `LaunchAsync`, `StopAsync`,
  `IsUpAsync`, `StatusAsync` and `RemoveAsync` - every method that touches a process - have zero.
  `SessionHistoryStore` has ten test files while the three call sites that overwrite with it have
  none.
- **The boundary is where the cheap half of the story ends.** `CodexHookInstallerTests` DOES test the
  failure branch and even asserts non-clobbering - and stops exactly where the consequence starts:
  nothing asserts what `SessionManager.CreateSession` does with that `false`, which is launch the
  session anyway with no preamble. A green suite that tests the producer reads as if the pair were
  covered.

So: hunt by what is hard to test, not by what looks wrong, and check where each test STOPS.

**Three categories, and the third breaks the obvious remedy:**

1. Enumeration gap - the case was never considered.
2. No vocabulary - nothing in the type could express the third state.
3. **Fully considered, documented, and deliberately permissive - and still a defect.** Both hook
   installers state the fold and its consequence in their own doc comments ("the session still
   launches, just without the preamble hook"), and both run on every session launch.

Category three kills "make the unconsidered case visible": the case was considered and written
down. The doc comment IS a warning wearing a note, at the design level. The answer to "visible to
whom" is: to the person the consequence happens to, at the moment it happens. The ruling that
follows: **a session that starts without its preamble is a session running without the standing
rules**, including the owner's no-attribution law. It either refuses to start, or starts visibly
marked as running without its rules.

Counts by category: NOT YET COMPILED - taken from the class sweep (issue 2733) when it merges.

**A sub-class: an exemption whose justification is a claim about behaviour.** `PrintBanAuditTests`
lets a banned flag past because `ClaudeProcess.cs` "launches every Claude Code session" - which the
sweep showed is false. It reads as verified because it lives in a test, and no test fails when the
behaviour moves.

**The rule, for somebody who was never here:** when a result describes the world rather than a
decision, "I could not find out" is a real answer and it needs its own name. If it has no name it is
already inside one of the others - and it is inside the permissive one, because that is the branch
nobody writes a test for.

Class sweep count (how many other places return an enum where one value doubles as "could not
determine"): NOT YET RECORDED - issue 2733 reports 2001 catch sites, 174 folds, 9 defects; the final
figure goes here with the instrument's control.

### What the fix looks like, done properly

The Phase 0 seat's harness is the worked example:

- It was given a THIRD outcome: `KILLED`, `SURVIVED`, `BROKEN` - and `BROKEN` cannot be read as either.
- The new gate was run against a KNOWN-BAD input: on the same non-compiling mutant, in one run, the
  new harness reported `BROKEN` while the old absence-based reading reported `SURVIVED`.
- The mutations were re-derived against the CURRENT tree: 19 mutations, all 19 compiled, all 19 killed.
- Its OTHER absence-based instruments were re-checked without being asked: a reachability grep
  re-run with a control that must hit (549 control matches); the attribution scan fired at a planted
  bad line (2 hits) before its clean result on a 171252-byte diff was believed.
- The harness and its list are committed at `scripts/mutation/launcher-update-mutations.py`.

**A zero is only evidence once the instrument has been seen to produce a non-zero.** Every zero in
this report carries the control that proves the instrument fires.

### The second failure - the opposite shape

Five seats and their Architect spent hours behind a continuous integration check that a written rule
in this repository forbids waiting for (`CLAUDE.md` section 5a). Main had no branch protection and no
required status checks; every pull request was MERGEABLE the whole time. Found by querying the
repository, not by re-reading the rule.

This is the reverse of the lead finding: the structure existed, was written down, was correct, and
nobody reached for it. **Already fixed, and this report is not the fix:** issue 2732 carries the
analysis; `CLAUDE.md` section 5a is now an imperative with a trigger (when the local gate is green
and the review is in, RUN `gh pr merge`); every mandate on this mission had its definition of done
rewritten from a state to an imperative with a trigger; the blocked-ness query is a command anybody
can run. The rule: **a prohibition on an absence cannot fire.** The Manager did not apply it either,
for hours, while writing "merge on green" into other seats' mandates.

Deliberately not fixed, the owner's call on 2732: the .NET build keeps running on these branches,
because it covers what the local gate's two-minute budget skips.

---

## The rig, and why it is not slot 7

The mandate said slot 7. Two facts, checked against the code on origin/main, made that impossible:

1. **A slot Director cannot be restarted by any launcher.** The launcher's supervisor manages
   exactly one Director - `<root>\app\cc-director.exe` in `<root>\instances\default` - and its
   `director/restart` handler ignores the `Path` the command carries. Filed as issue 2743, a limit of
   the feature and not of the rig.
2. **A second launcher on this machine against the production Gateway would REPLACE DevThrottle_1's
   launcher entry and command stream** - registration keys on tenant plus the bare machine name, last
   writer wins. Filed as issue 2742. A machine-name override was put to the Architect and refused:
   it would be a deliberate capability to register as another machine.

So run one is an isolated rig: its own storage root, its own Gateway (the desktop tray build, auth
on), its own launcher and its own Director, all built from this tree at one commit, with the real
machine unreachable by construction. `scripts/restart-qa-rig.ps1` stands it up; the Director is
started by the launcher through the production start route, which is the first thing the rig proves.

| | |
|---|---|
| Root | `%LOCALAPPDATA%\cc-director-restart-qa-rig` |
| Gateway | `http://127.0.0.1:7911`, auth on, tailscale off |
| Binaries | 2.0.6+4f742a4d3 at first stand-up; rebuilt after every rebase |
| Isolation control | production `/launchers` still lists the real launcher (pid 69640) for SOREN_NORTH after the rig came up; production `/directors` unchanged |

**This makes the epic two runs, on the Architect's ruling (2026-09-07).** Run one, this report,
proves the MECHANISM. Run two - a genuine restart of DevThrottle_1 on production, the owner's own
accept on his own phone, driven from SORENLAPTOP under rule zero - proves the EXPERIENCE, which is
what he asked to see. Run two is initiated by the Architect after run one passes; the run tooling
takes a Gateway address, a credential, a machine and a Director id, so run two is a change of target.

---

## The before picture

NOT YET RUN. Produced by `scripts/restart-qa/before-picture.py` at the moment of the request:
every live session on the Director with id, name, mission, role, what it was doing, repository,
branch and uncommitted count, from the Gateway's own records.

Attachment: `attachments/before-picture.md` and `.json`.

## The approval

NOT YET RUN in full. Must show: the request created by a session key (which session, in its own
words); the direct restart route refusing that same key (`403 session_key_out_of_scope`, with the
control that the same key reaches an allowed route); what the owner saw before accepting.

**Already shown on the rig, before the run (14:47Z, build 4f742a4d3):** a RawCli seat on the rig
Director ran `scripts/restart-qa/session-key-probes.ps1` with its OWN session key - the only honest
way, since a key never leaves its session - against a made-up machine name, so nothing could have
restarted even with no guard at all.

| Case | Expected | Observed | Artefact |
|---|---|---|---|
| direct restart with a session key | 403 `session_key_out_of_scope` | 403, "a session key may not call POST /machines/.../director/restart ... never the admission surface" | `attachments/negative-cases/session-key-probes-rig-prerun-2026-09-07T1447Z.json` |
| control: the same key on an allowed route of the same machine | 400 "repoPath is required" (the route is reached; the 403 above is the guard, not machine resolution) | 400 "repoPath is required" | same file |
| the Phase 6 request route with the same key | not yet defined | NOT RUN - recorded as such, not as a pass | same file |

The same probe runs again during the pass, on the tree that carries Phase 6, with the request route
filled in.

**What this run does not exercise here:** the owner's accept on the rig is performed with the
rig Gateway's shared token by the driver, standing in for him. That proves the mechanism and not his
experience of it. Run two covers the experience.

## The drain

NOT YET RUN. Every handover and the index; which seats were `drained`, `covered`, `blocked`,
`unreachable`; the secret sweep with its control hit (a clean sweep proves nothing until it has been
shown to fail on a document deliberately seeded).

## The gap

NOT YET RUN. How long the Director was down, measured: the shutdown signal time, the last heartbeat
of the old Director id, the first registration of the new one, and the first stream Hello.

## The after picture, against the before picture, session by session

NOT YET RUN. For each seat: restored, or deliberately not and why. A restored seat is shown doing the
work it was doing before - the turn where it picks its own thread up, quoting a fact that exists
nowhere but its handover.

## What was lost

NOT YET RUN. Every difference between before and after. There will be some.

## The blocked path - the first time anything has refused to stop

NOT YET RUN. Before this run nothing had ever refused to stop, so every earlier claim about
never-force rested on a path no one had seen run.

The wedged seat: DESIGN SETTLED, SEEDING IN PROGRESS. See "the wedge" below.

Expected, and each asserted: the drain stops; the wedged seat is recorded with its true state and
the reason (`blocked` in its own words if it could answer; `unreachable` with the wait if it could
not); the restart does NOT happen; the seats already closed stay closed and are in the record; the
owner is told.

Then the recovery in the same pass, BEFORE the wedge is cleared: the already-closed seats restored
from the workspace with no restart, each shown carrying on the work its handover describes, no live
seat duplicated, ending recorded `directorOutcome: not-restarted` with `seatOutcome.scope: all`.

### The seeded fleet

Two missions, a real Architect-Manager-Worker chain, two agent families, one snoozed seat, one wedged
seat, and real uncommitted work in two real worktrees cut from origin/main. Every agent seat was given
a fact that exists nowhere but in its own context, so a restored seat can be asked for it.

| Seat | Agent | Reports to | Real work, uncommitted by design | Fact only its handover can carry |
|---|---|---|---|---|
| Rig Alpha - Architect | Claude Code | nobody (mission head) | docs/rig-alpha/DESIGN.md | rulings D-4117, D-4118, D-4119 |
| Rig Alpha - Manager | Claude Code | Architect | docs/rig-alpha/MANAGER-LOG.md | checkpoint M-2261 |
| Rig Alpha - Worker A - status file | Claude Code | Manager | docs/rig-alpha/STATUS.md | note W-7703 |
| Rig Alpha - Worker B - launcher test run | Claude Code | Manager | a foreground `dotnet test` run; docs/rig-alpha/TEST-RESULT.md | note W-7704 and the counts |
| Rig Beta - Manager | Claude Code | nobody (mission head) | docs/rig-beta/BETA-LOG.md | checkpoint B-5150 |
| Rig Beta - Worker C - launcher notes | Codex | Beta Manager | docs/rig-beta/LAUNCHER-NOTES.md | note W-8812 |
| Rig - standalone - weekly count (snoozed) | Claude Code | nobody | docs/rig-beta/STANDALONE-COUNT.md, then held itself for 720 minutes | count S-3391 |
| Rig - wedged - RawCli on a command that never returns | RawCli, `cmd /c ping -t 127.0.0.1` | nobody | none - it cannot do anything | none - it cannot write one |

Observed at 14:44Z: all seven agent seats had done their work (seven files on disk, `git status`
showing them untracked in both worktrees), the standalone was `holdState: Held`, and the wedge was
still flagged.

**A finding from the seeding itself.** Four of the seven agent seats had their seed prompt typed by
the Director into a Claude Code composer that was still initialising. The echo check missed twice,
the Director stamped `promptDeliveryUnresolved: true` on each with the reason in full, and the text
sat parked in the composer, doubled and unsubmitted - the exact failure Phase 5's restore is told to
check for before calling a seat restored. The flag fired on 4 of 8 spawns, so it is an instrument
that has been seen to say no. There is no product retry verb; the prompts were sent again through
the same route once the composers had settled, all four accepted, and the flag cleared. A restore
that reads this flag and re-sends is doing the right thing; one that reads `turnCount` alone would
have called four dead seats alive.

### The wedge

Phase 4's drain (pull request 2731) has two ways a seat can fail to reach a clean stop, and they
are different states:

- `blocked` - the seat ANSWERED and declared itself blocked, with `blocked-reason` in its own words.
- `unreachable` - the seat never wrote a document within the ninety-minute handover deadline; it is
  still running and nothing was forced.

A seat that is genuinely incapable of producing a handover - the mandate's requirement - cannot
declare anything, so it lands as `unreachable`. The seed is a RawCli session sitting on a command
that never returns.

**Proven wedged before it was needed, through the drain's own delivery path.** The Director's prompt
route - the one behind `SessionCommandExecutor.SendPromptAsync`, which the drain's `SendAsync` uses -
was given the kind of message a drain sends. It answered `accepted: false` with
`EchoVerifiedSubmit: the composer never echoed the typed text after 2 attempts - the TUI is not
accepting input`; the buffer kept scrolling ping replies with no trace of the text; no document
appeared at the path it was told; the `ping -t` process was still alive afterwards; and the session
row went to `promptDeliveryUnresolved: true`. So the seat is incapable, not unwilling - and the
Director knows it at the moment of the attempt, not only ninety minutes later. Artefacts:
`attachments/wedge-proof/`.

**Also observed on the rig, against the skill's own text.** The director-restart skill says a
restarted Director gets a new identifier. On the rig the Director id `9f46482f` SURVIVED a full
stop and start through the launcher (new pid, same id), because the id store keys on executable
path plus instance. Neither claim should be assumed; the registration is re-read every time.

## The negative cases

Each must be shown FAILING correctly in this same pass:

| Case | Expected | Observed | Artefact |
|---|---|---|---|
| Restart on a machine that cannot be restarted | refused BEFORE the owner is asked, naming the Phase 1 reason | NOT YET RUN | |
| Ask twice | second refused | NOT YET RUN | |
| Let an approval expire | refused | NOT YET RUN | |
| Restart with a session still live | `onlyIfEmpty` refuses AND names the count | NOT YET RUN | |
| Drain with the wedged seat in place | drain stops, seat recorded, restart does not happen | NOT YET RUN | |
| Recover from that blocked drain with no restart | closed seats come back doing their own work; no live seat duplicated | NOT YET RUN | |
| Corrupt a registration while sessions are live, ask for a restart | REFUSED (issue 2730) | NOT YET RUN | |

## What this run still did not exercise

- The owner's own accept on his own surface (see The approval). Run two.
- NOT YET COMPLETE - filled after the run.

## How we came to believe something untrue

The sentence "a partly drained Director is a normal, recoverable state" sat in the workspace schema
as a code comment directly above the code that made it false: an outcome list in which a blocked run
had exactly one terminal state and no way to record that a recovery had happened. Everyone on this
mission had read it many times. Nobody noticed, and nobody would have, until a wedged session was
scheduled to make it matter - it was found by reading the actual type definitions instead of the
issue that described them.

It happened TWICE in one day. `LegacyWorkspaceImport`'s header said "The bytes stay on disk under the
new name; nothing is deleted", and thirteen lines below it `RenameAside` did
`if (File.Exists(target)) File.Delete(target)` - destroying a backup that could be the only surviving
copy of a workspace. Neither failed a test. Neither looked like a defect. A claim written next to the
code that contradicts it reads as documentation, not as a defect.

Every seat was then asked to grep its own diff for the safety claims it had written and check each
against the code underneath, reporting even a nothing:

| Seat | False safety sentences found | Of which real |
|---|---|---|
| Phase 2 | 4 | 4, one a shipped fail-open (issue 2730) |
| Phase 0 | 4 | 3, including a class comment asserting as fact the one invariant it documents everywhere else as never exercised |
| Others | NOT YET COLLECTED | |

## MERGEABLE and evidence-still-valid are two different facts

A branch can be conflict-free against main while its green gate describes a tree that no longer
exists. A gate result is a claim about a specific tree and it expires the moment the base moves, so a
rebase invalidates the evidence you were about to merge on. Caught mid-mission; the missing step
(confirm the branch contains current origin/main, rebase if not, re-run the gate, then merge) was
added to every mandate.

## A seat caught itself about to commit the headline finding

The sweep seat's first fail-before-the-fix mutation was `if (false)`. It did not compile (CS0162) and
the run exited non-zero, which reads exactly like "tests failed, mutation killed". Banking that
number would have been witness three, reported and committed in the same act. It reported the
near-miss, redid the mutation in a form that builds, and published both numbers: 5 of 12 killed, 7
correctly surviving.

## The gate was two things and everyone reported the first

Reviewed and not-reviewed collapsed into "gate green". It surfaced only when positive evidence was
demanded - who reviewed it, when, and where the output is:

- **The merged bootstrap went in on half a gate.** Phase 0 (`4f742a4d3`) had exactly one Codex
  review, at 2026-09-06T23:47Z on commit `99b500252`. Three commits followed it - the rig's
  self-update, the four safety-sentence corrections, and the mutation COMPILE GATE - and the merge
  came 19 minutes after the last. None was reviewed.
- **The compile gate is the sharpest instance.** That unreviewed commit is the instrument that voided
  every mutation claim on the mission, and its replacement numbers were trusted. A Codex seat was put
  on it afterwards, asked whether the gate distinguishes three states or merely moves the fold, and
  whether the safety sentences were fixed in the code or softened in the prose.
- **Where the demand was answered properly it worked.** Phase 4 produced agent, version, session id,
  timestamp, a 184KB saved output and the exact two commits reviewed - and volunteered that its own
  fix round was still unreviewed.

## Our own lead finding turned up inside the drain built to fix it

Phase 4's review returned 29 findings. The most serious: an unknown or missing drain-report state
fell into the permissive `drained` branch. Also: a re-read failure at close time that closed the seat
anyway, destroying the only session that could rewrite the document; an amendment saying `blocked`
ignored because only restore fields were re-applied; a refused deletion recorded as flagged; a
session spawned after the capture present in no record and silently destroyed; a cycle in the
reporting chain letting a whole ring be reaped; a finding's excerpt able to publish a SECOND secret
on the same line.

## The mechanical form of the defect

Phase 3's bounded check found one in its own diff: `if (stored.Origin != Captured) return` decided
whether a captured workspace's seats were immutable - a NOT test against ONE known value, so any
origin it has never heard of falls through into the permissive branch, including an origin written
by a NEWER build of this same software. Rewritten as `if (stored.Origin == Authored) return`.

**`!=` against a known-BAD value is fail-open by construction; `==` against the known-SAFE value is
fail-closed.** They read almost identically and differ only when the world grows a value you did not
write - which is exactly what a future version of your own code does.

The same seat's account of why the rest of its diff was clean, which is the right shape for a
negative result: the capture COPIES states rather than interpreting them; every enum read in
validation is an allow list that refuses the unnamed value; the legacy import refuses a file whole on
any shape it does not understand. "Clean, and here is the mechanism" is evidence; "clean" is not.

## A grep across a large tree is an instrument like any other

The sweep's first coverage number was wrong because a bare `StopAsync` matched 156 test files across
unrelated types. It disambiguated rather than banking the figure, and the corrected answer was far
worse than the wrong one. Every count in this document was produced by an instrument, and an
instrument that has not been shown to distinguish what it claims to distinguish is not evidence.

## The fix round is where the defects were

Phase 2 counted three defects its own change produced and had caught - a surviving acknowledgement
mutant, four false safety sentences, and a regression introduced while narrowing a catch that left
field reads uncovered and would have turned a shrugged-at route into a 500. All three found after
the code looked finished, by the author.

## A warning in a document is a design defect wearing a note

The Manager proposed deriving the welded outcome field rather than removing it, then wrote warnings
into two mandates telling future readers never to trust that field alone - an hour after agreeing
that welding two facts into one token was the defect. When you catch yourself writing "never read
this alone", that sentence is a report FROM THE DESIGN that it is wrong. The test: if the fix
requires people who were not in the conversation to remember something forever, it is not a fix.
Beside the false safety sentences because they are the same failure from opposite ends: one is prose
that contradicts the code, the other is prose that props the code up.

---

## Attachments

- `attachments/before-picture.md`, `.json` - NOT YET RUN
- `attachments/after-picture.md`, `.json` - NOT YET RUN
- `attachments/index.json` (the workspace as the drain wrote it) - NOT YET RUN
- `attachments/handovers/` - NOT YET RUN
- `attachments/negative-cases/` - NOT YET RUN
- `attachments/rig-stand-up.txt` - the rig coming up, with the isolation control
