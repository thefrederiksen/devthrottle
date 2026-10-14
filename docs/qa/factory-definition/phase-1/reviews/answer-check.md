# Answers - review of pull request 3997, the schemas and the whole-definition check

The Developer's answer to each finding in `review-check.md` beside this file. All eight are accepted and fixed; each
fix has a test that fails without it.

## 1. A seat file nested 2,000 levels deep kills the Gateway process - ACCEPTED, FIXED

The YAML tree reader now carries a depth and stops at `DefinitionDocument.MaxDepth` (64, the same limit
System.Text.Json applies to JSON), with a finding on the line where the nesting passed it: "Lists and mappings are
nested more than 64 deep here; a definition never needs that. Flatten it." The deepest real definition nests four.
Because the parser is lazy, stopping early also bounds the time the reviewer saw (40 seconds for the raw parse).

Tests: `Check_YamlNestedDeeperThanTheLimit_IsAFindingNotACrash` at 2,000 and 120,000 levels (the depths that crashed
the test host in the review), and `Check_YamlNestedToTheLimit_Parses`, which shows 64 lists parse and 65 are refused.
No timing assertion was added: the brief forbids the real clock in these tests, and the 120,000-level case finishing
at all, inside a run that completes in under a second, is the bound.

## 2. `preventOverlap: False` accepted on a start with a check, and stored as true - ACCEPTED, FIXED

The reader now holds every true/false value as `true` or `false` however it was written (`True`, `FALSE`), so every
consumer compares one spelling; the two comparisons in the check are unchanged and now right. Tests:
`Check_StartWithACheckAndPreventOverlapFalseInCapitals_IsRefused` (`False` and `FALSE`) and
`Check_PreventOverlapFalseInCapitals_IsStoredAsFalse`.

## 3. The schema loader accepts a keyword on a type that never enforces it - ACCEPTED, FIXED

`DefinitionSchema.Load` now allows each constraining keyword only on the type that enforces it (`properties`,
`required`, `additionalProperties` on objects; `items`, `minItems`, `uniqueItems` on arrays; `enum`, `pattern`,
`minLength`, `maxLength` on strings; none on booleans), and refuses anything else: "the keyword 'maxLength' does
nothing on type 'array', so the schema cannot use it there." `items` on a string is refused this way, so the
unwalked `items` with `oneOf` inside it can no longer pass. The four shipped schemas load unchanged. Test:
`Load_KeywordOnATypeThatDoesNotEnforceIt_IsRefused`, five cases taken from the review's own probe.

## 4. The interval message prints a negative number of seconds - ACCEPTED, FIXED

`FactoryDefinitionCheck.IntervalSeconds` computes in `long`, and the model's `SeatStartDefinition.EverySeconds` is a
`long?`, so `Read` (which builds the model without the bounds check) cannot overflow either. Test:
`Check_HugeInterval_SaysItsTrueLengthInSeconds` ("An interval of 600000h is 2160000000 seconds; ...").

## 5. A file with a byte-order mark is refused with a sentence that contradicts itself - ACCEPTED, FIXED

Refused by name, not stripped (stripping would make the stored text and what the check read differ, and the
repository's rule is to fix the cause rather than absorb it). A YAML file, a JSON file and a SKILL.md that begin with
one get one error on line 1: "<path> begins with a byte-order mark. Save it as UTF-8 without a byte-order mark."
Files under `files/` are not parsed and are not affected. Tests: one each for `factory.yaml`, `rules/base.json` and
`skills/mail-voice/SKILL.md`.

## 6. A path may hold a newline or other control character - ACCEPTED, FIXED

`DefinitionPaths.ShapeProblem` refuses any control character with its own reason, "it holds a control character,
such as a line break", and the path is shown with line breaks turned to spaces so the finding stays one sentence on
one line. Test: `Check_PathHoldingALineBreak_IsRefusedInOneLine`.

## 7. A null file text is quietly treated as an empty file - ACCEPTED, FIXED

A null text now throws an `ArgumentException` naming the path ("The definition's content holds no text for
'files/null.txt'; every path must map to a text."), so a caller's binding defect is seen rather than absorbed. A
throw rather than a finding, because the content did not come from the person writing the definition. Test:
`Check_NullText_IsTheCallersDefectAndThrows`.

## 8. One non-ASCII character in a test source file - ACCEPTED, FIXED

The literal is now the escape `'é'`. The byte-order mark tests use the named constant
`DefinitionDocument.ByteOrderMark` rather than a literal, and a sweep of every file in the pull request finds no
non-ASCII character.
