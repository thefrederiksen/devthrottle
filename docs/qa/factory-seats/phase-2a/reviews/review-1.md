# Factory Seats, phase 2a - review 1: the brief store, its routes and its command

Date: 2026-10-13. Reviewer: a Reviewer seat opened by the Implementation Lead (a different agent from the
Developer). Branch reviewed: `origin/factory-seats/phase-2a-brief-store`, four commits ahead of `origin/main`.
The review lives on `factory-seats/review-2a`, cut from that branch in a worktree; the shared checkout was
never touched.

## Verdict

The change does what the mission document's phase 2a asks, and the places where harm would be real held up
under reading: I could not find a route by which one account reads or writes another account's brief, a
session publishes, or a session outside the factory writes at all. One finding is returned, medium: nothing
bounds how far one seat's brief history can grow, every session of the factory can grow it, and the history
route hands back all of it. Its harm is real but it is a bound to set, not a defect in what was built; the
decision belongs to the seat that built this work.

## Scope

**What I read, in full:** the store (`src/CcDirector.Gateway/Factory/Briefs/FactoryBriefStore.cs`), the six
routes (`src/CcDirector.Gateway/Api/FactoryBriefEndpoints.cs`), the entity and the data context changes, both
hand-written migrations (the Entity Framework one for local and the one for PostgreSQL), the answer bodies
(`src/CcDirector.Gateway.Contracts/FactoryBriefDtos.cs`), the host wiring, the session key guard entries, the
command line module (`tools/cc-devthrottle/src/factory_brief_ops.py`) and its five commands in `cli.py`, the
command reference section, all three new test files (store, routes through handlers, routes through the mapped
endpoints), the command line tests, and the Developer's proof (`docs/qa/factory-seats/phase-2a/README.md`) -
read as self-testimony and checked, not believed.

