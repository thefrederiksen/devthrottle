# Factory Seats, phase 2c - the Gateway opens every run: the proof

Date: 2026-10-13. Mission document: `docs/missions/factory-seats-2026-10-13/MISSION.md` (issue #3950).
Branch: `factory-seats/phase-2c-gateway-opens-runs`, cut from `origin/main` at `4ebd3e9c2`. This phase changes no
screen, so there are no screenshots here. It changes words the Gateway gives to an agent, so the proof is those
words, as the Gateway composed them.

## What was built

- **A talk.** For a seat whose brief is PUBLISHED on the Gateway, `FactoryTalkSeed` gives the talk that brief's
  text and its version, in place of the path of a brief file. A seat with no published brief is told the path of
  its file, in exactly the words it was told before. A draft is never given.
- **A scheduled run.** For a schedule linked to a seat that has a published brief, the Gateway opens the run ahead
  of the schedule's own prompt: who the seat is, from the registration, in the sentence a talk uses, then the
  brief's text with its version. `ScheduledRunOpening` is the one place the run's text is put together; it is pure,
  and `DirectorCronSessionStarter` calls it where it used to call `ScheduledRunClosingInstruction`. A schedule in no
  factory, and a seat with no published brief, are given what the closing instruction alone gives, byte for byte.
- **One sentence for who the seat is.** `FactorySeatOpening.WhoYouAre` writes it for a talk and for a run, from the
  registration as it stands when the session starts. That is what makes renaming a seat one registration.
- **Which version was given is kept.**
  - A scheduled run: a new column `cron_runs.BriefVersion` (one Entity Framework migration for each provider,
    `AddBriefVersionToCronRuns`), carried on the run as `briefVersion` wherever a run is read, for example
    `cc-devthrottle schedule runs <id> --json`. The activity row the run leaves in its factory carries the same
    number in `factoryAgentVersion`, the field the activity record already kept for a factory agent's version.
  - A talk: `briefVersion` in the answer that starts it, a line in the Gateway's log, and `factoryAgentVersion` on
    the `talked` line the talk records before it ends. The talk is told to record it: its first closing step now
    reads `cc-devthrottle factory record ... --version <n> --outcome talked ...`.
- **The boss's role is Boss (decision D4).** A talk no longer says another role word beside "the boss". The
  registry refuses a boss seat whose role is not `Boss`, with the fix. The reference (`docs/cli-reference.md`) and
  the shipped terminology skill no longer describe the exception.

## The four openings, as the Gateway composed them

The fixture is one factory, WarmForward, whose boss seat has the schedule `cj_a721e6`. The texts below were printed
by the test `Proof_PrintsTheFourOpeningsAsTheGatewayComposedThem`, which calls `FactoryTalkSeed.Compose` and
`ScheduledRunOpening.PromptFor` and nothing else. They are also saved whole in `composed/`. To print them again:

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Proof_PrintsTheFourOpenings" --logger "console;verbosity=detailed"
```

### A scheduled run - a seat with no brief on the Gateway (`composed/run-no-brief.txt`)

```
Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.

When you have finished, the last thing you do is report how this scheduled run went: run `cc-devthrottle run result ok`, or `cc-devthrottle run result problem "<one line why>"` if something went wrong.
```

### A scheduled run - a seat with a published brief, version 3 (`composed/run-published-brief.txt`)

```
You are the boss of WarmForward (factory id: warmforward; your agent id: boss).

This is one of your scheduled runs: the schedule "WarmForward - morning run" (cj_a721e6). Your brief is given to you here in full, as the Gateway holds it (version 3); it is not a file in the factory's folder. With it comes what this schedule asks of this run.

--- your brief, version 3 ---
character: You run WarmForward. You are careful with money and plain with the owner.
duties:
  - read the overnight numbers
  - tell the owner what changed
--- end of your brief, version 3 ---

Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.

When you have finished, the last thing you do is report how this scheduled run went: run `cc-devthrottle run result ok`, or `cc-devthrottle run result problem "<one line why>"` if something went wrong.
```

What differs between the two, and nothing else does:

```diff
--- run-no-brief.txt
+++ run-published-brief.txt
@@ -1 +1,12 @@
+You are the boss of WarmForward (factory id: warmforward; your agent id: boss).
+
+This is one of your scheduled runs: the schedule "WarmForward - morning run" (cj_a721e6). Your brief is given to you here in full, as the Gateway holds it (version 3); it is not a file in the factory's folder. With it comes what this schedule asks of this run.
+
+--- your brief, version 3 ---
+character: You run WarmForward. You are careful with money and plain with the owner.
+duties:
+  - read the overnight numbers
+  - tell the owner what changed
+--- end of your brief, version 3 ---
+
 Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.
```

### A talk - a seat with no brief on the Gateway (`composed/talk-no-brief.txt`)

```
You are the boss of WarmForward (factory id: warmforward; your agent id: boss).

The owner is here now and is talking with you. This is the owner's own session, opened from the Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a report from it - the owner is reading your answers as you write them.

Before you say anything, read:
- your brief: /home/soren/factories/warmforward/agents/boss.yaml
- the factory's goal: /home/soren/factories/warmforward/GOAL.md
- the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters
- the factory's recent activity: cc-devthrottle factory activity --factory warmforward -n 30
- the factory's status as the owner sees it on the Factories screen: cc-devthrottle factory status --factory warmforward
  It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.

You work under the same rules as your scheduled runs.
These are the rules of your scheduled runs, exactly as your schedule (cj_a721e6) starts them:
--- the rules of your scheduled runs ---
Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.
--- end of the rules of your scheduled runs ---
The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: do not rename this session to a dated name, do not send the morning email, and do not run cc-devthrottle session done. The owner closes this session.

Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest activity line with the outcome talked, if there is one) and what you need from the owner.

