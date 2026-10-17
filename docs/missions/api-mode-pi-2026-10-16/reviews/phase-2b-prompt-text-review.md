# Phase 2 follow-on review: the prompt's text on its receipt (Gateway side)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 2 follow-on, item 2. Pull
request #4146, branch `api-mode-receipt-prompt-text`, reviewed at its head commit `3ac62bd28`, which
sits on the Director's unmerged contract pull request #4121 (branch `apimode/p4-prompt-text`, head
`b4c0c84cb`). Only what #4146 adds on top of that branch was reviewed
(`git diff origin/apimode/p4-prompt-text...review-4146`). Reviewer seat, opened by the Delivery Lead,
running a different agent family from the Developer. Written 17 October 2026 on devlinux, session
9ae46f73 ("Prompt Delivery - Reviewer - API mode prompt text #4146").

**Verdict: the change holds. The text is stored and served verbatim on every path, the two migrations
follow the repository's pattern and leave old rows null, the first-wins rule is kept in the store and
in the live ruling, and the tests are real proofs over a real SQLite file, the real ingest and real
HTTP. Four findings. Finding 1 is a sentence this pull request repeats in three places that the
combined code does not keep: the "older Director sends no text, the receipt stays null" case cannot
reach the store, because the contract branch refuses such a batch whole. That is the contract
branch's rule, not a defect in storing or serving, but the claim and the two test comments that
rest on it should be corrected, or the real test written, before the two merge. Findings 2 to 4 are
small and the Developer decides, except that finding 3 is a decision whoever rebases second over
#4142 must make in writing.**

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full:** every file the branch changes on top of `origin/apimode/p4-prompt-text` except the
two generated migration designer files and the two model snapshots, which I read as diffs. The whole
of `ApiModeReceiptStore.cs` and `ApiModeLiveRuling.cs` on the branch, not only the diff. The three
prompt events and `SessionEventJson` in `CcDirector.Gateway.Contracts.ApiMode` on the contract
branch, and the contract branch's Director-side diff (`ApiModeSessionHost`, `PiDialect`) to see which
events carry the text. `ApiModeEventIngest.Parse`, `ApiModeEventBuffer.Trim`, the batch limits in
`DirectorUpStreamMessages`, the hub's inbound message size, `ApiModeVerdictReceipts.Read` (the fleet
list's projection), `SessionLiveEndpoint` and `SessionReceiptsEndpoint`. Pull request #4142's store
and ruling (`Supersede`, `Supersedes`) for finding 3. The event model's "What a `promptId` is". The
generated `packages/client-core/src/api/schema.ts` on this branch and on `origin/main`, and
`packages/client-core/src/apimode/liveTypes.ts` on `origin/api-mode-phase-3-live-chat`. The two new
and two extended test files. The previous two `AddColumn` migrations and the previous designer files
for the pattern.

**Ran:** nothing. The gate runs on Windows and the brief says not to run tests here. The Developer
reports 223 passed in the touched classes and their callers and 14 in the pending-model-change checks;
I did not repeat that run, and every statement below is from reading the code at the head commit.

**Could not reach:** a real PostgreSQL (the migration was read against the pattern, not applied); the
behaviour of a Director that predates the contract branch (there is no test that sends an event
without `promptText` through the real parser, which is finding 1).

## 2. What is right, so the next seat does not re-check it

- **Verbatim, on every path.** The store assigns the string it was handed and nothing else:
  `KeepPromptText` is one assignment, there is no `Trim`, `Substring`, `Replace` or normalisation in
  any of the three touched product files, `ToDto` copies the column, the ruling's `Copy` and
  `ReceiptItem` copy the field, and `KeepPromptText` in the ruling is the same one assignment. The
  column is unbounded on both providers (`TEXT`, `text`) with no `HasMaxLength`. The endpoints
  serialise with the host's default System.Text.Json options, which escape newlines and keep every
  space, and the two new endpoint tests read the real JSON body back and compare it equal to a
  string with leading and trailing spaces, an inner newline and a trailing newline. The ingest test
  round-trips each of the three events through the real serializer. The prompt's text appears in no
  log line (it is the user's words and can be long).
- **The migrations are the repository's pattern.** Each is one `AddColumn<string>` with
  `nullable: true` and no default, the same shape as `AddSessionNameToCronRuns` and
  `AddFactoryCheckState`; `Down` drops the one column. The designer files carry `ProductVersion`
  9.0.2 like the `AddLicenseAlarms` pair before them. Each snapshot gains exactly one property. On
  both providers an existing row gets `NULL` for the new column, and `ToDto` serves that as null.
  `origin/main` has no migration newer than this branch's base (the newest on main is
  `AddLicenseAlarms`, 2026-10-17 08:22 and 08:25; these are 15:19 and 15:48), so the order stays
  clean after the rebase onto main that the pull request body promises.
- **First wins, never blanked, in both keepers.** Store and ruling use the same two-line rule: set
  only when the row has none and the event carries a non-empty text. The tests pin a later event
  with null, a later event with empty, and a later event with other text, in the store and in the
  ruling, and the ruling started from stored receipts keeps the stored text.
