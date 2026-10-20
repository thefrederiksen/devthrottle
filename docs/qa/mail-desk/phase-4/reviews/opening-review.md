# Review: a routed session opens as its seat's scheduled run does (pull request 4284)

Reviewer seat: "Mail Desk - Reviewer - routed seat opening", opened by the phase 4 Tech Lead (session 128).
Date: 20 October 2026.

## Verdict

**Approve. No findings.** Nothing within the scope below breaks, and nothing needs to change before merge.

## Scope

Read, from origin (fetched 20 October 2026), `git diff origin/mail-desk-phase-4...origin/md4-opening` at commit
`720499e6b`, measured against section 3.3 of `docs/qa/mail-desk/phase-4/CONTRACT.md` as it stands on
`origin/mail-desk-phase-4` (the branch's own edit of the contract was ignored, as instructed):

- `src/CcDirector.Gateway/Running/ScheduledRunOpening.cs` (the new `SeatOf`, `MailDeskOpening`, and the shared private
  `Opening`), read whole.
- `src/CcDirector.Gateway/MailDesk/MailDeskRouter.cs` (the new `PrePrompt` and its one caller in `StartOneAsync`, and
  `Target`, which supplies the definition).
- `src/CcDirector.Gateway.UnitTests/MailDesk/MailDeskRouterOpeningTests.cs`, read whole.
- For comparison, not changed by the branch: `FactorySeatOpening.cs`, `FactoryTalkSeed.IsBoss`,
  `PublishedFactoryDefinition.BriefOf`, `DirectorCronSessionStarter.StartDefinedAsync` (how a defined factory's
  scheduled run is opened), and `FactoryPublishPlan.Manifest` with `FactoryPublishApply` (what a publish writes into
  the registration).

Ran, on devlinux, the filter the brief named:
`dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk|FullyQualifiedName~ScheduledRun|FullyQualifiedName~FactorySeat"`
with `DOTNET_USE_POLLING_FILE_WATCHER=1` and a scratch `LOCALAPPDATA`: **925 passed, 0 failed, 0 skipped.**

Not reached: the Director side (unchanged by this branch), the rest of the parked Gateway unit suite, and any live
run on the rig.

## What was checked, and what was found

1. **A scheduled run's opening is unchanged, byte for byte.** The old single method was split into a private
   `Opening(seat, whichRun, whatIsAsked)`. With the scheduled-run arguments it produces exactly the old text: the
   sentence `This is one of your scheduled runs: the schedule "<name>" (<id>).`, then ` Your brief is given ...`, and
   `With it comes what this schedule asks of this run.` The tail (brief block, kit sentence, goal sentence) is
   untouched. This is also held by tests that pin the scheduled-run wording as written text and were not edited by
   the branch: `FactorySeatOpeningTests.cs` line 319 and `FactorySeatPromptsTests.cs` lines 176 and 234 (one with a
   brief version, one with a definition version). They pass.
2. **The routed opening is built from the same published version the router starts.** `StartOneAsync` takes
   `published` and `seatDefinition` once from `Target`, and passes the same two objects to both
   `FactorySeatStart.Build` (the kit and the agent) and `PrePrompt` (the opening). The brief comes from
   `published.BriefOf(seat)`, the same call a defined factory's scheduled run uses. No draft is read anywhere.
3. **The registration.** A scheduled run reads the seat's name, role, the title and the boss seat from the
   registration; `SeatOf` reads them from the published definition. A publish writes exactly those four fields from
   the definition into the registration (`FactoryPublishPlan.Manifest`, `FactoryPublishApply` "the registry row"), and
   a defined factory's registration accepts no other writer, so the two say the same thing. Not a finding: if a
   registry ever did drift, the routed session would follow the definition, which is the version its kit is built
   from.
4. **The boss seat and other factories.** `IsBoss` compares the seat id with the definition's `BossSeat`, so a routed
   boss of any factory, and the Mail Desk's own seat, are introduced as "the boss of <title>"; a worker by name and
   role. The factory introduced is always the target factory's own definition. Tests 1 and 3 hold both cases.
5. **No email body or preview text.** The opening adds only the seat's identity, brief, kit and goal sentences. The
   Mail Desk's own text is unchanged from before this branch.
6. **No sender or subject in a log line.** The branch adds no log line.
7. **The tests.** The new tests compare against calls to `ScheduledRunOpening` and `FactorySeatOpening`, not pasted
   copies; the few literal strings (the "You are Clara ..." line, "definition version 3", the start of the Mail Desk's
   text) pin facts about the fixture, not the shared wording. The second test proves the routed and scheduled
   openings share the first line and everything from the brief on, differing only in the sentence that says what
   started the session. The rig uses a fixed clock (`MailDeskRoutingRig.Now`); no sleep, real clock, process or port.

## Observations that are not findings

- The fourth test is named `RouteAsync_SeatOfAFactoryWithNoBrief_...`, but what it sets up is a factory with no
  published definition at all. What it asserts is correct; only the name says less than it proves.
