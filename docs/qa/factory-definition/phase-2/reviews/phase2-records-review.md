# Review - Factory Definition phase 2: factory records on the Gateway and the one-time upload (pull request 4000)

Reviewer: a separate review session, 14 October 2026. Pull request head reviewed: `33142543751c06f20c60f49f54283a46b4b5c549`
(branch `factory-definition/p2-records`; merge base with origin/main is `b4f079a0c`, which is origin/main's head at the
time of this review, so the branch is rebased onto the current main).

## Verdict

One finding, accepted or declined by the Developer: the machine credential is handed a working document's full current
text through a stale write, although every read route refuses it and the code, the tests and the QA report all say it
reads no record. Within the account's own tenant, so no cross-tenant harm; the fix is one line in the route. Everything
else the brief asked me to judge - tenant isolation, route coverage including the session key guard, the stale-write
refusal across replicas, the twin migrations, append-only, the four QA claims - I found correct, with the evidence below.

## Scope

What I read, all from the pull request head or origin/main (never the shared checkout or the Developer's tree):

- The whole diff of pull request 4000: `FactoryJournalStore`, `FactoryDocumentStore`, `FactoryAttachmentStore`,
  `FactoryRecordNames`, the three entities, the `GatewayDbContext` entries (keys, lengths, indexes, the PostgreSQL `C`
  collation lines, the tenant scope), `FactoryRecordEndpoints` (all 564 lines), the `SessionKeyGuard` arm, the
  `GatewayHost` registration, `FactoryRecordDtos`, both `AddFactoryRecords` migrations with their snapshots, the
  `PostgresProviderProofTests` census entries, `factory_record_ops.py` and the `cli.py` commands, the `headers`
  argument added to `cc_shared/gateway.py`, all three Gateway test files (89 tests) and `test_factory_record_ops.py`
  (17 tests), the QA report, `upload_proof.py` and `upload-proof-output.txt`.
- On origin/main, the code the new routes stand on: `AuthMiddleware.Accept` and `AcceptSession` (which context items
  each credential kind stamps), `HostedTenantBoundary.ResolveRequestTenant`, `GatewayEndpoints.ResolveReadTenant`,
  `FactoryNaming.TryAct`, `SessionOriginSurfaces.FromDeviceType`, the memory routes' treatment of a Director
  credential, `GatewayMintedKeyEntity`, and the two existing `string.Compare` queries in Gateway stores.
- The mission document sections 3 (goal 12), 4 (F2, A5, A7), 5.3, 5.6, 6, 7 and 8; the Delivery Lead's rulings in the
  review brief and the delivery log.

What I ran, in my own detached worktree at the pull request head (`D:\ReposFred\_wt\fd-p2-review`, removed
afterwards; the Developer's tree was not touched):

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "Category=FactoryRecords|Category=ReviewScratch"`:
  90 passed, 0 failed, 0 skipped - the 89 shipped record tests plus one scratch test of mine (below), never committed.
- The scratch test `REVIEW_the_machine_token_is_handed_a_documents_current_text_by_a_stale_upload_write`: a session
  writes version 1 of `outreach/ledger.json`; the machine credential's `GET .../documents/read` is 403; the machine
  credential's `PUT .../documents` with `expectedVersion: 0` and an unused import key is 409 `Stale` and its
  `current.text` equals the session's text; the history still holds one version. It passed, which is the evidence for
  finding 1.
- `dotnet ef migrations has-pending-model-changes --context GatewayDbContext` for `src/CcDirector.Gateway` (SQLite)
  and, with `CC_GATEWAY_EF_PROVIDER=postgres`, for `src/CcDirector.Gateway.Migrations.Postgres`: both answer
  "No changes have been made to the model since the last migration."
- `python -m pytest tools/cc-devthrottle/tests/test_factory_record_ops.py -q`: 17 passed, 0 failed.
- A text comparison of the two `Up` bodies (tables, columns, keys, indexes) and of the collation census against the
  context's `UseCollation("C")` lines, by reading, as recorded under "Migrations" below.

What I could not reach:

- The PostgreSQL behaviour of the stores themselves. The QA report's `test-database.ps1` run proved the migration from
  nothing and the collation census on a real PostgreSQL; no test runs the record stores on PostgreSQL, which is also
  true of the memory, brief and definition stores. The date narrowing uses `string.Compare` in a query, which two
  existing Gateway stores (`DevReportStore`, `TurnVerdictStore`) already use on the hosted database, so I take its
  translation as proven by production rather than by a test here.
- The hosted Gateway. The upload was proved against a local desktop Gateway with the shared machine token. On the
  hosted Gateway that token is refused by `AuthMiddleware` (MH-2) and the credential would be a Director's device key.
  I confirmed from `AuthMiddleware.Accept` that a device key stamps `AuthenticatedCredentialItemKey` exactly as the
  shared token does, and that a Director's device type ("workstation" by default) is not a person surface, so the
  route would take the same upload branch - but no run proves it, and the QA report says so.
- The cross-replica race itself (two Gateway processes inserting one version at the same instant). As in the brief and
  definition store reviews, no test pauses one writer between its read and its insert. The database-level refusal is
  proven by `THE_DATABASE_ITSELF_REFUSES_A_SECOND_WRITER_OF_THE_SAME_DOCUMENT_VERSION`, and I read the catch branch
  line for line: a key violation is reported as stale only when the head now holds the attempted version, with the
  head's text attached; anything else is `WriteFailed` with the innermost message.

## Judged, and found correct

**Can the machine-token path write anything outside the three routes, or without the import key?** No. The three
write handlers are the only places `TryWriter` is called; `TryWriter` reaches the upload branch only when the request
carries a verified credential that is neither a person surface nor a session AND names an import key; it then also
demands a well-formed machine header or answers 400. A machine credential with no import key is 403 in words; a
person or a session that sends an import key is 400 in words. Each of those is pinned by a test that reads the status
and the sentence (`FactoryRecordEndpointsTests`), and by the live proof over HTTP. The seven read handlers all pass
`TryReader`, which refuses anything that is neither a person nor a session with 403 - tested on all seven.

**Can a session key write another factory's records?** No. Every handler first passes `FactoryNaming.TryAct` with the
factory from the path, the same gate briefs, schedules, triggers and memory use. A session of another factory gets the
gate's own 403 sentence, a session of no factory 403, a session the Gateway does not yet know 409. Tested through the
handlers with a fake lookup (`A_session_of_another_factory_reaches_none_of_these_records`); the gate itself is
origin/main code with its own tests. The tenant comes from the credential (`ResolveReadTenant`, deny when none), never
from the request, and the stores query through the tenant-scoped context; `One_accounts_documents_are_invisible_to_another`
proves another account sees nothing and can write its own copy.

**Route coverage and the session key guard.** The ten mapped routes and the guard's `IsFactoryRecordRoute` agree verb
by verb: GET/HEAD/POST on `journal` and `attachments`, GET/HEAD/PUT on `documents`, GET/HEAD on the four one-record
paths, nothing else - and `FactoryRecordRoutesThroughThePipelineTests` asserts both lists for all ten routes and for
eight shapes that must not exist (DELETE and PUT on a journal entry, DELETE on documents, POST on documents, PUT on
attachments, the bare prefix). That test also goes through `Map` itself, so the JSON field names the command line
depends on (`expectedVersion`, `contentBase64`, `importKey`, `outcome`, `current`) are proven at the wire, not only in
the handler signature. The guard is added to all three arms (read, create, update) that the brief and memory routes
are in.

**The stale write.** A writer states the version it read; a mismatch is refused 409 `Stale` with the newest version,
text included, under `current`; the primary key (tenant, factory, name, version) makes a second writer's insert
impossible across replicas, and the catch reports it as the same stale refusal with the head's text. A document that
does not exist written expecting anything but 0 is told to expect 0. The command line never looks the version up
behind the caller's back and prints the current text on a stale refusal. Proven at the store (two writers, two store
instances over one file, the database refusing a duplicate key), at the handler (409, sentence, `current.text`), through
the pipeline (JSON), by the command line test (the text printed, the next command named), and live over HTTP in the
proof output (version 2's text handed to writer B, who merged to version 3).

**Migrations.** The SQLite and PostgreSQL `Up` bodies create the same four tables with the same columns, keys and nine
indexes, in the same order, under `gateway` on PostgreSQL; the PostgreSQL twin adds `C` collation on exactly the
columns the context declares (`Factory`, `Seat`, `EntryDate`, `ImportKey`, the document `Name`) and the census test is
extended with exactly those. Both snapshot diffs are pure additions (198 and 208 lines, no deletions), both migrations
order after every existing one, and `has-pending-model-changes` is clean on both providers (my run, above). The
import-key unique index is on a nullable column and relies on both providers passing any number of nulls, which is
stated in the context and true of SQLite and PostgreSQL.

**Append-only.** The journal store's only write is `Append` (asserted by reflection in a test); documents are only ever
inserted as the next version; an attachment is written once with its bytes in one transaction; no route or store
method edits or deletes, and the guard admits no DELETE or PUT on a record. A journal entry may name only attachments
its own factory holds, checked before the insert.

**Caps.** 2 MB per document version, 256 KB per journal entry, 4 MB per attachment, 1 GB of attachments per factory,
100 attachment ids per entry - the Delivery Lead's numbers, as named constants, each tested at the cap and one past it
with the sentence quoted, and the two attachment caps reported on the listing so a seat can see how much is left. The
per-factory total is read before the insert, so two replicas can each pass it at the same instant; the store says so
and the slack is one file per writer, which I agree is harmless for a cap whose purpose is to stop a runaway writer.

**The upload.** Seven roots taken from the phase 0 inventory with the same kind rules in the same order; a folder with
no factory, databases and scratch are counted and listed by name with the reason, never skipped; two files of one
factory whose names fold to one are both held back and listed; a file that is not UTF-8 text is refused by name;
nothing local is written. The reconciliation requires `read == stored + not uploaded + refused` AND `refused == 0`, and
exits non-zero otherwise - and the fake-Gateway test proves a refused file produces "DO NOT reconcile" and exit 1. The
import key is the file's path under its root, unique per factory, so the rerun stores nothing twice; the live output
shows 1,481 = 1,112 + 369 + 0 twice, 1,112 rows in the database all authored `upload` by this machine, the attachment
bytes held equal to the bytes the rows state, and 1,481 files unchanged by fingerprint. The redaction in the committed
output is right: no prospect or customer name appears.

**The four QA claims of section 7** - two writers with the current text, an oversized attachment refused, upload counts
equal, an idempotent rerun - each has a store test, a handler test, a pipeline or command line test, and the live run,
and I read each test rather than its name.

## Findings

### 1. The machine credential reads a working document's current text through a stale upload write

**What.** `WriteDocument` answers every stale write with `current`, text and all - the right answer for a session that
must merge. The upload branch of `TryWriter` goes through the same path. So a holder of the machine credential sends
`PUT /factory-records/{factory}/documents` with `expectedVersion: 0`, any unused `importKey` and the machine header,
naming a document a session has written, and the 409 it gets back carries that document's newest text under
`current.text`. Nothing is written. The credential has read a record the read routes refuse it on all seven routes.

**Proof.** Reproduced in my worktree with a scratch handler test (session writes version 1; the machine credential's
read is 403; its stale upload write is 409 with `current.text` equal to the session's text; history still holds one
version) - see "What I ran". And it is already in the committed proof: section 6 of `upload-proof-output.txt` has the
machine credential, as writer B with import key `qa/3`, handed `current: version 2, text "row-a\nrow-b"`, four lines
before "The machine credential reading the documents: 403".

**Harm.** The class summary of `FactoryRecordEndpoints`, the `TryReader` comment ("it reads no record"), the test
`The_machine_token_reads_no_record_on_any_of_the_read_routes` and the QA report all state an invariant the code does
not keep. On a self-hosted Gateway the machine token is the account's own credential and on the hosted Gateway it is a
Director's device key of the same tenant, so this is a within-tenant read, not a tenant breach - but the Delivery
Lead's ruling admits the machine credential to these routes on the strength of "the upload and nothing else", and
"nothing else" is false. A safety sentence must match the code beneath it; the next person extending the upload path
will read the sentence.

**Suggested shape, for the Developer to accept or decline.** For `Writer.Kind == Upload`, answer a refusal with
`current: null` (or with the summary, `withText: false`). The upload tool never merges - it only reports a refusal by
name - so it loses nothing. Then extend the machine-token read test with this probe so the sentence is held.

## Observations, not findings

- **On the hosted Gateway "the machine credential" is any Director's device key of the account**, and the gate that
  makes it the upload is two caller-supplied fields (an import key and a machine header). The machine name recorded as
  the author is therefore unverified; the self-hosted token has no machine identity to check it against, so the header
  is the only source. Rows are honest about their kind (`upload`), and a Director is the account's own process, so I do
  not call this a defect - but it is a new capability for a Director (memory lets a Director read one list route and
  write nothing), and it is worth one sentence in the mission record.
- **An upload into a document a session has already written is refused as stale**, listed by name, and the counts do
  not reconcile. That is the designed behaviour ("a person decides which stands"); on SOREN_NORTH today nothing has
  written these tables so it cannot happen on the first run, but it will be the ordinary case for any second machine
  uploaded after seats have started writing on the Gateway in phase 5.
- **A `.log` journal file with no date in its name takes its day from its modification time**, and a rerun after the
  file grew is an import conflict, listed by name. Also designed, and the inventory's largest journal file is 33 KB
  against a 256 KB cap, so the one-entry-per-file shape holds today.
- The pull request body's test counts (89 Gateway, 17 command line) match the files.