- **The fleet list does not pay for it.** `ApiModeVerdictReceipts.Read` projects named columns and
  does not select `PromptText`, so the row verdict for every API-mode session in the list is not
  inflated by prompt text. Only the one session's receipts read and its live items carry it.
- **The store's own bound is unreachable by design and tested as a guard.** `MaxPromptTextLength`
  equals the batch limit the ingest already enforces on the summed event JSON before any event is
  parsed, so no event can hand the store a longer text. A text of twice the 64 KB verbatim limit is
  stored whole, which is the right ruling for the user's own words.
- **The hub admits it.** The hub's `MaximumReceiveMessageSize` is `MaxInboundMessageBytes`, which
  resolves to 32 MB, so a 4,000,000-character batch arrives; the batch limit, not the transport, is
  the ceiling.
- **The tests meet the test standard.** Per-test SQLite file harness, a fixed clock injected into
  the store, the real ingest and buffer, the existing in-process HTTP test host for the two
  endpoints. No sleep, port, process or real clock.
- **No attribution** in the commit message.

## 3. Findings

### Finding 1. "A receipt from an older Director stays null" is not a path the combined code has

**Where:** `ApiModeReceiptEntity.PromptText` comment ("Null when no event carried any (a receipt
from a Director older than the text on the events)"), `ApiModeReceiptDto.PromptText` comment, the
pull request body, and the comments on `Items_AResetStartsFromAStoredReceiptWithoutText_PromptTextIsNull`
and `Read_AReceiptFromADirectorThatSentNoText_PromptTextIsNull` ("as from an older Director").

**Severity:** medium as a statement, low as code. Nothing stored or served is wrong; a sentence the
next seat will rely on is.

**What happens:** on the contract branch `PromptText` is `required string` on `PromptQueued`,
`PromptDelivered` and `PromptRefused`, and `SessionEventJson.Options` is the default resolver.
System.Text.Json enforces `required` members on deserialization: a JSON object without `promptText`
throws `JsonException` ("was missing required properties"). `ApiModeEventIngest.Parse` turns that
into `ArgumentException("event i is not a session event: ...")`, and the hub refuses the batch
whole. So an older Director's receipt is never stored null; its batch is never stored at all.

**Concrete failure case:** a Director on the current release runs a Pi session in API mode. The
hosted Gateway deploys main with #4121 and #4146. The Director's next push carries
`{"type":"promptQueued","sessionId":...,"promptId":...,"reason":"turnRunning","position":1}` with no
`promptText`. The Gateway answers a refusal; the Director resends the same events on its next push
and is refused again; the session's receipts and live stream stop at the last accepted sequence
until the Director updates. That may be the intended rule (Directors update themselves and the
mission is unreleased), but then it is the contract branch's rule to state, and this pull request
should not describe a tolerance it does not have.

**What to do:** one of two, and the Tech Lead picks. Either the contract branch makes the property
optional on the wire (`string?`, no `required`) and this pull request's claim becomes true, with a
test that sends an event JSON without `promptText` through `Parse` and asserts a null receipt. Or
the claim is cut from the two comments, the body and the two test comments, which then say what
they do prove: a row stored before the column existed, or by a caller that passed no text, serves
null. The "null when absent" tests themselves are right and stay either way.

### Finding 2. The store and the live ruling disagree on an event the store sets aside

