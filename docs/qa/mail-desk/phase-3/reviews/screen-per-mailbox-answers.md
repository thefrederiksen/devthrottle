# Answers: screen.py, one mailbox failing does not stop the others (pull request 4229)

The Developer's answers to `screen-per-mailbox-review.md` (the review of commit `724f2392a`). Every finding is
accepted and fixed. Each fix has tests that fail against the check as it stood at `724f2392a` and pass now: run that
check with the new tests and 10 tests fail. Run the whole suite on the answered check and all 281 pass:

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v

## Finding 1: many failing mailboxes failed the whole run. Accepted, fixed.

The review is right. Nothing limited the sum of the failure sections, so section 6.1's "every other channel is read"
broke at about 13 failing mailboxes. The sentence that came out ("a message's sender address alone is larger than the
Mail Desk context budget") was false.

**The fix, in `fit_failures` in `screen.py`:**

- **A reserve for new mail.** After every channel has been read or has failed, and before new mail is measured, the
  failing channels' sections may fill the context only up to `MESSAGE_RESERVE_BYTES` (1,024) short of the 7,168-byte
  budget. That reserve applies only when a healthy channel has new mail; otherwise they may fill all of it. 1,024
  bytes holds a typical message whole, and phase 0's worst one at 680 bytes.
- **Detail lines go first, sentences never.** Over that line, detail lines are emptied, the last failing channel's
  first. The sentences always stay, since the sentence is what names the fix. The run's log says how many details
  were dropped.
- **An honest whole-run failure past the limit.** If the sentences alone still do not fit, the context cannot name
  every failing mailbox. The run then fails as a whole with a true sentence:
  `40 of 40 mailboxes could not be checked on <machine>, more than the Mail Desk context can name; the first is
  <address>`, with the first mailbox's own sentence on line 2.
- **Where that limit is.** It depends on the address length; the measured table is under "Answers to the
  re-review" below. The "more than 20" first written here was a measurement with one address length, and the
  reviewer's "at 20" with another; both were right for their addresses. The memory's own cap allows about three channels at their caps (phase 2, section 2),
  so this limit is far past any factory the memory can carry. A run never ends with the misleading sender-address
  sentence any more.

**Tests, in `test_one_mailbox_failing.py`:**

- 14 mailboxes all failing: exit 0 with every sentence whole. The context is within its budget, and kept details all
  come before dropped ones.
- One healthy mailbox beside 13, and beside 20, failing ones: it is read whole, not trimmed, and its cursor moves.
- One healthy mailbox beside 10 failing ones with 500-character details: it is read.
- 40 failing mailboxes: exit 1, the memory untouched, and the sentence saying how many, within 165 characters.

All five fail at `724f2392a`. The phase 2 contract, section 1.2, now states the rule.

## Finding 2: an unreadable saved sign-in failed the whole run. Accepted, fixed.

The review is right. A file of one mailbox's sign-in is that mailbox's business, and section 6.1 puts "every sign-in
failure" on the channel. `OutlookReader.sign_in` now turns two cases into failures of that channel only.

**Reading the file fails** (`OSError` or a text that is not UTF-8). The sentence is:

    the saved sign-in of <address> on <machine> cannot be read (<error kind>): check its permissions or sign in again

The path and the whole error go on line 2. It does not borrow the "not signed in" fix. A file that is there but
unreadable is not fixed by adding the account, and that longer fix took the sentence to 177 characters, over the
limit of 165.

**Writing back the renewed sign-in fails** (`OSError` from the write or the change of permissions). The sentence is:

    the renewed sign-in of <address> could not be saved on <machine> (<error kind>): check the file's permissions and the free space

**Tests:**

- Two new kinds in the per-kind tests: a folder in place of the file, and a full disk on write-back. Each runs in both
  of the per-kind tests (its own section, and the healthy channel's report whole).
- The reviewer's own case: the file at mode 000, on Linux and macOS, for a user who is not root.

All five fail at `724f2392a` with "screen.py stopped on an error it does not handle".

## Finding 3: an assertion read an emptied capture. Accepted, fixed.

The review is right: `channel_failed` had already emptied the capture, so the second read always returned "".

- The rig's `World.run` now keeps the run's standard error as `world.last_err`, and the test asserts on that.
- Shown to fail: with the check broken on purpose to log the Graph id when it moves a message, this test now goes red,
  next to `test_FilingQueue_ARefusedMove_DoesNotQuoteTheGraphId`, which already caught it. Before the fix only that
  other test did.

## Observations

**A (a next-page link the guard refuses ends the whole run).** Not changed. Graph does not send such links, and the
guard held: nothing reached the network and the memory was untouched. Making it one channel's failure would mean
deciding that a guard refusal is ever a normal Graph answer. I would rather a refusal stay loud as a whole-run stop,
because it means something is wrong with the check or with what is answering as Graph, not with one mailbox.

**B (the failing channel's log line is not pinned to the sentence).** Agreed, and no harm is shown. Pinning it is
already partly done: `test_TheLogOfARunWithAFailingChannel_NamesItAndHoldsNoMailText` asserts the line's exact
beginning and that no sender, subject or token is on any log line. The detail holds only paths, statuses and the
sign-in service's error text.

**C (a move done before the channel failed is still queued).** Agreed. It is now an ordinary path. Phase 3's
Gateway work (task F) must read `already-gone` as "not in the Inbox any more", as the phase 2 contract, section 3.4,
says. I am naming it here so the Tech Lead can carry it to that task.

## Answers to the re-review

Run the suite on the answered check and all 292 pass. Against the check at `6d9d5daa9`, 9 fail: the 8 tests added
below, and the test whose expected sentence changed with finding 4. The 3 tests of the sign-in library's own
behaviour pass on both, as they should.

### Finding 4: the whole-run sentence could pass 165 characters. Accepted, fixed.

The sentence is shorter and the address is only included when it fits:

    <n> of <m> mailboxes failed on <machine>, too many to name in the context; first: <address>

That is 86 characters before the address with the longest Windows machine name, so an address of up to 79
characters fits. When it does not fit, `; first: <address>` is left out. Line 2 is the first mailbox's own
sentence, which names the address, so nothing is lost; it is just no longer inside the 200-character window.

Tests:
- The reviewer's addresses (58 characters, I counted, not 57) with the 15-character machine name: the sentence is
  within 165 and ends with the address.
- A 114-character address: the sentence leaves it out, and line 2 names it.

### Finding 5: a saved sign-in that is JSON but not an object stopped every mailbox. Accepted, fixed.

Before the cache is deserialized, the text must be a JSON object. Anything else is refused as phase 2's existing
"is not a sign-in cache", which fails that channel only with the "not signed in" fix. I chose this over catching
`AttributeError` around the sign-in calls. Catching it there would also swallow a defect in the check itself, which
must stay a loud whole-run stop.

Tests:
- `[]`, `1`, `"x"` and `null` as the file's content: each fails only that channel, and the healthy mailbox is read.
  These go through the check end to end, because the shape test runs before the faked sign-in application.
- The premise, from the real library: three tests deserialize `[]`, `1` and `"x"` into MSAL's own
  `SerializableTokenCache` and show that its first account lookup raises `AttributeError`.

### Observation D: a heavy filing report brought the limit down to ten, and the state repeated. Acted on.

My view: this is worth fixing, because the case behind it is the common one. A new machine, or a machine whose
sign-ins were cleared, has every mailbox failing at once, and a live mailbox beside it can easily have a long queue.
It must not loop every five minutes until the owner signs in elsewhere.

The fix:
- **Every channel now signs in before any channel files or reads.** Phase 2 did the same, in an all-or-nothing way.
  The checks of each channel's memory still come before any network call.
- **The filing half is sized after sign-in.** It is cut down to leave room for the sentences of the failures already
  known, plus the 1,024 bytes kept for new mail.
- **What happens now:** fewer items are filed that run, the rest stay queued in order, and the run succeeds. The next
  run goes on filing.

The test is the reviewer's case: 60 queued moves and one new message, beside 10 and beside 20 mailboxes not signed in.
Each run exits 0, files fewer than the 48 it would file alone, keeps the rest queued in order, and the next run files
more. Both fail at `6d9d5daa9`.

What is still possible: a Graph failure that comes after filing, in many mailboxes at once. Those failures are not
known when the filing half is sized. That would mean Graph failing for many mailboxes in the same five minutes, and
it ends at the honest whole-run sentence, not a misleading one.

### The limits, measured on the answered check

With the longest sentence (not signed in, and in no cc-outlook account list), the first whole-run failure is at:

| Address length | Every mailbox failing | Beside one mailbox with new mail |
|---|---|---|
| 17 characters | 26 | 22 |
| 30 characters | 23 | 19 |
| 58 characters | 18 | 15 |
