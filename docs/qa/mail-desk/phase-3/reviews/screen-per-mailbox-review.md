# Review: screen.py, one mailbox failing does not stop the others (pull request 4229)

Reviewed: branch `mail-desk-p3-screen-per-mailbox` at commit `724f2392a`, into `mail-desk-phase-3`.
Rule reviewed against: section 6.1 of `docs/qa/mail-desk/phase-3/CONTRACT.md` (read from `origin/mail-desk-phase-3`),
and the check's own contract `docs/qa/mail-desk/phase-2/CONTRACT.md` as this pull request changes it.
Reviewer seat on the Mail Desk mission, phase 3. The reviewer read and ran; it built nothing and fixed nothing.

Three findings. The first demands a change before the rule in 6.1 holds; the other two are smaller. Three
observations follow that demand nothing. The scope is at the end.

## Findings

### 1. From thirteen failing mailboxes up, the failures fail the whole run, and a healthy mailbox beside them loses its run

**What breaks.** Each failing channel's section in the context is about 535 bytes with a realistic detail (the
"not signed in" sentence plus its line 2 naming the path looked for). The sections are counted against the same 7 KB
budget as new mail, and nothing bounds how many of them there are. Measured through the test rig, with every
mailbox not signed in:

| Memory | Result |
|---|---|
| 13 channels, all failing | exit 0, count 0, context 6994 bytes: as 6.1 says |
| 14 channels, all failing | **exit 1**, memory untouched, line 1: `the Mail Desk context came to 7530 bytes, over the budget of 7168` |
| 1 healthy channel with one ordinary new message, plus 12 failing | exit 0, the message reported whole |
| 1 healthy channel with one ordinary new message, plus 13 failing | **exit 1**, memory untouched, line 1: `a message's sender address alone is larger than the Mail Desk context budget` |

With the detail at its full 500 characters the thresholds come earlier (one healthy channel plus 10 failing is
already exit 1).

**Why it must change.** Section 6.1 says "a run where every channel failed prints count 0 and exits 0, with every
failure in the context", and that every other channel "is read as if the failing one were not there". Above the
threshold neither holds: the healthy channel's cursor does not move and its mail is read again next run, and the
Gateway gets one whole-run sentence instead of the per-mailbox failures that 6.2 turns into named problems. The
scenario is not exotic: a factory with many mailboxes started on a new machine, or after a machine's sign-ins were
cleared, has every mailbox failing with the longest sentence there is, and the owner then never sees a single "run
cc-outlook -a ... auth" for any of them, every five minutes, until enough of them are signed in by other means. The
second sentence in the table is also wrong on its face: no sender address is too large; the failures took the room.

**Where.** `run()` in `screen.py`: the failure sections are put into `sections` before the message loop measures
`build_context(sections)`, and the only guards are the end-of-run `CONTEXT_BUDGET_BYTES` check and `trim_to_fit`,
both of which raise `CheckFailed` outside the per-channel `try`. `FAILURE_DETAIL_CHARS` bounds one detail, not the
sum.

**How it was shown.** A throwaway test outside the repository, using the suite's own `world` rig, wrote memories of
N channels with no saved sign-in (with and without one healthy channel holding one message) and ran `main()` for
N from 8 upwards; the numbers above are its output.

### 2. A saved sign-in file that cannot be read fails the whole run, not that channel

**What breaks.** `OutlookReader.sign_in` reads the token file inside `try: cache.deserialize(path.read_text(...))
except ValueError`. An `OSError` from `read_text` (permission denied, an input or output error, a file replaced by a
folder) is not caught there and is not a `CheckFailed`, so it reaches `main()` as an unforeseen error. Measured with
the second channel's token file set to mode 000 and a healthy first channel beside it:

    exit 1, memory untouched, line 1:
    screen.py stopped on an error it does not handle: PermissionError: [Errno 13] Permission denied: '/...'

The healthy mailbox was not read and its cursor did not move. The same holds for the write-back of a renewed sign-in
(`path.write_text(cache.serialize())`, then `os.chmod`) when the disk is full or the file has become read-only.

