# Review - Factory Definition phase 1: the definition store and its migrations (pull request 3998)

Reviewer: a separate review session, 14 October 2026. Pull request head reviewed: `c05d2eaf14f36fbad6a267f486d1e27901a89e91`
(branch `factory-definition/p1-store`, three commits on top of origin/main `b4f079a0c`).

## Scope

What I read:

- The whole diff of pull request 3998 against origin/main: `FactoryDefinitionVersionEntity`, `FactoryDefinitionStore`,
  the `GatewayDbContext` entries (key, lengths, the two PostgreSQL `C` collation lines, the tenant scope), the
  `GatewayHost` registration, the one-line change to `DefinitionPaths`, both migrations with their Designer files
  and both model snapshots, the `PostgresProviderProofTests` entry, and all 876 lines of
  `FactoryDefinitionStoreTests`.
- The template it was built from: `FactoryBriefStore` on origin/main, side by side with the new store, and the
  `AddFactorySeatBriefs` migrations for both providers.
- The binding design (`DESIGN-phase1-definition-format.md`, sections 1 and 8), the Developer's brief, and the
  mission document sections 5.2, 5.3, 5.5a, 5.5b and 7.
- `DefinitionPaths` as merged in #3997, which the store now delegates every path rule and cap to.

What I ran, out of tree (a detached scratch worktree at the pull request head, deleted afterwards; the Developer's
worktree was not touched):

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactoryDefinitionStoreTests"`:
  78 passed, 0 failed, 0 skipped.
- One mutation: removed `&& v.State == FactoryDefinitionStates.Draft` from the publish update's `Where` and ran the
  same 78 tests: 78 passed, 0 failed. That is the evidence for finding 2.
- A text comparison of each migration's Designer body against its snapshot body: identical for SQLite and for
  PostgreSQL, which is what a `dotnet ef` generated migration looks like and a hand-edited one does not. Both
  snapshot diffs add only the new entity (63 and 61 inserted lines, no deletions). Both migrations create one table
  and nothing else, and their timestamps order after every existing migration in both folders.
- A search of the branch for readers of the table: only the store, its entity, the context, the host property and
  the tests name `factory_definition_versions` or `FactoryDefinitions`. Nothing existing reads it.

What I could not reach:

- The database-level race itself (two replicas inserting the same version, the key violation caught and reported as
  stale). The store takes a lock per instance and I cannot pause one writer between its read and its insert, so the
  `DbUpdateException` branch of `Append` is exercised by no test here, exactly as it is exercised by no test in the
  brief store. I read it line for line against the brief store's; the logic, including `WasTakenByAnotherWriter`,
  is the same, and the primary key is proven to exist by `THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION`.
- The PostgreSQL proof (`test-database.ps1`): not run by me; the pull request body reports it green after a re-run.

What I checked and found correct, so it is not repeated below as findings:

- Append-only, key (tenant, factory, version) as the concurrency control; a save states the version it read; stale
  refused with the newest version's summary; a key violation reported as stale only when the head now holds the
  attempted version. All as the brief store.
- Person-only publish and restore, each refused for a session with the design's sentence (section 8, adapted), and
  nothing changes on the refusal. Publishing never backwards; restore writes a new published copy with `RestoredFrom`;
  restore can lose a race through its optional expected version. A session's draft bound at 20 waiting above the
  published version; a person not bound; publishing makes room.
- Caps read from `DefinitionPaths` (256 KB per file in UTF-8 bytes, 4 MB per version, 1000 files, 200 characters per
  path), the path rules and the places asked of the same `ShapeProblem` and `Classify` the check uses, empty content
  refused, a lone surrogate refused rather than silently replaced, the change note capped at 500. Each is tested at
  the cap and one past it.
- Content stored as JSON with keys in ordinal order, so the same map is the same bytes; proven by a test that reads
  the raw column. Round trip byte for byte, including a NUL character, a character pair and an empty file. Because the
  JSON escapes the NUL, the PostgreSQL `text` column never sees a NUL byte, which it would refuse.
- Tenant isolation through the global query filter, with a test that another account sees nothing, cannot publish or
  restore, and starts its own chain at 1.
- Migrations: same table, same key, same columns, same lengths, same nullability on both providers; `C` collation on
  `Factory` and `State` on PostgreSQL, matching the brief table and listed in the provider proof.
- No fallback: a writer kind that is neither person nor session throws; a damaged content row throws; no `catch` that
  returns a default. Logging on every refusal and every write. Plain English and ASCII in the store and the entity.

## Findings

### 1. A path the rules accept can still be one no export can write, and one Windows machine cannot create

File: `src/CcDirector.Gateway/Factory/Definition/DefinitionPaths.cs`, `ShapeProblem` at line 61 to 80 and
`Classify` at line 88 to 110; reached from `FactoryDefinitionStore.WhyNotAPath` at
`src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionStore.cs` line 276 to 279.

The store's own summary says it "holds no path the check would refuse ... so a caller that forgot to run the
whole-definition check still cannot store a definition no export could write". The rules it asks are the design's
section 1 rules, and those rules say nothing about what a file system will accept. So all of these are accepted and
stored:

