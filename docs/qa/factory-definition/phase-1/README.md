# Factory Definition, phase 1 - the definition routes, the command line, and the proof

Date: 2026-10-14. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (sections 5.2, 5.3,
5.5b and 7). Design: the phase 1 Tech Lead's definition format (`DESIGN-phase1-definition-format.md`, in the mission's
briefs folder). Branch: `factory-definition/p1-routes-cli`, rebased onto `origin/main` at `de6b5e8be` (pull request
3999, "what changes", is in it). This phase has no screen, so there are no screenshots; the Cockpit is phase 6.

## What was built

- **Eight routes** under `/factory-definitions/{factory}` (`src/CcDirector.Gateway/Api/FactoryDefinitionEndpoints.cs`):
  the published version and the newest version, each with its content; one page of the history without content
  (`?limit`, `?before`); one version with its content; what changes (`/diff?from=N&to=M`); save a draft; publish;
  restore. Who is asking is read by `FactoryNaming.TryAct`, the gate a seat's brief and a factory's schedules and
  triggers already pass: a person, or a session of that factory. A machine token or a Director may read and may not
  write.
- **The whole-definition check runs before every write**, with the account's fleet skill ids (the skill store) and
  the machines its Directors report now (every registered Director that has not said goodbye). Any error refuses
  with HTTP 422, the body `{ error, outcome: "CheckFailed", findings: [ {severity, file, line, message} ] }`, and
  nothing is stored. Notices ride along on the success, under `findings`.
- **A publish and a restore answer with what changes** against the version that was published just before (the
  whole definition, listed as added, on the first publish), composed by `FactoryDefinitionChanges.Compare`.
- **The routes are in the session key guard** (`SessionKeyGuard.IsFactoryDefinitionRoute`), publish and restore
  included: a session's publish reaches the store and is refused there in its own words.
- **The command line**: `cc-devthrottle factory definition get | history | save | publish | restore | diff`
  (`tools/cc-devthrottle/src/factory_definition_ops.py`), with its section in `docs/cli-reference.md` and its six
  entries in `cc-devthrottle actions`.
- **Nothing else reads the table.** No change to how anything runs.

## The check, as run

Run on Windows on 2026-10-14, from this worktree, rebased onto `origin/main` at `de6b5e8be`. The code for these runs
is commit `97dd6e9cc` (the seven lines) and `3f1103bef` (the whole command line suite, after its one fix). Every
command ran in the foreground, one after another.

| Command | Result |
|---|---|
| `.\scripts\test-local.ps1` | GREEN at `97dd6e9cc`, tree clean: ten suites, 4,495 tests, every TRX `outcome=Completed`. It names the three parked suites as a coverage gap; the three lines below are those suites' factory tests |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Trigger"` | Passed: 1,671, failed: 0, skipped: 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed: 28, failed: 0, skipped: 0 |
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~InheritedSessionEnvironment"` | Passed: 16, failed: 0, skipped: 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 167 files, 2,068 tests passed |
| `uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" --with pytest python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule or trigger"` | 1,051 passed, 3,425 deselected |

The Python line is run through `uv` because the machine's own Python has click 8.1.8, below the tool's floor of
8.2.1, and fails 548 of those tests for that reason alone. That exact line is the one above.

Also run, beyond the check:

| Command | Result | Why |
|---|---|---|
| the same `uv run ... python -m pytest tools/cc-devthrottle/tests -q` (the whole command line suite) | First run: 1 failed, 4,472 passed, 3 skipped. After the fix: `test_help_and_errors_axi.py` and `test_factory_definition_ops.py` 403 passed | The help of every command is checked there. The first run failed `test_shortHelp_EveryCoveredCommandAndGroupHasAOneLineSummary`: four of the new summaries were 81 to 100 characters and the limit is 80. They were shortened and those two files run again |

**Not run, and why.** `scripts/test-database.ps1`: this branch adds no migration and no table, so the PostgreSQL
proofs have nothing new to apply; issue 4001 (a timed-out database test leaving later ones on "cannot connect to
invalid database") was therefore not met. `HostedStatsServeTests`, which fails on main too (issue 3569), is not in
any filter above and was not run. `-Parked` was not run.

Of the Gateway unit tests, 63 are this branch's own: `FactoryDefinitionEndpointsTests` (36, through the handlers over
the real store, the real check and a real database) and `FactoryDefinitionRoutesThroughThePipelineTests` (27, through
the mapped endpoints in memory with real JSON bodies, and the guard). No port is opened by either. The command line
has 38 tests in `test_factory_definition_ops.py`.

## Against a real Gateway

`rig/run_qa.py` starts a Gateway built from this tree on port 7994 with an empty storage root of its own under
`%TEMP%`, Tailscale off and authentication ON, runs the real `cc-devthrottle` from this tree against it, and stops it.
It cannot reach the owner's Gateway or any real factory. Every command it ran, everything it printed and its exit
code are in `transcript.txt`, exactly as printed (the two folders a command names are written `<repo>` and `<rig>`).
The definition it saved is `example/`; every failure case is that folder with one named edit, which the transcript
states above the command that used it.

