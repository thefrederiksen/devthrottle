# Phase 2 review: the Gateway's display ruling for API-mode sessions (pull request #4086)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 2. Reviewer seat, opened by
the phase 2 Tech Lead (session 138). Reviewed at the pull request head `6e427877c`, on the branch
`api-mode-p2-display-fold`, stacked on `api-mode-p2-receipts`. Only what this pull request adds over
the receipts branch was reviewed: commits `7254aea19`, `5d4ba388f` and `0f82ecede` and their merges
of main. Date: 16 October 2026.

## Verdict

**Accept, with two findings.** Neither blocks the merge on its own; finding 1 is the one the
Tech Lead's brief named as a requirement and the code does not meet it, so the Developer should
answer it before the phase is called done. Everything else the brief asked me to check holds, and
the proof for each is written below so the next seat can check it rather than read it.

## Scope: what I read, what I ran, what I could not reach

**Read in full:** `ApiModeSessionFold.cs`, `ApiModeRowStamp.cs` (both the facts reader and the
stamp), `ApiModeLiveRuling.cs`, `ApiModeDisplayDtos.cs`, the `Words` and `Tone` additions to
`ApiModeReceiptDtos.cs` and `SessionReceiptsEndpoint.cs`, the `ApiMode` property on `SessionDto.cs`,
the wiring in `GatewayEndpoints.cs` and `GatewayHost.cs`, the `schema.ts` entries, and the three test
files (`ApiModeSessionFoldTests.cs` with its `ApiModeSessionVerdictTests` class,
`ApiModeLiveRulingTests.cs`, `ApiModeVerdictOnTheRosterTests.cs`). For context I read the whole event
model (`event-model.md`, all five rules and the Delivery Lead's failure-case ruling), the receipt
store, the event buffer's feed interface and its bounds, the ingest, the fixture data the tests
build their sequences from, and the project instructions' rule 7 (the client is dumb, the Gateway owns all ruling).

**Ran:** the build, and `dotnet test src/CcDirector.Gateway.UnitTests --filter
"FullyQualifiedName~ApiMode"`: 205 tests, 204 passed, 1 failed. The one failure
(`PushApiModeEventsHubTests.PushApiModeEvents_AfterHello_StoresTheReceiptsAndTheEvents_AndAnswersTheWatermark`)
is in a file this pull request does not touch (it is the receipts pull request's), it failed in the
test class constructor on a unique-key collision in the device registry's import marker when run
beside the other tests, and it passed when run alone. See observation A below; it is not a finding
against this pull request. I did not run `scripts/test-local.ps1`, as instructed.

**Could not reach:** the live stream (#4083) that will consume `ApiModeLiveRuling` is not in this
pull request, so "the live ruling's state per reader when it starts mid-stream" was checked on the
ruling's own contract and tests, not on a real subscriber. The Director's event push does not exist
yet in this repository (no caller of `PushApiModeEvents` outside the Gateway), so whether a Director
re-sends events the Gateway never acknowledged is not checkable; see observation B. The client
schema file is generated from a running Gateway (`gen:api`), so I checked its shape against a
precedent in the same file rather than regenerating it.

## What the brief asked me to check, and what I found

- **Rule 7, the client is dumb.** Every field on `ApiModeSessionDisplay` is a finished string or a
  boolean, plus the two counts and `Kind` and `Tone`, which are documented as style-lookup keys
  only. Every `ApiModeLiveItem` carries its finished text, status words and colour hint. The receipts
  read carries `Words` and `Tone` ruled by the same static function the live ruling uses, so the two
  readers cannot disagree. Nothing is left for a client to decide. Holds.
- **A terminal-mode row is byte-for-byte unchanged on both routes.** `ApiMode` is marked
  ignore-when-null and the stamp assigns null to every row that is not in API mode.
  `GetSessions_ATerminalModeSession_IsByteForByteTheRowWithoutApiMode` compares the raw serialised
  row from a Gateway with the facts wired against one without, through the real route on an in-memory
  server; `GetSession_ById_...` proves the property is absent on the single-session route. Holds.
- **Honesty after a Gateway restart.** The connection fact, which lives only in the in-process
  buffer, reads null with a "Not known" sentence after a restart
  (`Stamp_AfterAGatewayRestart_...`). Working or idle and the turn number are rebuilt from the
  durable receipts, which the ingest writes before the buffer; the fold says "Not known" for an open
  turn only when the wheel report contradicts it. That is honest on the facts the Gateway holds. Its
  one assumption is that the receipts are not behind reality across the restart; see observation B.
  One wording defect in the "Not known" sentence is finding 2.
- **Agreement with the event model's rules.** Rule 1 (one ending per prompt): the live ruling's
  `IsFinal` keeps the first ending and logs the second, as the store does
  (`Rule_ARefusalAfterDelivery_TheFirstEndingStands`). Rule 5 (the turn's outcome beside Delivered):
  `TurnEnd` rewrites the receipts of the prompts the turn carried, and a reader seeded from stored
  receipts does the same (`Rule_SeededFromStoredReceipts_...`). Cancelled versus failed: "Delivered -
  stopped" in yellow against "Delivered - the turn failed: reason" in red, and "The last turn was
  stopped" against "The last turn failed" on the row. Rule 3 (every tool card closes): a card whose
  start the reader missed is closed with a title that says so, never a guess. The queue: the count
  and the Director's reason, grouped when the reasons differ. Holds.
- **Tenant isolation.** The receipts are read inside the request tenant's scope and the feed is
  read by tenant; `Stamp_AnotherAccountsSessionWithTheSameId_FoldsNothingOfIt` proves a second
  account with the same session id folds nothing of the first. Holds.
- **Thread safety.** The fold is pure and static. `ApiModeLiveRuling` is documented as one instance
  per reader, called from one thread, which is what the feed's synchronous per-session delivery
  gives a subscriber. The receipt store's read is under its own lock. Holds; the lock's cost is
  finding 1.
- **Cost of stamping on `GET /sessions`.** Does not hold: finding 1.
- **Plain English.** Every user-facing string is a plain sentence. "API mode" is the mission's own
  product name, used in the issue title, so I do not count it. Holds.
- **The tests prove what they claim, to the test standard.** No port (an in-memory test server), no
  Docker, no process, no sleep, an injected clock everywhere a clock is read, and the harness's
  SQLite file for the store. The fixture sequences are the event model's Pi mapping of the recorded
  wire logs. The roster tests read the wire by the camel-case names the clients read. Holds.

## Findings

### Finding 1 (medium): `GET /sessions` does one database read per API-mode row, under the receipt store's lock

**Where:** `ApiModeRowStamp.Stamp` calls `facts.Read(t, s.SessionId)` once per API-mode row, and
`ApiModeDisplayFacts.Read` calls `ApiModeReceiptStore.Read(sessionId)`, which opens a database
context and runs one query inside `lock (_gate)`.

**The harm.** The brief named this as a requirement: "no per-row database round trip". With K
API-mode sessions in an account, every roster read runs K queries, one after another, and every
connected client polls the roster. `_gate` is the same lock every receipt write takes, so a roster
read with many API-mode rows holds the hub's event ingest off the store for its whole run, and a
burst of ingest holds the roster read. Roster latency then grows with the number of API-mode
sessions and with ingest load, on the route every client reads most. Today the fleet has no
API-mode sessions, so nothing is slow yet; the design is what fails, and it fails on the day the
feature is used at the scale it exists for (one API-mode session per seat across a fleet).

Two smaller costs ride along: the fold's `turnOrder.Contains` on a `List<string>` is quadratic in
the number of receipts per session (a session kept for the 90-day retention can hold thousands), and
each `Read` materialises every receipt of the session, including the verbatim acknowledgements of up
to 64 kilobytes each, to compute a verdict that needs only the state, the turn id, the outcome and
the queue reason.

**What a fix looks like (the Developer decides).** The repository already has the shape: the
factory agent chip is stamped from `FactorySessionStarts.Read(tenant, sessionIds)`, one read for all
the ids on the roster. A batch read keyed by session id, projecting only the columns the fold needs,
removes the per-row round trip. Alternatively the feed's own documentation describes a reader that
keeps per-session state through `SubscribeAll` and starts from the receipts; a verdict cache fed that
way would make the roster read free of database reads altogether.

Developer's answer: **Fixed.** Stamping a roster now costs one database query for all its API-mode rows, and that
query takes no lock.

- `ApiModeRowStamp.Stamp` first gathers every API-mode row, then calls `ApiModeDisplayFacts.ReadReceipts` once with
  all their session ids, then folds each row. A roster with no API-mode row reads nothing at all.
- The read is the new `ApiModeVerdictReceipts.Read(tenant, sessionIds)`: one query (`session_id IN (...)`) on a context
  created for the given account, so the global tenant filter keeps every other account's rows out. It reads straight
  from the database, not through `ApiModeReceiptStore`, so it never takes the store's `_gate`: a roster read does not
  wait behind the ingest, and the ingest does not wait behind a roster read. The store's file is the receipts pull
  request's (#4082), and I left it unchanged.
- It projects only the six columns the verdict needs, into the new `ApiModeVerdictReceipt` record (prompt id, state,
  queue reason, turn id, turn outcome and its reason). The acknowledgement and the refusal reason are never read. The
  fold now takes that record, so it cannot lean on a column the roster read does not fetch.
- The quadratic `turnOrder.Contains` is gone: a `HashSet` beside the list counts the turns in one pass.
- The event feed is still read once per row, but that is an in-memory read, not a database one.

Tests (`ApiModeVerdictReceiptsTests`, through the real ingest and store over the harness's SQLite file):
`Stamp_ARosterOfManyApiModeRows_ReadsTheReceiptsOnceForAllOfThem` (five API-mode rows and two terminal ones: one read,
for exactly the five, and every API-mode row folded), `Stamp_ARosterWithNoApiModeRow_ReadsNothing`,
`Read_SeveralSessions_AnswersEachOneGroupedAndOldestPromptFirst`, `Read_AnotherAccountsSessionWithTheSameId_AnswersNothingOfIt`,
`Read_NoSessionIds_AnswersEmptyWithoutAQuery`, and `Read_ThroughTheBatchReader_FoldsTheSameVerdictAsTheStoresOwnRead`
(the batch read gives exactly what the store's own read gives, in the same order). The earlier verdict and roster tests
now run through the batch reader too. That the read takes no store lock holds by construction: the reader is built from
the database alone and is never handed the store. I have no test that blocks the store's lock, because the lock is
private to #4082's store.

A verdict kept current from the feed, the alternative you named, would remove even the one query. I did not build it,
because it would need its own rebuild-after-restart path, and one query per roster read is small.

### Finding 2 (low): the agent label names a cause the fold cannot know

**Where:** `ApiModeSessionFold.Agent`, the last line: when the wheel is with the protocol process
and the feed holds no connection event, an incomplete replay reads
"Not known since the Gateway started: the Director reports the agent process holds the conversation".

**The harm.** `ApiModeEventReplay.Complete` is false in four cases, and only one of them is a
Gateway restart. The buffer also reports incomplete when it let go of the session (over 1,000
sessions held), when its bound trimmed the oldest events of a long session (over 2,000 events or
2,000,000 characters, which a busy session reaches in a day), or when the Director started a new
generation. In each of those the Gateway did not start, and the sentence on the user's screen says
it did. The "Not known" part is honest; the "since the Gateway started" part is a guess about the
cause, which is exactly what the sentence exists to avoid. The fix is a sentence that states only
what is known: that this Gateway holds no connection event for the session.

Developer's answer: **Fixed.** The label is now "Not known: the Gateway holds no connection event for this session. The
Director reports the agent process holds the conversation". It states what is known and names no cause, so it is true
in all four cases: a restart, an eviction, a trimmed buffer and a new generation. The sentence is one constant,
`ApiModeSessionFold.NoConnectionEventHeld`. A new test, `Fold_AReplayTrimmedWithNoGatewayRestart_SaysOnlyThatNoConnectionEventIsHeld`,
covers the trimmed case with no restart, and the restart test asserts the new words.

## Observations (not findings: no harm proven, recorded so the next seat need not rediscover them)

- **A.** `PushApiModeEventsHubTests` builds a `DeviceRegistry` with its parameterless constructor,
  which opens the machine's default registry path; run beside other tests in the same process it
  failed on a unique-key collision in the import marker, and alone it passed. That is a test whose
  pass depends on the machine (the project instructions' test standard, rule 5a). It belongs to the receipts pull request, not this one;
  the Tech Lead should know it is there.
- **B.** The verdict after a Gateway restart rests on the receipts being complete across the
  restart. Events a Director sent while the Gateway was down reach the receipts only if the Director
  re-sends what the watermark never acknowledged. The watermark contract is built for that, and the
  ingest and the store are idempotent so a re-send is safe; but no Director push exists yet, so the
  contract is unproven. Whoever builds the Director push should treat "re-send until acknowledged" as
  a requirement, or the "Working - turn N" verdict can be a confident wrong one after a restart
  during a turn.
- **C.** `FoldedAccountRoster` (the Fleet Manager digest and the events route) is a third caller of
  `StampFleetRolesAndFold` and was not given the facts, so rows served through it carry no API-mode
  verdict. The stamp documents null as "nothing was read, so nothing is claimed", which is honest; it
  is noted so that a later reader of the digest does not expect the verdict there.
- **D.** `PromptQueued.position` is in the event model but neither the receipt words, the live item
  nor the row's queue label shows a prompt's place in the queue. The brief asked for "the queue and
  why", which is met; if a client later needs the order, it is a Gateway addition, not a client rule.
- **E.** The ingest's `CheckFields` does not check `ConnectionEnded.StderrTail` for null, and the
  live ruling calls `Prepend` on it. The contract marks it required and the Director's own
  serialiser always writes it, so no path produces the null today.
- **F.** `schema.ts` is headed "auto-generated" and was edited by hand here. The entries match the
  generator's shape for a nullable object reference (compare `turnVerdict?: null | ...` in the same
  file) and sit in the property order of `SessionDto`, so a regeneration should produce the same
  text.


### Developer's answer to the observations

- **A.** Left alone, as the Tech Lead instructed. It belongs to #4082, and its Developer fixes it; I take the fix when I
  merge that branch again.
- **B.** Agreed. Re-send until acknowledged is a requirement on whoever builds the Director's push. It is recorded
  here, and I will name it in my report to the Tech Lead. Nothing in this pull request can prove it.
- **C.** Intended. The digest's rows carry no verdict, and the stamp documents null as "nothing was read, so nothing is
  claimed". If the digest later needs the verdict, its caller passes the facts, and that is a one-line change.
- **D.** Agreed, not built: the brief asked for how many prompts wait and why, and that is met. If a client needs each
  prompt's place in the queue, it is a Gateway addition to the queue words.
- **E.** No change. The contract requires `StderrTail`, and the Director's serialiser always writes it. Checking for null
  belongs in the ingest's `CheckFields`, which is #4082's code; the ruling should not add a fallback for a field the
  contract requires.
- **F.** Agreed. The hand edit follows the generator's shape and the property order of `SessionDto`, as #4077 did.
