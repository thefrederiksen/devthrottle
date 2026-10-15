# Factory Definition, phase 4 - a publish drives the factory, proven against a real Gateway

Date: 2026-10-15. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (section 7, and the
Delivery Lead's addition of a plan that names what it deletes). Branch: `factory-definition/p4-qa-report`, rebased onto
`origin/main` at `4b0dc005b` (pull request 4008, briefs through the definition), which holds all of phase 4: 4005 (the
refusals of direct edits), 4006 (the one-transaction publish and restore, `6601fcb17`) and 4008. This branch adds no
product code: the rig, its transcript, the saved test output and this report. Phase 4 has no screen of its own; the
seat page is read through its Gateway route, and the Cockpit is phase 6.

## What is in this folder

- `rig/run_qa.py` - the rig. It drives a throwaway Gateway through every case below and writes the transcript.
- `transcript.txt` - every command the final run made, everything it printed and its exit code, exactly as printed.
  The two folders a command names are written `<repo>` and `<rig>`.
- `apply-tests.txt` and `postgres-test.txt` - the output of item 2's tests, with the commands that produced them.

## How it was run

`rig/run_qa.py` follows phase 1's rig. It starts a Gateway built from this tree on port 7997 with an empty storage
root of its own under `%TEMP%`, Tailscale off and authentication ON, with factory agents switched on in that root's own
`config/config.json`. It runs the real `cc-devthrottle` from this tree against it, and stops it. It cannot reach the
owner's Gateway or any real factory.

Callers are made as phase 1 made them: rows the rig writes into its own `gateway.db` - a person (an enrolled browser
device), a session of `qa-outreach` and a session of `invoice-factory`, each with a key of its own.

**Talks and runs are real session starts, sent to a stand-in Director.** A talk and a scheduled run each need a
Director on the factory's machine. The rig enrols a workstation device key for the Factory Manager rig's stand-in
Director (`docs/qa/factory-control/factory-manager/rig/standin-director`, Director id `qa-standin-director`, machine
`QA-RIG`). It connects to the Gateway as a Director does, accepts each session create, and writes it down exactly as
received: the create's JSON and its first prompt. So the transcript shows what the Gateway SENT to start a talk or a
run, and what it recorded and answered for it. No agent was started, so nothing shows what an agent did with that
prompt.

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
dotnet build docs/qa/factory-control/factory-manager/rig/standin-director/StandinDirector.csproj
uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" python docs/qa/factory-definition/phase-4/rig/run_qa.py <out dir> --briefs
```

The committed transcript is from the final run, on 2026-10-15 at 14:52 UTC, from the tree committed as `b2920036e`.
Every id below is from that run.

**The definition published** is phase 1's `example/` with three edits, stated in the transcript above the command
that used it:
- the factory's machine is `QA-RIG`;
- the boss names a secret, `secrets: [qa-mail-password]`;
- the writer has a timed start (`0 10 * * 2,4`, Europe/Copenhagen, "Write this week's post."), so the writer's name is
  on a schedule.

**The factory before its first definition** (section 0 of the transcript): `qa-outreach` is registered with seats
boss, writer and scout. It has a schedule for the boss (`cj_3a110b`, "QA Outreach boss morning") and one for the
scout (`cj_3e5047`, "QA Outreach scout at noon"), and a trigger `qa-outreach-new-mail`. The scout's schedule has one
run, and the trigger has one check, reported by the stand-in Director.

## The cases

### 5. The plan names a schedule AND a trigger it will delete; their runs are still there after

The definition has no scout. `factory definition diff qa-outreach` on version 1, before anything is published, ends:

```
What publishing version 1 would do to the live factory now:
  ...
  Registry: seat scout (Scout) removed.
  ...
  Schedule cj_3a110b (QA Outreach - Boss) changed: name "QA Outreach boss morning" -> "QA Outreach - Boss".
  ...
  Schedule cj_3e5047 (QA Outreach scout at noon) deleted: its seat 'scout' is not in the definition; its runs are kept.
  Trigger qa-outreach-new-mail deleted: checks from a definition run from phase 5b, so until then a defined factory has no triggers; its run history is kept.
```

`factory definition publish qa-outreach --version 1 --replaces 0` (exit 0) applied exactly those steps:

```
  Definition: version 1 published.
  ...
  Registry: seat scout (Scout) removed.
  ...
  Schedule cj_3a110b (QA Outreach - Boss) changed: name "QA Outreach boss morning" -> "QA Outreach - Boss".
  Schedule cj_d1d73a (QA Outreach - Writer) created for seat writer: Tuesday and Thursday 10:00 Europe/Copenhagen.
  Schedule cj_3e5047 (QA Outreach scout at noon) deleted: its seat 'scout' is not in the definition; its runs are kept.
  Trigger qa-outreach-new-mail deleted: checks from a definition run from phase 5b, so until then a defined factory has no triggers; its run history is kept.
```

After the publish, the schedules were `cj_3a110b,QA Outreach - Boss` and `cj_d1d73a,QA Outreach - Writer`, and
`trigger list` answered `No triggers in this account.`

**The runs are kept:**
- **The deleted schedule's run.** `schedule runs cj_3e5047` still lists its run. It is the same row as before the
  publish: scheduled `2026-10-15T14:52:36.1908978Z`, session `ffbd701a-6570-4ced-a272-16bf4f7925ec`, `started`.
- **The deleted trigger's check.** The row is kept in `trigger_runs`; the rig read it from its own `gateway.db`:
  `632C625A-..., FC518691-..., 2026-10-15 14:52:38.703934, ..., nothing-to-do, 0, ..., qa-standin-director`
  (1 row).

  **But no route reaches it any more.** The trigger runs route answers 404 once its trigger is gone:

  ```
  $ GET /triggers/fc518691-a72b-4185-aff8-8602238977ae/runs
    status: 404
    answer: {"code":"trigger_not_found","error":"no trigger 'fc518691-a72b-4185-aff8-8602238977ae' in this account"}
  ```

  So "its run history is kept" is true of the table and not of anything a person can open. See Findings.

### 8. The publish-time secret question answered "cannot answer yet"

Every publish and restore of the definition carries this notice (versions 1, 2, 3 and 6 in the transcript):

```
notices[1]:
  seats/boss.yaml:11: QA-RIG cannot answer yet whether it holds 'qa-mail-password': the question needs a Director that can be asked it, which comes with phase 5. The seat's start will check it.
```

A save carries its own wording, `seats/boss.yaml:11: Whether QA-RIG holds 'qa-mail-password' is not checked yet.`
A brief save or a brief publish carries the same notices, because it saves or publishes the whole definition.

### 1. A seat renamed by one edit and one publish

Version 2 is version 1 with one edit, `seats/writer.yaml`: `name: Writer` -> `name: Author`. Publishing it with
`--replaces 1` (exit 0) applied:

```
  Definition: version 2 published.
  Registry: seat writer renamed Writer -> Author.
  Schedule cj_3a110b (QA Outreach - Boss) kept as it is.
  Schedule cj_d1d73a (QA Outreach - Author) changed: name "QA Outreach - Writer" -> "QA Outreach - Author".
```

**Where the new name shows:**
- **The seat page** (`GET /gateway/factories/qa-outreach/seats/writer`, 200):
  `"crumb":"Factories / QA Outreach / Seats / Author"`, `"name":"Author"`, `"talk":{"label":"Talk to Author",...}`.
- **The schedule** (`schedule list`): `cj_d1d73a,QA Outreach - Author,yes,"0 10 * * 2,4",QA-RIG`.
- **A run.** `schedule run cj_d1d73a` fired. The session create the Gateway sent was named
  `QA Outreach - Author - 2026-10-15 16:53`, and its first prompt opens:

  ```
  You are Author, Writer of QA Outreach (factory id: qa-outreach; your agent id: writer).

  This is one of your scheduled runs: the schedule "QA Outreach - Author" (cj_d1d73a). Your brief is given to you here in full, as the Gateway holds it (definition version 2, part of the factory's definition); it is not a file in the factory's folder. ...

  --- your brief, definition version 2 ---
  Write one post from the template.
  --- end of your brief, definition version 2 ---
  ```

  The run row records `"briefVersion": 2, "briefVersionKind": "definition"`; it is the older of the two rows in item
  7's `schedule runs cj_d1d73a --json`, and the database row reads `2F89CEB5-..., QA Outreach - Author, writer, 2,
  definition`.
- **A talk.** `POST /gateway/factory-agents/factories/qa-outreach/seats/writer/talk` answered 201:

  ```
  {"sessionId":"1cbbe26e-85e9-463e-8532-b706badc8fdc","sessionName":"QA Outreach - Author - talk with the owner",...,"briefVersion":2,"briefVersionKind":"definition"}
  ```

  The talk's first prompt opens `You are Author, Writer of QA Outreach`.

### 3. A direct edit on a defined factory refused; the operating routes still work

Every way to define `qa-outreach` other than through its definition was refused with one sentence:

| What was tried | The answer |
|---|---|
| `schedule create ... --factory qa-outreach --seat boss` | `Error: This factory is defined by its definition; edit the definition.` exit 1 |
| `PUT /cron/jobs/cj_3a110b`, the stored schedule with `cronExpression` moved to `0 7 * * 1-5` | `409 {"error":"This factory is defined by its definition; edit the definition.","outcome":"DefinedByDefinition"}` |
| `schedule delete cj_3a110b` | `Error: This factory is defined by its definition; edit the definition.` exit 1 |
| `schedule unlink cj_3a110b` | `Error: This factory is defined by its definition; edit the definition.` exit 1 |
| `trigger add ... --factory qa-outreach` | `Error: This factory is defined by its definition; edit the definition. (HTTP 409)` exit 1 |
| `factory register --manifest <rig>\qa-outreach.manifest.json` | `Error: Not registered: This factory is defined by its definition; edit the definition.` exit 1 |
| `factory purpose qa-outreach "A purpose set by hand"` | `Error: Not set: This factory is defined by its definition; edit the definition.` exit 1 |

**What still works on the defined factory:**
- `schedule disable cj_3a110b`: `Disabled QA Outreach - Boss (cj_3a110b).`, exit 0.
- `schedule enable cj_3a110b`: `Enabled QA Outreach - Boss (cj_3a110b).`, exit 0.
- `schedule run cj_3a110b`: `Fired the schedule.`, exit 0. The stand-in received the create
  `QA Outreach - Boss - 2026-10-15 16:53`, given `--- your brief, definition version 2 ---`.
- `factory goal-number post --factory qa-outreach --value 2 ... --by boss`:
  `posted: 9c91d804-dc6f-47cd-a942-618feb697d61`, exit 0.

**Trigger pause and resume were not shown on the defined factory, and cannot be yet.** A defined factory has no
trigger until phase 5b: the first publish deletes them, and `trigger add` is refused. They were run on the undefined
factory (item 4):
- `Paused trigger qa-undefined-new-mail. Its checks still run and are recorded; it starts no session until resumed.`
- `Resumed trigger qa-undefined-new-mail. A check that counts work starts a session again.`

That pause and resume work on a defined factory is covered by 4005's `PauseThenResume_ATriggerOfADefinedFactory_BothWork` (in `DefinedByDefinitionRoutesThroughThePipelineTests`), not by this rig.

### 4. A factory with no definition is unchanged

`qa-undefined` was registered with no definition, and the same edits were all accepted (exit 0, or 200):
- `factory register` (twice);
- `schedule create`;
- the raw `PUT /cron/jobs/cj_e2085e` with the cron moved (200, the schedule answered with `"cronExpression":"0 7 * * 1-5"`);
- `schedule unlink` and `schedule link`;
- `trigger add`, `trigger pause` and `trigger resume`;
- `factory purpose`;
- `schedule delete`.

The `_UNDEFINED_FACTORY_` tests on main, all passing in the Gateway unit test line of the check below:
- `DefinedByDefinitionRoutesThroughThePipelineTests`: eleven tests, each named `..._UNDEFINED_FACTORY_...AsOnMain`.
  They cover CreateSchedule, CreateTrigger, DeleteSchedule, DeleteTrigger, DisableSchedule, EditSchedule,
  EditTrigger, LinkSchedule, Purpose, Register and UnlinkSchedule.
- `DefinedByDefinitionTests`: `IsDefined_UNDEFINED_FACTORY_NoDefinitionAtAll_IsFalse` and
  `IsDefined_UNDEFINED_FACTORY_OnlyDrafts_IsFalse`.
- `FactoryPublishApplyTests`: `Publish_And_Restore_And_Plan_LeaveTheUNDEFINED_FACTORY_UntouchedWhole`.

There are no Python tests of this kind: the command line passes the Gateway's sentence through and makes no ruling of
its own.

### 6. A restore puts an older version back as a new newest version; a stale `--replaces` refused

`factory definition restore qa-outreach --version 1 --expected-version 2 --replaces 2` (exit 0) applied:

```
  Definition: version 3 written, a copy of version 1, and published.
  Registry: seat writer renamed Author -> Writer.
  Schedule cj_3a110b (QA Outreach - Boss) kept as it is.
  Schedule cj_d1d73a (QA Outreach - Writer) changed: name "QA Outreach - Author" -> "QA Outreach - Writer".
```

After it, the schedule list read `cj_d1d73a,QA Outreach - Writer`, and the seat page read
`"crumb":"Factories / QA Outreach / Seats / Writer","name":"Writer"`.

Then version 4 (Author again) was saved as a draft. A publish and a restore that both still stated version 2 as the
published one were refused, and nothing was written:

```
$ cc-devthrottle factory definition publish qa-outreach --version 4 --replaces 2
Error: Not published: version 3 is the published definition now, not 2. Read what changes again and publish once more.
[exit 1]

$ cc-devthrottle factory definition restore qa-outreach --version 2 --expected-version 4 --replaces 2
Error: Not restored: version 3 is the published definition now, not 2. Read what changes again and restore once more.
[exit 1]
```

### 7. The brief through the definition (after 4008)

At the start of this section, version 3 is published (the restore of version 1) and version 4 is the newest, a draft.

**Get.** `factory brief get --factory qa-outreach --seat writer` answers from the definition:

```
qa-outreach/writer  version 3  published  written by person device browser qa-browser  2026-10-15T14:53:16.7649441Z
A newer version, 4, is waiting and is not published. Read it with 'get --draft'.
This brief is part of the definition of qa-outreach. Its versions are the definition's versions, and a publish publishes the whole definition - every seat, start and file in it - not only this brief.
Write one post from the template.
```

The JSON form carries `"versionKind": "definition"`.

**Set, as a person.** The file is the published brief with one line added. The command was
`factory brief set ... --expected-version 4`, and it answered:

```
Saved: version 5 of the brief of 'writer' is a draft.
A talk or a run is given version 3 until a person publishes this draft.
```

**Only the brief changed.** The rig read versions 4 and 5 through `GET /factory-definitions/qa-outreach/versions/N`
and compared every file byte for byte. One file of six differs, `['seats/writer.yaml']`, and only by the line added:

```
--- version 4: seats/writer.yaml
+++ version 5: seats/writer.yaml
@@ -3,6 +3,7 @@
 role: Writer
 brief: |
   Write one post from the template.
+  End every post with one question to the reader.
 grants: [publish-posts]
```

`factory definition diff qa-outreach --from 4 --to 5` says the same thing:
`Seat Author (writer) changed. brief changed: 1 line added, 0 removed`.

**A stale set is refused.** A second save that still stated version 4:

```
Error: Not saved: another writer got there first. The brief is now at version 5 (by person device browser qa-browser); you sent version 4. Read version 5, merge your change into it, and save again with that version.
```

**A session of the factory.**
- Its `set --expected-version 5` was accepted as version 6, and again only `seats/writer.yaml` differs from version 5.
- Its `publish --version 6 --replaces 3` was refused, exit 1:
  `Error: Only a person publishes a factory's definition. A session of the factory may save a draft; the owner reads it and publishes it.`

**Publish, as a person.** `factory brief publish --factory qa-outreach --seat writer --version 6 --replaces 3` went
through the publish transaction, exit 0:

```
Published: version 6 of the brief of 'writer'. Version 6 is now the brief a talk or a run is given.
...
applied:
  Definition: version 6 published.
  Registry: seat writer renamed Writer -> Author.
  Schedule cj_3a110b (QA Outreach - Boss) kept as it is.
  Schedule cj_d1d73a (QA Outreach - Author) changed: name "QA Outreach - Writer" -> "QA Outreach - Author".
```

The rename is in it because version 6 was built on version 4, the Author draft. A brief is saved on the newest
version, so publishing it publishes every draft change beneath it. The answer says so: its `what changes` lists
`name Writer -> Author` beside the brief. See Findings.

**The seat page's brief panel answers from the definition:**
- **Before the publish**, `GET .../seats/writer/brief/editor` started from the session's draft, and said:
  `"This starts from version 6, the newest draft, written by session 3f7f9cca-... today 10:53. It is not the published brief: talks and runs of this seat are given version 3. This brief is part of the definition of QA Outreach. ..."`
- **After the publish**, the seat page's `brief` read:
  `"versionText":"Definition version 6, published by a person (device browser qa-browser) today 10:53."`
  with the three lines of text and the definition note. The panel's other routes:
  - `/brief/editor`: `"This starts from version 6, the published brief. ..."`
  - `/brief/history`: `"3 versions of the definition changed this brief. Definition version 6 is published. ..."`,
    with rows titled `"Definition version 6"` and so on.
  - `/brief/versions/6`: `"title":"Definition version 6"`.

**A talk and a run are given the published brief, marked as a definition version.**
- The talk answered 201 with `"briefVersion":6,"briefVersionKind":"definition"`. Its first prompt holds
  `--- your brief, definition version 6 ---` with the three lines, and tells the agent to record
  `--version "definition v6"`.
- The run of `cj_d1d73a` was given the same brief.
- `schedule runs cj_d1d73a --json` shows `"briefVersion": 6, "briefVersionKind": "definition"` for this run, and 2 for
  item 1's.
- The database rows read `3B52C118-..., QA Outreach - Author, writer, 6, definition` and
  `2F89CEB5-..., QA Outreach - Author, writer, 2, definition`.

### 2. A publish whose schedule step is made to fail changes nothing (tests, not the rig)

**This case is proven by tests, not on the rig.** The fault is injected through a test-only seam, a database
interceptor that makes the `cron_jobs` write throw, handed to `FactoryPublishApply` through its context opener. A
Gateway built for use has no such seam, so the rig cannot make a schedule write fail.

`FactoryPublishScenario.PublishWhoseScheduleWriteFails_ChangesNothing` is the one scenario, run on each database:
- **The setup.** Version 1 is published with a trigger the factory had before. Version 2 moves the boss to 07:00 and
  renames the writer.
- **The failing publish.** Version 2's publish runs through the failing contexts.
- **What it asserts.** It reads the whole live state of BOTH factories before and after, as one text, and they are
  equal: the registry, the schedules, the triggers, and every definition version's standing. The answer is
  `WriteFailed`, with
  `The publish changed nothing: writing schedule <id> for seat 'boss' failed: ...`. Nothing was applied. Version 1
  is still published and version 2 is still a draft.
- **The control.** The same publish through the database's own contexts then goes through.

**SQLite** - `apply-tests.txt`:
`dotnet test src/CcDirector.Gateway.UnitTests --no-build --filter "FullyQualifiedName~FactoryPublishApplyTests"`
gave `Total tests: 14, Passed: 14`. Among them:
- `Publish_WhoseScheduleWriteFails_ChangesNothing_RegistrySchedulesTriggersAndThePublishMark`;
- `Restore_WhoseScheduleWriteFails_ChangesNothing_AndWritesNoCopy`.

**PostgreSQL** - `postgres-test.txt`:
`.\scripts\test-database.ps1 -Filter "FullyQualifiedName~FactoryPublishPostgresTests"` gave
`RESULT: GREEN - 1 test(s) executed against the throwaway PostgreSQL, none failed, none skipped.` The test is
`Publish_WhoseScheduleWriteFails_ChangesNothing_OnPostgres`.

## The check, as run

Run on Windows on 2026-10-15, on this branch at `b2920036e` (`origin/main` `4b0dc005b` plus the rig commits), every
line in the foreground, one after another. The local gate ran one commit earlier, with only the rig's brief section
and the transcript uncommitted; nothing it builds or tests reads them.

| Command | Result |
|---|---|
| `.\scripts\test-local.ps1` | GREEN: ten suites, 4,495 tests, every TRX `outcome=Completed` |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Trigger"` | Passed 2,178, failed 0, skipped 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed 28, failed 0, skipped 0 |
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~InheritedSessionEnvironment"` | Passed 16, failed 0, skipped 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 168 files, 2,102 tests passed |
| `uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" --with pytest python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule or trigger"` | 1,195 passed, 3,425 deselected (run with `-p no:cacheprovider` added, so it leaves no cache folder in the tree) |

Also run, by the Tech Lead, on `7a991c14b` (the code of `4b0dc005b`: 4008 rebased onto `785719ae9`):

| Command | Result |
|---|---|
| `.\scripts\test-local.ps1 -Parked` | GREEN: 16 suites, every one `outcome=Completed`. Gateway.UnitTests 12,811; Gateway.Tests 1,624, in four shards; Core.Tests 4,516 |
| `.\scripts\test-database.ps1` (every database test) | GREEN: 75 executed, 0 failed, 0 skipped |

## Findings

None of these blocks phase 4. Each is a place where something the phase built says a thing that is not so, or offers
a thing that is refused.

1. **A deleted trigger's kept checks cannot be reached.**
   - The publish says `its run history is kept`, and the row is kept.
   - But `GET /triggers/{id}/runs` answers 404 `trigger_not_found` once the trigger is gone, and
     `cc-devthrottle trigger runs` asks that route.
   - So only the database shows them. A route keyed on the trigger's id, or its name, that reads a deleted trigger's
     rows would make the sentence true.
   - A deleted schedule's runs ARE still served (`schedule runs cj_3e5047`).
2. **A talk with a defined factory's seat tells the agent to do refused things.** The talk's first prompt (sections 1
   and 7) still says, on a goal change:
   - write the new goal into `<rig>\qa-outreach-folder\GOAL.md`, which is not where a defined factory's goal is held;
   - "register the factory again ... cc-devthrottle factory register --manifest", which item 3 shows is refused.

   4008 fixed the brief part: the talk is given the brief in full, and no longer a path that is not on disk.
3. **The seat page offers actions that are refused.**
   - The seat page of the defined factory still answers `"editLabel":"Edit schedule"` and
     `"label":"Take off this seat"` on each schedule.
   - The Gateway refuses both (the PUT and `schedule unlink` in item 3).
   - This is for the Cockpit work of phase 6, or for the seat page fold to stop offering them.
4. **`schedule disable` suggests a refused command.** On the defined factory its next steps are
   `schedule enable cj_3a110b` and `schedule delete cj_3a110b`, and the delete is refused.
5. **A brief publish also publishes the draft changes beneath it** (item 7). A brief is saved on the newest
   definition version, so a person publishing a one-line brief edit also published the waiting Author rename.
   - The answer lists it, and the command line and the seat page say "a publish publishes the whole definition", so
     nothing is hidden.
   - But a person who reads only the brief panel can publish a rename they did not look at.
   - Worth a line in the Cockpit's publish confirmation in phase 6.
6. **Outside phase 4: a check time with an offset is stored four hours off.** On a trial run before the final one,
   the rig reported a trigger check with `checkedAtUtc` `2026-10-15T13:39:15.962338+00:00`. The Gateway stored and
   answered it as `checkedUtc` `2026-10-15T09:39:15.962338Z`.
   - Four hours is this machine's offset from UTC, so the offset is being read as local time.
   - A Director sends `Z`, so the rig now does too, and the committed transcript does not show this.
   - It is worth its own issue, since any caller that sends an offset gets a wrong time.

## What this does NOT prove

- **That an agent did anything.** The talks and runs went to a stand-in Director, which records the session create
  and first prompt and starts nothing. What a real Director does with them is not shown.
- **A failing publish on a real Gateway.** Item 2 is the tests' word, through a test-only seam. Nothing here makes a
  Gateway built for use fail a write halfway, and no crash or power cut mid-transaction was tried.
- **PostgreSQL for anything but item 2.** The rig runs on SQLite. Only the one failure scenario ran on PostgreSQL.
- **Trigger pause and resume on a defined factory.** A defined factory cannot hold a trigger until phase 5b. They were
  run on the undefined factory only.
- **Two people publishing at the same moment.** The stale `--replaces` refusals were shown one after the other, not
  as a race.
- **The secret check itself.** Item 8 shows the publish saying the question cannot be answered yet. Whether a Director
  holds the secret is asked from phase 5, and is not exercised here.
- **The Cockpit.** No screen was opened; the seat page and the brief panel were read through their Gateway routes.
- **The hosted Gateway, other machines, and macOS or Linux.** One Windows machine, one local Gateway.
