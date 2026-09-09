# Phase B report - Seat 3, the controls, and the local stack

Written for the Architect by the Phase B Manager (session `578d8e29`). Branch
`mission/stop-a-session`. **Nothing was merged to main** - that is the Architect's, and it is the only
"done".

Phase B had two jobs. The second turned out to be the more important one.

---

## Job two first: THE END-TO-END GAP IS CLOSED

**A real session has been stopped.** The thing the Phase A report named as the mission's largest
remaining risk - that every layer was proved against a stub of the one below it, and that the run
proving otherwise might not be possible at all before the branch could merge - is no longer open.

A Gateway built from this branch and a Director in slot 6 pointed at it, both local, both isolated
from anything the owner is running:

    stopped 75f2e3f2 - process 16316 ended, row removed
    the worktree C:\ReposFred\devthrottle-stack\_stack\scratch-repo was left untouched - it had no uncommitted changes
    reason: the local stack smoke test for the Stop a session mission

Process 16316 was a real `claude.exe`, and it did not exist afterwards.

The recipe is `missions/stop-a-session/local-stack-recipe.md` - every command, every configuration
value, and a table of the four things that went wrong and what fixed each. The QA seat repeats it.

**What else the live stack proved, because it was standing anyway:**

| Ruling | What was run | What came back |
|---|---|---|
| 2 - a dirty tree | A second session stopped while its repository held an uncommitted file | Went through without argument, named the worktree, said the changes were left untouched, and **the file was still on disk afterwards** |
| 3 - the second stop | The same stop run again | `not on this fleet - nothing in this account carries the id 75f2e3f2, so no machine was asked and no machine's processes were searched`, **exit code 0** |
| 4 - the refusal | A stop with no reason | Refused, named the reason as what was missing, named the flag, **exit code 1** |
| 4 - the audit trail | `GET /gateway/governance/audit-events?eventType=stopped` | Both stops present, `category: intervention`, `eventType: stopped`, an actor, and **the exact reason that was typed** |
| 5 - the machine-readable answer | `session stop --json` | The whole folded answer, parsed |

That last row is the one worth pausing on. **The owner accepted Ruling 4 on the explicit ground that
stops are audited.** Until this run, that ground was a code path nobody had exercised. It has now
been exercised, and the reasons read back out of the trail in the words they were typed in.

**One honest correction to the mission document's own illustration.** Section 6 sketches the second
stop answering `already stopped`. It answers `notOnFleet`, and that is right rather than a defect:
the first stop removed the row, so there is no longer a machine to ask. `alreadyStopped` is for a row
still present with no process behind it. Ruling 3 says exactly this; the illustration is looser than
the ruling. Worth knowing before someone reads a screenshot and calls it a mismatch.

---

## Job one: the controls

Three surfaces now go through `POST /sessions/{sid}/stop`, and **not one of them composes a
sentence.** Each renders the Gateway's `headline`, then each of `details` in order, verbatim.

| # | Thing | Where | Commit |
|---|---|---|---|
| 1 | One shared function, `stopSession(sessionId, reason)` | `packages/client-core/src/api/client.ts` | `321394fa` |
| 2 | The Cockpit: `Close session` becomes `Stop session`, asks why, and shows the answer | `apps/cockpit/src/sessions/SessionMenu.tsx` | `321394fa` |
| 3 | The phone: the same, laid out for a phone | `apps/mobile/src/components/{useSessionManage.ts,SessionAppBar.tsx}` | `321394fa` |
| 4 | The reason box submits on Enter on both surfaces | both | `1e65aaf9` |
| 5 | The Director's own door onto the route | `src/CcDirector.ControlApi/GatewayClient.cs`, `ControlApiHost.cs` | `d8964402` |
| 6 | The Director window: `Close Session` becomes `Stop Session`, with a dialog | `src/CcDirector.Avalonia/{MainWindow.axaml.cs,StopSessionDialog.axaml(.cs)}` | `382d5439` |

**`killSession` is deleted, not left beside the new function.** Nothing in `apps/` or `packages/`
references it. The one remaining caller of the legacy `DELETE /sessions/{sid}` door is the native
phone client, which does not ship inside the Gateway container - which is the whole reason the
Architect kept that door. I corrected the Gateway comment that still named `killSession` as a caller
(`015e5b0f`); it now says what is true.

