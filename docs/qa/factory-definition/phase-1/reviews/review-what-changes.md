# Review - Factory Definition phase 1: what changes between two versions (pull request 3999)

Reviewer: a separate review session (bed08308), 14 October 2026. Read only; nothing in the Developer's worktree was
touched.

## Scope

**What I read.** The four files of pull request 3999 at its head commit 17d841afa (branch
`factory-definition/p1-what-changes`): `FactoryDefinitionChanges.cs`, `DefinitionTextDiff.cs`,
`FactoryDefinitionChangesDtos.cs` and `FactoryDefinitionChangesTests.cs`. Against them: the binding design
(`DESIGN-phase1-definition-format.md`), the Developer's brief, mission sections 5.2, 5.3, 5.5a, 5.5b and 7, and
Developer B's merged code the fold builds on, read from origin/main (`FactoryDefinition.cs`, `DefinitionPaths.cs`,
`FactoryDefinitionCheck.cs` including `Read` and `Build`, `FactoryScheduleText.cs`, `DefinitionFixture.cs`).

**What I ran**, in a detached scratch worktree of the same commit under my own scratch folder, never in
`D:\ReposFred\_wt\fd-p1-diff`:

- The pull request's own tests: 38 passed, 0 failed.
- A fuzz of the private edit script against a reference longest-common-subsequence table: 5000 random pairs of
  0 to 24 lines over alphabets of 2 to 5 distinct lines. Every script was valid (each old and new line consumed
  exactly once, every equal pair truly equal) and shortest. Myers and its backtrack are right.
- Timing on files at the 256 KB cap (60,000 to 80,000 lines): every shape from "all lines the same" to "1000 edits
  exactly at the limit" to "2000 edits, over the limit" took 19 to 59 ms. A whole compare of 15 such files took
  656 ms. Bounded, as the brief asks.
- Determinism: the same two maps in reversed order give byte-identical JSON, including on a file of repeated
  lines.
- Probes of the change kinds the tests do not assert (ask removed, a grant's seats changed, a seat's secrets and
  factory skills changed, bossSeat, goalApprovedOn and defaults.model removed, a rule set removed while its seat
  drops it, a cron that is not a named shape, "1-5" to "MON-FRI", a file whose line endings alone change, a whole
  seat removed). Every sentence was right except where finding 1 says otherwise.

**What I could not reach.** The full local gate (`.\scripts\test-local.ps1`) is the Tech Lead's check and I did not
run it. There is no client yet, so how the sentences render was not seen. ASCII: the four files hold no byte
outside printable ASCII; no abbreviations found.