**Why it must change.** Section 6.1 puts "every sign-in failure" on the per-channel side, and this pull request's
own docstring says a failure of "its sign-in" belongs to the channel. A file one mailbox's sign-in lives in is as
much that mailbox's business as the file not being there, which is already per-channel (`not_signed_in`). Left as it
is, one mailbox's file permissions stop every mailbox on the machine, and the sentence the owner sees is the
"does not handle" one rather than "<address> is not signed in on <machine>: ...". The contract's "anything the check
did not foresee" clause covers defects in the check, not a failure the check can name.

**How it was shown.** A throwaway test with the rig: `world.plant(SECOND)`, `os.chmod(path, 0)`, two channels, run.

### 3. One assertion on standard error in the changed tests can never fail

**What breaks.** `test_MoveFailing_WithARealLengthGraphId_KeepsTheWholeSentence` in
`test_what_the_gateway_keeps.py` ends with
`assert graph_id not in world.capsys.readouterr().err`. `channel_failed` has already called `world.run()`, which
calls `capsys.readouterr()` and so empties the capture; the second read returns an empty string, and the assertion
holds whatever the check wrote. Measured: after `world.run()`, a second `readouterr().err` has length 0. Broken on
purpose by logging the Graph id in `OutlookReader.file`, this test still passed; the leak was caught only by a
different test, `test_FilingQueue_ARefusedMove_DoesNotQuoteTheGraphId` in `test_request_guard.py`.

**Why it must change.** The test claims a guarantee (the id is not on standard error when a move fails) that it does
not check. The suite as a whole still catches that leak today, but through one other test, and under the rule that a
test which fails on correct code is deleted, the guarantee could vanish without anyone noticing. The fix is in the
test, not the check: keep the standard error that `world.run()` returned, or assert on it inside `channel_failed`.

## Observations (no change demanded)

**A. A next-page link that the guard refuses ends the whole run, with a sentence naming no mailbox.** The Inbox and
Sent readers follow `@odata.nextLink` from Graph's answer. A link off `https://graph.microsoft.com` raises
`RefusedRequest`, which `OutlookReader.graph` does not turn into a `CheckFailed`, so it is exit 1 with line 1
`refused: https://evil.example is not https://graph.microsoft.com`; a link that is not text is exit 1 as an unforeseen
`TypeError`. Measured: nothing reached the network in either case (the guard holds), the memory was untouched, and
the healthy channel was not read. Graph does not send such links, so this is noted for completeness and for 6.2's
reader: the sentence names neither the mailbox nor the machine.

**B. The failing channel's log line is not pinned to the sentence only.** Appending the detail to
`could not be checked, the other channels go on: ...` left all 271 tests green. No detail carries a token, a sender,
a subject or body text (they hold paths, HTTP statuses and the sign-in service's error text), so no harm is shown;
it is recorded so the next person knows that line is unguarded.

**C. A move done before the channel failed is done on the server and still queued in the memory.** That is what
6.1's "byte for byte" asks for, the next run gets `already-gone` for it, and `test_ChannelFailing_TheMoveDoneBeforeItIsAlreadyGoneNextRun`
proves nothing is moved twice. The contract's instruction that the Gateway read `already-gone` as "not in the Inbox
any more" matters more now than in phase 2, because this path is now ordinary rather than a failed run.

## What was checked and found sound

- **Memory back as it was, in every per-channel path.** `original = copy.deepcopy(channel)` is taken after
  `check_channel` (which does not write) and before any network call; on `CheckFailed` the list entry is replaced
  (`channels[index] = original`) and `memory["channels"]` is that same list, so the compact write at the end carries
  the original. The failing channel is skipped by the end-of-run cursor and key pruning (`if index in failed`), and it
  has no candidates so the message loop's `setdefault("keys", {})` never touches it. The hardest case (live move
  done, Sent folder read, then the Inbox failing) is covered by a test that compares the file's bytes, and it goes red
  when the restore is removed or the copy is taken by reference.