**Three behaviours that are the same on every surface**, because Ruling 5 is about one event described
one way:

- The confirm control is **off** while the reason box is empty or only whitespace. The Gateway would
  refuse that stop, so a control must not offer a click that can only come back refused.
- On success the control **does not close silently**. It shows the headline and the detail lines and
  stays up until the person dismisses it. On the Cockpit that dismissal is what fires `onClosed`; on
  the phone it is what returns to the roster. The old behaviour - close the moment the call returns -
  was the defect.
- On failure it stays open, shows the failure in the Gateway's own words, and **keeps the reason the
  person typed**, so a retry does not start by making them write their sentence again.

**No surface looks at the verdict word.** There are four today; a fifth is one edit on the Gateway and
none in any client. Both Workers wrote a mutation that branches on the verdict and watched it go red.

---

## What is proven, and how

**Every test was watched failing on purpose**, against a named mutation, with the red message
recorded as it actually printed. The two tables are in `worker-d-notes.md` (27 mutations) and
`worker-e-notes.md` (22 mutations). Two of them are worth lifting out because they are findings, not
ceremony:

> **A disabled button means the second assertion had no teeth.** Worker D asserted the empty-reason
> rule twice - the control is disabled AND nothing is sent - and only the first had teeth.
> `fireEvent.click` on a disabled button never fires `onClick`, so **removing the guard inside the
> send left the suite green.** Adding Enter, which a disabled attribute does not block, gave it teeth,
> and the same mutation now goes red on both surfaces. It also surfaced a real behaviour difference
> between the Cockpit and the phone that had nothing to do with layout.

> **A test that deadlocks under its own mutation cannot go red.** Worker E's "a second press while one
> is in flight sends nothing" awaited the second press. With the guard removed, that second call waits
> on the same outstanding answer the first is waiting on, and the run HUNG instead of failing. Found
> only because the mutation was actually run.

### A correction, recorded rather than quietly fixed

**The first draft of this report carried two literal placeholders, `PARKED_RUN_DEFAULT` and
`PARKED_RUN_RESULT`, in the rows for the local gate.** They were in an uncommitted draft written ahead
of a run that was still going, and the Architect caught them before it was committed - which is
exactly how Phase A's `PARKED_RESULT` got there, and the run Phase A's was hiding had caught a
cross-tenant isolation regression. It is recorded here rather than filled in silently, because a
placeholder in a test table is a check whose pass condition is nobody looking.

**Both runs completed.** The answer to the Architect's question is below, and every number in it I
read myself.

**The suites. Every row was run by me on the final commit `484ffe40`, except the parked row, whose
numbers I read out of its result files rather than taking the Worker's account of them:**

| Suite | Result |
|---|---|
| `@devthrottle/client-core` | 98 files, **1041 passed**, exit 0 |
| `@devthrottle/cockpit` | 36 files, **317 passed**, exit 0 |
| `@devthrottle/mobile` | 9 files, **55 passed**, exit 0 |
| `npm run typecheck` | all four workspaces clean, exit 0 |
| `.\scripts\test-local.ps1` | 8 suites **Completed, 0 failed**. `Gateway.UnitTests` produced NO result - stopped at the 120-second ceiling. See below; it is pre-existing. |
| `CcDirector.Gateway.UnitTests` alone, to completion | **4231 total, 4223 passed, 0 failed**, 8 skipped, 1 m 51 s |
| `.\scripts\test-local.ps1 -Parked` | **ELEVEN result files. 2 failures, both proven to be this host.** Full table below. |

**The parked run, read from its own result files** (`cc-test-local-2ede0a48`, finished 03:07):

