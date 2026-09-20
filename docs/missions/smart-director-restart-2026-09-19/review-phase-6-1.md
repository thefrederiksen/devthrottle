# Review - Smart Director Restart, phase 6: the retired skill and the public page

Written by the Reviewer seat opened by the Delivery Lead for phase 6, on 20 September 2026. It reads work
it did not write. It is advice, not a command: the seat that built the work decides what happens to each
finding.

## Scope

**What I reviewed:** `git diff 8fcda423b HEAD` on branch `smart-restart/p6-retire` (commit `74aa3e6eb`) -
eight files: the published skill body and its metadata, the proof, the mandate, the public page
`docs/public/features/10-smart-restart.md`, and its wiring in `docs/public/index.json`,
`docs/public/features/01-overview.md` and `docs/features/feature-inventory.yaml`.

**Freshness:** `origin/main` is `8fcda423b`, exactly the base of this branch, so every code file in this
worktree is identical to `origin/main`. Every claim below was checked against `origin/main` with
`git show` and `git grep`, never against a memory of it.

**What I read:** the engine and its contracts (`src/CcDirector.ControlApi/SmartRestart/`:
`ISmartShutdown.cs`, `ISmartShutdownRun.cs`, `DirectorSmartShutdown.cs`, `DirectorWayUp.cs`,
`SmartShutdownWords.cs`; `src/CcDirector.ControlApi/Drain/`: `DirectorDrain.cs`, `DrainPaths.cs`), the
screens (`src/CcDirector.Avalonia/SmartRestart/`, the File menu and `OnClosing` in
`src/CcDirector.Avalonia/MainWindow.axaml.cs`), the facts behind the by-hand section
(`PendingInteraction.cs`, `CodexDriver.cs`, `PiDriver.cs`, `CcStorage.cs`, `SessionKeyGuard.cs`,
`WorkspaceValidation.cs`, `tools/cc-devthrottle/src/cli.py`), and the mission document, the mandate, the
proof and the phase 1, 2 and 3 proofs.

**What I ran, all in the foreground, all read-only against the fleet:**

- `cc-devthrottle skill get director-restart`, diffed against the committed attachment
  `attachments/phase-6/director-restart-skill-v5.md`: **identical, line for line**. What the fleet reads
  is what was committed.
- `cc-devthrottle director list --fields id,name,machine,version,state`: three Directors, all on 2.8.1
  or 2.7.0 - the skill's "which Directors have it" section is true today, not just when it was written.
- `GET /gateway/workspaces` with this session's key: returned `restart-20260919-2040-devthrottle-1`, the
  record shape the skill describes.
- Tags: newest is `v2.8.1`; `git tag --contains` of both merge commits the proof names is empty. The
  feature is in no release, as both documents say.
- `scripts/check-inventory-drift.ps1`: OK, 33 source paths and 7 pages, exit 0 - the same numbers as the
  proof.
- The mission's own check: `dotnet test src/CcDirector.Gateway.UnitTests --filter
  "FullyQualifiedName~Drain|FullyQualifiedName~Restart"` - **606 passed, 0 failed**; and
  `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~RetiredMessagingWords"` -
  **5 passed**. Both match the proof exactly.
