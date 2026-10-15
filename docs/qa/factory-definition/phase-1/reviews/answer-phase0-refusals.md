# Answer - Factory Definition phase 1: the check refuses what phase 0 showed an agent cannot take (pull request 4003)

The Developer's answer to `review-phase0-refusals.md`, finding by finding, plus the one addition the Tech Lead put
into this pull request after the review.

## Finding 1 - no test tells the key's line from the first item's line: FIXED

`Check_CodexSeatWithARuleSet_IsRefusedOnItsRulesLine` now writes the rule sets as a block list (`rules:` on line 6,
`- base` and `- outreach` on lines 7 and 8) and asserts line 6. `Check_CodexSeatWithAFactorySkill_IsRefusedOnItsFactoryLine`
writes the skill as a block list (`factory:` on line 7, `- mail-voice` on line 8) and asserts line 7. The
reviewer's mutation M2 (the refusal on the first item's line) now fails both.

## Finding 2 - no test reads the factory default for the skill table: FIXED

`Check_SeatOnACodexFactoryDefault_IsRefusedTheSameForRulesAndSkills` gives the boss seat, which names no agent, a
factory skill as well as its rule set, on a Codex factory default, and asserts both errors exactly: line 10 with the
Codex rules sentence and line 12 with the Codex skill sentence. It is now the one test that proves the default
reaches both tables.

## Notes

- **The skills-line test's name: TAKEN.** Renamed to `Check_CodexSeatWithAFactorySkill_IsRefusedOnItsFactoryLine`,
  which is the line it asserts and the line the document names.
- **The fixture's model: TAKEN.** The Outreach seat now runs `claude-sonnet-5-5` (a Claude model, and different from
  the factory default `claude-opus-5-5`, so the tests that read a seat's own model still see one).
- **Phase 0 finding 4, the leading-slash path: DONE in this pull request**, as the Tech Lead placed it in design
  section 9. In a rule set, an entry whose tool is not `Bash` and whose argument begins with exactly one slash is an
  error on that entry's line, with the design's sentence (the entry, then `./<rest>` and `//<rest>` as the two ways
  to write it). It runs in the format stage, beside the rule set's own parse, because it is about one file's
  content. Tests: `Read(/secrets/**)` refused on its line with the exact sentence; an entry in a block list refused
  on its own line; `Read(//secrets/**)`, `Read(./secrets/**)`, `Read(~/secrets/**)` and `Bash(/usr/bin/x)` each
  accepted. `docs/factories/definition-format.md` says it.
- **The ClaudeCode reason in the document ("the one settings file it is launched with"): LEFT** for phase 5's
  document, as the reviewer suggests; the launch is phase 5's to describe.

## Checks after the fixes

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory"`: 1369 passed, 0 failed.
- `.\scripts\test-local.ps1`: GREEN, 10 suites, every outcome Completed (4495 tests).
