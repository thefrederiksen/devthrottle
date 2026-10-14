# Factory Definition, phase 2 - factory records on the Gateway: QA report

Machine SOREN_NORTH, 14 October 2026. Branch `factory-definition/p2-records`, rebased onto origin/main `b4f079a0c`.

## What this phase adds

Three record stores on the Gateway, one table each, plus the table holding the bytes:

- `factory_journal_entries` - a seat's journal. Append-only: date, text, the writing session, attachment ids. There is no edit route and no edit method.
- `factory_document_versions` - working documents (the outreach ledger, the playbook, `focus.md`). Every version is kept. A write states the version it read; a stale write is refused with the current text, so the writer can merge. The primary key (tenant, factory, name, version) is the concurrency control, so two replicas cannot both write version 4.
- `factory_attachments` and `factory_attachment_contents` - an attachment's description, and its bytes in their own table, so listing a journal never reads a picture.

The caps are named constants, taken from the phase 0 inventory:

| Cap | Constant | Value |
|---|---|---|
| One working document version | `FactoryDocumentStore.MaxDocumentBytes` | 2 MB (the largest today is 1.66 MB) |
| One attachment | `FactoryAttachmentStore.MaxAttachmentBytes` | 4 MB (2 MB would refuse 16 of 484 real files) |
| All attachments of one factory | `FactoryAttachmentStore.MaxFactoryBytes` | 1 GB |
| One journal entry | `FactoryJournalStore.MaxEntryBytes` | 256 KB |
| Attachments named by one entry | `FactoryJournalStore.MaxAttachmentsPerEntry` | 100 |

Routes are under `/factory-records/{factory}/journal|documents|attachments`. Commands: `cc-devthrottle factory journal list|get|add`, `factory doc list|get|history|write`, `factory attach list|get|add`, and `factory records upload`.

Nothing existing reads these tables in this phase.

## The one-time upload, and who may run it

`cc-devthrottle factory records upload` reads every records folder under `%LOCALAPPDATA%` (seven roots, from the phase 0 inventory) and sends each file as a journal entry, a working document or an attachment. It never deletes or changes anything on the machine.

