# Stop a session - the Architect's state note

This is the Architect's durable memory. Everything the Architect knows about this mission can be
rebuilt from this file plus `missions/stop-a-session.html`. Nothing important lives only in a
session's conversation.

**Status:** ACTIVE. Started 8 September 2026.

| | |
|---|---|
| Mission document | `missions/stop-a-session.html` (all six rulings settled) |
| Issue | thefrederiksen/devthrottle #2633 |
| Mission id on the Gateway | `e1141592` - "You can start a session - you have to be able to stop one", the one the owner seeded |
| Branch | `mission/stop-a-session` |
| Worktree | `C:\ReposFred\devthrottle-stop-a-session`, cut from `origin/main` at `6710a86c` |
| Conduct | `cc-devthrottle workflow instructions mission` - not restated here |

---

## What the Architect established in the code before settling anything

Read from `origin/main` at `6710a86c`. These are the load-bearing facts the rulings rest on, so a
later reader can check them rather than take them on trust.

1. **The thing that actually ends a session already exists.** `SessionCommandExecutor.KillAsync`
   (`src/CcDirector.ControlApi/SessionCommandExecutor.cs`) is the Director's `kill` verb. It signals
   the agent first and forces the process after a short grace, then removes the row, and it is
   already best-effort about a process that had already died. It answers `{ killed, removed }` and
   nothing else - no process identifier, no worktree, no verdict.