| Suite | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| CcDirector.Gateway.Tests | 2438 | 2389 | **2** | 47 |
| CcDirector.Core.Tests | 4380 | 4372 | 0 | 8 |
| CcDirector.Gateway.UnitTests | 4231 | 4223 | 0 | 8 |
| CcDirector.Avalonia.Tests | 420 | 420 | 0 | 0 |
| CcDirector.Core.UnitTests | 227 | 227 | 0 | 0 |
| CcDirector.Engine.Tests | 63 | 63 | 0 | 0 |
| CcDirector.HostedAgent.Tests | 88 | 88 | 0 | 0 |
| CcDirector.Launcher.Tests | 188 | 188 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | 25 | 25 | 0 | 0 |
| cc-director-setup.Tests | 25 | 25 | 0 | 0 |
| cc-director-setup-engine.Tests | 541 | 541 | 0 | 0 |

**The two failures are the same two Phase A named, failing for the same proven reason.** I read their
messages rather than accepting the Worker's account:

    PathContainmentLinkEscapeTests.ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused
    PathContainmentLinkEscapeTests.ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused

    "This host cannot create a FILE symbolic link (A required privilege is not held by the client...).
     On Windows that needs Developer Mode or elevation. The link-escape regression cannot be proven
     without a real link, so this test fails loudly instead of silently skipping into a false green."

They test path containment, which this branch does not touch. They remain unproven in both directions
on this machine, exactly as Phase A recorded.

**AND THE THING THAT MATTERS MOST ABOUT THAT RUN: it found no regression.** Phase A's parked run caught
a cross-tenant isolation break that the default gate could not see. This one did not, and that is a
result rather than an absence - eleven suites reported, and the only two failures are a named host
limitation with its own explanatory message.

**One honesty about that run's timing.** It started at 01:41, which is BEFORE two later commits:
`015e5b0f` (a comment in `GatewayEndpoints.cs`) and `40ed8ae0` (a paragraph in `docs/VisualStyle.md`).
Neither changes any executable line. I did not re-run eighty minutes of parked suites for a comment and
a document, and I am saying so rather than letting "the parked run was green" quietly cover commits it
never saw. Everything executable in this phase was in the tree when it ran.

**`.\scripts\test-local.ps1` runs NO web tests at all**, so it says nothing whatever about three of
the six things this phase built. The three web suites above are the whole of that coverage, and they
are the reason I ran them myself rather than quoting a Worker.

### The served artifact, not just the source

Component tests prove a shell renders what a stubbed function returns. They say nothing about whether
the shell that a Gateway actually SERVES contains the new path. So I read both bundles back out of the
running Gateway (built from `c0764023`) and looked for strings only the new code has - and for strings
only the old code had:

| String | Cockpit | Phone |
|---|---|---|
| `Stop session` | 4 | 4 |
| `recorded with the stop` | 1 | 1 |
| `sessions/${...}/stop` | present | present |
| **`Close session`** (the old Cockpit wording) | **0** | - |
| **`close that session`** (the old shared function) | **0** | **0** |
| **`Remove session`** (the old phone wording) | - | **0** |

The bottom half is the half that matters most: the silent close is not still sitting in the shipped
bundle beside the new stop, which is what Ruling 5 forbids. **It is not a click**, and it says nothing
about whether the dialog opens or looks right.

---

## What is NOT proven, named as gaps

1. **No interface control has ever been used by a person.** Every stop in this report went through the
   command line. The Cockpit button, the phone sheet and the Director window dialog have component
   tests and a bundle check, and no photograph. That is the QA seat's work by design, and it is the
   largest single thing still open.
2. **No test drives a shell through the real `stopSession` to a real Gateway.** The hop is proven in
   client-core; the render is proven in the shells with the function stubbed. The join is proven by
   construction - both shells import the one function and nothing else calls the route - and by the
   bundle check above, and by nothing else. A wiring mistake between `SessionMenu` and the real
   function would not be caught by any test in this phase.
3. **Nothing proves the Director's rail row disappears.** That happens in `OnExternalSessionRemoved`,
   reached only when a real Gateway sends a real stop down a real tunnel. Worker E read the chain and
   it is intact; reading is not proving. It is the QA report's frame 6.
4. **No pixels are asserted anywhere.** Neither the dialog's compliance with the visual guide nor the
   phone layout at a phone width is verified by anything but eye.
5. **The local stack is single tenant, one machine, and one commit on both halves.** It says nothing
   about the hosted multi-tenant path, nothing about stopping across a machine boundary, and it cannot
   produce `stoppedNotDescribed` at all - that needs a Gateway from this branch talking to an older
   Director, which the recipe does not build.