- **No leak into the context, standard error or memory from a per-channel failure.** The sentence and detail are
  built from the address, the machine, HTTP statuses, `shown_url` (query and message id removed), file paths, and
  the sign-in service's error text through `short_error`. The token lives only in `reader.token` and is never
  formatted. The log line in the `except` carries the sentence only. The sign-in cache write-back goes to its own
  file, never into the memory.
- **The request guard is untouched.** `git diff` shows no change to `graph_request`, `check_request`, `transport`
  or the move path; `transport` is still the only caller of `urlopen`, and `graph_request` the only caller of
  `transport`. Every refused shape in `test_request_guard.py` still shows an empty network log.
- **The whole-run failures are still whole-run.** Missing, empty, not UTF-8, not JSON, no channels, empty channels,
  a channel with no address, one address twice, and the memory over its cap all exit 1 with the file untouched, with
  a signed-in channel beside them; and an unforeseen error inside one channel still exits 1 (both tested).
- **The 10-message and 7 KB budgets hold while the failure sections fit** (finding 1 is the case where they do not):
  failures are in `sections` when every message is measured, so the context written is never over 7 KB on exit 0,
  and `count` is messages reported plus filing items attempted from the channels that were read.
- **The tests can fail.** Besides the pull request's own seven breakages, the check was broken here five ways and
  put back after each: the restore removed (1 red), the failed-channel skip removed (1 red), a per-channel failure
  re-raised as whole-run (1 red), the detail not cut (1 red), the copy taken by reference (1 red). Runs used `-x`,
  so one red means at least one.

## Scope

**Read, in full:** `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py` on the branch and its diff against
`origin/mail-desk-phase-3`; `docs/qa/mail-desk/phase-2/CONTRACT.md` diff; section 6 of the phase 3 contract from
`origin/mail-desk-phase-3`; `conftest.py` and `test_one_mailbox_failing.py` in full; the diffs of
`test_sign_in_and_failures.py`, `test_what_the_gateway_keeps.py`, `test_no_traceback.py` and
`test_text_channels_and_keys.py`. `test_request_guard.py` and the unchanged tests were run, not re-read.

**Ran:** the suite as instructed (`uv run --no-project --with pytest --with "msal>=1.28" python -m pytest
docs/qa/mail-desk/phase-2/tests`): 271 passed in 3.6 seconds on Linux (devlinux). Two throwaway probe files outside
the repository, loaded against the suite's `conftest` rig, for the measurements above. Six deliberate breakages of
the check, each restored with `git checkout` and the tree confirmed clean after.

**Could not reach:** a real Microsoft Graph or a real sign-in service (the rig fakes `urlopen` and
`PublicClientApplication`, so the shape of real error answers, and whether Graph answers 404 to a move of an id it
has already moved, remain as the contract marks them: not observed on a real mailbox). Windows and macOS paths
(`director_base`) were not run. The Gateway side of section 6.1 and all of 6.2 are not in this pull request; a
search of the Gateway's Mail Desk code on `origin/mail-desk-phase-3` found nothing that parses the context's
`version`, so raising it to 2 breaks nothing there today. The pull request's own description was not used as
evidence.

## Re-review

Reviewed: the same branch at commit `6d9d5daa9`, which answers the three findings above
(`screen-per-mailbox-answers.md`). The same reviewer, the same rig, the same throwaway probes re-run against the
answered check, plus one new probe for the new limit.

**Suite:** 281 passed in 4.5 seconds at `6d9d5daa9`. **The new tests fail on the old check:** with `screen.py`
swapped for its text at `724f2392a` and everything else left at `6d9d5daa9`, 10 failed and 271 passed, and the 10
are exactly the tests the answers name (two new failure kinds in each of the two per-kind tests, the mode 000 file,
fourteen all failing, a healthy mailbox beside 13 and beside 20, beside 10 with full details, and 40 failing as a
whole-run failure). The tree was confirmed clean after the swap. **Nothing else broke:** every test that passed at
`724f2392a` passes at `6d9d5daa9`, and the six deliberate breakages from the first review are not repeated here; the
one that matters for finding 3 was.

