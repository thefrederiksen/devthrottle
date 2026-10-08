# Review of Item B - the factory CEO becomes the Boss, with no name of its own

Reviewer session: Factory Design and Improvements - Reviewer - Item B, 8 October 2026.
Worktree reviewed: D:/ReposFred/devthrottle-fdi-b, the uncommitted changes on branch factory-boss-rename
(41 modified files, 4 untracked migration files), on top of commit 3f07647c3.

## Scope

What I read:

- The whole diff of the 41 modified files: the contracts, the entity and context, the two model snapshots,
  the fold, the registry store, the Talk seed, the session key guard comments, the shipped terminology
  skill and its `.claude/skills` copy, the Cockpit (views, routes, fixtures, tests), client-core, the
  command line (`cli.py`, `factory_registry_ops.py`, their tests), `docs/cli-reference.md`, and the ten
  committed manifests under `docs/missions/factories-screen-2026-10-06/manifests`.
- The four untracked migration files (SQLite `20261008200248_RenameCeoSeatToBossSeat` and PostgreSQL
  `20261008200341_RenameCeoSeatToBossSeat`, each with its Designer file).
- The design report in the Item A worktree, Item B and the decisions card.
- A sweep of the whole tree (`git grep`) for `ceoSeat`, `CeoSeat`, `NoCeoText`, `CeoText`, `CeoLatest`,
  `noCeoText`, `fa-ceo`, the removed fold helpers, the word CEO and the word head in the factory code of
  the Gateway, the contracts, the Cockpit, the mobile app, client-core and the command line, and the six
  person names the bosses used to carry.

What I ran, all green:

| Proof | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~SessionKeyGuardTests\|FullyQualifiedName~Skills"` | 1009 passed, 0 failed, 1 minute 11 seconds |
| `npm test -- src/factory` in `apps/cockpit` | 87 passed in 5 files |
| `npm test -- src/factory` in `packages/client-core` | 17 passed in 2 files |
| `python -I -m pytest tests/test_factory_registry_ops.py tests/test_axi_step_6c_help_and_errors.py -p no:cacheprovider` in `tools/cc-devthrottle` with the click 8.2.1 Python | 542 passed, 3 skipped |

The Skills filter includes the test that fails when the shipped terminology body and the `.claude/skills`
copy differ; I also diffed the two bodies myself with the frontmatter stripped: identical.

What I could not reach:

- `src/CcDirector.Gateway.Tests` (the route test that registers through HTTP and the PostgreSQL proofs):
  not run, as instructed, because of the machine-wide lock. The changes to `FactoryRegistryRouteTests.cs`
  were read by eye only.
- Neither migration was applied to a real database. I checked the migration bodies, and that each Designer
  file's target model is byte for byte the provider's model snapshot (a diff of the two `Build` methods
  is empty for SQLite and for PostgreSQL); the snapshots carry `BossSeat` and no `CeoSeat`.
- No live Factories screen was opened.
- I did not run revert proofs. Where I say a test would go red on revert I am reading its assertions,
  not a run.

## Checks the brief asked for

1. A boss shown by a person's name, or CEO or head as a product word: none in product strings. The fold's
   words are "No boss named", "Latest from the boss", "Talk to the boss", "Starting the talk with the
   boss...", "Only seats the boss hired are listed", "This factory has no boss named.", "Nothing from the
   boss in the last 7 days."; the page header is the boss seat's role word ("Boss", or "CFO" for Center
   Consulting); the Seats tab row and the Talk seed and session name say "the boss" / "Boss". The command
   line says boss in its help, its `register` print-out, its `list` fields and the actions catalogue. The
   leftovers are in comments and test names only - findings 2, 3 and 5.
2. Alias or fallback: no reader of `ceoSeat` or `CeoSeat` remains anywhere outside the terminology
   skill's "older names" table and the mission-history folders. The fold's duplicate-name rule is gone.
   One silent path remains at the Gateway boundary - finding 1.
