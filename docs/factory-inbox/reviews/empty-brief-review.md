# Review: pull request 4606 - a published definition with a blank brief is refused in the check's own words

Reviewed 6 November 2026 by a separate review session. Worktree `devthrottle-seat-brief`, branch
`fix/factory-seat-empty-brief-guidance`, one commit 153c91624 on top of origin/main 19f42f2f1. The
worktree was not edited.

## Scope

Two files, 10 lines added, 15 removed:

- `src/CcDirector.Gateway/Factory/Definition/FactorySeatStart.cs` - removes the per-seat blank-brief loop that
  pull request 4602 added after the parse refusal in `ReadPublished`, trims the trailing full stop off the joined
  error text so the refusal does not read "..  Publish", and leaves a comment saying why the loop is not needed.
- `src/CcDirector.Gateway.UnitTests/Factory/Definition/FactorySeatStartTests.cs` - the test from 4602 now
  replaces the whole `brief: |` block (not just the key line) with `brief: ''`, and asserts the exact refusal
  text with `Assert.Equal` instead of two `Assert.Contains` on words the loop would have produced.

Questions asked of this review: is the loop unreachable on every path that reads a published version, so removing
it loses no protection; does the owner still get clear guidance; does the test prove it.

## What was checked

**Why main is red.** Run 42166081596 on main (sha 19f42f2f1) has exactly one `[FAIL]`:
`FactorySeatStartTests.ReadPublished_APublishedVersionWithASeatHavingAnEmptyBrief_RefusesItWithClearGuidance`.
The assertion failure text is `Not found: "seat 'boss' with no brief"` against a message beginning
`version 1 of the definition of 'devthrott...`. That is the FIRST refusal in `ReadPublished` (the "cannot be read"
one), not the loop's. So on main the loop never ran even in the test written to exercise it. The claim in the pull
request matches the evidence.

**Is the loop reachable at all.** Traced in the worktree (which is origin/main plus this one commit):

1. `FactorySeatStart.ReadPublished` calls `FactoryDefinitionCheck.Read(version.Files)` and throws when
   `read.Definition` is null. The loop sat after that throw, so it could only run on a definition the check built.
2. `FactoryDefinitionCheck.Read` builds a definition only when `ReadFormat` returns non-null, and `ReadFormat`
   returns null whenever any finding was added during its run (`return findings.Count > errorsBefore ? null : parsed;`).
3. Every seat file goes through `ParseAndValidate`, which runs `DefinitionSchema.For(Seat).Validate` and returns
   null when the schema reports a problem.
4. `Schemas/seat.schema.json` lists `brief` under `required` and gives it `"pattern": "\S"` with the description
   "What the seat is told to do. Must not be blank." `DefinitionSchema.CheckString` reports a pattern miss as an
   error: `'brief' is '', which is not right. What the seat is told to do. Must not be blank.` A missing brief is a
   required-key error; a `brief:` with nothing after it is a type error; a brief of only spaces or newlines fails
   `\S`. There is no shape of blank brief the schema lets through.
5. The rule is not new: `git log -S'Must not be blank'` on the schema file shows it in the file's first commit,
   b4f079a0c on 14 October 2026 (Factory Definition phase 1). No version could ever have been published under a
   seat schema without it, so 4602's premise ("definitions published before this check existed") describes a
   version that cannot exist. The store itself does not validate on `Publish` (the test shows `store.Publish`
   returning Ok for a blank brief), but every reader that builds a `FactoryDefinition` goes through the check.

So `definition.Seats` can never hold a seat whose `Brief` is blank, and the loop was dead code on every path.

**Every path that starts a seat reads through the same door.** `FactorySeatStart.ReadPublished` is wired as
`definitionOf` in four places in `GatewayHost.cs` (the cron starter, the talk, the seat start, the check starts) and
read directly by `FactoriesScreenEndpoints`. The three places a started seat is given its brief
(`DirectorCronSessionStarter.cs:223`, `FactorySeatStartEndpoints.cs:105`, `FactoryTalkEndpoints.cs:116,168`) all
call `published.BriefOf(seat)` on the `PublishedFactoryDefinition` that `ReadPublished` returned. 4602's commit
touched only `FactorySeatStart.cs` and its test, so "session startup re-validation" in its message WAS this loop
and nothing else; there is no second validation site that this pull request leaves unguarded.

One reader does not go through the check: `FactoryDefinitionBriefs.InVersion` reads the seat's brief text straight
out of the stored file for the brief panel (`FactoryBriefEndpoints`, `FactoriesScreenEndpoints`). It never called
the removed loop, so this pull request changes nothing there; it is a display path, not a seat start. Out of scope.

**Does the owner still get clear guidance.** The surviving refusal is:
`version 1 of the definition of 'devthrottle' is published but cannot be read; the definition has validation
errors: seats/boss.yaml:4 'brief' is '', which is not right. What the seat is told to do. Must not be blank.
Publish a version that passes the check (cc-devthrottle factory definition check).`
That names the file, the line, the key, the rule, and the command to run. It is better than the loop's message,
which named the seat and file but no line and no command. The `TrimEnd('.')` is what stops the text reading
`Must not be blank.. Publish`.

**Does the test prove it.** Yes, and it is now an honest test. On main the replacement `Replace("brief: |",
"brief: ''")` left the indented line `  Run the factory.` dangling under a scalar, so the fixture it built was a
YAML format error, not a blank brief - the test was not exercising the rule it was named for. The new replacement
removes the block body too, so the file is well-formed with `brief: ''` on line 4 (seat, name, role, brief), and
`Assert.Equal` on the full message pins the schema refusal, the line number and the guidance. Run locally in the
worktree: `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactorySeatStartTests"` -
16 passed, 0 failed, including this test and the sibling `...ThatDoesNotParse_IsRefusedInWords`, which still asserts
"is published but cannot be read".

## Findings

None blocking.

Non-blocking, for the author's judgement, no change required:

- `errors.TrimEnd('.')` strips every trailing full stop, so a finding that ended in "..." would lose all three.
  No finding does today (`Shorten` truncates values in the middle of a message, never at the end), so this is
  cosmetic.
- `Assert.Equal` on the whole message couples the test to the schema's description text. That is the point of the
  test (the words the owner reads), so it is the right assertion; it will need updating if the description changes.

## Verdict

APPROVE. The loop was unreachable on every path that reads a published version, because the format stage never
builds a definition from a seat file the schema refused, and the schema has refused a blank brief since the day it
existed. Removing the loop loses no protection. The owner's guidance is clearer than before, naming file, line,
rule and command. The test now builds the input it claims to build and pins the exact refusal, and it passes
locally with the rest of its class. Merging this makes main green again: that test is the only failure in main's
latest completed run.
