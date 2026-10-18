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