3. Migrations: both `Up` are `RenameColumn("CeoSeat" -> "BossSeat")` on `factory_registry` (PostgreSQL
   with schema `gateway`), both `Down` are the reverse `RenameColumn`; no drop, no table rebuild, so the
   stored registrations keep their boss. Both snapshots and both Designer models carry `BossSeat` with
   max length 64 and the provider's column type, matching the entity and the context mapping.
4. Tests that pin the change: the store's no-name rule is tested (`RefusedManifests` row "boss named a
   person": a seat named "Nora Hale" on the boss seat is refused, the message contains `set its name to
   "Boss"`, and nothing is stored) - remove the store check and that row goes red. The fold's "never a
   name" rule is pinned by `Boss_WithTheRoleWordBoss_SaysBoss_AndNeverANameEvenWhenTheRegistryHasOne`,
   which registers a boss seat named "Nora Hale" and asserts no "Nora" in the header, the Talk labels or
   the card - revert the fold and it goes red. The seat row's busy label is pinned both ways (row 0 "the
   boss", row 1 "Savings Engineer"). The Talk seed is pinned for the boss, for a boss with a distinct
   role (CFO) and for an ordinary seat. One test lost its subject - finding 5.
5. ASCII, abbreviations, attribution: the only non-ASCII bytes in the changed files are the UTF-8 byte
   order marks on the two generated migration files, their Designer files, the two snapshots and
   `SessionKeyGuard.cs`; every prior migration file and the snapshot at HEAD carry the same mark, so it is
   the generator's convention, not new. The one accented word in `test_axi_step_6c_help_and_errors.py`
   is at HEAD and untouched. No "Co-Authored-By", "Generated with", Claude or Anthropic anywhere in the
   diff. Prose is plain English; CEO and CFO appear as the role words being discussed.

## Findings

### Finding 1 (low) - the Gateway still accepts the old key `ceoSeat` and registers the factory with no boss, silently

`RegisterFactoryRequest` is bound by the default JSON options, which ignore an unknown member; the
Gateway has no `UnmappedMemberHandling` setting anywhere. A PUT to `gateway/factory/registry` whose body
still says `"ceoSeat": "ceo"` therefore binds `BossSeat = null`, passes every store check (a boss is
optional), and stores the factory with no boss. The screen then says "No boss named" and the Talk button
is gone, with no error anywhere.

The only refusal of the old key is in the command line (`factory_registry_ops.py` refuses any key outside
`MANIFEST_KEYS`, naming the allowed keys), which is good and is tested. But the design says "a manifest
with the old key is refused with the fix", and that is true only when the request comes through a
cc-devthrottle built from this change. A machine whose cc-devthrottle is older than this change, or any
session that PUTs its own JSON, re-registering a factory after the Gateway is deployed un-bosses that
factory without a word. The route is open to every session key, so this is reachable.

Harm proved: a silent default on the old name, which is exactly what item (2) of the brief asks to find.
The fix is one check in the store or the route (refuse a body that carries `ceoSeat`, with the fix in the
message) and one unit test; or a strict JSON binding for this request. Low because the one shipped
writer already refuses.

### Finding 2 (low) - client-core's contract still describes the boss by a person's name and as "head"

`packages/client-core/src/factory/factoriesScreenClient.ts` lines 79 and 81, on `FactoryListRow`:
"The factory head's Talk button ("Talk to Ruth Calder"), whatever the head's title." and "\"No head
named\" when talk is null." This is the contract the Cockpit codes against, and it now documents words the
Gateway no longer sends and a product word (head) the ruling retired. Not a history note. The line 14
comment on `busyLabel` ("Starting the talk with Nora Hale...") is still right for an ordinary seat row.

### Finding 3 (low) - the entity's history note records the wrong old name

`src/CcDirector.Gateway/Data/Entities/FactoryRegistryEntity.cs` line 31: "Was `BossSeat` until 8 October
2026 (renamed, with its column, ...)". The old name was `CeoSeat`. A history note that names the new name
as the old one will mislead whoever reads the migration later.

### Finding 4 (low) - the Cockpit fixtures and the command line test fixture still show bosses by name

- `apps/cockpit/src/factory/fixtures.ts`: the list rows carry `talk.label` "Talk to Nora Hale" and "Talk
  to Ada Brennan" with busy labels naming them, the page carries "Talk to Nora Hale", and the Seats fixture
  names the boss seat "Nora Hale" with role "Boss". `FactoriesScreen.test.tsx` line 112 asserts the button
  "Talk to Nora Hale". The Cockpit renders verbatim so the tests pass, but these fixtures no longer show
  what the Gateway emits, and the brief said the fixtures follow.
- `tools/cc-devthrottle/tests/test_factory_registry_ops.py` `REGISTERED`: the boss seat is `"name": "Nora
  Hale", "role": "CEO"` with `bossSeat: "nora-hale"` - a registration the Gateway now refuses, presented as
  the Gateway's answer. The `_manifest` helper likewise sends a boss seat named Nora Hale; the command line
  does not check names, so it is harmless, but the example is now wrong.