In a talk the owner and you may change anything about the factory, the goal included - the owner is here to approve it. If the goal changes:
- write the new goal into /home/soren/factories/warmforward/GOAL.md, with today's date and this session's id (cc-devthrottle session whoami) beside it;
- register the factory again so the page shows it: cc-devthrottle factory register --manifest <the factory's manifest>, with goalFile GOAL.md and goalApprovedOn set to today. If you do not have the manifest, write one from cc-devthrottle factory list --json, keeping only the keys the manifest takes (docs/cli-reference.md, Factory registry and goal number).

Before the talk ends you MUST do all three of these:
1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent boss --outcome talked --what "<one line of what was decided>"
2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
3. Run cc-devthrottle factory status --factory warmforward again. For every failing or waiting item that is now resolved, mark it handled with its evidence - and only if it is resolved:
   cc-devthrottle factory record --factory warmforward --agent <the item's seat> --outcome done --corrects <the item's row id> --what "Handled: <the evidence>"
   Then tell the owner the word the factory ends on.
```

### A talk - a seat with a published brief, version 3 (`composed/talk-published-brief.txt`)

```
You are the boss of WarmForward (factory id: warmforward; your agent id: boss).

The owner is here now and is talking with you. This is the owner's own session, opened from the Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a report from it - the owner is reading your answers as you write them.

Before you say anything, read:
- your brief: version 3, given to you in full below this list. The Gateway holds it; it is not a file in the factory's folder.
- the factory's goal: /home/soren/factories/warmforward/GOAL.md
- the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters
- the factory's recent activity: cc-devthrottle factory activity --factory warmforward -n 30
- the factory's status as the owner sees it on the Factories screen: cc-devthrottle factory status --factory warmforward
  It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.

--- your brief, version 3 ---
character: You run WarmForward. You are careful with money and plain with the owner.
duties:
  - read the overnight numbers
  - tell the owner what changed
--- end of your brief, version 3 ---

You work under the same rules as your scheduled runs.
These are the rules of your scheduled runs, exactly as your schedule (cj_a721e6) starts them:
--- the rules of your scheduled runs ---
Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.
--- end of the rules of your scheduled runs ---
The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: do not rename this session to a dated name, do not send the morning email, and do not run cc-devthrottle session done. The owner closes this session.

Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest activity line with the outcome talked, if there is one) and what you need from the owner.

In a talk the owner and you may change anything about the factory, the goal included - the owner is here to approve it. If the goal changes:
- write the new goal into /home/soren/factories/warmforward/GOAL.md, with today's date and this session's id (cc-devthrottle session whoami) beside it;
- register the factory again so the page shows it: cc-devthrottle factory register --manifest <the factory's manifest>, with goalFile GOAL.md and goalApprovedOn set to today. If you do not have the manifest, write one from cc-devthrottle factory list --json, keeping only the keys the manifest takes (docs/cli-reference.md, Factory registry and goal number).