**How each caller was made.** The rig writes three rows into its own Gateway's `gateway.db`, the way the Factory
Manager rig (`docs/qa/factory-control/factory-manager/`) does:

- **A person**: a `device_credentials` row for a browser device, holding the SHA-256 of a fresh key. The command line
  sends that key as its bearer, so the Gateway reads the caller as a person's device.
- **A session of the factory**: a `session_keys` row holding the hash of a fresh session key, as a Director registers
  one, and a `session_history` row naming the factory `qa-outreach` - the record `FactoryNaming.TryAct` reads. The
  command line sends that key exactly as a session's own `cc-devthrottle` does, so it passes the real session-key
  check and the real session key guard.
- **A session of another factory**: the same, with `session_history` naming `invoice-factory`.

No Director is connected to the rig, so the default machine is reported as a notice on every write, as the check is
written to do.

### The flow: draft, publish, second publish, diff, restore

```
$ cc-devthrottle factory definition save qa-outreach --dir <repo>\docs\qa\factory-definition\phase-1\example --expected-version 0 --note "the first definition"      (as a person)
Saved: version 1 of the definition of qa-outreach is a draft (6 files, 936 bytes).
No version is published yet.
notices[1]:
  factory.yaml:9: No Director on SOREN_NORTH is connected now, so it could not be confirmed.
[exit 0]

$ cc-devthrottle factory definition publish qa-outreach --version 1      (as a person)
Published: version 1 of the definition of qa-outreach. Version 1 is now the published definition.
what changes: First publish: 9 factory settings set, 2 seats added, 1 start created, rules tightened on base and boss, rules loosened on base, 1 grant added, 1 file added.
  ...
  Seat Writer (writer) added.
  ...
[exit 0]
```

The second version renames the writer, changes its brief and moves the boss's start an hour:

```
$ cc-devthrottle factory definition diff qa-outreach      (as a person)
qa-outreach: from version 1 to version 2.
what changes: 1 seat changed, 1 start moved.
  Seat Author (writer) changed.
    name Writer -> Author
    brief changed: 1 line added, 1 removed
      - Write one post from the template.
      + Write one post from the template, and link the last one.
  Boss: start moved, Weekdays 08:00 Europe/Copenhagen -> Weekdays 09:00 Europe/Copenhagen.
[exit 0]

$ cc-devthrottle factory definition publish qa-outreach --version 2      (as a person)
Published: version 2 of the definition of qa-outreach. Version 2 is now the published definition.
what changes: 1 seat changed, 1 start moved.
  (the same lines as the diff)
[exit 0]

$ cc-devthrottle factory definition restore qa-outreach --version 1 --expected-version 2      (as a person)
Restored: version 1 of the definition of qa-outreach. Version 3 is now the published definition.
what changes: 1 seat changed, 1 start moved.
  Seat Writer (writer) changed.
    name Author -> Writer
  ...
  Boss: start moved, Weekdays 09:00 Europe/Copenhagen -> Weekdays 08:00 Europe/Copenhagen.
[exit 0]

$ cc-devthrottle factory definition history qa-outreach      (as a person)
count: 3 of 3. Version 3 is the published definition.
versions[3]{version,state,written,by,files,bytes,note}:
  v3,PUBLISHED NOW,2026-10-14T23:...Z,person device browser qa-browser,6,936,"restored from version 1."
  v2,published,2026-10-14T23:...Z,person device browser qa-browser,6,959,"the second definition"
  v1,published,2026-10-14T23:...Z,person device browser qa-browser,6,936,"the first definition"
[exit 0]
```

`get` prints the summary and the file list, and `get --file seats/writer.yaml` prints the restored writer exactly as
version 1 held it (both in the transcript).

### The refusals, by the answer that carries them

| Case | What the Gateway answered, as the command line printed it | Exit |
|---|---|---|
| A stale save (read version 1, the definition is at 3) | `Error: Not saved: the definition of qa-outreach is at version 3, not 1; read it again, merge, and save once more. Version 3 was written by person device browser qa-browser.` | 1 |
| A session of the factory saves a draft | `Saved: version 4 of the definition of qa-outreach is a draft (6 files, 959 bytes).` (allowed) | 0 |
| That session publishes | `Error: Only a person publishes a factory's definition. A session of the factory may save a draft; the owner reads it and publishes it.` | 1 |
| That session restores | `Error: Only a person publishes a factory's definition, and restoring an older version publishes a copy of it. A session of the factory may save the content it wants as a new draft; the owner publishes it.` | 1 |
| A session of another factory reads, then saves | `Error: this definition belongs to factory 'qa-outreach', so only a person or a session of 'qa-outreach' may change or run it` (both) | 1 |
| An unknown key (`colour: red` added to the writer) | `Error: The definition was not saved: the check found 1 error in the definition of qa-outreach. Fix each one where it is and try again.` then `errors[1]:` `seats/writer.yaml:4: 'colour' is not a key of a seat file. The keys are: seat, name, role, line, machine, agent, model, repository, brief, starts, grants, rules, secrets, skills, files.` | 1 |
| A dangling reference (the writer names `files/missing.md`) | `errors[1]:` `seats/writer.yaml:9: The seat names the file 'files/missing.md', which is not in this definition.` | 1 |
| A factory skill and a fleet skill with the same name (`skills/dev-throttle/SKILL.md` added; the writer names the fleet skill `dev-throttle`) | `errors[1]:` `seats/writer.yaml:8: 'dev-throttle' is both a factory skill (skills/dev-throttle/SKILL.md) and a fleet skill, so a seat would not know which one it got. Rename the factory skill.` | 1 |
| A factory skill on an agent that cannot load one (the writer on Codex names the factory skill `house-style`) | `errors[1]:` `seats/writer.yaml:10: Phase 0 has not shown that Codex can load a factory skill, so a seat on Codex cannot name one yet.` | 1 |

