# Review - Factory Definition phase 1: the schemas and the whole-definition check (pull request 3997)

Reviewer: a separate review session, 14 October 2026. Branch `factory-definition/p1-check`, one commit `c13795863` on
top of origin/main `e587b06c0` (the worktree was current with origin/main when read). The worktree was read only;
everything run was run in a scratch copy made with `git archive` of that commit, under this session's scratchpad.

## Scope

**What I read, in full:** all eighteen files of the pull request (`gh pr diff 3997 --name-only`): the six source files
under `src/CcDirector.Gateway/Factory/Definition/`, the four schemas, the two-method addition to `CronSchedule.cs`, the
csproj line, the four test files and `docs/factories/definition-format.md`; the pull request body; the binding design
`DESIGN-phase1-definition-format.md`; the Developer's brief `BRIEF-phase1-dev-B-check.md`; mission document sections
5.2, 5.3, 5.5a, 5.5b and 7; and the code the check leans on (`CronSchedule.TryParseCron`, `TryFindTimeZone`,
`TriggerDefinition.MinimumIntervalSeconds` and `MaximumIntervalSeconds`).

**What I ran, in the scratch copy:**

- The pull request's own definition tests, `FullyQualifiedName~Gateway.Tests.Factory.Definition`: 144 passed, 0 failed.
- Thirty-four probe tests of my own (inputs the brief asks about: malformed roots, byte-order marks, merge keys,
  complex keys, directives, deep nesting, a capitalised boolean, an interval at the pattern's top, a misplaced schema
  keyword, a null text, a path holding a newline). The probe source is
  `scratchpad/fd-p1-check-copy/src/CcDirector.Gateway.UnitTests/Factory/Definition/ReviewProbes.cs` and
  `ReviewParserProbe.cs`; their output is `scratchpad/probes.txt`, `nesting.txt` and `parser.txt`.
- A sweep of every added line for non-ASCII characters, carriage returns and abbreviations.