The upload authenticates with a machine credential. The Gateway accepts that credential ONLY on the three record write routes, and ONLY when the request carries an import key and names its machine in the `X-DevThrottle-Upload-Machine` header. Every row it writes has author kind `upload` and the machine name as its author. The import key (the file's path under its records root) is unique per factory, so a second upload stores nothing new.

It was proved against a local test Gateway (the desktop Gateway, on port 7996, with its own empty storage root). It was NOT run against the hosted Gateway. The full output is in [upload-proof-output.txt](upload-proof-output.txt), and [upload_proof.py](upload_proof.py) reproduces it in one foreground run. File names are not shown in this report or in that output, because the records name prospects and customers. On screen the upload lists every file it does not upload, by name.

### Upload counts equal

First run:

```
Read 1481 = stored 1112 + not uploaded 369 + refused 0. The counts reconcile.
```

Second run, on the same Gateway: every file answers "already stored" and nothing new is written:

```
  clickfunnels-factory  [clickfunnels]  read 869 (318 attachment, 1 database, 22 journal, 363 scratch, 165 working document)
      stored 505 (new 0, already stored 505), not uploaded 364, refused 0
...
Read 1481 = stored 1112 + not uploaded 369 + refused 0. The counts reconcile.
```

The test Gateway's own database afterwards: 85 journal entries + 543 document versions + 484 attachments = 1,112 rows, all 1,112 naming this machine as an upload. It holds 247,830,740 attachment bytes, the same total the rows state.

The records folders were fingerprinted before and after (path, size, modified time): 1,481 files before and after, 0 changed, 0 gone.

### What is not uploaded, counted and listed, never silently skipped

| Kind | Count | Why |
|---|---|---|
| Scratch (`scratch/`, `transcribe/` working folders) | 363 | Working files a factory makes and throws away, not records |
| Databases (`*.db`, `*.sqlite`) | 4 | The stores hold journals, documents and files; the design has no kind for a database |
| `cc-factory/vercel-release` | 2 | The folder belongs to no factory |

The full list, by kind and redacted folder (`<folder>/<first folder>/.../*.ext`), is in the proof output.

## The two-writers proof

Both writers read version 1 of `outreach/ledger.json`. Writer A adds a row and writes; writer B adds a different row and writes after it:

```
  writer A: 200 Written, version 2
  writer B: 409 outcome Stale
    error: "'outreach/ledger.json' is at version 2, not 1; read it again, merge, and write once more"
    current: version 2, text "row-a\nrow-b"
  writer B merges into what it was handed and writes against version 2: 200 Written, version 3
```

Nothing is lost: version 1, 2 and 3 are all kept, and writer B's row reaches version 3 by merging the text it was handed.

## An oversized attachment refused

```
An attachment of 4194305 bytes: 413 outcome TooLarge
    error: "'walk/big.png' holds 4194305 bytes; one attachment holds at most 4194304 (4 MB). Nothing was stored. Make the file smaller - a screenshot at a lower resolution, or as a JPEG - and add it again"
```

## The machine credential refused outside the upload

```
The machine credential writing without an import key: 403
    error: "a factory's records are written by a person, by a session of the factory, or by the one-time upload"
The machine credential writing an upload row without naming its machine: 400
    error: "the one-time upload names the machine it runs on in the X-DevThrottle-Upload-Machine header, and this request did not"
The machine credential reading the documents: 403
    error: "a factory's records are read by a person, or by a session of the factory"
A wrong credential: 401
```

A person or a session that sends an import key is refused too (400, "only the one-time upload names the rows it stores; leave importKey out"). The unit tests cover that case.

## Checks

| Check | Result |
|---|---|
| `.\scripts\test-local.ps1` (default) | GREEN, 10 projects |
| `.\scripts\test-local.ps1 -Parked` | GREEN at `7dbde7ad9` (the change rebased onto `e587b06c0`), 22,121 tests (Gateway.UnitTests 11,946; Gateway.Tests in 4 shards; Core.Tests 4,516) |
| After rebasing onto `b4f079a0c` (phase 1, which adds no migration) | Default gate GREEN at `f95de8daf`; every Gateway unit test under `Factory` (1,191) passing |
| New Gateway tests (stores, routes, the whole pipeline) | 89, all passing |
| New command line tests (`test_factory_record_ops.py`, a fake Gateway) | 17, all passing |
| `tools/cc_shared` gateway tests (the new `headers` argument) | 68 passing |
| `npm run typecheck`, `npm test --workspace @devthrottle/client-core` | Passing (2,068 tests) |
| Entity Framework `has-pending-model-changes`, SQLite and PostgreSQL | Clean |
| `.\scripts\test-database.ps1` (PostgreSQL in Docker) | 50 of 74 passed; see below |

**PostgreSQL.** The two tests that prove this phase's migration on a real PostgreSQL passed in the full run: `Migrate_CreatesGatewaySchemaAndTables_OnRealPostgres` (every migration from nothing, including both new ones) and `Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres` (the census of C-collated key columns, extended with the new tables). In that run, 24 tests in suites this change does not touch could not reach the database ("cannot connect to invalid database", read timeouts, a "could not be opened or migrated" on open), while other sessions' builds saturated the machine's disk. Three re-runs could not start at all: under that load PostgreSQL took longer than the rig's fixed 90-second wait to accept a connection (one shutdown checkpoint took 48 seconds to sync). Once the disk was quieter, those 24 were re-run on their own, at commit `331425437`: `Verdict: outcome=Completed total=24 executed=24 passed=24 failed=0 skipped=0`, `RESULT: GREEN - 24 test(s) executed against the throwaway PostgreSQL, none failed, none skipped.`

**A test that already failed on main.** In `pytest tools/cc-devthrottle/tests -k "factory or schedule or trigger"`, 548 tests fail on the base commit and the same 548 fail here. The 39 extra failures are that same generated test (`test_every_valued_option_GivenNoValue_ExitsTwoAndListsOptions`) applied to the new `factory journal|doc|attach|records` commands. Its help line names the command group instead of the subcommand, for every command, before and after this change. That defect is not in this change.

## Limits

- **Seats are not attributed by the upload.** The local folder names do not match seat ids, so uploaded journal entries and attachments have no seat. Each row keeps its source path as its import key, shown as `source` on a journal entry.
- **On the hosted Gateway the upload credential must be a Director's device key.** The hosted Gateway refuses the shared machine token. No cc-secrets entry for the upload credential exists on SOREN_NORTH yet. The entry the command reads (`CC_GATEWAY_UPLOAD_TOKEN`) is asked for with:

  ```
  cc-secrets ask devthrottle-gateway-upload-token --username SOREN_NORTH --reason "Factory Definition one-time records upload: this machine's Director device key for the hosted Gateway" --uses run
  ```