**Where:** `ApiModeReceiptStore.RecordQueued`, `RecordDelivered`, `RecordRefused` (the "already
ended" and "not newer" branches return before `KeepPromptText`); `ApiModeLiveRuling.Rule` (the three
receipt arms call `KeepPromptText` before the finality check); `ApiModeLiveRuling.ReceiptIfChanged`
(an item is emitted only when the receipt's words change).

**Severity:** low. Unreachable with a Director from the contract branch, which puts the text on
every one of the three events (checked in the `ApiModeSessionHost` and `PiDialect` diff). It
matters only if a future Director carries the text on some events and not others.

**Concrete case, the two keepers:** a Queued without text, then a Refused without text, then a late
Queued with text at a newer sequence. The store logs "a prompt that ended cannot be queued again" and
keeps the row's text null; the ruling sets the text and then returns, so the live reader is handed a
receipt item with the text that the stored receipt does not have. The next reset shows the reader a
receipt without text that it had seen with one.

**Concrete case, the words rule:** a Queued without text and reason `turnRunning`, then a Queued with
text and the same reason. The store keeps the text (the reason refresh path writes it). The ruling
keeps it too, but the receipt's words are unchanged, so `ReceiptIfChanged` emits nothing and no live
reader learns the text until the next event changes the words.

**What to do:** either move `KeepPromptText` in the ruling below the finality check so the two
keepers set it under the same condition, or leave it and say in the `KeepPromptText` comment that
the ruling keeps text the store would set aside. For the words rule, nothing now: with a Director
that always carries the text, the first event sets it and the first item carries it.

### Finding 3. Over #4142, a retry's text never replaces the refused attempt's, and nobody has said so

**Where:** #4142's `ApiModeReceiptStore.Supersede` clears every field the refused attempt wrote and
moves the row's first position; it does not know `PromptText`. This pull request's `KeepPromptText`
is first-wins. After the rebase, a retry under the same prompt id keeps the text of the refused
attempt and ignores the retry's.

**Severity:** low for correctness, because of the event model's own definition. A retry "reuses its
Gateway id exactly": the id names one prompt, so the retry carries the same words, and keeping the
first is keeping the right text. It matters as an unstated decision in a merge of two pull requests
that each touch the same three methods.

**Concrete case:** Refused with text "run the tests" at sequence 3; the retry's Delivered at
sequence 9 with text "run the tests". Stored text: "run the tests", from sequence 3, on a row whose
first position now says sequence 9. Correct, and invisible. If a Director ever retried with edited
words, the stored text would be the words the agent did not get.

**What to do:** whoever rebases second writes one sentence in `Supersede`'s comment ("the prompt's
text is kept: a retry under the same id is the same prompt") and one test in the supersede suite
asserting the kept text after a retry. Do not clear `PromptText` in `Supersede`: the retry always
carries the text under the contract branch, but clearing would turn a late text-less event into a
blanking, which this pull request's rule forbids.

### Finding 4. One long prompt is held three times and can empty a session's live buffer

**Where:** `ApiModeEventBuffer.Trim` lets the oldest events go while the session is over 2,000,000
characters of raw event JSON, but keeps the newest event even when it alone is over the bound
(`state.Events.Count > 1`). `ApiModeEventIngest.Parse` charges each event its JSON length, and the
same prompt text travels on Queued, on Delivered (beside the acknowledgement) or on Refused.

**Severity:** low, and acceptable as designed. Noted so the size reasoning in the pull request body
is complete.

**Concrete case:** a pasted prompt of 1,100,000 characters. Queued carries it: 1.1 million. Delivered
carries it again: 2.2 million held for the session, and every event before the Queued is let go.
A live reader on its next read gets `Complete: false`, is reset, and rebuilds from the stored
receipts and history, which carry the text. A prompt of 2,000,001 characters does this on its first
event alone; one of up to 4,000,000 (the batch limit) is still accepted and held alone. The window
of live events behind a prompt of N characters is therefore 2,000,000 minus 2N, not minus N.

**Why acceptable:** the reset path is exactly what the design does on eviction, nothing is lost that
the stored receipts do not keep, and a prompt that long is an edge the user made. The alternative,
carrying the text on one event only, is the contract branch's decision and would reintroduce the
"text arrives on an event the keeper sets aside" case of finding 2.

**One more number, pre-existing and not changed here:** the buffer's worst case is 1,000 sessions
at 2,000,000 characters, about 4 GB of UTF-16, and the same bound was there before this change.

## 4. Answers to the six questions in the brief

1. **Verbatim.** Holds. See section 2, first point. The one place the Gateway changes what it was
   given is that an empty string is treated as "no text" and stored null. A Director never sends an
   empty prompt (the Director refuses a prompt command with no text), and null and empty read the
   same to a client, so this is harmless, but it is a tiny rewrite and the `KeepPromptText` comment
   says so honestly.
2. **The migrations.** Both follow the pattern; old rows upgrade to `NULL`; order against main is
   clean. See section 2, second point.
3. **First wins with #4142 in flight.** Correct under the event model, because a retry is the same
   prompt. It does not matter for the words stored; it matters that the merge says so and proves it.
   Finding 3.
4. **Size.** No own limit, counted by the buffer at its JSON length, bounded only by the batch. One
   long prompt can evict a session's whole live buffer, and the same text is charged twice per
   prompt. Acceptable because eviction is a reset to the stored receipts, which keep the text.
   Finding 4.
5. **`schema.ts`.** Verified: the generated file on this branch and on `origin/main` contains
   neither `/sessions/{sid}/receipts` nor `/sessions/{sid}/live/items`, and neither
   `ApiModeReceiptDto` nor `ApiModeLiveItem`. The Developer was right not to invent them. The phase 3
   Chat branch (`api-mode-phase-3-live-chat`) carries `promptText?: string | null` on its own
   `liveTypes.ts` item, under the JSON name this pull request serves. A note, not a finding: those
   two routes are missing from the OpenAPI document the generator reads, which is why phase 3 has a
   hand-written type at all; that is worth a line in the phase 3 review.
6. **Tests and logging.** The tests are real proofs (section 2). The one proof missing is the one
   named in finding 1. Logging: every store change still goes through `Write`, which logs the
   operation, session, prompt, position and what changed; a kept text adds no line and the text is
   never logged, which is right.

## 5. What the next seat need not re-check

The verbatim path end to end inside the Gateway; the two migrations and the snapshots; the fleet
list projection; the hub's inbound size; the phase 3 type name. What the next seat must still
settle: finding 1's choice (contract branch or claim), and finding 3's sentence and test in whichever
of #4142 and #4146 lands second.