2. **A Gateway route already reaches it.** `DELETE /sessions/{sid}`
   (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs`) forwards the `kill` verb over the Director
   tunnel and answers `{ killed: true }`.
3. **A session key may not call it.** `SessionKeyGuard` (`src/CcDirector.Gateway/Util/`) is an allow
   list; its `DELETE` branch does not include `/sessions/{sid}`, and its `POST /sessions/{sid}/*`
   switch allows `request-deletion` but nothing that ends a session. This is exactly the refusal
   Ruling 4 removes.
4. **Un-flagging is one command away, not new plumbing.** `DELETE /sessions/{sid}/request-deletion`
   and the Director's `cancel-deletion` verb both exist. The allow list refuses the route and no
   command calls it. Section 3 of the mission document already said this; it checks out.
5. **There is an append-only audit ledger to record a stop in.**
   `GovernanceAuditLog` / `governance_audit_events`, category `intervention`, with an actor and a
   short control-flow note. A stop reason is exactly the shape of note it takes, and it explicitly
   must not carry prompt content.

## Two facts the mission document got wrong, corrected here

The mission document says the Director window and the Cockpit "both show a live session and neither
can end one". That is not true of the Cockpit, and it changes Seat 3's work from *add a button* to
*re-point a button and make it speak*.

- **The Cockpit can already close a session today.** `apps/cockpit/src/sessions/SessionMenu.tsx`
  calls `killSession` from `packages/client-core/src/api/client.ts`, behind a confirmation. It sends
  no reason, and on success it shows nothing at all - it just closes the dialog. That is the exact
  defect this mission exists to fix, already built, in a nicer font.
- **The mobile app has the same control**, `apps/mobile/src/components/useSessionManage.ts`, calling
  the same shared `killSession`. Mobile is not named in the mission's three seats, but it shares the
  client-core function, so it cannot be left behind: changing the shared function without changing
  mobile would either break mobile or leave it as the silent second path Ruling 5 forbids. Mobile is
  therefore IN, as a consequence of the shared function, and for no wider reason.
- **A native phone client also calls the route** - `phone/CcDirectorClient/Voice/GatewayClient.cs` -
  and that one does NOT ship inside the Gateway container image, so it does not deploy in lockstep
  with the Gateway. This is why `DELETE /sessions/{sid}` is kept rather than deleted; see the route
  decision below.

## The route decision (mechanics under Ruling 5, decided by the Architect)

**`POST /sessions/{sid}/stop`, body `{ "reason": "..." }`, is THE stop.** It matches every other
session verb on the Gateway (`prompt`, `interrupt`, `hold`, `request-deletion` are all
`POST /sessions/{sid}/<verb>`), a body is the natural place for a sentence, and it is one word added
to the allow list's existing three-segment `POST` switch - which is the shape Ruling 4 describes.

**`DELETE /sessions/{sid}` is kept, and stays REFUSED to session keys.** It is kept because a native
phone client that does not deploy with the Gateway calls it, and removing it would break an app in
somebody's hand. It is not a second implementation: it becomes a thin forward into the same stop
handler and the same fold, so there is one stop and one audit trail behind two doors. Keeping it
refused to session keys is what preserves the owner's ruling exactly - **an agent's key can only ever
stop a session with a reason attached.**

## The phases, and what each one proves

| Phase | Seat | What lands | Proven by |
|---|---|---|---|
| 1 | Seat 1 - the Director ends it | The stop returns a structured, honest outcome: the process identifier, whether a process was ended or was already gone, whether a row was removed, and the worktree it leaves behind with whether that worktree had uncommitted changes. Reconciles row against process inside the owning Director. | Unit tests per state, each watched failing on purpose |
| 2 | Seat 2 - the route and the command | `POST /sessions/{sid}/stop` with a required reason; the refusal that names the reason as what is missing; the audit record; the fold that writes the one sentence; the allow-list entries for the stop and for cancelling a deletion; `cc-devthrottle session stop` and `session done --undo` | Unit tests plus the command line answering for real |
| 3 | Seat 3 - the controls | The Cockpit's close re-pointed at the stop, asking for a reason and showing the Gateway's answer; the same in the Director window; mobile carried along because it shares the function | Component tests, then the screenshots in the report |
| 4 | Inspection | An independent session FROM A DIFFERENT AGENT FAMILY TO THE BUILDERS, adversarial, reviewing the whole branch diff against the rulings. Writes to a file, replies one line. Never fixes. | Its written review, and the fixes that answer it |
| 5 | The QA report | The document of section 6, written by a seat that did not build the feature, driving the real product | The report itself - this is the mission's goal |
| 6 | Landing | The Architect merges the slices to `main`, lands this record, and emails the owner | Merged pull requests |

## Running log

- **8 Sep 2026** - Architect seated. A second Mission was created here by mistake and then removed;
  every seat attaches to the seeded Mission `e1141592`. Worktree confirmed clean at
  `origin/main` `6710a86c`. Rulings 1, 3, 5 and 6 settled in the mission document. Code facts above
  established and the two document errors corrected.

- **8 Sep 2026** - Phase A landed on the branch: the Director's honest answer, the stop route and its
  fold, the allow list, the audit record, and the two commands. 71 new tests, each watched failing on
  purpose. Ruled on its four open decisions in
  `missions/stop-a-session/architect-ruling-on-phase-a.md`: the reason belongs on the POST door
  (confirmed - the handoff was self-contradictory and the Manager was right to read past it); the 502
  on an un-foldable answer is REVERSED; stops must not be counted as interventions in the outcome
  ledger; the rest confirmed. **Ruling 3 gained a fourth verdict word**, `stoppedNotDescribed`, and
  the mission document is amended rather than contradicted by a note.
- **The largest remaining risk is the end-to-end run.** Nothing has stopped a real session; every
  layer was proved against a stub of the one below it. The QA run needs a Gateway built from this
  branch, and the branch cannot merge before the QA run exists - so the stack is stood up LOCALLY.
  Phase B de-risks that and writes down the recipe; the QA seat repeats it independently.

- **8 Sep 2026, later** - Phase A finished, including four Architect-ordered corrections: the 502
  reversed to `stoppedNotDescribed` on both doors, stops excluded from the outcome ledger's
  intervention count, `--json` on `session stop`, and the parked run completed.
- **The parked run caught a real regression, and this is the mission's best evidence for its own
  conduct.** Making `DELETE /sessions/{sid}` a thin forward silently changed its answer for an
  unknown session from 404 to a 200 `notOnFleet`, breaking two existing tests that live ONLY in the
  parked suite - one of them a CROSS-TENANT ISOLATION test pinning that one account naming another
  account's session gets a not-found. Worker B had reported its own 16 route tests green, which was
  true and beside the point: it never ran the whole parked suite, so it never saw what its change did
  to the tests already there. Proof that covered the wrong thing, caught only because the numbers
  were demanded before landing.
- **The fix was to leave the security test alone.** The legacy DELETE door keeps its 404; Ruling 3's
  "nothing on this fleet is a success" governs THE STOP, which is the new verb. Nothing is stopped on
  that path at all - no Director asked, no fold, no audit row - so the doors differ only in how each
  says "there is nothing of yours here", not in how either stops a session. The available alternative
  was to edit a cross-tenant isolation test until it agreed with the new code, and that is the move to
  distrust. CONFIRMED by the Architect.
- **Two failures left, and they are the host, not the branch:** two path-containment tests that create
  a FILE symbolic link and deliberately fail loudly rather than skip when the host cannot. Probed:
  Developer Mode unset, not elevated. The directory-link test beside them passes, which is exactly the
  split the privilege explains. They cannot be proven either way on this machine, and that is recorded
  as a gap rather than a pass.
- **A false alarm worth recording, because it is the mission's own failure family.** The Architect
  watched for the `PARKED_RESULT` placeholder to disappear by grepping the report for that string.
  When the corrected report described the correction - "the first draft carried a literal
  PARKED_RESULT placeholder" - the grep still matched, so the watch took its other branch and
  announced that the Manager had left with the placeholder still in place. Both halves were false.
  A check that cannot tell a thing from a sentence ABOUT that thing is not a check. Verified by
  reading the table row rather than trusting the watch, which is the only reason it was caught.

- **9 Sep 2026 - Phase B finished and accepted.** The controls on all three surfaces, `killSession`
  deleted rather than left beside the new function, and no surface reads the verdict word.
- **THE END-TO-END GAP IS CLOSED.** A real session was stopped through a Gateway built from this
  branch and a Director in slot 6, both local, both isolated: `stopped 75f2e3f2 - process 16316
  ended, row removed`. Process 16316 was a real `claude.exe` and it was gone afterwards. The dirty
  tree, the second stop, the refusal, `--json` and **the audit trail read back in the words that were
  typed** were all exercised live. The recipe is `local-stack-recipe.md`; the QA seat repeats it.
- **The placeholder pattern repeated, and was caught again.** Phase B's draft carried
  `PARKED_RUN_DEFAULT` and `PARKED_RUN_RESULT`. Both runs then completed: eleven result files, and the
  only two failures are the same environmental symlink pair Phase A named. **Two phases in a row wrote
  a test table ahead of the run.** That is a habit, not an accident, and the next brief should say
  plainly: never write a result row before the run that fills it.
- **Rulings on Phase B:** the pre-existing Cockpit mock fix STAYS (a suite whose exit code cannot be
  read is a check that fails open, and those web suites are this phase's only coverage);
  `docs/VisualStyle.md` gained a paragraph saying it does NOT govern the browser shells and naming
  their token sets, rather than being extended here; the two latent dialog faults were FILED as issue
  #2780 rather than fixed or lost - and the Manager corrected the Worker's claim while filing it, from
  "several other dialogs" to exactly one verified file; Title Case on the Director window confirmed.
- **One honest correction to the mission document's illustration:** section 6 sketches the second stop
  answering `already stopped`. It answers `notOnFleet`, which is right - the first stop removed the
  row, so there is no machine left to ask. The illustration was looser than Ruling 3.
- **A correction to what the Architect praised above, and it matters.** The Phase B Manager's
  correction of the Worker was only HALF right, and it said so unprompted after being released. The
  "several other dialogs" half was correct - exactly one file carries the pattern. But issue #2780 also
  said it was "not established" whether `DrainDirectorDialog` actually fails, "because I did not check
  it". The Worker had checked. The Manager then reproduced it rather than taking the Worker's word -
  having just corrected two of its claims - and it throws a `NullReferenceException` at
  `DrainDirectorDialog.axaml.cs:50`, the first constructor line touching a named control. **The Worker
  was right and the issue was wrong**, and a public comment on #2780 says so rather than editing it
  away. So `DrainDirectorDialog` is a real, shipped, latent fault, not a suspicion.
  What is genuinely still open is narrower: both observations come from the headless test host, and
  nobody has opened that dialog in the real desktop application. Either it is broken there too, or the
  headless host differs from the real one - and that difference would matter on its own, because the
  headless host is where this project's window tests run.
  **The Architect's own lesson from this:** I praised a correction before verifying it. Being right
  about the first half bought no credit for the second, and only the Manager's own honesty caught it.

- **9 Sep 2026 - the independent inspection landed and it was hard.** Eight defects, four P1, every one
  reproduced. All accepted, none disputed, none deferred. Ruling in
  `architect-ruling-on-inspection-1.md`; the findings in `inspection-1.md`.
  **Its most valuable result was not a finding:** replacing the production liveness check with the
  constant `false` left ALL 68 executor tests passing. The headline fact of the feature - was a
  process running, and did we end it - is produced by a method no test protects, because every test
  injects its own substitute. Phase A had admitted that gap in prose. A written admission is not a
  control, and the inspection is what showed the difference.
- **Ruling 3's fourth verdict was broadened** to cover an unreadable liveness check: could not be
  determined is never "gone" and never "ended". Liveness has three answers, not two.
- **The QA seat was halted before capturing any frame** - four defects sat directly under the frames
  it was about to take. Released; its recipe notes are the durable part.
- **The QA seat found the trap that nearly published client names**, and found it by obeying the one
  rule marked non-negotiable: read every image before committing it. An installed `cc-devthrottle` on
  the path inherits `CC_GATEWAY_URL` and answers from the HOSTED Gateway, printing the owner's entire
  live fleet - every session and repository name on every machine - while looking completely normal.
  A quoted PowerShell string was re-parsed by `cmd`, the path edit was silently dropped, and the first
  frame captured the owner's real roster with client repository names in it. **It was read and deleted
  before it was committed.** Independently verified by the Architect: ZERO images exist anywhere in
  this mission's history. Nothing leaked.
- **A requirement arrived from the owner THROUGH the Phase C Manager, not directly to the Architect:**
  the QA report is to show every session that built and fixed this feature, and how many. Accepted -
  it is additive and harmless - and recorded as second-hand rather than absorbed silently, because a
  requirement the Architect did not hear itself should be said out loud. Roster in `who-built-it.md`:
  fifteen sessions, six phases, fourteen from the building family and one Inspector from a different family.
- **Six of the fifteen cannot be named**, and the reason is the mission's own subject. A session reaped
  by the polite deletion flag leaves no durable record of its identifier, the Director logs do not
  carry it, and a session key is refused 403 on the Gateway session-history route. **Ruled: record and
  FILE AN ISSUE, do not fix.** Opening that route is an allow-list change, and this mission does not
  smuggle one in behind a fix phase - Ruling 4's allow-list change was decided by the owner in
  writing, and this one has not been.
  The irony belongs in the QA report in one honest sentence: the feature being built WRITES an audit
  row naming who stopped a session and why; the polite path this fleet has used for years writes
  nothing - which is exactly why six of this mission's own seats can no longer be named. **The gap
  this mission was raised to fix is the same gap that ate its own paper trail.**
- **The branch was rebased onto a fresh `origin/main` (`db141e28`) before the fixes**, per Rule Zero;
  main had moved while the mission ran.
- **An Architect error, recorded because burying it would be worse than making it.** Committing the
  paragraphs above, I ran `git add -A` in the shared mission worktree while the Phase C Manager was
  actively editing in it. It swept that Manager's in-progress work - `ProcessLiveness.cs`,
  `SessionCommandExecutor.cs`, `StopSessionProvider.tsx`, the Cockpit and mobile changes and several
  new test files - into commit `a6c4c28c`, whose message describes only documentation. It was pushed
  before I noticed.
  **Not unpicked, deliberately:** the branch is shared by several seats, undoing a pushed commit would
  need a force push, and resetting underneath a session that is actively working is the same hazard
  Rule Zero warns about. Nothing was lost - the files are committed, not reverted. The Manager was
  told immediately and told to fix forward rather than revert.
  **The lesson, stated so the next Architect does not repeat it:** an Architect that writes files into
  a worktree a Manager is working in must stage its OWN PATHS by name - never `git add -A`. A commit
  whose message does not describe its contents is a lie in the history, and this one is.

## THE ARCHITECT SEAT WAS REPLACED - 9 September 2026

**The owner ended the first Architect seat (`e66d53fb`) because it kept stopping to report progress
to him instead of driving the work to completion.** Law 1 says bother him ONCE, at the end, with the
QA report. That seat surfaced to him a dozen times. It was the right call to replace it, and the
reason is recorded here rather than softened, because the next seat needs to know what the failure
was: not a wrong decision, a wrong CADENCE.

The successor is `edc83278`, also named `Stop a session - Architect`, seated with the whole record
and one instruction: drive it to a finished QA report, merge to `main`, and email the owner. It
inherits everything in this file.

**What was true at the handover:** Phases A, B and C complete and pushed. All eight findings from
inspection 1 closed, four with a named residual. 60-plus commits ahead of `main`, zero behind,
nothing merged, no pull request open. The second inspection and the QA report were both still to do.

**The three mistakes the first Architect made, kept visible so they are not repeated:** it reported
to the owner instead of running; it ran `git add -A` in a worktree a Manager was working in and broke
the build; and it praised a correction before verifying it, when half of that correction was wrong.

---

## 9 Sep 2026, 12:35 - the Architect seat was replaced, and what the new seat owns

The owner ended the previous Architect seat because it kept stopping to report to him instead of
driving the work to finish. **This seat is `edc83278`.** The two open briefs
(`brief-inspection-2.md`, `brief-qa-report.md`) each told their seat to report to the outgoing
session; both were re-pointed at this one in commit `583d59d8`, because a review or a report sent to
a reaped session goes nowhere.

**The outgoing Architect's own last word, and it is right:** the QA report is the ONLY reason this
mission is still open, and the second inspection should run IN PARALLEL with it rather than after
it. Nothing else may displace the frames.

### What is done and accepted

Phases A, B and C. All eight inspection findings closed - `phase-c-report.md` is the account, and it
is honest enough to put four named residuals in its second sentence rather than its last section.
The branch is 62 commits ahead of `origin/main` and zero behind.

### The one open gate item, taken by this seat

`CcDirector.Gateway.Tests` never reported - it was still executing when the Phase C Manager was told
to push, and it died with that session. **This seat is running it**, in this worktree, from
`583d59d8`. It is the only suite that runs the host-bound route tests and the cross-tenant isolation
tests, and it is where Phase A's cross-tenant regression was caught. Its absence is not a pass.

### The two seats now running, in parallel

| Seat | Session | Family | Holds |
|---|---|---|---|
| Inspector, round two | `ef5ef9aa` | A different family to the builders | `brief-inspection-2.md` - is each of I1 to I8 actually CLOSED, or only NARROWED, or OPEN. Writes `inspection-2.md`, never fixes. |
| Quality | `02aa6c1a` | The building family, but it did not build this | `brief-qa-report.md` - the eight frames and `qa-report.html`. THE GOAL. |

Both were told the other is working in this same worktree, and told to copy the tree to their own
scratch directory if a build collides on a locked output rather than kill anything.

**An Architect error, caught and corrected inside a minute:** I told the Quality seat its own session
identifier was "ef-prefixed", which is the Inspector's. I sent a correction telling it to take its
identifier from `cc-devthrottle session whoami` and from nobody's message - including mine. Recorded
because the roster finding this mission is folding in is precisely that a seat which records its own
identifier is the only one that can be named afterwards, and a seat that copies one out of a message
is how a roster acquires a wrong row.

### The merge plan - four slices, in this order

89 files, 14,688 insertions. The layer boundaries are real and they slice cleanly. `CcDirector.Gateway`
does NOT reference `CcDirector.ControlApi` at compile time - the two speak over the tunnel as JSON -
but the Director's executor tests live in `CcDirector.Gateway.UnitTests`, so the Director and the
Gateway travel together as one slice.

| # | Slice | Paths | Why it is its own pull request |
|---|---|---|---|
| 1 | The engine | `src/CcDirector.ControlApi`, `src/CcDirector.Core`, `src/CcDirector.Core.Tests`, `src/CcDirector.Gateway`, `src/CcDirector.Gateway.Contracts`, `src/CcDirector.Gateway.UnitTests`, `src/CcDirector.Gateway.Tests` | The stop itself: the honest verdict, the route, the fold, the allow list, the audit row. Nothing else works without it. |
| 2 | The command line | `tools/cc-devthrottle`, `tools/cc_shared` | Calls the route over HTTP. No compile tie to slice 1; merges after it so a command never names a route that is not there. |
| 3 | The controls | `packages/client-core`, `apps/cockpit`, `apps/mobile`, `src/CcDirector.Avalonia`, `src/CcDirector.Avalonia.Tests`, `docs/VisualStyle.md` | Three surfaces through one shared function. Same reason it merges after slice 1. |
| 4 | The record | `missions/` including `qa-report.html` and its images | The mission's paper trail, and the goal document. |

Slices 2 and 3 stub the Gateway in their own tests, so each passes standing alone; the ORDER is
about not shipping a client that calls a route that has not landed. Each slice branch is built and
gated before its pull request is opened, and the release gate
(`.\scripts\test-local.ps1 -Parked -Configuration Release`) runs on merged `main` at the commit that
would be tagged - not on a pull-request head, and not on an earlier run.

### The rules this seat is holding itself to

- **Stage OWN PATHS BY NAME.** The previous Architect's `git add -A` in this shared worktree swept a
  Worker's in-progress code into a documentation commit, deleted three lines of a handler, broke the
  build with CS8321 and blocked another Worker. Two other seats are in this worktree right now.
- **Never write a result row before the run that fills it.** Three phases tried; all three were
  caught. The `Gateway.Tests` row below stays empty until the run I am watching fills it.
- **Read every image before it is committed.** The repository is public and the fleet carries client
  names.
- **Bother the owner ONCE**, at the end, with the report and the email.

---

## 9 Sep 2026, afternoon - THE GOAL IS CAPTURED

`missions/stop-a-session/qa-report.html` exists, with eighteen images in `missions/stop-a-session/qa/`.
Written by session `02aa6c1a`, which did not build the feature, against `84ba787e`. **This is the
document the mission existed to produce, and it is the best thing on this branch.**

Ten frames, not eight: the eight of section 6, plus the audit ledger read back, plus a ninth frame
this seat commissioned after reading the draft.

**All eighteen images were read by the Architect, one at a time, before any of this was accepted.**
Every frame shows only `scratch-repo`, `scratch-repo-dirty` and sessions named "throwaway A/C/D"; the
Gateway is proved in-frame as `127.0.0.1:7997` in every command-line capture; the governance token is
redacted to `<the machine token>`. **Nothing leaks.** That check is not delegable and was not delegated.

### The ninth frame, and why it was worth twenty-five minutes

The draft's own section 8 named its biggest hole honestly: every stop had been made with the
machine's credential, so **Ruling 4 - the permission the owner personally granted, for one session to
stop another - had unit tests and no photograph.** It was commissioned as a bounded addition with a
hard stop, not a new phase, and it came back better than specified. One frame, from inside a real
session, carries both halves of the ruling:

- the session's own key on the legacy reasonless door: **`HTTP/1.1 403 Forbidden`**
- the same key on the stop, with a reason: **`stopped e3fd6eb9 - process 28396 ended, row removed`**
- and the ledger row afterwards: **`ACTOR: session 83c56885-42f0-40a3-bdfd-23239383fee7`** - a
  session, not the machine.

The key was used and never printed. **An agent's key can only ever stop a session with a reason
attached** is now shown rather than asserted, which is the condition the whole permission model rests
on.

### The client-name trap fired a SECOND time, from a new direction, and was caught

The first quality seat was caught by an installed command line silently answering from the HOSTED
Gateway. This one was caught by something else entirely: **two Directors on this machine have the
identical window title**, so a capture that selected its window by title matched the OWNER'S Director
and photographed his real roster - repository names, session names, all of it. Read, recognised and
deleted before it was committed. Every capture after it selects by PROCESS IDENTIFIER and refuses to
fire unless that exact process is in front.

**Twice now, two different mechanisms, one control caught both: read every image before you commit
it.** The trap is not the mistake; the trap is the machine. Nothing else would have caught either -
both captures succeeded and both files looked normal.

### A ruling on the one thing in the frames that looks like a rule violation

`Claude Code` appears as an agent badge in the Director window frame, and `claude.exe` in two
command-line frames. **Ruled: these stay.** The badge is the product labelling which agent CLI a
session runs, and the process name is the operating system answering a question the frame had to ask;
both are evidence, neither is a claim about who wrote the code. The standing rule forbids
ATTRIBUTION, and the repository already ships `ClaudeCode` as a command-line value. Doctoring
evidence inside a quality report to satisfy a rule about credit would be the worse error by a
distance. The report reaches the same conclusion in its own closing paragraph, independently.

### What the report says is NOT proven, carried forward so it is not lost at merge

Kept here because a gap that lives only in a document nobody re-reads is a gap that comes back.

- The fourth verdict, `stoppedNotDescribed`, **was not producible on this stack** - its first cause
  needs a Gateway from this branch talking to an OLDER Director, and the rig builds both halves from
  one commit. It has unit tests and no field sighting.
- The phone application was never opened. It shares the changed function and was changed with it.
- One machine, one account, one tenant. Nothing crossed a machine boundary, and nothing here speaks
  to the hosted multi-tenant path.
- Both clicking surfaces were driven by simulated input, not by a hand - real controls in the real
  product, but neither is a person.
- The Cockpit could not be reached the documented way on a self-hosted Gateway; the browser was given
  the machine token directly. **The frames are real; the door was not the front one.** Worth an issue.
- Two smaller things left alone deliberately: the recipe's Cockpit address lands on a not-found page
  (`/sessions`, not `/c`), and a previous run's data home is still on disk and is not this run's to
  sweep.
