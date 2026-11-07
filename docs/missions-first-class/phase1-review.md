# Missions First-Class - Phase 1 review

Reviewer: a separate review session, 6 November 2026.
Reviewed: commit 50c472c88 on branch `missions-first-class-phase1`, pull request 4605, read in the worktree
`D:\ReposFred\devthrottle-missions-p1-review` as `git diff origin/main...HEAD` (origin/main at 19f42f2f1).
The pull request head is one commit further, 07ceb9a23, which adds only `docs/missions-first-class/phase1-proof.md`;
I read that file too.

## Verdict

No defect that breaks the three verdicts, the section extraction, the link, its permission rule, or persistence.
The design decisions in the mission document (sections 4 and 5) are implemented as written. Eight findings follow:
two must change before merge (1 and 2), one is a sequencing decision for the Delivery Lead (3), one is a
correctness gap the owner should rule on (4), and four are small (5 to 8).

## Scope: what I read, what I ran, what I could not reach

Read, in full: the mission document sections 4, 5 and 7, the Developer's brief, every file in the diff, the full
`MissionStore.cs`, the publish route and its `OwnSession` check, `SessionKeyGuard`'s rule for the dev-reports
route, the attach route's shape, `DevReportLinkRoute` (the `/r/` address), the shape check's order and nesting
rules, and the proof file on the pull request.

Ran, in this worktree, nothing edited:
- `dotnet test` on `CcDirector.Gateway.UnitTests` filtered to `MissionButtonFoldTests`, `MissionDocumentLinkerTests`,
  `MissionDocumentSectionsTests`, `DevReportShapeCheckTests`, `DisplayFoldTraceMatchesPushTests` and the
  `FleetDisplayStateObserver` tests: 132 passed, 0 failed.
- `dotnet test` on `CcDirector.Core.UnitTests` filtered to `MissionStoreHeldReadsTests`: 7 passed.
- `tools/cc-dev-reports` tests: 58 passed.
- `tools/cc-devthrottle` mission tests (`test_mission_list_axi.py`, `test_mission_lifecycle.py`,
  `test_free_text_escaping.py`): 324 passed with click 8.5 installed into a scratch folder. With the machine's
  click 8.1.8 they fail 197 times because that click mixes standard error into standard output under the test
  runner; the proof file already states the suites need click 8.2 or later, and I confirmed the same files fail
  the same way from origin/main. Not a defect of this change.
- A byte scan of every changed source file for characters outside ASCII (finding 1 comes from it).

Not reached: the full `-Parked` gate (the proof file reports it green at 50c472c88; I did not re-run 90 minutes
of tests), a live Gateway or Director (phase 1 renders nothing), and the hosted Gateway's shared `missions.json`.

## Findings

### 1. A non-ASCII character sits inside a regular expression in `MissionDocumentSections.cs` - must change

`src/CcDirector.Gateway/DevReports/MissionDocumentSections.cs` line 41:

```
private static readonly Regex Whitespace = new(@"[ \t\f\v ]+|\r", RegexOptions.CultureInvariant);
```

The character class holds a literal no-break space (U+00A0) after `\v`. It is invisible in a diff and in most
editors, so the next person who reformats the line, or a tool that normalises whitespace, removes or replaces it
without anyone seeing, and the regular expression silently stops collapsing no-break spaces. The repository's
rule is ASCII only in every file, and this is exactly the case the rule exists for. Write it as `\u00A0` inside
the verbatim string (`[ \t\f\v\u00A0]+`), which the .NET regular expression engine reads as the same character,
and the intent is visible.

### 2. The command-line reference and the tool readme still describe the old flags, fields and keys - must change

The brief's rule for `cc-devthrottle` is the AXI standard, and the repository's `CLAUDE.md` tells every agent to
read `docs/cli-reference.md` for the exact flags before calling a tool. Neither document was touched:

- `docs/cli-reference.md` lines 952 to 954 say the default fields are `id,name,state` and the valid fields are
  `id, name, state, why, why-updated, state-changed, run`. The default is now `id,name,state,document` and the
  valid list has `document` and `goal`. The example output on lines 960 to 972 shows three columns and the old
  help lines. The broken-row rule on lines 980 to 985 does not name `documentReportId` or `goal`.
- `docs/cli-reference.md` line 2764 and `tools/cc-dev-reports/README.md` lines 8 and 20 document
  `cc-dev-reports open <file> [--json]` with no `--mission-document`, and list the `--json` keys without
  `missionDocument`, which is now in `RESULT_KEYS` for every command.

An agent that follows the house rule and reads the reference will not find the flag this mission exists to
introduce, and a parser written from the documented key list will be wrong. Update both documents in this pull
request; the phase 3 skill text is a separate thing and does not replace the reference.

### 3. `mission list` now fails outright against every Gateway that predates this change - a sequencing decision

`_mission_document` in `tools/cc-devthrottle/src/mission_ops.py` exits 1 when a row has no `documentReportId`,
and `_require_mission_rows` runs every checked reader on every row before anything is shown, so this happens
even for `--fields id,name` and for `--json --name`. That follows the tool's existing rule (a `workflowRunId`
missing from a row is refused the same way, and the `BROKEN_FIELDS` test pins it), so it is not a code defect
and I do not ask for a fallback.

What breaks is the order of shipping. The tool travels with the Director release; the Gateway that sends the
field is deployed in phase 3. Any Director release cut between this merge and that deploy puts a `mission list`
on every machine that exits 1 with "this Gateway predates mission documents and needs updating", and section 7's
own check (`cc-devthrottle mission list shows this Mission with a document`) cannot be run. The Gateway side of
this change is additive and safe to deploy alone (the mission document's merge plan says so), so the Delivery Lead
should deploy the hosted Gateway right after this pull request merges, not wait for phase 3, and say so in the
pull request.

### 4. A republish without the flag leaves the stamped goal and why stale while the button opens the newer document - owner ruling

The publish route reads the why and goal only when the body carries `missionDocument: true`. A report that is
already a Mission's document, republished the ordinary way (`cc-dev-reports open mission.html`, which is what every
agent has typed until now and what the dev-reports skill still teaches until phase 3), gets a new version and the
Mission keeps the old goal. The Developer disclosed this under "Not proven here".

Why it must change, or be ruled on: the fold stamps the goal on the session as a Gateway truth and the client
renders it verbatim (rule 7). From that moment the tooltip says one goal and the document one click away says
another, and nothing on the Gateway notices. The Gateway can tell without any flag: when `report.Id` equals the
`DocumentReportId` of the publishing session's own Mission, that report IS the mission document, and re-reading
the two sections needs no new permission (a report id belongs to one session, and that session is the one
attached to the Mission). Proposed: in the publish route, after `PublishAs`, if the flag was not passed and the
session's Mission has `DocumentReportId == report.Id`, call the same `Link` and carry the same `missionDocument`
block in the answer. If the owner prefers the flag to stay the only trigger, the answer should at least warn:
"this report is Mission X's document; its goal was not refreshed because --mission-document was not passed".

### 5. The no-Mission refusal can be wrong for a few seconds after `mission attach` - small