Harm: the next person reading the fixtures to learn the screen's words learns the old ones. No runtime
effect.

### Finding 5 (low) - test names and comments that describe current behaviour still say CEO or head, and one test lost its subject

- `FactoryOwnerActionsTests.List_AnArchivedFactorysCeo_NoLongerMakesALiveCeoReadAsADuplicate` with the
  comment "both had Max Ridley as CEO; with Tallyhand archived the button names him". The duplicate-name
  rule it was written to pin no longer exists; the test now asserts "Talk to the boss", which is what the
  label says whether or not Tallyhand is archived. It still proves an archived factory is not listed
  (`Assert.Single`), but its name and comment claim something the code no longer does. Either rename it
  to what it now proves or delete it.
- `FactoriesScreenFoldTests`: the class summary (line 9, "the CEO's Talk button"), the `Linked` helper
  summary (line 51, "the WarmForward CEO"), the section header at line 214 ("the CEO's button"), and the
  test name `Page_CeoLatest_IsTheCeosNewestLines_NotOtherSeatsOrStarts` (line 365) for a property now
  called `BossLatest`.
- `FactoriesScreenRound2Tests`: the class summary (line 10, "the factory's head may have any title") and
  the section header at line 361.

The brief allows comments that record history; these are not history, they describe the tests as they
stand. No runtime effect.

### Observations, not findings

- The manifests folder README (`docs/missions/factories-screen-2026-10-06/manifests/README.md`) still
  says "CEO Malik Grant", "No CEO in the definition: `ceoSeat` left out" and "the screen will say No CEO".
  It is the survey record of 6 October and was already out of date before this change (it says
  `cc-factory` has no `ceoSeat`; the manifest had one). History, left as it is.
- Where the product names a seat in a sentence ("Goal number - posted by Boss, today 06:20", "Boss failed
  today 05:09 (America/Toronto): ...", the waiting line "Boss, today 06:20: ..."), it reads the registered
  seat name, which the store now guarantees is "Boss". That obeys the ruling - never a person's name - and
  stays correct because the store refuses anything else. "posted by the boss" would read more naturally;
  a choice, not a defect.
- The brief says eleven committed manifests; the folder holds ten manifests and the README. All ten have
  `bossSeat` or none, no `ceoSeat`, and every boss seat is named "Boss" (role "Boss", or "CFO" for Center
  Consulting). Machine Care and mindzie Web have no boss, as before.

## Summary

Five findings, all low. The rename is complete in the product's words, the data path and the command
line; the migrations are correct and match the snapshots; the no-name rule is stored, enforced and
tested; the two terminology copies agree. The one thing worth fixing before merge is finding 1 (the
Gateway's silent acceptance of `ceoSeat`), because the design promises a refusal there and the screen
would show a factory with no boss and no error. The other four are stale words in comments, test names and
fixtures.