**What I read to compare, because the new work is shaped on it:** `FactoryMemoryStore` and its routes,
`FactoryNaming` (the gate that decides who may act on a factory's rows), the session key guard as a whole, and
the authentication middleware's one place that stamps a request's identity (a session key cannot dress itself
as a person's device: the middleware sets the device stamp only for a verified device key, and says so in its
own comment).

**What I did not read line by line:** the two generated migration Designer files (several thousand lines each;
I verified instead that both snapshots, both migrations and the entity configuration agree on the key, the
index, the column lengths, the text types and the collations), and the rest of the two-thousand-line data
context beyond the changed parts.

**What I ran:** the seventy new brief tests of the Gateway unit test suite, filtered, on this machine
(`dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~FactoryBrief"`): seventy passed,
none failed. The command line tests for the brief (`tools/cc-devthrottle/tests/test_factory_brief_ops.py`):
nineteen passed and eleven failed - every failure is this machine's `click` being 8.1.8 where the tool declares
a floor of 8.2.1, the first version that hands back standard error separately from standard output; the
already-merged factory memory command tests fail the same way on this machine, so this is the machine and not
the branch. The refusals the failing tests pin still showed their non-zero exit codes in the output I could
see.

**What I could not reach:** the full Gateway unit test suite, the hosted Gateway test suite, the database
test suite against a real PostgreSQL (the Developer reports it green; I did not repeat it), the web and phone
suites, and anything the Developer reported about Linux, which I did not re-run. The Developer's claim that
one Cockpit test fails on the branch was not checked; the branch touches no web file, so the claim that it is
not this phase's is consistent with the file list, but I did not run that suite.

## The places where harm would be real, and what I found

**One account reading or writing another account's brief.** The account comes from the request's own
credential, never from the path, and every read and write goes through a context scoped to that account, whose
global filter drops other accounts' rows before any query the store writes. The composite key includes the
account, so two accounts holding the very same factory and seat identifiers each own their own chain - there
is no cross-account squat. Both new test files pin this with two accounts holding identical identifiers; I
read the tests and the code behind them and found no gap between them. Nothing found.

**A session publishing, by any route, header or credential.** Publishing and bringing back decide what every
later run of the seat is told, so the mission's proposal P3 makes them a person's acts. The guard lets a
session key reach those two routes on purpose, and the refusal happens in words one layer in - exactly the
pattern the factory memory restore already uses. The person test inside the routes is the same one the
factory memory routes and `FactoryNaming` use: a verified device key of the phone or the browser kind. A
session key cannot forge that stamp; the authentication middleware sets it in exactly one place, on a
verified device credential, and a session key goes through a different arm that deliberately does not set it.
The store itself refuses a publish or a bring back from any author that is not a person, so a second caller of
the store would have to invent a new author kind, and an invented kind is rejected as a defect rather than
accepted. The only way the store writes a published row is the person-only publish and the person-only bring
back; a draft save cannot produce one. Nothing found.

**A session outside the factory writing at all.** Every route, read and write, first passes
`FactoryNaming.TryAct`, the one gate the factory's schedules and triggers already use, with the factory named
in the path. A session of another factory, or of no factory, is refused in that gate's own words on all six
routes; a session the Gateway has no record of yet is told to try again rather than judged. The reading of who
is asking is not a second one invented for briefs: it is the same device stamp, the same session stamp, the
same factory lookup, and the person's identifier comes from the same helper the memory routes use. The
account's shared machine credential may read and may not write, because a version records who wrote it and
that credential is neither a person nor a session - refused in words, and pinned by a test. Nothing found.

**The append-only promise and the stale-save refusal.** A save never edits a row: it inserts the next version,
and the primary key on account, factory, seat and version is what stops two writers, including two replicas
of the hosted Gateway over one database, from both minting the same version - the second insert cannot exist,
and the failure is told apart from a real storage failure by checking whether the wanted version now exists,
so a caller is never sent to merge against a writer that never was. A writer states the version it read and is
refused with the newest version riding the refusal. The one change a stored row ever sees is the publish mark,
and the update matches the row only while it is still a draft, so two people publishing the same version
cannot both be recorded. Bringing an older version back adds a copy as the newest version rather than moving
anything backwards, and can lose a race with a draft saved in between when the person states the version they
read; when they state nothing it goes after whatever is newest, which is the memory store's own rule and is
written on the command. Publishing backwards is refused and the refusal says to bring the version back
instead. Nothing found.

**The migrations.** The local and the PostgreSQL migrations agree with each other and with the entity and both
snapshots: same table name, same four-column key, same tenant index, same column lengths and text types, and
the PostgreSQL one carries the "C" collations the context asks for on the three exact-match columns. The
database test suite now asserts those collations for this table. Nothing existing reads the new table - I
searched the whole source tree for the data set and found only the store, the context, the migrations and the
tests. An empty table answers every read as "no brief on the Gateway", which is the state every existing seat
starts in, and that is pinned by tests. Nothing found.

**The command line claiming success when the Gateway refused.** It does not. Every refusal path exits with
code one and prints the Gateway's own sentence, plus what to run next; an answer that should carry a saved or
published version but does not is itself reported as a failure rather than shown as a success; a missing file
calls nothing. The command never looks up the version it sends, so it cannot overwrite text its caller never
saw. Tests pin the refusal sentences and the exit codes. Nothing found.

## The finding

**Medium. Nothing bounds the growth of one seat's brief history, every session of the factory can grow it, and
the history route returns all of it.** A save adds a version of up to 65,536 bytes. There is no cap on how many
versions one seat may hold, no cap on their total size, and no limit on how often a writer may save: factory
memory caps a factory's whole memory at 512 kilobytes and a hundred notes and prunes old versions, while the
brief store, by the mission's own goal that every version is kept, prunes nothing - the Developer's proof says
so honestly, and the decision to prune nothing is right. But "every version is kept" rules out pruning, not a
bound on writing. The writers include a session key of the factory, an autonomous credential: an agent that
decides to save a draft on every turn, or one stuck retrying, can add rows without end, and each row is a
whole version of the brief's text. The harm lands on the hosted Gateway, where several accounts share one
database: the table grows without limit for as long as the loop runs, and `GET .../history` - which any person
or session of the factory may call - returns every version's whole text with no page and no cap, so the read
that shows the history grows with it until the answer itself is the outage. What must change is a bound, and
which bound is the Implementer's call: a cap on the versions one seat keeps of a draft that was never published
(for instance, only the newest unpublished draft counts against a per-seat total), a limit on how often a
session may save, or a page on the history route. Until one of those exists, the store's own promise - "every
version is kept, so an older one can always be brought back" - is also its unbounded liability. I did not
change anything; a review never fixes what it finds.

## Observations, which are not findings

- **No person can publish anything until phase 2b exists.** The command line holds only a session key, so its
  `publish` and `restore` always refuse - by design of P3, printed in the Gateway's own words, and written
  into the command reference. The person's surface is the brief panel phase 2b builds. So phase 2a merged on
  its own is inert: drafts can be saved and nothing can ever be published. That matches the mission's phase
  order and is recorded here so nobody reads "publish is built" as "publish is reachable".
- **The two publish and bring back commands in the command line can never succeed.** They exist to teach: an
  agent that tries is told to save a draft and let the owner publish. If the Implementation Lead would rather
  the mission document's "the same in `cc-devthrottle factory brief`" mean a person at a terminal can publish,
  that needs a person's credential on the command line, which the tool does not hold today - a decision to make
  before phase 2b, not a defect in this phase.
- **The history route answers with every version's whole text**, as factory memory's does. Because the brief
  store never prunes, this is the growth surface named in the finding above; factory memory can afford it
  only because its history is pruned to about fifty versions.
- **The Developer's proof is honest where it matters.** Its "what this does not prove" section names the same
  gaps I found by reading: no request went through a real running Gateway, no two replicas raced in a live
  test, and no brief test ran against PostgreSQL. I found nothing in it that overclaims.
