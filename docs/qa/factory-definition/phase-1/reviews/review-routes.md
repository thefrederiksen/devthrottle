# Review - Factory Definition phase 1: the definition routes, the command line and the report (pull request 4004)

Reviewer: a separate review session, 14 October 2026. Branch `factory-definition/p1-routes-cli` at `d56da9363`, four
commits on top of `de6b5e8be`, which is `origin/main` at the time of reading (the branch is not behind main).

## Scope

**What I read, all of it from the branch or from origin/main, never from a stale tree:**

- The whole diff of pull request 4004 (20 files, 3,751 lines added): `FactoryDefinitionEndpoints.cs`, the
  contracts in `FactoryDefinitionDtos.cs`, the `GatewayHost.cs` wiring (`FleetSkillIdsOf`, `ConnectedMachinesOf`),
  the three `SessionKeyGuard` allow-list entries and `IsFactoryDefinitionRoute`, `factory_definition_ops.py`, the
  `cli.py` commands and action entries, `docs/cli-reference.md`, both test classes, the Python tests, the rig
  `run_qa.py`, the example folder, `transcript.txt` and the report `README.md`.
- What it builds on, from origin/main: `FactoryDefinitionStore.cs`, the entry of `FactoryDefinitionCheck.Check` and
  `Read`, `FactoryDefinitionChanges.Compare` and `ReadOrThrow`, `FactoryNaming.TryAct` and its refusal,
  `FactoryBriefEndpoints.cs` (the template), `SkillStore.ListPublished`, `DirectorRegistry.ListDirectors(TenantId)`,
  `rules.schema.json`, the guard's structure, and `docs/axi-standard.md`.
- The binding design, the Developer's brief, and mission sections 5.2, 5.3, 5.5a, 5.5b and 7.
- The diff of pull request 4003 (open, not merged), because the report says its two refusals are waiting on it, and
  because finding 1 depends on WHERE that pull request puts one of its rules.

**What I ran, out of tree.** A `git archive` of the branch into my scratch folder, never the worktree:

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactoryDefinitionEndpoints|FullyQualifiedName~FactoryDefinitionRoutesThroughThePipeline"`:
  passed 63, failed 0, skipped 0. That matches the report's count for the two classes (36 and 27).
- `uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" --with pytest python -m pytest tools/cc-devthrottle/tests/test_factory_definition_ops.py -q`:
  38 passed.
- A scan of the diff for any byte outside ASCII: none.

**What I could not reach.** I did not start the rig's Gateway, so the transcript is the Developer's evidence; I
checked each quoted refusal against the sentence the code composes (the check's "The definition was not saved: the
check found N errors ...", the store's two `OnlyAPerson...` constants, `FactoryNaming.ActRefusal`, the store's stale
sentence) and each matches. I did not run the default gate or the mission's other five lines; the report's table
is the Developer's. I did not run anything against PostgreSQL.

**What I checked and found in order (no finding):**

- Who may do what. Every route passes `FactoryNaming.TryAct` with `what = "definition"`, so a session of another
  factory or of none is refused with that gate's sentence (403) on every read and write, and a session the Gateway
  does not know yet gets the 409 "not yet known". A machine token or a Director reads and is refused every write by
  `TryWriter` (403, "written by a person, or by a session of that factory"). A session's publish and restore reach
  the store and are refused there with the two sentences; the store's `SaveDraft` always writes `published: false`,
  so no route lets a session change which version is published. All proven in `FactoryDefinitionEndpointsTests`
  and, with real JSON bodies, in the pipeline tests.
- The guard. `IsFactoryDefinitionRoute` is in the GET, POST and PUT allow lists, shaped exactly as the brief
  route's; the pipeline test holds the eight mapped routes and the guard to one list and holds eleven wrong shapes
  out of both.
- The check runs before every write with the account's real inputs (`FleetSkillIdsOf` lists built-in and tenant
  skills, on or off; `ConnectedMachinesOf` is every registered Director of the account without `StoppedAtUtc`), a
  422 carries `error`, `outcome: "CheckFailed"` and every finding with file and line, and nothing is stored (each
  refusal test asserts `VersionsKept()`). A publish and a restore check again. A session's publish skips the check
  and goes straight to the store's refusal, which is a recorded decision (report, decision 3).
- The command line decides nothing: every refusal and finding is printed as received, `file:line: message`, errors
  then notices, exit 1; "what changes" is printed sentence by sentence as composed. `--json` prints the Gateway's
  answer unchanged, `get --file --json` prints only that file, `history --count/--before` reach the query so the
  filter applies to `--json` too, an empty history prints `count: 0`, no identifier is cut, `--draft` with
  `--version` is a usage error (exit 2) that calls nothing, `save` requires `--expected-version` and never looks it
  up. A file that is not UTF-8 is named and nothing is sent.
- The report. Every phase 1 case in mission section 7 and both "Phase 1 also" cases are in the transcript as the
  real Gateway's answer, with its exit code: draft, publish, second publish, diff, restore; the stale save; the
  session's publish (and restore); the unknown key with `seats/writer.yaml:4`; the dangling reference with
  `seats/writer.yaml:9`; the skill name clash on the line naming the fleet skill; the factory skill on Codex with
  the "not shown yet" reason, which is what the code says until 4003 merges. The two Codex refusals of 4003 are
  correctly marked as waiting, and the rig already has `--phase0` for them. The report says how each caller was made
  and what it does not prove.

## Findings

### 1. A publish or a restore that SUCCEEDS can be answered with a 500, once the format rules tighten - and pull request 4003 tightens them

