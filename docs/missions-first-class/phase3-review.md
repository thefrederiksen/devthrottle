# Missions First-Class - Phase 3 review (the skills teach the flag)

Reviewer: a separate review session, 6 November 2026. Pull request 4685, branch `missions-first-class-phase3`,
head 7fe977b8f, merge base 6144c8fef on origin/main. I read and did not edit the worktree.

## Scope

What I read:

- The pull request diff (`git diff origin/main...HEAD`): three files, 147 lines added, nothing removed.
  `src/CcDirector.Gateway/Skills/Content/dev-reports.skill.md`, `devthrottle-method.skill.md`, and the new
  `docs/missions-first-class/phase3-proof.md`.
- Both skill files in full on the branch, so the new text is read in its surroundings.
- The mission document (sections 4, 5, 6), the phase 3 Developer brief, `docs/missions-first-class/phase1-proof.md`.
- The phase 1 code on origin/main, not the proof's description of it: `MissionDocumentLinker.cs` (the refusal text,
  the permission rule, the republish refresh), `DevReportPublish.cs` (the order of refuse, write, link),
  `DevReportShapeCheck.cs` (the why and goal rules), `DevReportStore.cs` (what makes two publishes the same
  report), `DevReportInheritance.cs` (the one case a report passes to another session), the `cc-dev-reports` tool
  (`cli.py`, `reports_ops.py`: the flag name and what the output prints), and `cc-devthrottle`'s mission commands
  (`cli.py`, `mission_ops.py`: `mission create`, `mission attach <session> <mission>`).
- The repository `CLAUDE.md` rule "A BUILT-IN SKILL IS CHANGED IN ONE PLACE", `.claude/skills/` on the branch, and
  the state of pull requests 4619 and 4685 on GitHub.

What I ran: a byte scan of every added line for characters outside ASCII (none found); `git diff` of `CLAUDE.md`
against origin/main (unchanged); a listing of `.claude/skills/` (no `dev-reports` and no `devthrottle-method`
directory exists, and none was added). I did not run the test gate; the proof's gate result is the Developer's claim
and I did not repeat it. I did not publish a report with the flag against a live Gateway.

## What is true to phase 1's code

Checked against origin/main, each one holds:

- The flag is `--mission-document` on `cc-dev-reports open` (`cli.py`), sent as `missionDocument: true`.
- The markers are `data-dev-report="why"` and `data-dev-report="goal"`, each optional, at most one, after the
  questions section (`DevReportShapeCheck.cs`); the existing sentence "Sections may not contain other sections"
  covers the no-nesting rule, and "evidence last if present" still orders them before the evidence.
- The Mission is the publishing session's own; the request names no Mission (`MissionDocumentLinker.Resolve`).
- The refusal: the skill's quoted words "This session is attached to no Mission" are the start of the refusal
  sentence, "Nothing was published" is in it, the attach command in it is exactly
  `cc-devthrottle mission attach <session> <mission>`, and the "wait a few seconds" advice is in it. The refusal
  happens before the report is written (`DevReportPublish.Run`).
- One document per Mission; a different report takes the link over and the output prints `replaced` with the old
  report id (`reports_ops.render`). A missing goal section is printed as a note and the goal shows as `(none)`.
- A republish without the flag of the report that is the Mission's document refreshes the why and goal
  (`RefreshIfDocument`), and the tool prints the mission document block for it too.
- Nothing else in either skill changed: the diff is additions only, one command line, two table rows, one section,
  one bullet. The built-in skill rule is kept: one source each, no repository copy, `CLAUDE.md` untouched.
- The new text is plain English with no abbreviations, and ASCII throughout.

## Findings

### 1. "Republishing the same report" and "refreshes them, with or without the flag" are true only from the session that published it, and the skill does not say so

The new section says: "Republishing the same report updates the same link" and "A republish of the Mission's
document refreshes them, with or without the flag." Both rest on the republish being the SAME REPORT, and a report
is keyed on (session, file path) - `DevReportStore.PublishOnce` finds it by `SessionId` and `Key`, and the skill's
own line 15 says "publishing it from a later session makes a new report". The one exception, `DevReportInheritance`,
passes reports only on a Director restart restore, never on a handover between seats.