`DevReportSessionMission` in `GatewayHost.cs` reads the pushed roster row's `MissionId`, then the history row.
As I read the attach route (`POST /sessions/{sid}/mission`), it validates the Mission and hands the change to the
Director, which applies it and pushes the row back; the Gateway's copy changes on that push. An agent that runs
`cc-devthrottle mission attach` and then `cc-dev-reports open --mission-document` in the next breath is told
"This session is attached to no Mission ... Attach this session to its Mission first", which it has just done.
The instruction sends it round again. Either make the refusal honest about the window ("an attachment made a
moment ago may not have reached the Gateway yet; wait a few seconds and publish again"), or record the attachment
on the Gateway's own row when the attach route accepts it. I did not reproduce this live; it is read from the
code, so confirm the window exists before changing the text.

### 6. The button's address is built from a second copy of `/r/` - small

`MissionButtonFold.For` writes `$"/r/{reportId:D}"`. The one source of that address is
`Api.DevReportLinkRoute.Prefix` in the same assembly, and the link route's remarks explain why the address is
exactly one segment after that prefix. Use the constant, so a change to the route cannot leave every Mission
button pointing at an address the Gateway no longer serves.

### 7. The publish route's ordering is untested, and it is the one property the brief demanded - small

The brief asked that a session with no Mission "gets a clear refusal, never a silent no-op". The refusal itself
is tested on `MissionDocumentLinker.Resolve`; what is not tested is that the route asks `Resolve` BEFORE
`PublishAs` (so nothing is written) and answers 409 with `mission_unknown` when the Mission vanishes between
`Resolve` and `Link`. The proof file says no suite hosts the dev report routes, and I confirmed it: the only tests
naming `/dev-reports` are the key-guard table and one inline row in the team reports tests. A `TestServer` with the
in-memory store and a fake `missionOf` would host the route in milliseconds and meets the gate's standard (no
port, no clock). Two tests: a refused publish leaves the store with no new version; a vanished Mission answers 409
and the report exists at version 1.

### 8. The 4,000-character cut can split a surrogate pair - small

`SectionText` cuts with `text[..MaxLength]`. If the 4,000th UTF-16 unit is the high half of a pair (an emoji or
other character outside the basic plane), the stored goal ends in a lone surrogate; the JSON writer stores it as
the replacement character and the tooltip ends in one. Step back one unit when `char.IsHighSurrogate(text[MaxLength - 1])`,
or cut on a text element boundary with `StringInfo`.

## What I checked and found correct

- The three verdicts are folded once, in `MissionButtonFold`, called from `StampFleetRolesAndFold`, which the
  roster route, the single-session route and the display push all run; the push copies it into
  `SetDisplayStateRequest.MissionButton` and includes it in the change signature. Every row is assigned on every
  fold, so a Director's echo cannot survive. The fold reads no clock and no file. The client has nothing to decide.
- `MissionsForFold` reads `List(tenant, includeEnded: true)` from the store's held copy: no file read per push,
  and a session on an ended Mission still gets its name.
- Section extraction uses the same parser and the same unrendered-element rule as the shape check; present, absent
  and empty are three answers; the goal is set from the document every time and the why only when the document has
  one, as the contract says.
- The permission rule is structural: `OwnSession` refuses any session id that is not the caller's key, the
  request names no Mission, and `MissionStore` finds nothing outside the caller's tenant. Refusal happens before
  `PublishAs`, so a refused publish writes nothing.
- Persistence is the JSON file; a record without the new fields reads as no document, no goal (tested), and an
  older Gateway reading a newer file ignores the unknown properties. No database migration is needed.
- `--json` keeps one shape across every `cc-dev-reports` command (the key tuple test), `mission list --json` is
  still the Gateway's raw answer, and an older Gateway that ignores the flag is reported as a failure, never a link.
- No fallback programming found. Logging is on entry, exit and refusal in every new public method.

## Re-check of d5e76dcf8

The branch was rebased (origin/main is still 19f42f2f1) and carries three answer commits: 50b10798a (the code),
8700f11ee (the reference, the readme, the help text, the answers in the proof file) and d5e76dcf8 (the proof file
made ASCII only). I checked out d5e76dcf8 detached in the review worktree, read the Developer's answers in
`docs/missions-first-class/phase1-proof.md`, read every changed line against origin/main, and ran:

- `dotnet test` on `CcDirector.Gateway.UnitTests` filtered to every class under `Missions` (now including
  `DevReportPublishTests`), `DevReportShapeCheckTests`, `DisplayFoldTraceMatchesPushTests` and the
  `FleetDisplayStateObserver` tests: 139 passed, 0 failed.
- `tools/cc-dev-reports` tests: 59 passed. `tools/cc-devthrottle` mission tests: 324 passed (click 8.5 from the
  scratch folder, as before).