**What I could not reach:** I did not run `.\scripts\test-local.ps1` (the Tech Lead's gate) and did not run the
parked suites; the pull request's claim of a green gate is its own testimony. I did not try nested flow mappings
(`{a: {a: ...`) separately from nested flow lists; they go through the same recursion. The stack-overflow depth will
differ between Debug and Release and between threads of different stack sizes; the fact of the overflow will not.

**Design section 7, item by item.** Every error and every notice exists, on the line that makes the reference, and each
has a test that asserts file, line and words and would fail if the rule were removed (`AssertOneError` demands exactly
one error, so a removed rule gives zero and fails). Parse errors: `DefinitionDocument.cs:85-96` (YAML), `:121-125`
(JSON); duplicate key `:146`, `:304`; anchor `:250`, alias `:242`, tag `:256`. Schema: unknown key `DefinitionSchema.cs:233`,
missing required `:246`, wrong type `:189-212`, bad value `:291-314`. Paths and caps: `FactoryDefinitionCheck.cs:144-160`,
`:213-218`. References: repository `:418`, grant `:431` and `:433`, rule set `:444`, file `:461`, check `:548`, fleet
skill `:497`, factory skill `:479`, boss seat `:389` and `:391`, grant naming a missing seat `:583`, the lists
disagreeing `:585`, skill file with no SKILL.md `:230`, unsupported agent by the schema enum (`seat.schema.json:39`,
`factory.schema.json:58`, tied to `AgentKind` by `Schemas_AgentEnums_AreTheSupportedAgents`). Same-name skill `:494`.
Factory skill on an agent that cannot load one `:485`, from the one table in `FactorySkillSupport.cs:178-188`. Start
rules `:512`, `:514`, `:539`, `:550`, `:557`. Notices: secrets `:450`, `:560`; machine `:619`; repository `:421`; unused
`:593-604`. The cap of 200 plus one `:96-104`. Unknown keys cannot slip through nested objects, list items, merge keys
(`<<` is reported as an unknown key), aliases, tags or JSON (every object schema says `additionalProperties: false`,
and the loader refuses one that does not). Non-mapping roots, empty files, comment-only files, a lone `---`, a
`%YAML` directive, a complex key, tab indentation, quoted keys and a JSON file nested 65 deep all come back as findings,
not exceptions.

Nothing found means nothing found within that scope. The findings follow, worst first.

## Findings

### 1. A seat file nested 2,000 levels deep kills the Gateway process (stack overflow)

**File and line:** `src/CcDirector.Gateway/Factory/Definition/DefinitionDocument.cs:234-317`, the recursive
`YamlTreeReader.Read()` (it calls itself at `:276` for each list item and at `:293` and `:300` for each key and value,
with no depth limit). `DefinitionSchema.cs:318-325` (`Identity`) recurses the same way over the tree but is never
reached, because the reader dies first.

**Failing input:** the fixture's valid definition with `seats/writer.yaml` replaced by

```
seat: writer
name: W
role: W
brief: B
extra: [[[[ ... 2,000 opening brackets ... ]]]]
```

4,047 bytes, far under the 256 KB file cap, so the cap at `FactoryDefinitionCheck.cs:155` lets it through to the parser.

**What happened:** `dotnet test` reported "The active test run was aborted. Reason: Test host process crashed : Stack
overflow." at depth 2,000, 20,000 and 120,000. The same text fed to YamlDotNet's parser in a plain `while
(parser.MoveNext())` loop survived at 120,000 (240,007 events), so the recursion that overflows is this pull request's
own tree reader, not the library. (The block form `- - - x` is refused by YAML syntax at line 5, so flow brackets are
the way in; nested flow mappings `{a: {a: ...` take the same path.)

**The harm:** a stack overflow cannot be caught in .NET. When this check runs on the Gateway (the phase 1 store wires
it into save, publish and restore), one four-kilobyte draft from any session of any account ends the whole Gateway
process, every tenant with it. The brief asked exactly this: "Does anything crash instead of reporting a finding
(deep nesting)?" The JSON side already has the right shape: `Utf8JsonReader`'s depth limit of 64 turns the same
attack into a finding on line 1 ("The maximum configured depth of 64 has been exceeded"). The YAML reader needs an
equivalent: a depth counter in `Read()` that stops with a `DefinitionParseError` on the line where the nesting passed
a small fixed limit, and a test that a file nested past it is a finding, not a crash.

A second effect seen while locating this: the raw YamlDotNet parse of the 240 KB nested file took 40 seconds. A depth
limit that stops reading early bounds that too, since the parser is lazy; worth a test that the deep file is refused
in well under a second.

### 2. `preventOverlap: False` (capital F) on a start with a check is accepted, and is stored as true

**File and line:** `src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionCheck.cs:549`
(`preventOverlap?.Value is DefinitionScalar { Value: "false" }`) and `:712` (`BuildStart`, the same comparison).

**Failing input:** the fixture's `seats/outreach.yaml` (a start with `every: 10m` and `check: checks/new_mail.py`) with
`preventOverlap: False` added to the start.

**What happened:** 0 errors, the definition was built. Design section 7 says a start with a check and `preventOverlap:
false` is an error, and mission 5.5a says "a publish that would turn it off is refused" because it is what keeps
`factory_check_state` to one writer. With `preventOverlap: False` on a start WITHOUT a check, the built model's
`PreventOverlap` is `True`: the opposite of what the file says.

**Why:** the reader deliberately recognises `True`, `TRUE`, `False` and `FALSE` as booleans (`DefinitionDocument.cs:323`,
the YAML 1.2 core schema), and the schema accepts them as `boolean` (`seat.schema.json:103`), but the two consumers
compare the raw text to the one spelling `"false"`. The test `Check_StartWithACheckAndPreventOverlapFalse_IsRefusedOnThatLine`
only tries lower case. Either normalise a boolean scalar's `Value` to `true`/`false` when the reader classifies it, or
compare without regard to case in both places; and add the capitalised case to the test.

### 3. The schema loader accepts a keyword on a type that never enforces it

**File and line:** `src/CcDirector.Gateway/Factory/Definition/DefinitionSchema.cs:119-166` (`Guard`).

**Failing input:** `DefinitionSchema.Load` with a schema whose property `a` is `"type": "array"` carrying
`"pattern"`, `"maxLength": 1` and `"enum": ["q"]`; property `b` is `"type": "string"` carrying `"minItems": 5` and
`"items": { "anything": true, "oneOf": [] }`; property `c` is `"type": "boolean"` carrying `"enum"` and `"pattern"`.

**What happened:** `Load` accepted it, and `Validate` of `a: [one, two]`, `b: hello`, `c: true` reported 0 problems.
None of those keywords did anything. The `items` under the string was never walked, so the unimplemented `oneOf`
and the unknown `anything` inside it were not refused either, which is a hole in the one guarantee the class states
("a schema must never SAY more than this validator DOES", `:21-25`) and the test `Load_UnimplementedKeyword_IsRefused`
is meant to prove.

**The harm today:** none. I checked every keyword's placement in the four shipped schemas: patterns, lengths and enums
sit on strings, `minItems`, `uniqueItems` and `items` on arrays. The harm is the promise: phase 3 serves these schemas
to editors and agents as the contract, and the next edit that puts `maxLength` on an array item's list by mistake
will be a rule the editor shows and the Gateway never applies, with every schema test green. One more loop in `Guard`
(each keyword allowed only on its type, `items` walked wherever it appears) closes it; it is the brief's own
question, "Can the schemas say more than the validator enforces".

### 4. The interval message prints a negative number of seconds

**File and line:** `src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionCheck.cs:627-638` (`IntervalSeconds`,
`number * 3600` in `int`), reached from `:534`. The schema pattern `seat.schema.json:79` allows up to `999999h`.

**Failing input:** the fixture with `every: 600000h` (or `999999h`).

**What happened:** "An interval of 600000h is -2134967296 seconds; it must be between 60 and 86400 seconds." The
outcome is right (refused, since any overflow of a value under 2^32 lands negative) but the sentence is false, and the
design asks for "one plain sentence that says what is wrong". Compute in `long`, or tighten the pattern so the number
cannot pass what the units can hold.

### 5. A file with a byte-order mark is refused with a sentence that contradicts itself

**File and line:** `DefinitionDocument.cs:69` (YAML), `:103-107` (JSON), `FactoryDefinitionCheck.cs:256` (SKILL.md
frontmatter). The design is silent on byte-order marks.

**Failing inputs and what happened:** the fixture's `factory.yaml` with U+FEFF in front: two errors on line 1,
"'<U+FEFF>factory' is not a key of factory.yaml. The keys are: factory, title, ..." and "The key 'factory' is required
in factory.yaml". `rules/base.json` with one: "rules/base.json is not valid JSON: '0xEF' is an invalid start of a
value." `skills/mail-voice/SKILL.md` with one: "must begin with frontmatter: a line '---'" when the file visibly does.

**The harm:** a definition folder written by a PowerShell 5.1 script on this machine gets a byte-order mark by default
(`>` and `Out-File`, as the repository's own instructions note), and whether it survives to the check depends on how
the caller decoded the file (`File.ReadAllText` strips it; `Encoding.UTF8.GetString` over raw bytes keeps it). The
owner then reads that `factory` is both a listed key and not a key. Whichever rule is chosen, strip a leading
U+FEFF or refuse it by name, the sentence must say "byte-order mark" and what to do, and one test should hold it.

### 6. A path may hold a newline or other control character

**File and line:** `src/CcDirector.Gateway/Factory/Definition/DefinitionPaths.cs:60-80` (`ShapeProblem`).

**Failing input:** the fixture plus a file at the path `files/a<newline>b.txt`.

**What happened:** accepted, definition built, and the notice "'files/a<newline>b.txt' is not named by any seat" is
printed across two lines. Design section 1's path rules do not forbid it, so this is a gap in the design as much as
the code; but phase 3's export cannot create that file on any operating system, and a finding is meant to be one
sentence. Low: refuse a control character in a path with its own reason.

### 7. A null file text is quietly treated as an empty file

**File and line:** `src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionCheck.cs:140` (`content[path] ?? ""`).

**Failing input:** the fixture plus `files/null.txt` mapped to `null`.

**What happened:** accepted, built, one notice that the file is unused. A null text can only come from a caller
defect (a request body that bound a missing field), and the repository's rule 3 says fix the root cause rather than
fall back. Low: throw, or report it as an error on that path, so the caller's defect is seen.

### 8. One non-ASCII character in a test source file

**File and line:** `src/CcDirector.Gateway.UnitTests/Factory/Definition/FactoryDefinitionCheckTests.cs:471`,
`new string(<e-acute>, ...)`, the character written as a literal e with an acute accent.

**The harm:** the repository rule is ASCII only, everywhere, because of how Windows tools and logs render anything
else; it is the one non-ASCII character in the whole diff. The test's intent (a two-byte character) is kept exactly by
the escape backslash-u-0-0-e-9 in a char literal.

## Not findings, noted for the Developer

- The abbreviation sweep found only `env` inside a quoted list of settings keys in the people's document
  (`definition-format.md:142`), which names a real key and is fine.
- `every: 10M` (capital unit) is refused by the pattern with the sentence that names `s, m or h`; `timeZone: utc`
  is accepted because the schedules' own lookup accepts it. Both match the design's "the same parser the schedules use".
- A `---` line inside an indented block scalar in the frontmatter does not close the frontmatter early (checked).
