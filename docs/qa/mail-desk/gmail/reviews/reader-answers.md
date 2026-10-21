# Answers to the review of the Gmail reader in `screen.py`

The review: `docs/qa/mail-desk/gmail/reviews/reader-review.md` (approve, two findings, six observations), of commit
`65e6c80c5`. The answers are in the commit after it on `mail-desk-gmail-reader`. Every finding and every observation
is answered below, accepted or declined with the reason.

The suite, as the brief gives it:

```
uv run --no-project --with pytest --with "msal>=1.28" --with google-auth python -m pytest \
    docs/qa/mail-desk/phase-2/tests docs/qa/mail-desk/gmail/tests -q
```

**633 passed** (phase 2: 292, unchanged by this round; Gmail: 341, 19 of them new). G0's own tests,
`docs/qa/mail-desk/gmail/g0/tests`, still pass (65). The review's 679 was these two folders plus G0's tests.

Each fix was undone on its own and each turned the suite red; the table is in `docs/qa/mail-desk/gmail/CONTRACT.md`,
section 7.

## Finding 1. The "not signed in" fix names `accounts add` with a name that may be taken - accepted, fixed

The reviewer is right: cc-gmail's `accounts_add` prints "already exists" and returns for a name that has an
`auth_method`, so two mailboxes sharing a local part (the reviewer's `info@` case) got a fix that does nothing.

The fix (`GmailReader.free_name`): the sentence names a name no account directory has, compared without regard to case
(so it is free on Windows and macOS, whose file systems ignore case, too). It tries the part before the `@` first, then
that plus the domain's first label (`desk-example`), then that with a number (`desk-example-2` onwards). The sentence
stays one command that can be run as written. The longest form still fits the 165 characters.

Tests: `test_NoAccount_TheNameToAddIsTakenAlready_AFreeOneIsNamed` (the reviewer's case: account `desk` holds
`desk@example.org`, the channel is `desk@example.com`, and the fix names `desk-example`) and
`test_NoAccount_ThoseNamesTakenToo_ANumberedOneIsNamed_WithoutRegardToCase` (`Desk` and `DESK-example` taken, so
`desk-example-2`). Each Outlook channel beside them is read, and its cursor moves.

## Finding 2. Three unexpected answers from Google escape their channel and stop every channel - accepted, fixed

All three were reproduced as the reviewer describes, and each is now that channel's failure sentence, checked where the
answer is read:

| Where | The answer | Now |
|---|---|---|
| `refresh` | the token service answering a JSON list | checked as an object before any `.get`: `the Google sign-in service failed on <machine> for <address>: its answer held no access token` |
| `message_ids` | a `nextPageToken` holding a control character | a page token must be printable ASCII with no space, else `the Gmail API answered a list without usable message ids ...`; it never reaches the guard |
| `known_hashes` | a Sent message whose `payload` is not an object, or whose headers are not a list | `the Gmail API answered a Sent message without usable headers for <address> while reading the Sent mail` (a new row in phase 2's failure table) |

I did not take the simpler choice of catching `RefusedRequest` beside `CheckFailed` in the run. That loop is shared
with Outlook, and today a refusal outside Outlook's filing is an unforeseen failure of the whole run; catching it there
would change the Outlook path, which this work must not do. It would also turn a guard refusal, which should never
happen outside filing, into a quiet failure of one channel. Checking each shape where it is read keeps the guard's
refusals loud and fixes only what the reviewer found.

Tests, each with an Outlook channel beside the Gmail one, read and with its cursor moved:
`test_TheSignInServiceFailing_FailsThatChannelSayingSo[a-json-list]`,
`test_TheGmailApiFailing_FailsThatChannelAndTheSameMailIsReadNextRun[bad-page-token]` and
`test_ASentMessageWithoutUsableHeaders_FailsThatChannelOnly`. All of these failed the whole run before the fix.

I looked for other paths of the same kind in the Gmail reader. Every other value read from Google is checked before it
is used (`read_gmail_message` returns None for any wrong shape; `message_ids` checks the page and every id;
`gmail_header` takes only text), and the store's files are checked in `sign_in`. I found none.

## Observation 1. The cursor rests on Gmail listing newest first by `internalDate` - accepted, written as a premise

The reviewer is right that G0 measured the same order twice, not the order's key, and that "nothing skipped" rests on
it. No code change: reading every listed message's metadata to sort it myself would cost one call per new message
after a long gap (up to 10,000), and the Tech Lead's 22-call budget for a normal run is what G0 measured. The contract
(section 4) now says it is a premise, quotes Gmail's reference for it, says exactly what goes wrong if it is false,
and asks phase 6 to check one real list's order against `internalDate` once. It needs the real mailbox, which this
work may not touch.

## Observation 2. The tests cannot see whether the write-back is atomic - accepted, a test added

A test proved easier than the reviewer expected, with no second thread:
`test_WriteBack_IsOneOsReplaceOfATemporaryFileInTheSameFolder` watches `os.replace` and asserts exactly one move, from
a file in token.json's own folder onto token.json. The reviewer's breakage 5 (token.json written in place) now turns
it red. So all 16 of the review's breakages are caught.

## Observation 3. One sentence is 174 characters at the contract's own sizes - accepted as a finding, fixed

As the Tech Lead asked. The error in `the Gmail API could not be reached ... while <step>: <error>` is now cut to 41
characters (`GMAIL_UNREACHABLE_ERROR_CHARS`), so the longest form (`while moving a message to Spam`) is 165 with a
31-character address and a 15-character computer name. The whole error is still on the detail line. Outlook's
sentence keeps its 50 characters: it is phase 2's, and phase 2 accepted its length. That way the Outlook path does not
change.

`test_EveryGmailApiSentence_FitsTheLimitWithLongNamesAndALongError` measures all three Gmail API sentences
(unreachable, HTTP, not JSON) at all four steps, with an error of 64 characters. With 50 it failed 4 times, once for
each step. The contract's claim in section 6 now names the sizes it is measured at.

## Observation 4. The Kelvin sign - accepted, closed

It costs one comparison, so it is done. `read_gmail_message` records whether the address was written in ASCII, and
`known_hashes` searches only for those. So a `From` of `Kate@example.com` whose K is the Kelvin sign (U+212A) is shown as `kate@example.com`, as before,
but is never known. `test_KnownSender_AnAddressWrittenWithTheKelvinSign_IsNeverSearched` sends it with a Sent message to
`kate@example.com` in the box: no search is made.

## Observation 5. Redirects - declined, no change

I agree with the reviewer and with phase 2's screen review. Only Google can send the redirect, and `urllib` turns a
POST into a body-less GET, so no redirect can become a write. Refusing redirects would need a custom opener on the
shared `transport()`, which Outlook uses too; that is a change to the Outlook path for no harm anyone has shown.

## Observation 6. `cc-gmail accounts remove <name>` is a placeholder - agreed, no change

As the reviewer says: which account to keep is the owner's choice, and the detail line names every directory.
