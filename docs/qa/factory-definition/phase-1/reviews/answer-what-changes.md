# Answer - Factory Definition phase 1: what changes between two versions (pull request 3999)

The Developer's answer to `review-what-changes.md`, finding by finding. The branch was rebased onto origin/main
before these changes.

## Finding 1 - starts paired by position: FIXED

`CompareStarts` no longer pairs by position. Both sides are first put in one fixed order taken from what each start
is (kind, cron, time zone, interval, check, prompt, prevent overlap, secrets), never from where it is written. Each
published start that is not written the same in the draft then takes the closest unpaired draft start of the SAME
kind - a time with a time, an interval with an interval - preferring the same prompt, then the same check, then the
same time zone. A start whose kind changes is reported as one start switched off and another created, never as
moved.

Tests: `Compare_StartAddedAboveAMovedOne_GivesOneTrueAccountWhateverTheOrder` runs the reviewer's two inputs (the
mail check written above and below the moved morning round) and asserts both give the reviewer's second, true
account exactly. `Compare_StartKindChanges_IsSwitchedOffAndCreatedNotMoved` asserts a time start replaced by an
interval start.

## Finding 2 - "too different" note on a file only added or only grown: FIXED

`DefinitionTextDiff` no longer runs the search when either changed middle is empty: the script is then all added or
all removed, matched exactly, and the "too different to match" note cannot appear. It appears only when both sides
hold changed lines and the search passes `MaxEditDistance`.

Test: `Compare_LongFileAddedOrGrown_IsNeverCalledTooDifferent` adds a 1200-line check and grows a two-line text
by 1200 lines; each note is the "not shown" sentence alone (800 and 802 lines, as the reviewer measured).

## Finding 3 - ask removed not asserted: FIXED

`Compare_AskRemoved_IsLoosened` asserts "Rule set base loosened: ask Bash(npm publish:*) removed." All six
directions are now asserted to the exact sentence.

## Observations

- **Summary joins rule sets and seats by name: LEFT.** The summary is one line and names where rules changed; the
  rule and seat entries below it say which is which ("Rule set outreach ..." or "Seat Outreach (outreach) ...").
  Prefixing every name in the summary would double its length for a collision the details already resolve.
- **"factory settings changed" on a first publish: TAKEN.** It now reads "10 factory settings set".
- **Nested parentheses in "(was claude-opus-5-5 (the factory's default))": LEFT.** True, and rare (a seat losing a
  model that came from a default that is itself removed); any other wording makes the common case, "machine
  SOREN_NORTH (the factory's default) -> devlinux (the factory's default)", read worse.
- **`ReadOrThrow` throws without a log line: TAKEN.** It now writes
  `[FactoryDefinitionChanges] Compare FAILED: the <which> definition does not parse: <file>:<line> <message>` first.
- **Fallback above `MaxEditDistance` reports whole-middle counts: LEFT, as the design's own trade.** The note says
  why, and with finding 2 fixed it appears only when both texts really changed in more than 1000 places.

## Checks after the fixes

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory"`: 1238 passed, 0 failed.
- `.\scripts\test-local.ps1`: see the message to the Tech Lead and the pull request.