Before the talk ends you MUST do all three of these:
1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent boss --version 3 --outcome talked --what "<one line of what was decided>"
   Keep --version 3 exactly as written: it records which version of your brief this talk was given.
2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
3. Run cc-devthrottle factory status --factory warmforward again. For every failing or waiting item that is now resolved, mark it handled with its evidence - and only if it is resolved:
   cc-devthrottle factory record --factory warmforward --agent <the item's seat> --outcome done --corrects <the item's row id> --what "Handled: <the evidence>"
   Then tell the owner the word the factory ends on.
```

What differs between the two, and nothing else does:

```diff
--- talk-no-brief.txt
+++ talk-published-brief.txt
@@ -5,3 +5,3 @@
 Before you say anything, read:
-- your brief: /home/soren/factories/warmforward/agents/boss.yaml
+- your brief: version 3, given to you in full below this list. The Gateway holds it; it is not a file in the factory's folder.
 - the factory's goal: /home/soren/factories/warmforward/GOAL.md
@@ -11,2 +11,9 @@
   It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.
+
+--- your brief, version 3 ---
+character: You run WarmForward. You are careful with money and plain with the owner.
+duties:
+  - read the overnight numbers
+  - tell the owner what changed
+--- end of your brief, version 3 ---
 
@@ -26,3 +33,4 @@
 Before the talk ends you MUST do all three of these:
-1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent boss --outcome talked --what "<one line of what was decided>"
+1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent boss --version 3 --outcome talked --what "<one line of what was decided>"
+   Keep --version 3 exactly as written: it records which version of your brief this talk was given.
 2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
```

### The two "no brief" texts are the words from before this phase

Before any code of this phase was written, the unchanged code at `4ebd3e9c2` was made to write out the talk and the
scheduled run for this fixture. Those two texts are the constants `TodaysTalkToTheBoss`, `TodaysTalkToASeat` and
`TodaysScheduledRun` in `FactorySeatOpeningTests`, and three tests compare today's output with them. The two "no
brief" files above were also compared with those captures after the change: identical, both.

One deliberate exception, from D4: a boss whose registered role is another word used to be opened as "You are the
boss of X (its CFO)". It is now "You are the boss of X", with or without a brief.

## The cases the task named, and the test for each

All in `src/CcDirector.Gateway.UnitTests`. The scheduler cases run through the real `CronEngine`, the real
`DirectorCronSessionStarter`, the real registry and the real `FactoryBriefStore` over a real SQLite database, wired
with the same two lines the host uses; only the Director is a stand-in that keeps the create it was handed.

| Case | What is asserted | Test |
|---|---|---|
| A published brief and a newer draft | The run is given version 1's text; the draft's text and "version 2" are not in it; the run records 1 | `ASeatWithAPublishedBriefAndANewerDraft_ItsRunIsGivenThePublishedVersion_NeverTheDraft` |
| The same, for a talk | The talk is given the newest published version; neither the older version nor the draft is in it; the answer says 2 | `Talk_ToASeatWithAPublishedBrief_IsGivenItsTextAndVersion_AndTheAnswerSaysWhichVersion` |
| Only drafts | No published brief, so the run is started exactly as before and records no version | `ASeatWithOnlyDrafts_HasNoPublishedBrief_SoItsRunIsStartedExactlyAsBefore` |
| The published version changing between two runs | Run one is given and records 1, run two is given and records 2; both are read back from the database by another store | `ThePublishedVersionChangingBetweenTwoRuns_EachRunIsGivenAndRecordsTheVersionPublishedWhenItStarted` |
| A seat renamed in the registration | The next run says the new name; putting the old name back gives the earlier text exactly; the schedule's prompt and the brief are untouched | `ASeatRenamedInTheRegistration_TheNextRunSaysTheNewName_WithTheScheduleAndTheBriefUntouched`, and for a talk and a run as wording `ASeatRenamedInTheRegistration_TheNextTalkAndTheNextRun_SayTheNewNameAndNothingElseChanges` |
| A schedule in no factory | The run's text is the stored prompt and the closing instruction, exactly; no version recorded - while another seat has a published brief | `AScheduleInNoFactory_IsStartedExactlyAsBefore_EvenWhileAnotherSeatHasAPublishedBrief` |
| A seat with no brief | The same exact text, and the pins against the words from before this phase | `ASeatWithNoBriefOnTheGateway_ItsRunIsStartedExactlyAsBefore_AndRecordsNoVersion`, `Compose_ATalkToABossWithNoBriefOnTheGateway_IsWordForWordWhatItWasBeforeThisPhase`, `Compose_ATalkToASeatWithNoBriefAndNoSchedule_IsWordForWordWhatItWasBeforeThisPhase`, `PromptFor_AScheduledRunTheGatewayDoesNotOpen_IsByteForByteWhatItWasBeforeThisPhase` |
| A brief at the size cap | A brief of exactly 65,536 bytes, marked at both ends, is saved, published and found whole in the run's text and in a talk's | `ABriefAtTheSizeCap_IsGivenWhole_ToTheRunAndToATalk` |

Beyond the named cases: a seat is never given another seat's brief; a run that did not start records no version; a
schedule whose factory or seat is not registered is not opened; a talk with a brief differs from today's talk only by
the brief and its version; Handle the issue gives the boss its brief too; the registry's two new refusals.

## The refusal, by the answer that carries it

| Case | The registry's answer | Test |
|---|---|---|
| A manifest whose boss seat has another role word | `The boss seat 'nora-hale' has the role 'CFO'. The boss's role is Boss: set its role to "Boss".` | `Register_BadManifest_IsRefusedWithAReason_AndNothingIsStored`, row "boss with another role word" |
| The role in other letters (`boss`) | contains `set its role to "Boss"` | the same test, row "boss with the role in other letters" |