After the refusals, `history --count 2` shows four versions in all - nothing refused was stored - and the session's
draft as version 4, written by `session <its id>`.

The caps are proven in the route tests, not on the rig: `A_FILE_OVER_ITS_CAP_IS_REFUSED_BY_THE_CHECK_and_nothing_is_stored`
(422, an error on the oversized file). So is a publish that the check refuses because the account changed after the
save: `A_PUBLISH_CHECKS_AGAIN_with_the_accounts_skills_as_they_are_NOW_and_publishes_nothing_on_an_error`.

### Waiting on the phase 0 follow-up

Pull request 4003 (the check refuses what phase 0 showed an agent cannot take) had not merged when this was
written. Until it does, every agent reads "not shown yet" for factory skills, which is what the last row above shows,
and no agent is refused rules. Once it merges, `rig/run_qa.py --phase0` adds a Codex seat carrying a rule set, and
the factory-skill row then carries Codex's own reason; both are to be added here, quoted.

## Decisions made inside the brief, for the Tech Lead to keep or replace

1. **`history --count N` / `-n`, not `--limit N`, and `--expected-version N`, not `--expect N`.** The brief wrote
   `--limit` and `--expect`. `factory brief`, the template, already spells the same two ideas `--count` and
   `--expected-version`, and the command reference tells every agent to use `--count`/`-n` for a result limit. One
   spelling per idea across the tool won. Renaming them is one edit each in `cli.py`.
2. **`save` requires `--expected-version`.** There is no default: 0 has to be said, so a caller can never save over
   a definition it did not read by forgetting the flag. `restore` keeps the brief command's default of 0 ("I did not
   check").
3. **A session's publish and restore skip the check and go straight to the store**, which refuses them. A session
   told what the check thinks of a version it may not publish anyway would not know what to do next.
   (`A_SESSIONS_PUBLISH_OF_A_VERSION_THE_CHECK_WOULD_REFUSE_is_still_told_only_a_person_publishes`.)
4. **The 422 body also carries `outcome: "CheckFailed"`**, beside `error` and `findings`, as every other refusal on
   these routes names its outcome.
5. **A restore records where it came from in `restoredFrom` and carries no change note.** The history prints
   "restored from version N" from that field.
6. **"The machines its Directors report now"** is every Director registered for the account that has not said
   goodbye (`StoppedAtUtc` unset). A Director that died without saying goodbye still counts until the registry evicts
   it; that can only turn a notice into silence, never into a refusal, because a machine is never an error.
7. **The fleet skills are every skill in the account's store** - built-in or the account's own, on or off. A skill
   switched off still exists, so a definition naming it is not dangling.
8. **"What changes" on a publish is measured against the version published just before**, read immediately before the
   publish. Two people publishing at the same moment could each be shown a difference against the version the other
   replaced; the store still lets only one of them publish each version.
9. **A failed write the store cannot explain (`WriteFailed`) is HTTP 500**, not 409: it is the Gateway failing, not a
   conflict the caller can resolve.
10. **`get --file` with `--json` prints only that file** (`factory`, `version`, `path`, `text`), so the filter applies
    to `--json` too.

## What this does NOT prove

- **No real Director and no real session.** The person and the two sessions are rows written into the rig Gateway's
  own database. The session-key check, the guard, the routes and the store are the real ones; how a real Director
  registers a key and how a real session comes to be recorded in a factory are not exercised here.
- **No connected machine.** With no Director connected, the "machine not connected" notice is the only machine finding
  shown. A connected machine silencing that notice is proven in the check's own tests, not here.
- **Two replicas racing were not run.** A second writer's refusal is shown by a stale save from one process.
- **The page of the history and the check's inputs ran only on SQLite.** No definition route test runs against
  PostgreSQL.
- **The whole Gateway unit suite was not run**, only the filter of the mission's check.
- **The Cockpit's development proxy does not know `/factory-definitions`.** Nothing in this phase calls the routes
  from a browser; phase 6 needs that line.