- An ASCII sweep of all eight changed files (clean; the inventory file's byte-order mark is pre-existing)
  and an attribution sweep (no agent or vendor names in the page, no "Co-authored-by", no "Generated
  with" anywhere).

**What I could not reach:**

- I did not run the feature. No Director in the fleet has it, and the quality assurance phase has not
  run; every sentence I checked describes what the code does, not what anyone has watched it do. The
  skill says so itself, plainly.
- The hosted Gateway's version is unknown to me: a session key is refused on the version and health
  routes. I could not confirm the deployed Gateway accepts the new drain-state marks. The skill warns
  about this rather than claiming it is fine, which is the right way round.
- The retired version of the skill is not in the repository, so the proof's account of what was cut is
  taken on the evidence of the live v5 body and the mission record, not on a diff of the two bodies.
- I did not run the parked suites, the web tests or the Python tests. Nothing in this diff touches them;
  the command-line claims were verified by reading `cli.py` on `origin/main` and running the two
  listing commands above.

**What checked out, claim by claim.** I verified against `origin/main`, among others: the File menu item
and the window close as the two doors; that with no sessions the close just closes and the File menu says
so and does nothing (the v5 correction is in the live body); the three choices and their exact button
titles; the time dropdown of 5, 10, 15, 30 and 60 minutes with 10 as the default; the confirm button dead
until the engine answers, refusals shown in the engine's own words; the Gateway unreachable refusing a
smart shutdown and a development slot refusing a restart; the two-thirds interrupt and the limit ending
every session still present, recorded with its conversation id; "Shut down now" and "Cancel and keep
working", including the bring-back against the same Director; the launcher asked only from the File menu
door; the operating-system shutdown writing the record and asking nothing; the handover directory and
file names and the workspace id shape; the way-up engine merged with no caller anywhere in the
application (no screen, no start-up check, no history window); the absence of a command-line door; the
by-hand facts (no conversation resume for one agent, no safe interrupt for another, the question-box
property never populated today); the record's fields as the page lists them; and the data-folder paths
for Windows and Linux. The page is written for a user: it names no session ids, no commands, no
repository paths, and its words for the row states and the count are the engine's own.

## Findings

### 1. The public page describes a feature no released build has, and says so nowhere - the one harm this review was opened to hunt

`docs/public/features/10-smart-restart.md`, from line 13 on: "**File, Smart Restart** shuts the sessions
down and then restarts the Director." Present tense, no version anywhere on the page, and none in the
two overview rows or the inventory entries that point at it.

The feature is merged on `origin/main` and is in no release: the newest tag is `v2.8.1`, and `git tag
--contains` of both feature merge commits is empty. Every live Director is on 2.8.1 or 2.7.0 (checked
today). The public documentation is served as raw markdown from GitHub, from `main`, with no build step
- so the page goes public the moment this merges, telling every reader to use a menu item their build
does not have.

The skill - the document for agents - handles the same fact with a whole section: which Directors have
it, how to check, what an older build shows instead, and what to do about it. The page - the document
for people, who have no way to check a Director's build by hand - carries nothing. A user on the current
release reads the page, opens the File menu, and the item is not there; nothing on the page lets them
reconcile that, so the documentation looks wrong or the feature looks broken.

**Why it must change:** this is the exact harm the mandate named - a document that tells a person to use
a menu item their build does not have. One sentence fixes it: the feature is not in a released version
yet and arrives in the next one. The sentence can come off the page the day the release ships.

### 2. Both documents promise the record is written on "Shut down and ignore all sessions"; the code deliberately does not write it when the Gateway cannot be reached, and the owner is never told

`docs/public/features/10-smart-restart.md`, line 36: "What was running is still written down first, so
you can see afterwards what was closed." The skill, line 44 of
`attachments/phase-6/director-restart-skill-v5.md`: "the record is written first, then everything is
ended at once". Both are unconditional.

The code is not. `ISmartShutdown.ShutDownIgnoringAllAsync` (`src/CcDirector.ControlApi/SmartRestart/
ISmartShutdown.cs`) says plainly: "It works with the Gateway unreachable too - then RecordWritten is
false ... and the sessions are still ended, because the owner chose to discard them." And the owner is
not told: `SmartShutdownCoordinator.RunDoorAsync`, the ignore-all branch, writes the refusal only to
the log ("the record was NOT written"); the sentence shown to the owner from the File menu says only
how many sessions were ended and that the Director was not restarted. The record's absence is silent.

The page sets up the very case itself, two paragraphs earlier: the most common refusal is that the
Director "cannot reach the Gateway, where the record has to be kept. The other two choices still
work." For the ignore-all choice, "work" means the sessions end and the record - the thing the page
promises so you "can see afterwards what was closed" - is silently not written. A user who chooses
ignore-all while offline is promised a record that does not exist, and finds out only by its absence.

**Why it must change:** the sentence promises behaviour the code deliberately does not have, in the one
case the page itself names as the most common. Either the sentence carries its condition (the record is
written when the Gateway can be reached, and there is nothing to see if it could not be), or the product
tells the owner when the record was not written - as it stands, the documentation and the product agree
with each other everywhere except here.

### 3. The page's handover folder path omits the run mark, so the shape it shows matches no real directory

`docs/public/features/10-smart-restart.md`, line 79:
`<data folder>/vault/handovers/director-restart/<when>-<director name>/<session>.md`.

The code builds `<timestamp>-<run mark>-<director name>` (`DrainPaths.DirectoryFor`,
`src/CcDirector.ControlApi/Drain/DrainPaths.cs`): a short unique mark sits between the timestamp and the
name, on purpose, so a cancelled run and a re-started one can never share a directory. The skill states
the path in full and correctly, line 68. So the page disagrees with the code and with the skill at once.

A user who reads the page to find their handovers meets directories like
`2026-09-20T101030-a1b2c3-DevThrottle_1` - matching no part of the pattern shown - and cannot tell what
the middle segment is or whether they are in the right place. Minor, and one line to fix: add the mark
to the pattern (or fold it into `<when>` with a word saying the folder name also carries a short unique
tag).

### 4. The page says bringing the sessions back is a separate step, then never says what the step is - and in this version the application offers no way to do it

`docs/public/features/10-smart-restart.md`, lines 89 to 90: "the sessions **do not come back by
themselves** ... Bringing them back is a separate step, and the record is what it works from."

That is true, and the honesty is right. But the page never says what the step is, and in this version
there is no way to take it from the application: the way-up engine is merged with no caller (verified -
no screen, no start-up check, no history window on `origin/main`), so the only door is the
`cc-devthrottle` command line, which the page never names. The page's opening sentence promises "the
work can be picked up again" (line 5); a reader who wants that pick-up can learn neither from the page
nor from the app how to get it. The command line tool is already a public, documented part of the product
(it has its own section in the public Tools documentation), so naming it costs the page nothing.

**Why it must change:** an action named with no way to take it is a dead end on a page whose whole
"Afterwards" section exists to prevent one. One sentence - naming the tool, or saying plainly that in
this version it is done with the DevThrottle command line tools - closes it.

## On the proof

The proof is self-testimony and I did not trust it; I re-ran its checks and re-read its sources. Every
check it claims matches what I got: 606 and 5 tests, the drift numbers, the tag facts, the identical
published body. Every source it names that I opened said what it says. Its "what I could NOT reach"
section is honest and matches what I too could not reach. The two sentences it lists as cut because they
could not be sourced ("the sessions are offered back on the next start" and anything about a restart
history) are genuinely unsupportable on `origin/main` today, and cutting them was right: the mandate
itself carried the first one, and the proof says so.

END OF REVIEW