### Finding 1: closed

The first review's probes, re-run unchanged against `6d9d5daa9`:

| Memory | At `724f2392a` | At `6d9d5daa9` |
|---|---|---|
| 13 channels all failing | exit 0 | exit 0 |
| 14 channels all failing | exit 1, "the context came to 7530 bytes" | exit 0, 7008 bytes, 11 of 14 details kept, the last 3 dropped, the log says so |
| 20 channels all failing | exit 1 | exit 0, 7142 bytes, 4 of 20 details kept |
| 23 channels all failing | exit 1 | exit 0, 6949 bytes, count 0 |
| 24 channels all failing | exit 1 | exit 1, `24 of 24 mailboxes could not be checked on <machine>, more than the Mail Desk context can name; the first is <address>`, line 2 that mailbox's own sentence, memory untouched |
| 1 healthy with one ordinary message, plus 13 to 19 failing | exit 1 from 13 up, "a message's sender address alone is larger..." | exit 0, the message whole and not trimmed, the cursor moved |
| 1 healthy with one ordinary message, plus 20 failing | exit 1 | exit 1, the honest "20 of 21 mailboxes" sentence |
| 1 healthy with one 550-character message, plus 13 failing | not measured | exit 0, the message whole, the cursor moved |
| 1 healthy beside 10 failing with 500-character details | exit 1 | exit 0 |

The misleading sender-address sentence did not appear in any run. The thresholds the answers state are confirmed
to within one: measured, the first whole-run failure is AT 20 failing beside one with new mail and AT 24 when every
mailbox fails, where the answers and the contract say "more than 20" and "more than 24". A wording nit, not a defect.
The read of `fit_failures` agrees with the measurements: the sections, including a healthy channel's filing results,
are measured before new mail is placed; details are emptied from the last failing channel back, each time
re-measured; the sentences are never touched; and the memory is not, since only `sections` is edited.

### Finding 2: closed

The first review's probe (the second mailbox's token file at mode 000, a healthy first mailbox beside it), re-run:
exit 0; the second channel's section carries
`the saved sign-in of second@example.org on testmachine cannot be read (PermissionError): check its permissions or sign in again`
with the path and the whole error on its detail line; the healthy mailbox was read and its cursor moved. The
write-back path is covered by the new `sign_in_write_back_fails` kind in both per-kind tests. Both new sentences are
within 165 characters with the suite's longest machine name.

### Finding 3: closed

`World.run` now keeps the run's standard error as `last_err`, and the assertion reads that. Broken on purpose the
same way as in the first review (the Graph id logged in `OutlookReader.file`), the suite now shows two red:
`test_MoveFailing_WithARealLengthGraphId_KeepsTheWholeSentence` itself and
`test_FilingQueue_ARefusedMove_DoesNotQuoteTheGraphId`. Before the fix, only the second went red.

### New finding 4 (low): the new whole-run sentence can pass 165 characters with a long address

**What breaks.** The sentence `<n> of <m> mailboxes could not be checked on <machine>, more than the Mail Desk
context can name; the first is <address>` ends with an address of any length. Measured: 40 channels with
57-character addresses (`a-rather-long-mailbox-name-number-00@subdomain.example.org`) and a 17-character machine
name give a first line of 175 characters. The Gateway's window after uv's line shows 165, so what is cut off is the
address, which is the one thing in the sentence that tells the owner where to start, and it is not shown again
(line 2 is not in the window). The test for this sentence uses 17-character addresses and so passes.

**Why it should change.** The contract's own rule, restated in this pull request, is "every sentence, wherever it
goes, is held to the same 165 characters", and every other sentence that names an address keeps within it by
construction or by `short_error`. Mailbox addresses of 40 to 60 characters are ordinary for a company mailbox on a
subdomain. The harm is bounded (the run is already failing for a reason the count explains, and 20 or more failing
mailboxes is rare), hence low.

### New finding 5 (low, shown at the sign-in library layer only): a saved sign-in whose JSON is not an object still fails the whole run