Would a registered factory now be refused? The account these sessions run in was read with `cc-devthrottle factory
list --json` on 2026-10-13: 14 factories, 11 with a boss, and every one of the 11 boss seats has the name `Boss` and
the role `Boss`. None of them would be refused when registered again. A factory in another account that was
registered with another role word keeps working as it is, and its next registration is refused until its manifest
says `Boss`.

## The check, as run

Run on Linux on 2026-10-13, from the worktree of the branch, in the foreground, one after another. `origin/main`
was fetched before the run and again after it and was still `4ebd3e9c2`, the commit the branch was cut from, so
there was nothing to merge.

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Schedule"` | Passed: 1390, failed: 0, skipped: 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed: 28, failed: 0, skipped: 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 166 files, 2058 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 110 files, 1155 tests passed |
| `python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule"` | 934 passed, 1 skipped |

Also run, because this change touches what they cover:

| Command | Result | Why |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactoryTalkEndpoint\|FullyQualifiedName~FactorySeatOpening\|FullyQualifiedName~ScheduledRun\|FullyQualifiedName~FactoryRegistryStore\|FullyQualifiedName~Migration\|FullyQualifiedName~GatewayHostBootSmoke\|FullyQualifiedName~BuiltInSkills\|FullyQualifiedName~ShippedSkills\|FullyQualifiedName~Architecture"` | Passed: 224, failed: 0 | The migration, the host's start, and the guard that the two copies of a shipped skill agree |
| `dotnet ef migrations has-pending-model-changes`, SQLite | No changes | The local migration agrees with the model |
| `dotnet ef migrations has-pending-model-changes`, `CC_GATEWAY_EF_PROVIDER=postgres` | No changes | The PostgreSQL migration agrees with the model |

The first full run of the first command failed one test, and it was this phase's own: a new assertion expected a
Windows folder joined to its brief file with a backslash, and the Gateway joins them with the folder's own separator
and leaves the file's slash as it was registered. The assertion was wrong, not the code; it was corrected and the
command run again to the result above. The first and the last command were run once more after the last change to
any file, with `origin/main` fetched first: the numbers in the table are from that run.

Of the Gateway unit tests, 30 are in this phase's two new classes (`FactorySeatOpeningTests`, 19, and
`ScheduledRunOpeningTests`, 11), three are added to `FactoryTalkEndpointTests`, and two rows to the registry's
refusals. The command line has one new test, which runs the exact command a talk is told to run and checks the
version reaches the Gateway.

