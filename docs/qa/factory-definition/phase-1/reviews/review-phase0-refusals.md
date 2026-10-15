# Review - Factory Definition phase 1: the check refuses what phase 0 showed an agent cannot take (pull request 4003)

Reviewer: a separate review session, 14 October 2026. Branch `factory-definition/p1-phase0-refusals`, one commit
`1552dd4dc`, read in `D:\ReposFred\_wt\fd-p1-diff` (not edited) and run from a scratch copy made with `git archive`.

## Scope

What I read:

- The whole diff of pull request 4003 (`gh pr diff 4003`) and its description.
- The binding design `DESIGN-phase1-definition-format.md`, sections 3, 7 and 9; the Developer's brief
  `BRIEF-phase1-dev-C2-phase0-refusals.md`; mission document sections 5.2, 5.3, 5.5a, 5.5b and 7; the phase 0 launch
  proofs `docs/qa/factory-definition/phase-0/launch/README.md` in full.
- On the branch: `FactoryDefinitionCheck.cs` (`CheckFactory`, `CheckSeat`, `CheckSkills`, `Items`), the two tables,
  `seat.schema.json` and the `defaults` part of `factory.schema.json`, `DefinitionFixture.cs`, and the three changed
  test files. `FactoryDefinitionChanges.Compare` reads with `FactoryDefinitionCheck.Read` (format stage only), so the
  changes tests that now put Codex on a seat with rules are not hiding a refusal.

What I ran, all in the scratch copy and never in the worktree:

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory.Definition"`: 329 passed, 0 failed.
- Four mutations of the product code, each followed by the same run and then restored:
  - M1, the new rules refusal block removed from `CheckSeat`: 3 failed (the Codex rules test, the Codex default test,
    the Gemini test). Caught.
  - M2, the refusal reported on the first rule's line instead of the `rules:` key line: 0 failed. Not caught. See
    finding 1.
  - M3, the Codex row of `FactoryRulesSupport` changed to "can take": 3 failed. Caught.
  - M4, the ClaudeCode row of `FactorySkillSupport` put back to "not shown yet": 3 failed. Caught.
- Three probes written as throw-away tests in the scratch copy, then deleted:
  - A Codex seat with a block-style list (`rules:` on line 6, `- base` and `- outreach` on lines 7 and 8) is refused
    on line 6, the key line. The product puts the finding where the design says.
  - A boss seat with no agent of its own, on a factory whose default is Codex, carrying `rules: [base]` (line 10)
    and `skills: factory: [mail-voice]` (line 12), is refused twice: line 10 with the Codex rules sentence and line
    12 with the Codex skill sentence. The inherited default reaches both checks.
  - A Codex seat with `rules: []` is not refused. Right: it carries no rule.
- An ASCII sweep of the whole diff: 0 lines with a character outside ASCII.

What I could not reach:

- I did not run `.\scripts\test-local.ps1` or the parked suites; the pull request says the default gate was green.
- I did not re-run the phase 0 proof scripts. The two sentences in the tables are checked against the README's
  wording, not against the machine.

Other routes a rule set could reach a Codex seat, checked and closed: a start has no `rules` key and the seat schema
refuses unknown keys, so a rule set can only be named on the seat's own `rules:`; `defaults.agent` is required and
both `agent` keys are an enum of the eight supported names, checked in the format stage, which stops the references
stage, so `Enum.Parse` in the check never sees an unknown or `RawCli` value; the one `agent` variable at
`FactoryDefinitionCheck.cs:426` (own, else default) feeds both the rules check and the skills check.

Design section 9 against the code: both tables hold one row per agent, ClaudeCode "yes", Codex "no" with the
sentence from section 9 word for word, every other agent "not shown yet" with the sentence from section 9; the
refusal is on the `rules:` key line (`FactoryDefinitionCheck.cs:466`) and on the `factory:` key line (unchanged from
#3997); the document says both. Changing one row is one edit. Repository rules: no fallback, plain English, ASCII
only. The check is a pure function with no logging, as it was before this change.

## Findings

Neither finding is a defect in the product. Both are a guard the tests do not hold.

### 1. No test can tell the `rules:` key line from the first rule's line

- File: `src/CcDirector.Gateway.UnitTests/Factory/Definition/FactoryDefinitionCheckTests.cs`, lines 873 to 889
  (`Check_CodexSeatWithARuleSet_IsRefusedOnItsRulesLine`, `Check_SeatOnACodexFactoryDefault_IsRefusedTheSame`), and
  line 864 (`Check_CodexSeatWithAFactorySkill_IsRefusedOnItsSkillsLine`) for the `factory:` line.
- Failing input: every rules test writes the list in flow style, `rules: [base]`, so the key and its first item are
  on one line. Mutation M2 moved the refusal from `rules.KeyLine` to `Items(rules)[0].Line`
  (`FactoryDefinitionCheck.cs:466`) and all 329 tests passed. The same holds for the skill tests and
  `factory: [mail-voice]`.
- Harm: the design (section 9) and the document both promise the refusal "on the seat's `rules:` line". A later
  change that reports it on an item's line, or on the seat's first line, passes the gate. For a block-style list the
  person publishing is then sent to the wrong line. The product is right today (probe above); the guard is missing.
- What would close it: write one of the Codex rules tests with a block-style list (`rules:` then `- base` on the
  next line) and assert the key's line, and the same for one factory-skill test.

### 2. The factory-skill refusal no longer has a test that reads the factory default agent

- File: `src/CcDirector.Gateway.UnitTests/Factory/Definition/FactoryDefinitionCheckTests.cs`, lines 882 to 890.
- What changed: the pull request removed `Check_FactorySkillOnTheDefaultAgent_UsesTheDefaultAgentsAnswer`, which
  was the only test proving that a seat with no agent of its own takes the factory default for the SKILL table. Its
  replacement, `Check_SeatOnACodexFactoryDefault_IsRefusedTheSame`, proves the default for the RULES table only
  (the boss seat carries rules and no factory skill).
- Failing input: the boss on a Codex default with `skills: factory: [mail-voice]` added. The product refuses it on
  line 12 (probe above), but no test on the branch asks.
- Harm: small, because one variable feeds both checks. The guard is the point: a change that resolved the agent
  separately for skills (as the old test once caught for ClaudeCode) would pass.
- What would close it: add the two skill lines to the boss seat in that test and assert both errors, which makes it
  the one test that proves inheritance for both tables.

## Notes, not findings

- `Check_CodexSeatWithAFactorySkill_IsRefusedOnItsSkillsLine` asserts line 7, the `factory:` line, which is what the
  document says ("refuses the seat on its `factory:` line"). The test's name says the `skills:` line, which is line
  6. Rename it.
- The fixture's Outreach seat is now ClaudeCode with `model: gpt-6-luna`. The check does not pair a model with an
  agent, so nothing breaks; the document's example was given `claude-opus-5-5` and the fixture was not. Cosmetic.
- Phase 0 README finding 4 (a rule path with a leading slash in a `--settings` file anchors at the kit folder, so
  the check or the rules schema should refuse or warn on it) is not in design section 9 and not in this pull
  request. It is for the Tech Lead to place, not a defect of this change.
- The document's ClaudeCode reason, "the rules are merged into the one settings file it is launched with", is
  consistent with README finding 3 (a per-session copy of that file), but a reader of the document alone could take
  it as the shared `hooks-settings.json`. Phase 5's document is the place to say "a copy for that session".