**File and lines.** `src/CcDirector.Gateway/Api/FactoryDefinitionEndpoints.cs`:
- line 302-303 (`Publish`): `store.Publish(...)` runs, THEN `FactoryDefinitionChanges.Compare(before, wanted!.Files)`;
- line 335-336 (`Restore`): the same order;
- line 247 (`Diff`): `Compare(published?.Files, compared.Files)`.

`Compare` calls `FactoryDefinitionCheck.Read` on BOTH stored versions and throws `ArgumentException` when one does
not parse (`FactoryDefinitionChanges.cs`, `ReadOrThrow`: "The published definition does not parse, so what changes
cannot be shown ..."). Nothing in the endpoints file catches it, and no exception handler is registered in the
Gateway, so the request ends as an unhandled exception: HTTP 500 with no body the command line can print.

**The input that breaks it.**
1. Today, on this branch, a person saves and publishes a definition whose `rules/base.json` holds
   `"deny": ["Read(/secrets/**)"]`. Today's `rules.schema.json` takes any non-empty string, so the check passes and
   version 1 is published.
2. Pull request 4003 merges and is deployed. Its `CheckRulePaths` is called from the `DefinitionPlace.RuleSet` arm
   of the FORMAT stage (`FactoryDefinitionCheck.cs`, the `Parsed` region, diff hunk at line 192), which is exactly
   what `Read` runs. So `Read(version 1)` now returns an error on `rules/base.json`.
3. The person fixes the rule in a new draft (version 2, `./secrets/**`), which the check accepts, and runs
   `cc-devthrottle factory definition publish <factory> --version 2`.

**What breaks.**
- `store.Publish` marks version 2 published. Then `Compare(before = version 1's files, ...)` throws. The person
  sees `Error: ...` with "check the Gateway" and exit 1, while the publish HAS happened. A retry answers 409
  "version 2 is already the published definition". The "what changes" the design promises on every publish
  (mission 5.3) is never shown for the one publish that fixed the problem.
- `GET .../diff` on that factory answers 500 for as long as version 1 is the published one, so the Cockpit's phase 6
  "what changes" view and `cc-devthrottle factory definition diff` are dead for that factory.
- The same for a restore: the copy is written and published, the answer is 500.

The sequencing is not hypothetical: phase 1 will go through it the moment 4003 merges behind 4004, for any
definition published in between that carries a one-slash path. It also repeats with every later tightening of the
format stage. The report's decision 8 ("what changes" is measured against the version published just before) does
not mention that the measurement can throw.

**What a fix would need to decide** (the Tech Lead's call, not mine): either compute the changes BEFORE the store
publishes and, when the old published version no longer parses, answer the publish with the changes against nothing
and a notice saying why ("version 1 no longer passes the format check at rules/base.json:5, so what changes is
listed as from nothing"), or refuse the publish in words before anything is written. Either way a write that
succeeded must never be answered with a 500, and `/diff` must answer in words.

### 2. The "unknown flag fails" test proves nothing for `save`, `publish` and `restore`

**File and lines.** `tools/cc-devthrottle/tests/test_factory_definition_ops.py`, lines 536-543:
`run(verb, FACTORY, "--no-such-flag")` with `assert result.exit_code != 0` and `gw.calls == []`.

**The input that breaks it.** For `save`, the command has no `--dir` and no `--expected-version`; for `publish` and
`restore`, no `--version`. Each of those is a required option, so the command exits 2 for the missing option whether
or not `--no-such-flag` is refused.

**What breaks.** If the group's unknown-option refusal were removed for these commands, the test stays green: the
AXI rule the test is named for (`docs/axi-standard.md`, "an unknown flag fails loudly") is proven only for `get`,
`history` and `diff`. The fix is to pass the required options alongside the unknown flag so the only reason to exit
is the flag, and to assert the "No such option" sentence.

### 3. A person publishing the version that is ALREADY published is told "was not published" when the account changed since

**File and lines.** `src/CcDirector.Gateway/Api/FactoryDefinitionEndpoints.cs`, lines 296-302: the check runs on the
wanted version BEFORE the store's own rulings (`AlreadyPublished`, `OlderThanPublished`).

**The input that breaks it.** Version 1 is published and names the fleet skill `browsers`. The owner removes that
skill from the account. The owner runs `publish --version 1` (a second time, by mistake or from a stale screen).

**What breaks.** The answer is 422: "Version 1 was not published: the check found 1 error in the definition of
<factory>. Fix each one where it is and try again." Version 1 IS the published definition; the store would have said
so (409, "version 1 is already the published definition"). The person reads a false statement about which version
is live and may conclude the factory has no published definition. Running the store's cheap rulings first, or wording
the 422 as "is published and now carries 1 error", would tell the truth. The same order affects a publish of a
version older than the published one.

## Noted, not findings

- The four read routes (`ReadPublished`, `ReadNewest`, `History`, `ReadVersion`) write no log line on entry, success
  or 404; only the writes and `Diff` do. The template brief routes are the same shape, so this is parity with what
  shipped, not a regression, but it is below the repository's logging rule.
- The report's mission line for Python (`-k "factory or schedule or trigger"`) ran at `97dd6e9cc`; after the help
  summaries were shortened in `3f1103bef` only two files were re-run. No C# changed after `97dd6e9cc`, so the C# gate
  stands. My out-of-tree run of the definition tests on the final commit passed (38 and 63 above).
- The report's quoted responses elide timestamps and repeated lines (`2026-10-14T23:...Z`, `...`); the transcript
  beside it carries the full text, and the report says so.
- `ConnectedMachinesOf` counts a Director that died without saying goodbye until the registry drops it, so the
  "no Director on <machine> is connected now" notice can be silent for a machine that is really off. The report
  records this (decision 6); a notice never refuses, so there is no harm to prove.