- `files/nul`, `files/con.txt`, `files/aux.md`, `checks/nul.py`, `checks/com1.py` - Windows reserved device names.
  `FactoryNames.Reserved` (`src/CcDirector.Gateway/Factory/FactoryNames.cs` line 60) already refuses exactly these
  for a memory note, with the reason written beside it: "a single note called aux would stop every later session of
  that factory on every Windows machine until a person found it and deleted it. One agent's write would close the
  factory." The check name pattern `^[a-z0-9]+([-_][a-z0-9]+)*$` matches `nul` and `com1`.
- `files/report.` and `files/report ` (trailing dot or space): Windows strips them on create, so the file lands under
  a different name, or fails.
- `files/a:b.txt`, `files/what?.md`, `files/x|y.txt`, `files/"quoted".md`, `files/a<b>.txt`, `files/a*.txt`: the
  characters `< > : " | ? *` cannot be in a Windows file name at all.
- `files/Brief.md` together with `files/brief.md`: two keys to the store, one file on Windows and on macOS.

The harm. Section 5.5a: the Director downloads a published version's `checks/` and `files/` into
`<storage>/factory-checks/<factory>/<version>/` on the seat's machine before it runs the check. A version holding
`files/nul` or `checks/nul.py` cannot be downloaded on any Windows Director, so that seat never starts, and it is
the PUBLISHED version that fails, which a person then has to repair with a new version. Section 7, phase 3: export,
import, export again must give identical bytes; a definition with `files/Brief.md` and `files/brief.md` exports to
one file on Windows and imports as a different definition, so that check fails for a definition the store accepted.

Where it belongs. The design (section 1) is silent on this, and the rule lives in `DefinitionPaths`, which merged in
#3997, so this is not a deviation by this pull request: the store does what the brief says and asks the check's
rule. But it is this pull request that first makes such a path a stored row, and the fix is one place - `ShapeProblem`
(the characters, the trailing dot or space), `Named` (the reserved names, as `TryNoteName` does), and one rule in
`WhyNotContent` for two paths that differ only by case. The Tech Lead decides whether that goes in here or in the
check's own follow-up; it should not wait for phase 3 to discover it.

### 2. The one guard that makes "set once" true across two replicas is covered by no test

File: `src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionStore.cs` line 216, the
`&& v.State == FactoryDefinitionStates.Draft` condition on the publish update; the `changed == 0` branch at line 222.

The comment above it says this is "the one change a stored row ever sees, and it only ever happens once: the update
matches the row only while it is still a draft, so two people publishing the same version cannot both be recorded as
the one who did." That is right, and it is the only thing that makes it right across two Gateway replicas: the
earlier `wanted.State == Published` check (line 198) runs inside one process's lock and sees a read from before the
other replica's update.

The failing input, with the guard removed: two persons on two replicas publish version N at the same moment. Both read
N as a draft, both run the update, and the second one's `PublishedBy` and `PublishedAtUtc` overwrite the first's. The
record of who published, which the design calls a person's act and the mission's rule R7 rests on, is then wrong, and
it is the first publisher's name that is lost.

What I proved: with that condition deleted, all 78 store tests still pass. The test that reads as if it covers this,
`Publishing_the_published_version_again_is_refused_and_keeps_who_published_it_first` (line 480), is refused by the
earlier in-process check and never reaches the update. The brief asked for "tests that would fail if a rule were
removed"; this rule can be removed without a test noticing. The brief store has the same gap, so this is a repeated
gap rather than a new one, but the definition store is the one that will carry a factory's whole shape.

A test that catches it needs the update to run against a row that was published between the read and the write.
One way without threads or sleeps: publish version N through the store, then mark it back to draft directly through
a context (the test already writes rows directly in `THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_VERSION`),
publish it again by a second person, and assert `PublishedBy` is the first person. That does not test the race; it
tests the guard. Or make the guard a small internal function with the `Where` expression and test the expression's
predicate on a published row and on a draft row, as `WasTakenByAnotherWriter` is tested on its own.

### 3. Note, not a finding: non-ASCII literals in the tests

`FactoryDefinitionStoreTests.cs` lines 115, 255 and 684 hold `caf\u00e9`, `\u20ac` and an emoji as literals. The repository rule
is ASCII only, in code too. The brief store's tests already carry a literal `\u20ac` (line 581 on origin/main) and merged,
and the file compiles and passes, so there is no harm to prove. Writing them as `\u20ac` and `\u00e9` escapes would
keep the rule without changing what the tests prove. I leave it to the Developer.

## Verdict

Nothing in the store, the entity, the context, the host registration or the migrations deviates from the design's
section 8 or from the brief store it was told to copy, and the proof the tests give is the proof that was asked for,
with the one exception in finding 2. Finding 1 is a gap in the path rules the store inherits, with a concrete harm in
phases 3 and 5b, and is for the Tech Lead to place. Neither blocks the store from holding a definition correctly
today, when nothing reads the table.
