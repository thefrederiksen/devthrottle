# Factory Definition, phase 3 - export and import

Date: 2026-10-15. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (section 5.4, decisions
F3, A8 and D7, goal 7, and section 7's phase 3 line). This phase has no screen; the Cockpit's export and import
buttons are phase 6. Every quotation below is copied from `transcript.txt` beside this file, which `rig/run_qa.py`
wrote against a real Gateway built from this branch.

## What was built

- **Three routes** (`src/CcDirector.Gateway/Api/FactoryDefinitionEndpoints.cs`), all in the session key guard:
  - `GET /factory-definitions/{factory}/export[?version=N]` - the definition as a folder of files: that version, or
    the published one, or the newest when none is published, **exactly as stored**. A factory with **no definition**
    is written from what the Gateway holds today (decision A8, `Factory/Definition/FactoryDefinitionExport.cs`), and
    everything a definition cannot hold is listed under `reported`, each with the file it would have gone in.
  - `POST /factory-definitions/import` - a folder's files, stored as the next **draft**. It goes through the SAME
    checked save as `PUT .../draft` (one private method, `SaveChecked`, serves both): the same whole-definition
    check, the same callers, the same 422 with every finding. It creates the factory's definition when it has none.
    It never publishes. The factory is the one the body names, or else the one `factory.yaml` names.
  - `GET /factory-definition-schemas` - the four JSON Schemas the check enforces, exactly as embedded, so an
    editor or an agent can check a file's shape without saving.
- **The command line moves files; the Gateway rules.** `cc-devthrottle factory export <factory> --out <folder>
  [--version N]` writes every file as UTF-8 with no byte-order mark and its own line endings, into a new or empty
  folder only, and never outside it. `cc-devthrottle factory import <folder> [--factory id] [--note text]` reads the
  folder exactly as `factory definition save` does. `cc-devthrottle factory definition schemas [--out folder]`.
  Entries in `cc-devthrottle actions`, and a section in `docs/cli-reference.md`.
- **The export of a factory with no definition.** `factory.yaml` from the registry entry (agent `ClaudeCode`, which
  every factory seat runs today, and no model, both reported); one `seats/<seat>.yaml` per seat with its published
  brief, or an empty brief and a report when it has none; one start per switched-on recurring schedule. Reported,
  never dropped: the factory's folder, a goal held in a file, a schedule's working folder, other machine,
  notification, kept-open session, work list, a one-off, random or switched-off schedule, a trigger (its check is a
  command; a definition's is a Python file), a schedule naming no seat of the factory, and a schedule id the registry
  lists that does not exist. The YAML is written by `DefinitionYamlWriter`, which quotes anything YAML would read as
  something other than the text, so the definition's own parser reads every value back exactly.
- **The fix from review 2 of pull request 4004.** A draft save whose content maps a path to `null` was a 500 (the
  check threw on it). The route now refuses it with **400**, outcome `NotText`, naming the path in the store's own
  words, before the check runs; an import feeds the same save and is refused the same way.

## The decisions taken here

- **"Import into a new id" is a folder whose `factory.yaml` names the new id.** Import checks a folder exactly as a
  save does, and a save refuses a `factory.yaml` that names another factory (shown below), so import never rewrites a
  file. To copy a factory under a new id, the one line `factory:` is edited; the round trip below shows that the
  folder imported and the folder exported after are the same bytes, and that the first factory's export differs from
  the copy's in that one file only.
- **An import goes after whatever version is newest.** A folder is a whole definition, not an edit of one, and the
  version it follows stays in the history. A write landing in between is still refused as stale by the store.
- **Export without `--version`** gives the published version, or the newest when none is published (so a factory
  that was just imported can be exported), and the summary says which and names a newer draft that was not exported.

## The flow and the failure cases, against a real Gateway

`rig/run_qa.py` starts a Gateway built from this branch on port 7996 with an empty storage root of its own under
`%TEMP%`, factory agents switched on in its own `config.json`, Tailscale off and authentication ON, and runs the
real `cc-devthrottle` from this branch against it. Callers are made as the phase 1 rig makes them: a person is a
browser device row, a session is a session key row and a session history row naming its factory. The two folders a
command names are written `<repo>` and `<rig>`.

### Export, import into a new id, export again: identical bytes

The phase 1 example was imported into `qa-outreach` and published, then exported twice:

```
# SHA-256 of every file in <rig>\exports\a (qa-outreach version 1, first export)
  e2148ee56d0b3bd35de2a0d82c75d6fe136a74ec9c0c18376e378d446db9af99    271  factory.yaml
  f1cde814e5a43007b31b6a7a6a590738bd1ffa354e9cd2d784b43704ae987b95     60  files/post-template.md
  de7c6209057eaa57162f115c477a83b0663660d61c56b5f578dd160395cb2adc    125  grants.yaml
  b4b33052929417a16eec6d4a685eadd3660466ab2a4d4016a9dd0a4fda2b8887    117  rules/base.json
  e50bfd508dbaae41769260110267dc3049c64640778c082c3fb3e7cdc6016b2c    227  seats/boss.yaml
  fbe59b0f18c45fe4c60493e1a7e209132b2ee1a22805bb885a5a0468aef4797f    181  seats/writer.yaml

# first export against second export: 6 files identical, 0 differ
# the example folder against first export: 6 files identical, 0 differ
```

The example is checked out here with Windows line endings, and the export kept them: the bytes on disk went to the
Gateway and came back unchanged. Then the one line `factory: qa-outreach` was changed to `factory: qa-outreach-copy`,
the folder imported (`This import created the factory's definition.`) and the new id exported:

```
# SHA-256 of every file in <rig>\exports\b (qa-outreach-copy version 1, exported after the import)
  8bbc9357de4267f217e0da00669f739a17b01812a36f279d63f44efadbcabee1    276  factory.yaml
  f1cde814e5a43007b31b6a7a6a590738bd1ffa354e9cd2d784b43704ae987b95     60  files/post-template.md
  de7c6209057eaa57162f115c477a83b0663660d61c56b5f578dd160395cb2adc    125  grants.yaml
  b4b33052929417a16eec6d4a685eadd3660466ab2a4d4016a9dd0a4fda2b8887    117  rules/base.json
  e50bfd508dbaae41769260110267dc3049c64640778c082c3fb3e7cdc6016b2c    227  seats/boss.yaml
  fbe59b0f18c45fe4c60493e1a7e209132b2ee1a22805bb885a5a0468aef4797f    181  seats/writer.yaml

# the folder imported into the new id against the export of qa-outreach-copy: 6 files identical, 0 differ
# the first export of qa-outreach against the export of qa-outreach-copy: 5 files identical, 1 differ (factory.yaml)
```

### Export of a factory with no definition

`qa-live` was registered with two seats, the boss's brief published, a recurring schedule for the boss, a random
schedule for the writer, and a paused trigger for the writer. Its export:

```
qa-live has no definition on the Gateway, so the folder is written from what it has today: its registry entry, published briefs, schedules and triggers (3 files). 6 things it has are not in the folder; each is listed with the file it would have gone in.
files[3]{path,bytes}:
  factory.yaml,100
  seats/boss.yaml,205
  seats/writer.yaml,49
reported[6]{file,message}:
  factory.yaml: No agent or model is recorded for this factory today: every factory seat runs ClaudeCode, on the model its machine uses by default. The folder says agent ClaudeCode and names no model.
  factory.yaml: The factory's folder '<rig>\qa-live-folder' on SOREN_NORTH is not part of a definition: nothing of a factory is kept in a folder of its own. The Gateway holds folders, not repository names, so no repository is listed: name the ones the seats work in under repositories.
  seats/boss.yaml: The schedule 'QA Live boss morning' (cj_9e13fd) is a start here, but its working folder '<rig>\qa-live-folder' is not kept: a seat works in a repository named under repositories in factory.yaml, or in none.
  seats/writer.yaml: The seat has no published brief on the Gateway; its brief is the file 'WRITER.md' in the factory's folder. The brief is left empty here: write it in before importing.
  seats/writer.yaml: The schedule 'QA Live writer at random' (cj_7fe264) is not a start here: it is a random schedule (window=09:00-17:00 perDay=2 minGap=30 shape=human), and a start is a five-field cron expression or an interval.
  seats/writer.yaml: The trigger 'qa-live-new-mail' (paused) is not a start here: its check is the command 'python check_mail.py', and a start's check is a Python file under checks/ in the definition. It runs every 600 seconds on SOREN_NORTH in '<rig>\qa-live-folder' with the prompt: Answer the {count} new mails.
```

Exported twice, the two folders hash the same (`3 files identical, 0 differ`). The export stored nothing.

### An import that fails the check is refused with file and line

That folder, imported as it is - the writer has no published brief - refused, and nothing stored:

```
Error: The folder was not imported: the check found 1 error in the definition of qa-live. Fix each one where it is and try again.
errors[1]:
  seats/writer.yaml:4: 'brief' is '', which is not right. What the seat is told to do. Must not be blank.
[exit 1]

$ cc-devthrottle factory definition history qa-live
count: 0. qa-live has no definition on the Gateway.
```

With the brief written in, the same folder became `qa-live`'s first version: `Imported: version 1 of the definition
of qa-live is a draft (3 files, 377 bytes). This import created the factory's definition.` Two more refusals, each
the check's own words on the file and line:

```
$ cc-devthrottle factory import <rig>\variants\unknown-key
Error: The folder was not imported: the check found 1 error in the definition of qa-outreach. Fix each one where it is and try again.
errors[1]:
  seats/writer.yaml:4: 'colour' is not a key of a seat file. The keys are: seat, name, role, line, machine, agent, model, repository, brief, starts, grants, rules, secrets, skills, files.

$ cc-devthrottle factory import <rig>\exports\a --factory some-other-factory
Error: The folder was not imported: the check found 1 error in the definition of some-other-factory. Fix each one where it is and try again.
errors[1]:
  factory.yaml:1: factory.yaml says the factory is 'qa-outreach', but this definition is being saved for 'some-other-factory'. They must be the same.
```

### An import never publishes

The published version, read before an import:

```
$ cc-devthrottle factory definition get qa-outreach
qa-outreach  version 1  published  written by person device browser qa-browser  2026-10-15T10:48:51.6383223Z  6 files  981 bytes
```

The import:

```
$ cc-devthrottle factory import <rig>\variants\writer-renamed --note "renamed the writer"
Imported: version 2 of the definition of qa-outreach is a draft (6 files, 981 bytes).
Nothing was published: version 1 stays the published definition.
```

And after it:

```
$ cc-devthrottle factory definition get qa-outreach
qa-outreach  version 1  published  written by person device browser qa-browser  2026-10-15T10:48:51.6383223Z  6 files  981 bytes
note: the example
A newer version, 2, is waiting and is not published. Read it with 'get --draft'.
```

A session of the factory may import, and its import is a draft too (`Imported: version 3 ... Nothing was published:
version 1 stays the published definition.`); `get` after it still reads version 1 as published. A session of another
factory is refused in the shared gate's words: `this definition belongs to factory 'qa-outreach', so only a person or
a session of 'qa-outreach' may change or run it`.

### A path mapped to null: 400 naming the path, never a 500

Sent as raw JSON, because the command line never sends a null:

```
$ PUT /factory-definitions/qa-outreach/draft      (raw HTTP, as a person)
  body: {"expectedVersion": 3, "content": {"factory.yaml": null, "seats/boss.yaml": "seat: boss"}}
  status: 400
  answer: {"error":"'factory.yaml' has no text; every file in a definition is text, even if it is empty","outcome":"NotText"}

$ POST /factory-definitions/import      (raw HTTP, as a person)
  body: {"factory": "qa-outreach", "content": {"factory.yaml": "factory: qa-outreach", "seats/writer.yaml": null}}
  status: 400
  answer: {"error":"'seats/writer.yaml' has no text; every file in a definition is text, even if it is empty","outcome":"NotText"}
```

### Nothing to export, and the schemas

```
$ cc-devthrottle factory export qa-outreach --out <rig>\exports\nothing
Error: qa-outreach has no definition on the Gateway and is not a registered factory, so there is nothing to export
[exit 1]

$ cc-devthrottle factory definition schemas --out <rig>\schemas
count: 4. Each schema checks the shape of one kind of file. A save or an import also checks what no schema can: ...
schemas[4]{name,applies}:
  factory.schema.json,factory.yaml
  seat.schema.json,seats/<seat>.yaml
  grants.schema.json,grants.yaml
  rules.schema.json,rules/<name>.json
```

## Tests

- `DefinitionYamlWriterTests` - more than sixty values YAML would otherwise read differently (`true`, `123`, `null`, `a: b`,
  `# x`, a leading or trailing space, a tab, CRLF, a line of spaces, several trailing line breaks, a byte-order mark
  inside, a character pair) each read back by the definition's own parser as exactly that text, after a key, as a
  list item and as the first key of a list item.
- `FactoryDefinitionExportTests` - an all-expressible factory written exactly and accepted by the check; every kind
  of thing a definition cannot hold reported with its file; a seat with no published brief refused by the check on
  its file and line; the same parts in any order give the same bytes.
- `FactoryDefinitionExportImportEndpointsTests` - through the handlers over the real store, check and database:
  export exactly as stored (CRLF and a non-ASCII character kept), twice the same; published over a newer draft;
  `?version`; a missing version and an unregistered factory each a 404 in words; a live export storing nothing;
  import creating the first draft, importing over a published definition as the next draft with the published
  version unchanged, refused by the check with exactly the findings a save gets, refused when the factory named and
  `factory.yaml` disagree or nothing names one, refused for a session of another factory and for a machine token; a
  null path refused with 400 on a save and on an import; and the round trip into a new id.
- `FactoryDefinitionRoutesThroughThePipelineTests` - the three routes through the mapped endpoints with real JSON, the
  null body review 2 sent, and the mapped routes and the session key guard held to the same list.
- `tools/cc-devthrottle/tests/test_factory_export_import_ops.py` - 16 tests against a real local HTTP server: bytes
  written exactly, twice the same, only into an empty folder, never outside it, the reported list printed, `--json`
  keeping one shape, import sending the folder to the import route and never publishing, refusals printed as
  `file:line: message`, and the schemas.

## The check, as run

Run on Windows on 2026-10-15, from this worktree, on the branch based on `origin/main` at `21a049971` (main had not
moved when it was fetched again after the last line). Every command ran in the foreground, one after another. The
first six lines ran at `67c2f7cb3`; the command line then failed two of its own help tests (below), fixed in
`97eefeaf7`, which touches only the command line and its tests, and the Python lines are the run at `97eefeaf7`.

| Command | Result |
|---|---|
| `.\scripts\test-local.ps1` | GREEN at `67c2f7cb3`, tree clean: ten suites, 4,495 tests, every TRX `outcome=Completed`. It names the three parked suites as a coverage gap; the three lines below are those suites' factory tests |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Trigger"` | Passed: 1,944, failed: 0, skipped: 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed: 28, failed: 0, skipped: 0 |
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~InheritedSessionEnvironment"` | Passed: 16, failed: 0, skipped: 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 167 files, 2,068 tests passed. See the note below |
| `uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" --with pytest python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule or trigger"` | 1,162 passed, 3,425 deselected (at `67c2f7cb3`); the whole suite below covers them at `97eefeaf7` |

Also run, beyond the check:

| Command | Result | Why |
|---|---|---|
| the same `uv run ... python -m pytest tools/cc-devthrottle/tests -q` (the whole command line suite) | At `67c2f7cb3`: 2 failed, 4,582 passed. At `97eefeaf7`: 4,584 passed, 3 skipped, 0 failed | The help of every command and the pinned actions list are checked there, outside the `-k` filter. The two failures were this branch's: the `factory export` summary was 85 characters (the limit is 80), and the three new actions were not in the test's list of actions added since the pin |

**The client-core note.** The first `npm test --workspace @devthrottle/client-core` run reported one file failing,
51 of its tests; its name was not captured. The two runs after it, with nothing changed, passed all 167 files and
2,068 tests. This branch changes no TypeScript and nothing the client reads.

**The Python line** is run through `uv` because the machine's own Python has click 8.1.8, below the tool's floor of
8.2.1, as phase 1's report says.

**Not run, and why.** `scripts/test-database.ps1` and `-Parked`: this branch adds no migration and no table. Of the
Gateway unit tests, 162 are this branch's own and run in the filtered line above: 151 in the three new classes and 11
added to the pipeline tests (three facts, eight route rows).