**One thing outside the code.** origin/main has moved to 1528ca20e (phase 0 launch-line proofs, #3996) since the
branch was cut from b4f079a0c. Nothing under `src/CcDirector.Gateway/Factory`, `Contracts` or the Factory tests
changed in between, so a rebase is routine, but the branch is behind.

## Findings

### 1. Starts are paired by position, so the same two starts written in a different order give two different accounts, one of them false

`src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionChanges.cs`, lines 182 to 198 (`CompareStarts`: "The
rest are paired in the order written").

**Input.** The fixture's boss seat, published with one start:

```yaml
starts:
  - cron: "0 8 * * 1-5"
    timeZone: Europe/Copenhagen
    prompt: Morning round.
```

compared with a draft where the owner adds a mail check at the top of the list and moves the morning round to
nine:

```yaml
starts:
  - every: 15m
    check: checks/new_mail.py
    prompt: Mail.
  - cron: "0 9 * * 1-5"
    timeZone: Europe/Copenhagen
    prompt: Morning round.
```

**Output** (run in the scratch worktree):

```
Boss: start moved, Weekdays 08:00 Europe/Copenhagen -> Every 15 minutes.
    check set to checks/new_mail.py ; prompt "Morning round." -> "Mail."
Boss: start created, Weekdays 09:00 Europe/Copenhagen.
    prompt "Morning round."
```

The same draft with the two starts written the other way round gives:

```
Boss: start moved, Weekdays 08:00 Europe/Copenhagen -> Weekdays 09:00 Europe/Copenhagen.
Boss: start created, Every 15 minutes.
    check checks/new_mail.py ; prompt "Mail."
```

**Harm.** The first account is not what happened: the morning round did not become a fifteen-minute mail poll
with a different prompt, and no nine o'clock start was created from nothing. This is the sentence the owner reads
at the publish gate, verbatim (rule 7), to decide whether to publish. And two drafts that define exactly the same
seat report different changes depending on where in the list a person wrote the new start, which is the kind of
order dependence the brief rules out. No reordering is needed to hit it: inserting a new start above an existing
one while touching the existing one is enough.

**What would fix it** (the Developer decides): pair the unmatched starts by closeness before position. A cron
start pairs with a cron start and an interval start with an interval start; within a kind, prefer the one with the
same prompt, or the same check; only a leftover is created or switched off. A start whose kind changes (cron to
every) reads truer as "switched off" plus "created" than as "moved". A test: the two inputs above must give the
second output, whichever order the starts are written in.

### 2. The "too different to match" note is false on a file that is only added, or only grows, by more than 1000 lines

`src/CcDirector.Gateway/Factory/Definition/DefinitionTextDiff.cs`, lines 90 to 97 (Myers is run even when one
middle is empty) and lines 53 to 54 (the note).

**Input.** The fixture plus a new `checks/big.py` of 1200 lines (any content). Also `DefinitionTextDiff.Compare(null,
text)` for the same text, and `Compare("a\nb\n", "a\nb\n" + text)`, a check that grows at the end.

**Output.**

```
Check checks/big.py added: 1200 lines.
note: The two texts are too different to match line by line (more than 1000 changes), so the changed part is
shown removed in full and then added in full. 800 more lines of this difference are not shown; at most 400 are shown.
```

The grown file gives the same note with "802 more lines".

**Harm.** A sentence the owner reads verbatim that is not true: there are not two texts, nothing is removed, and
the lines were matched exactly (the counts are right). It will appear on the common case of a new or extended
check, skill or template over 1000 lines, where it reads as a warning that something went wrong with the compare.

**Cause.** With one side empty Myers needs exactly m steps to reach the end, so at 1001 or more lines it gives up
at `MaxEditDistance` and takes the fallback, which happens to produce the right script. **What would fix it**: when
either middle is empty the script is all-added or all-removed and Myers need not run; the note then appears only
when both sides hold lines. A test: a file of more than `MaxEditDistance` lines added to a definition has a null
`Note` apart from the "not shown" sentence.

### 3. The tests leave one of the brief's six rule directions unasserted: an ask entry removed

`src/CcDirector.Gateway.UnitTests/Factory/Definition/FactoryDefinitionChangesTests.cs`, the Rules section (lines
289 to 386). Deny added, deny removed, ask added, allow added and allow removed are each asserted to the exact
sentence; ask removed is not. The brief asks for "rules tightened and loosened each way". I probed it and the
sentence is right today ("Rule set base loosened: ask Bash(y) removed."); the finding is only that nothing would
catch it going wrong. Low.

## Observations, not findings (no harm proven; for the Developer to take or leave)

- The summary line joins rule sets and seats by name, so a rule set and a seat that share a name collapse into
  one word: with the fixture, "rules tightened on outreach and writer" names the rule set `outreach`; the
  first-publish summary's "base, outreach and boss" has `outreach` standing for both the rule set and the seat.
  The detail lines disambiguate.
- On a first publish the summary says "10 factory settings changed" where nothing changed; "set" would read
  truer.
- "model removed (was claude-opus-5-5 (the factory's default))" nests parentheses; true, but awkward.
- `ReadOrThrow` throws without a `FileLog` line; the repository's logging rule asks that errors be logged. The
  route that calls it will probably log the exception, so low.
- The fallback above `MaxEditDistance` reports a 256 KB file with 1001 changed lines as "59941 lines added, 59941
  removed" with the note explaining why. That is the design's own trade and the note is honest about it; noted
  only so the Tech Lead knows the shape.

Everything else the brief asks for is there and was checked: seats added, removed and changed with each field in
words; a display-name rename as a change to that seat; brief line diffs with context, line numbers, a cap and a
"not shown" count; starts created, moved and switched off in the schedule's own words; rules tightened and
loosened in the right direction for every kind, and a seat taking or dropping a rule set; grants added, removed
and changed by allows, limits and seats; every factory.yaml field; files, checks and skills with the full line
diff; "Nothing changes." on equal versions; a layout-only change never reported as nothing; nothing left for a
client to decide.