What had to be true of the machine is what the phase 2a proof lists: the version 10 .NET kit first on the path,
`DOTNET_hostBuilder__reloadConfigOnChange=false` and `DOTNET_USE_POLLING_FILE_WATCHER=1` for `dotnet test`, and the
command line tests run in an existing environment that holds the tool's dependencies.

## Decisions made inside the brief, for the Implementation Lead to keep or replace

1. **A talk's version is recorded on its `talked` line, typed by the talk's own agent.** The task said to record it
   where the talk is already recorded. A talk is recorded in the factory by its `talked` line, and the activity
   record already had a field for the factory agent's version, so the talk is told to write `--version <n>` there.
   The Gateway also puts the version in the answer that starts the talk and in its log. The weakness is plain: it
   depends on the agent running the command as written. The alternative is a new column on the session's history
   row, written by the Gateway when it starts the talk. It was not built because that row is created by a
   Director's first report and is what decides which factory a session belongs to, and writing it earlier from a
   second place is a larger change than this phase.
2. **A scheduled run's version is on the run's own row, and on its activity row.** The second means a factory's
   activity lists show the seat with the version beside it ("Boss v3") for a run of a seat that has a published
   brief; the pages already draw a version that way when a row has one. No seat has a published brief today.
3. **The closing instruction is decided on the schedule's own prompt, never on the brief.** A brief that mentions
   `cc-devthrottle run result` does not take the instruction away.
4. **A schedule's prompt that begins with a slash stays first, and the opening follows it.** A slash prompt is a
   command the agent runs only when it is the first thing it is given, so leading it with the opening would stop
   the command the day the owner published a brief.
5. **The starter's seam gained a second method rather than a wider answer.** `ICronSessionStarter.StartRunAsync`
   returns the start and the version given, and by default calls `StartAsync` and reports no version. The engine
   calls it. The fifteen stand-ins in the tests were left as they are, in files other sessions are also changing.
6. **The sentence that introduces the run** ("This is one of your scheduled runs: the schedule ... With it comes
   what this schedule asks of this run.") and the two lines around the brief are this phase's wording.
7. **In a talk of a seat with a brief, the block "the rules of your scheduled runs" still shows the schedule's own
   prompt only.** The brief is given once, above it.
8. **The brief's text is given as it is.** Only line ends at its very end are dropped. A brief that itself contains
   the line `--- end of your brief, version N ---` is not altered; the owner publishes every brief.
9. **The registry refuses a boss seat with another role word; nothing rewrites one already stored.** The Factories
   screen still shows the stored role word of a factory registered before today. Two comments in
   `packages/client-core/src/factory/factoriesScreenClient.ts` still describe the old exception; they were left
   alone because this phase was told to touch no Cockpit file and another Developer is working there.
10. **The brief store is built earlier in the host's constructor**, ahead of the scheduler that now reads it.

## What this does NOT prove

- **No agent was given these words.** The text is proven as far as the create a Director would be handed. No
  session was started with a brief, so nothing shows how an agent behaves on being given one.
- **A 65,536 byte brief was not sent to a Director.** It is in the create the Gateway builds. Whether a Director
  and the agent's terminal take a first prompt of that size was not tried, and no limit on it was looked for
  outside the Gateway.
- **The host's own two wiring lines were not run by a test.** The tests wire the engine and the talk route with the
  same expressions; `GatewayHostBootSmokeTests` shows the host still starts. No run was fired through a real
  `GatewayHost`.
- **The PostgreSQL migration was not applied to a PostgreSQL.** It adds one nullable integer column and the model
  check finds no pending change. `scripts/test-database.ps1` was not run.
- **A talk that ends without its `talked` line leaves no version** except in the answer that started it and the
  Gateway's log (decision 1).
- **The version on the activity row was not looked at on a screen.** No screenshot was taken.
- **The seat page still shows the path of a brief file** for a seat that has a published brief. That panel is phase
  2b's.
- **The whole Gateway unit suite was not run**, only the filters above. Windows was not run at all: the pinned
  talks are compared after bringing line ends to one form, because a talk's lines end the way the computer ends
  lines, as they did before this phase.
- **Other accounts' factories were not read.** Whether another account has a boss seat with another role word is
  not known.