6. **The actor in every live stop was a machine token**, not a session key. The path a session's own
   key takes through the allow list was not exercised on real infrastructure.
7. **`killed` and `removed` are carried on the shared type and read by nothing.** The contract listed
   them so they are there. If nothing ever reads them, a later reader will wonder why.

---

## Things found that the Architect should rule on or note

### 1. `docs/VisualStyle.md` does not cover the web surfaces at all. RULED ON, AND DONE.

It says `**Current framework scope:** WPF` at the top, and in 743 lines contains no occurrence of
"cockpit", "web", "css", "react" or "mobile". Its palette is WPF hex values; the browser shells use
CSS custom properties with light and dark variants.

Both Worker briefs said that guide governs every interface change. For the Cockpit and the phone it
cannot. What was done instead: match the existing component vocabulary in each shell exactly - the
Cockpit's `.session-dialog-*` family, the phone's `.confirm-*` family - using only tokens already
defined there. **That is a judgement, not a rule being followed**, and it deserves a reviewer's eye. The Director
window's dialog IS drawn to the guide, which does cover it.

**The Architect confirmed the judgement and ordered the trap closed** (`40ed8ae0`): `docs/VisualStyle.md`
now carries one short paragraph at the top saying the browser shells are governed by their own token
sets, naming where those live (each shell's `styles.css`, and `client-core/src/settings/` for a control
that must appear on both), and giving the rule - use the tokens already defined in that shell and match
the nearest component family. **The guide is deliberately NOT extended to the web**; that is a separate
piece of work. This note exists only so that nobody applies a rule for one framework to another, or
believes a web change was checked against a guide that never described it.

### 2. A commit that is not this mission's, kept deliberately and separable. RULED ON: KEEP.

`253aa584` fixes four pre-existing Cockpit roster test mocks. Without it **the Cockpit suite exited
non-zero while all 314 tests passed** - the restart-requests panel polls inside the roster those files
render and reaches for an export the mocks did not have, so every poll threw an unhandled rejection:
18 errors, exit code 1, everything green.

**I kept it, and here is the reasoning.** This phase's only coverage is those web suites, and a suite
whose exit code cannot be read is a check that fails open. Reporting "the Cockpit suite is green"
while it exits 1 would be exactly the shape of claim this mission exists to remove. It is four
one-line additions, it is in its own commit, and it can be dropped on its own. Proven rather than
assumed: one file's fix removed exactly its three errors, and all four took the run from 18 errors and
exit 1 to none and exit 0, with the same tests passing.

**The Architect confirmed it stays**, on the mission's own principle: a suite whose exit code cannot be
read is a check that fails open, and reporting "the Cockpit suite is green" while it exits 1 is
precisely the claim this mission exists to remove.

### 3. Two latent faults, FILED as issue #2780 rather than fixed - and one Worker claim corrected

Per the Architect's ruling, these are filed rather than left in a mission record nobody re-reads:
**https://github.com/thefrederiksen/devthrottle/issues/2780**

- **A hand-written `InitializeComponent` may leave every named control null.** Worker E copied
  `private void InitializeComponent() => AvaloniaXamlLoader.Load(this);` into its new dialog. It
  compiles, every `x:Name` field was null at run time, and the first headless test threw a
  `NullReferenceException` in the constructor. Deleting the line and letting the XAML compiler generate
  the method fixed it.
- **`TextBox.TextChanged` does not fire for a programmatic set in the headless test host.** A control
  gate hung on that event looks tested and is not - the one test that exercises it fails while every
  other test in the file passes. Worker E's is hung on the `Text` property through `PropertyChanged`.

**A Worker claim I checked and corrected before it reached the issue.** Worker E wrote that
"`DrainDirectorDialog`, `SpeakDialog` and several others carry the same line". They do not.
**Exactly one** file under `src/` carries that pattern - a hand-written `InitializeComponent` on a
`Window` shadowing the generated one - and it is `DrainDirectorDialog.axaml.cs:55`. The four
`AvaloniaXamlLoader.Load(this)` calls elsewhere are `Application.Initialize()` overrides in
`App.axaml.cs` files, which is the normal correct pattern in classes that declare no named controls.