So from any OTHER session on the Mission, publishing the same file without the flag makes a new report,
`RefreshIfDocument` compares the Mission's `DocumentReportId` with the new report's id, finds them different, and
returns null: nothing is refreshed, no mission document block is printed, and the Mission button keeps opening the
older report with the older goal.

**What breaks:** this mission's own flow. The Architect publishes the document and shuts its seat down (the method,
"If you are the Architect"); the Delivery Lead, a different session, is the one who republishes it (the mission
document, phase 3: "This report is republished as the first linked mission document"; the proof: "the real
republish is the Delivery Lead's"). A Delivery Lead that reads "with or without the flag" and republishes without it
leaves a stale goal behind the Mission button - the very defect that phase 1's review finding 4 was fixed for.

**Why it must change:** one sentence in the mission document section of `dev-reports.skill.md`, something like: "The
refresh without the flag holds only for the session that published the document, because a report belongs to the
session and the file. From any other session on the Mission, publish WITH the flag: that makes a new report, which
takes the link over, and the output says which report it replaced." The method bullet can stay as it is.

### 2. Neither skill names the step that makes "a session attached to the Mission" true for an Architect

The method bullet says to publish "from a session attached to the Mission", and the dev-reports section says to
"attach the session first (`cc-devthrottle mission attach <session> <mission>`)". Neither says that the Mission
record must exist first, nor how it comes to exist. An Architect is opened by the owner, usually before any Mission
record exists for the work (the mission document counts 18 of 36 sessions with no Mission), and
`cc-devthrottle mission create "<name>"` is the only way to make one; `mission create` does not attach the creating
session, so the attach of the Architect's OWN session is a second step.

**What breaks:** the task asks whether the two skills would lead an Architect to a linked mission document. As
written they lead it to the refusal, which names `mission attach` but needs a Mission id the Architect does not have
and is not told how to get. The Architect then has to find `mission create` on its own (it is in the `fleet-naming`
skill and in `cc-devthrottle mission --help`, so it is findable, which is why this finding is smaller than the
first).

**Why it must change:** the method bullet, or the dev-reports section, should carry the two commands in order:
create the Mission (`cc-devthrottle mission create "<name>"`), attach your own session to it
(`cc-devthrottle mission attach <your session> <mission id>`), then publish with the flag. One sentence is enough.

## Observations, no change required

- **The Mission button is not shipped yet.** Both skills say every session on the Mission gets a Mission button that
  opens the document. Pull request 4619 (phase 2, the button in the Director) is still open as of this review. The
  proof says so plainly and asks that this pull request merge after 4619. The sentence is a promise to the owner, not
  an instruction an agent acts on, so it does no harm in the window; the merge order is the Delivery Lead's call.
- **The why is not re-stamped when the document has no why section**: the Mission keeps a hand-set why
  (`MissionStore.SetDocument`), and the output prints a note saying so. The skill says the Gateway "reads them and
  stamps them on the Mission", which is true when both sections are present and does not claim the why is cleared.
  Not false; left as it is.
- **Outside this mission:** the method skill's Reviewer line in section 2 still says "Pi on GLM-5.3", which the owner
  replaced with a fixed order of separate review sessions (ruling 28 September 2026, the global `CLAUDE.md`).
  The brief rules that stale text out of this change, so it is noted here for the Delivery Lead, not raised as a
  finding. The fleet preamble (`FleetPreambleTemplate.cs`) also shows `cc-dev-reports open <file.html>` without the
  flag; that is code, and out of this phase.

## Verdict

Two findings, the first one material. Finding 1 is a true sentence that reads as a general rule and is not one, and
the seat most likely to act on it (the Delivery Lead republishing the Architect's document) is exactly the seat it
misleads. Finding 2 is a missing step that an Architect can recover from by searching. Everything else the task asked
me to check holds: the flag name, the markers, the refusal, the republish refresh, the permission rule, the built-in
skill rule, plain English and ASCII, and nothing else in the skills changed.

## Re-check of 23ab8abaa

Checked out `23ab8abaa` (detached) after `git fetch origin`. Two commits since the reviewed head 7fe977b8f:
`6d693e73e` changes the two skill files and the proof; `23ab8abaa` changes only the proof (21 lines, the gate
result). The skill changes are additions and two reworded sentences, no removals of anything reviewed before. No
character outside ASCII in any added line. No `.claude/skills` copy was created, `CLAUDE.md` is still untouched.

Each new passage, checked against the phase 1 code on origin/main as before:

### Finding 1 - ACCEPTED, and the new text is true to the code

- "The same report means the same session and the same file": `DevReportStore.PublishOnce` finds the report by
  `SessionId` and `Key`, so this is exactly the rule.
- "From any other session on the Mission ... publish WITH the flag: that makes a new report, which takes the link
  over": `DevReportPublish.Run` with the flag resolves that session's own Mission, writes the new report, and
  `MissionDocumentLinker.Link` calls `MissionStore.SetDocument`, which replaces the previous document and reports
  the replaced id. True.
- "Without the flag it is an ordinary new report, and the Mission button keeps opening the old one":
  `RefreshIfDocument` compares the Mission's `DocumentReportId` with the new report's id, which differs, and returns
  null; the Mission record is untouched and `MissionButtonFold` keeps the old address. True.
- "A republish of the Mission's document from the session that published it refreshes them, with or without the
  flag": `RefreshIfDocument` needs the publishing session attached to the Mission and the report id to be the
  Mission's document; from the publishing session the id matches. True. The one case where a different session
  holds the same report id is a restart restore (`DevReportInheritance`), which the sentence does not cover and
  does not need to: that session then behaves as the publishing session, so the sentence is narrower than the
  code, never wider.

Verdict: closed. Nothing new that proves harm.

### Finding 2 - ACCEPTED, and the commands are the real ones

- `cc-devthrottle mission create "<name>"`: `cli.py` declares one argument, the name; `mission_ops.create_mission`
  prints the id and a next-step list that includes `mission attach`, and attaches nothing. "Prints its id and does
  not attach you" is true.
- `cc-devthrottle mission attach <your session> <mission id>`: `cli.py` takes a session (number, id prefix, or
  part of its name) and a Mission (id, prefix, or part of its name). The tool's `attach_session` and the Gateway's
  `POST /sessions/{sid}/mission` route refuse only an unknown Mission and a tenant boundary; neither refuses a
  session attaching itself, so "your own session" is a thing the Architect can do.
- The order in the method bullet (create, attach your own session, publish with the flag) matches the order the
  code needs: `Resolve` reads the attachment from the Gateway's own records before anything is written.
- The "wait a few seconds" advice is kept and still matches the refusal text.

Verdict: closed. Nothing new that proves harm.

### The gate on the final commit, for the Delivery Lead

The proof says two runs of `.\scripts\test-local.ps1` on `6d693e73e` ended OVER BUDGET (exit 1), not red: no test
failed, `CcDirector.Avalonia.Tests` and `CcDirector.Core.UnitTests` were stopped at the 120-second ceiling on a
loaded machine, and both passed alone with the gate's own filter (3392 and 1215 tests, 0 failed). The script's own
words on that verdict are "This is NOT a test failure. It means the suite no longer belongs in the default run."
So no single gate run on the final skill commit reports GREEN, and the suites it stopped are not ones this change
can reach (the change is two markdown skill files; the skill tests live in the parked Gateway suite, which the
proof ran by hand on the first commit). That is a statement of what the record holds, not a finding against the
text: whether a loaded-machine OVER BUDGET with every suite passing is enough to merge is the Delivery Lead's call,
and I did not re-run the gate myself.

### Verdict on 23ab8abaa

Both findings accepted and closed; the new sentences are true to phase 1's code, in plain English, ASCII, and
change nothing else in either skill. Nothing new found that proves harm.