**What breaks.** Finding 2 made an unreadable file one channel's failure, and a file whose text is not JSON was
already one (`not a sign-in cache`, through `ValueError`). Between the two sits a file that IS JSON but not an
object: `[]`, `1`, `"x"`. `SerializableTokenCache.deserialize` accepts it, and the first lookup then raises
`AttributeError` ("'list' object has no attribute 'get'"), which the `except (ValueError, RuntimeError)` around
`get_accounts` and `acquire_token_silent` does not catch, so it reaches `main()` as "stopped on an error it does not
handle" and stops every mailbox. Measured with the real token cache class (`deserialize` then `find` for the
account, which is what `get_accounts` does): `AttributeError` for all three contents. It could not be shown through
the check itself, because the rig replaces the sign-in application and never reads the cache; that limit is why
this is stated as shown at the library layer, not end to end.

**Why it should change.** It is the same class as finding 2: a defect in one mailbox's sign-in file stopping every
mailbox, where the per-channel sentence for a bad cache already exists. The likelihood is low (the file is written
by cc-outlook and the sign-in library, not by hand), hence low.

### New observation D (no change demanded): a large filing report brings the limit down to ten failing mailboxes, and the state repeats

Measured: one live mailbox with 60 queued moves (48 of which fit the filing half of the budget) and one new message,
beside 10 mailboxes not signed in, is exit 1 with the honest "10 of 11 mailboxes" sentence, where beside 5 it is
exit 0 with 48 filed and 12 left queued. In the exit 1 case the 48 moves were carried out on the server and the memory
was not written, so the next run attempts all 60 again, gets `already-gone` for 48, and lands in the same exit 1,
every five minutes, until enough of the failing mailboxes are signed in. The answers name the cause ("fewer when a
large filing report shares the run"), the sentence tells the owner what to fix, and at `724f2392a` the same memory
also exited 1 (with the misleading sentence), so this is not a regression. It is recorded so the Tech Lead knows the
figure is 10, not 20, when filing is heavy, and that the condition does not clear itself.

### Observations A to C

The Developer's answers are accepted as written: A stays a loud whole-run stop by a stated choice; B has no shown
harm; C is carried to the Gateway task that reads `already-gone`.

### Scope of the re-review

Read: the diff `bb93c97a4..6d9d5daa9` in full (the check, the contract, the rig, the two changed test files, the
answers). Ran: the suite at `6d9d5daa9`; the suite with the check swapped to `724f2392a`; the three probe files
against `6d9d5daa9`; one deliberate breakage, restored. Not reached: a real Graph, a real sign-in service, Windows and
macOS, the Gateway side of section 6.

## Second re-review

Reviewed: the same branch at commit `b1baaaa7f`, which answers findings 4 and 5 and acts on observation D by signing
every channel in before any channel files or reads, and sizing the filing half after sign-in. The Tech Lead asked
four questions about that reordering; each is answered below with what was run. No new finding.

**Suite:** 292 passed in 4.5 seconds at `b1baaaa7f`. **The new tests fail on the previous check:** with `screen.py`
swapped for its text at `6d9d5daa9`, 9 failed and 283 passed: the two long-address sentence tests, the four
JSON-but-not-an-object tests, the two heavy-filing tests, and the whole-run sentence test whose wording changed. That
is what the answers claim. The three tests of the sign-in library's own behaviour pass on both, as they should. The
tree was confirmed clean after the swap.

### The request guard is still the only way to the network

`git diff 724f2392a..b1baaaa7f` touches no line of `graph_request`, `check_request`, `transport`, `MOVE_PATH`,
`MESSAGE_ID` or `GRAPH_HOST`. At `b1baaaa7f`, `urlopen` is called in one place (`transport`), `transport` in one
place (`graph_request`), and `graph_request` in one place (`OutlookReader.graph`). The sign-in step that now runs
first talks to the sign-in service through the sign-in library, as it always did, never to Graph. The whole
`test_request_guard.py` file is in the 292.

### Observe mode still files nothing

Probe: a live mailbox with 60 queued moves, an observe mailbox with 5 queued moves, and 10 mailboxes not signed in,
in one memory. Exit 0; 39 POSTs, every one with the live mailbox's token; the observe mailbox's section carries
`filing: []` and `filingNotCarriedOut: {count: 5, ...}`; its queue of 5 is in the memory afterwards, untouched. The
order of network calls was recorded on one timeline: both sign-ins came before the first Graph call, and no sign-in
came after one.

### A sign-in failure still leaves that channel's memory byte for byte

Probe: a live mailbox whose renewal fails, with an id, a stale `sentCache`, a `keys` table and a 5-item queue. Exit
0; its entry in the memory, re-serialised the way the check writes it, is identical before and after; its section
carries the "not signed in" sentence; zero Graph calls were made for it and zero POSTs in the run. The first review's
mode 000 probe re-run gives the per-channel "cannot be read (PermissionError)" sentence, and the JSON-list token file
that finding 5 raised now gives the per-channel "not a sign-in cache" detail with the healthy mailbox read. The
`fail` closure restores `channels[index]` from a copy taken before `check_channel`, and the sign-in step writes
nothing into the channel's entry (the renewed cache goes to its own file), so there is nothing a sign-in failure
could have changed.

### Filing still never starves new mail, and the heavy-filing loop is gone

| Memory (one live mailbox, 60 queued moves, one new message, beside N not signed in) | At `6d9d5daa9` | At `b1baaaa7f` |
|---|---|---|
| N = 5 | exit 0, 48 filed, 12 left | exit 0, 48 filed, 12 left, the message reported whole |
| N = 10 | exit 1 every run | exit 0, 36 filed, 24 left, the message reported whole, 6685 bytes |
| N = 14 | exit 1 every run | exit 0, 21 filed, 39 left, the message reported whole, 6728 bytes |
| N = 10 with an observe mailbox beside | not measured | exit 0, 39 filed, 21 left; one more run empties the queue; 60 in Archive, none moved twice |

The sizing reads correctly: the filing half is cut to what is left of the budget after the known failures'
sentences and the 1,024-byte reserve, each filed result is counted against it, and `fit_failures` still runs after
reading, so the context written is never over 7,168. The limits table in the answers is consistent with this
reviewer's measurements at a third address length (21 characters: first whole-run failure at 24 all failing, at 20
beside one with new mail, both between the answers' 17- and 30-character rows).

### Findings 4 and 5

**4, closed.** The sentence is now `<n> of <m> mailboxes failed on <machine>, too many to name in the context`, with
`; first: <address>` only when the whole stays within 165. The first review's 58-character addresses now give 146
characters with the address named; a 114-character address leaves it to line 2, which names it. Both tested.

**5, closed.** The file's text must be a JSON object before it is deserialized; anything else is the existing
per-channel "not a sign-in cache". Shown end to end this time, because the shape test runs before the faked sign-in
application: `[]`, `1`, `"x"` and `null` each fail only that channel. The Developer's reason for refusing the shape
rather than catching `AttributeError` around the sign-in calls (a defect in the check must stay loud) is sound.

### What remains, by the Developer's own account

A Graph failure arriving after filing, in many mailboxes in the same five minutes, is not known when the filing half
is sized and can still end at the honest whole-run sentence. That is a true statement of the design's edge, it is
far rarer than the sign-in case that drove observation D, and it ends in a sentence that says what happened. No
change asked.

### Scope of the second re-review

Read: the diff `f47e51814..b1baaaa7f` in full (the check, the contract, the test file, the answers), and `run()`
at `b1baaaa7f` whole. Ran: the suite at `b1baaaa7f`; the suite with the check swapped to `6d9d5daa9`; the first
review's probes 1 and 3 and one new probe (call order, observe mode, heavy filing with new mail, draining the queue,
a sign-in failure's memory) against `b1baaaa7f`. Not reached: a real Graph, a real sign-in service, Windows and
macOS, the Gateway side of section 6.