And the issue does **not** claim that dialog is broken, because I did not check it. It declares six
named controls and touches one in its constructor, so it is at risk on the face of it - but it is a
shipped feature, which is some evidence it does not fail, and if it does not then the real rule is
subtler than "this line breaks named controls" and is worth understanding before anything is changed.
The issue says exactly that and names the one observation that would settle it: open the dialog once.

### 4. One judgement call on wording. CONFIRMED as it stands.

The Cockpit and the phone say `Stop session`; the Director window says `Stop Session`, Title Case,
because every other entry in that menu is Title Case and a single lower-case one reads as a mistake.
The WORD is the same on all three, which is what Ruling 5 is about; only capitalisation follows each
surface's own convention. If the Architect wants them identical to the letter it is one string.

### 5. Two facts about this machine that will bite the next seat

- **The installed command line predates this mission.** `cc-devthrottle` on this machine is from 23
  July 2026 and answers `No such command 'stop'`. The QA seat will hit this. The recipe says how to run
  the branch's command line without installing over the owner's tools, which is what must NOT be done.
- **A Debug publish of the Gateway ships no Cockpit and no phone at all**, silently - it starts, serves
  the fleet and answers healthz, and `GET /mobile` is a 404. That would have cost the report two of its
  frames. The recipe now publishes in Release and verifies both `index.html` files exist.

### 6. The Gateway unit suite is stopped by the budget - not this branch, and NOT by a factor of two

`.\scripts\test-local.ps1` reports `OVER BUDGET` on `CcDirector.Gateway.UnitTests` and records no
result for it. That is pre-existing: Phase A measured it against a clean `origin/main` extract, and
Worker E measured it here with its nine new tests moved out of the tree - same verdict, 4,214 tests.
Its nine tests take 502 milliseconds and cannot be what put a two-minute ceiling out of reach.

**I checked the Worker's characterisation of it and it is wrong, so I am correcting it rather than
forwarding it.** Worker E wrote that the suite is "over the budget by a factor of two", from runs of
3 m 57 s and 3 m 11 s. Run **alone on a quiet machine** it took me **1 minute 51 seconds - inside the
ceiling.** Both observations are real and together they say something more useful than either: the
suite is *borderline*, and what pushes it over is the gate's own parallel load, not its own duration.
So "make it fit" may be a smaller job than the Worker's number suggests, and whoever owns the budget
should have the accurate figure. It is still not this branch's to fix.

---

## Nothing contradicts a ruling

Both Workers looked and neither found anything in Rulings 1 to 6, the handoff, or `SessionStopDtos.cs`
that the code contradicts. One thing worth knowing before a screenshot is read as a mismatch: on the
Cockpit, a RETRYABLE failure reaches the dialog through `describeAndReport` -> `gatewayErrorMessage`,
which prefers the server's reason and appends "Try again." to it. The Gateway's words are not altered,
but on that one path they are not alone either.

---

## Housekeeping

- Both Workers were held until their work was verified, then flagged for deletion.
- The mission record is committed on the branch: the two Worker briefs, the two Worker notes, the
  local stack recipe, and this report.
- **The local stack is torn down, and the teardown half of the recipe is therefore proven too.** The
  test Director was **signalled, not killed** - `Local\cc-director-shutdown-8c303ef0...` - and exited
  cleanly, so it left no phantom "interrupted" journal entry. Its scheduled task is unregistered, the
  Gateway is stopped, and the named-instance registry is restored byte-for-byte from the backup the
  recipe takes at step 4. No slot 6 process and no slot 6 task remain.
- **Nothing of the owner's was touched, and it is checkable rather than asserted.** The owner's
  Director is process **1328** and the installed Gateway is process **2692**; those are the same two
  process ids at the end of this phase as at the start of it. No `taskkill`, no
  `Stop-Process -Name cc-director*`, and `scripts\redeploy-gateway.ps1` - which would have published
  over the owner's installed Gateway - was never run. The only force-stops were two processes whose
  image paths I confirmed were my own, under `C:\ReposFred\devthrottle-stack\`.