- A byte scan of every added line in the whole change against origin/main: no character outside ASCII.

I did not run the full gate; the Delivery Lead is running it.

### Verdict per finding

1. **Fixed.** `MissionDocumentSections.cs` line 42 now writes the no-break space as a backslash-u escape (u00A0) inside the character class. The byte scan of the whole change is
   clean, and the proof file is ASCII only.
2. **Fixed.** `docs/cli-reference.md` shows the new default and valid fields, a four-column example, the document
   count line, the document and goal rules, the broken-row rule with `documentReportId` and `goal`, the
   `--mission-document` flag on `open`, and the full `--json` key list with `missionDocument`. The readme shows
   the flag, its rules, and the key list. The `--fields` help text inside `cc-devthrottle` was stale in the same
   way and is fixed too, which I had not caught.
3. **Accepted as answered.** No code, by the Delivery Lead's ruling: deploy the Gateway right after the merge. I
   checked nothing further; the Delivery Lead owns the sequencing.
4. **Fixed and correct.** `MissionDocumentLinker.RefreshIfDocument` runs after every publish without the flag and
   re-reads the sections only when the session's own Mission has `DocumentReportId` equal to the report just
   written. The permission argument holds: `DevReportAuthor.PublishAs` returns the report for this session and
   this key, so the report id is this session's, and the Mission is the one the Gateway's records attach the
   session to. Any other report returns null and changes nothing. Tested both ways in `DevReportPublishTests`
   (`Run_RepublishWithoutTheFlagOfTheMissionsDocument_RefreshesTheGoal` and
   `Run_OrdinaryReportWithoutTheFlag_LeavesTheMissionAlone`); the tool prints the block when it comes and the new
   `test_cli.py` test pins that. One small loose end: `packages/client-core/src/devreports/CONTRACT.md` line 37
   still says "An ordinary report may carry either marker and nothing is read from it", which is now true only of
   a report that is not already a Mission's document. The readme says the new rule; the contract should say it too.
5. **Fixed (the wording), as far as this phase goes.** The refusal now ends "If you attached it a moment ago, the
   attachment may not have reached the Gateway yet: wait a few seconds and publish again", pinned in
   `Resolve_SessionWithNoMission_RefusesWithNoMission`. Recording the attachment on the Gateway's own row stays
   outside this phase, as the Developer says; it belongs to whoever next touches the attach route.
6. **Fixed.** `MissionButtonFold` builds the address as `{Api.DevReportLinkRoute.Prefix}{reportId:D}`; the fold
   tests still assert `/r/<id>`, so the two agree.
7. **Fixed, and the alternative is sound.** `DevReportPublish.Run` holds the three steps in order: the Mission is
   resolved before the store is touched, the report is written, then the link or the refresh. The route is now
   a mapping of `DevReportPublishOutcome` to a response. The tests run over the real report store on the
   in-repo throwaway SQLite harness (used across the unit test project; a temp file, no port, no clock) and a
   real mission store on disk. The mutation claim holds by reading: with the early return gone, `PublishAs`
   would write a row and `Assert.Empty(_reports.List(Tenant, Sid))` would fail. The lost-link test is honest
   about the one case it models (two stores over one file, a fixed clock holding the first store's copy), and
   the route's 409 for it is read from the code. What stays untested is only the response mapping, which the
   Developer states.
8. **Fixed.** `MissionDocumentSections.Cut` steps back one unit when the last kept unit is a high surrogate;
   tested on both sides of the limit.

### Anything new

Nothing that proves harm. Two observations, neither blocking:

- `RefreshIfDocument` now runs on every ordinary publish from every session: one roster lookup, a history read
  when the roster has no fresh row, and one held-copy read of the mission store. All cheap; the one file read
  it can cause (a Mission not in the held copy) happens once per publish for a session whose Mission is not on
  this Gateway, which is rare.
- Finding 4's contract wording, above.

The change is ready to merge on the Delivery Lead's green gate.
