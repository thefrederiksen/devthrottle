# Mail Desk Gmail support: the contract of the Gmail reader in `checks/screen.py`

The screening check reads a channel whose `provider` is `gmail` with its Gmail reader (`GmailReader` in
`src/CcDirector.Gateway/MailDesk/Template/checks/screen.py`), beside the Outlook one. This document is what that reader
reads, what it may send, and how it fails. Everything it does not say is phase 2's contract
(`docs/qa/mail-desk/phase-2/CONTRACT.md`, as amended by section 6.1 of `docs/qa/mail-desk/phase-3/CONTRACT.md`) and
holds for Gmail exactly as for Outlook: the memory, the output, the context and its `version` 2, the short keys, the
key table and its pruning, the filing queue, the 10-message and 7,168-byte stops across every channel together, the
cleaning of text, standard error, and one channel failing never stopping the others.

It follows the Tech Lead's rulings R1 to R7 (`docs/qa/mail-desk/gmail/RULINGS.md`) and the measurements of the G0 proof
(pull request 4333, `docs/qa/mail-desk/gmail/g0/README.md`). The tests are in `docs/qa/mail-desk/gmail/tests/`. Where
this document and the code disagree, the tests decide, and one of the two is a defect.

## 1. How it runs

- Nothing changes in how the check is run. Its only package is still MSAL 1.28 or later (the PEP 723 header): the Gmail
  reader uses plain `urllib`, and no Google library.
- A channel's `provider` chooses its reader: `outlook` or `gmail`. Any other value fails that channel:
  `provider <name> has no reader in this version of screen.py (channel <address>); this version reads: outlook, gmail`.
- A Gmail channel and an Outlook channel may be in one memory. They share the run's 10 messages and 7,168 bytes, oldest
  first across both; each is signed in on its own; one failing leaves the other read.

## 2. The sign-in (R1 and R2)

### 2.1 Where it looks

cc-gmail keeps one store per operating-system user (`CcStorage.user_tool_config("gmail")` in
`tools/cc_storage/storage.py`). The check reads only that store:

| System | The store |
|---|---|
| Linux | `<home>/.cc-director/config/gmail` |
| macOS | `<home>/Library/Application Support/cc-director/config/gmail` |
| Windows | `%LOCALAPPDATA%\cc-director\config\gmail` (cc-storage's own rule: `<home>\.cc-director\config\gmail` when `LOCALAPPDATA` is unset) |

On Linux and macOS `<home>` comes from the operating system's account record (`pwd`), never from `HOME`, and
`LOCALAPPDATA` is not read (it is not that system's variable). There is no other place, no fallback, and no look into
a Director's `instances/*`. `CC_DIRECTOR_ROOT` does not move the store (issue 3011), and the check never sees it anyway.

### 2.2 The account

- The account is the directory under `accounts/` whose `config.json` has an `email` equal to the channel address,
  without regard to case. A `config.json` that is not JSON names no address (cc-gmail cannot read it either).
- **The nickname is the directory's name.** cc-gmail's `-a` takes exactly that: `resolve_account` returns an explicit
  account unchanged and `get_account_dir` joins it under `accounts/` (`tools/cc-gmail/src/auth.py`). A test reads this
  premise from cc-gmail's source.
- None found: the channel fails, naming `cc-gmail accounts add <name>` (which asks for the address) with a name no
  account has yet, compared without regard to case: the part before the `@`, else that and the domain's first label
  (`desk-example`), else that with a number (`desk-example-2`). cc-gmail's `accounts add` does nothing for a name that
  exists, so a taken name would be a fix that cannot work (review finding 1). More than one: the channel
  fails, naming every directory on the detail line. The check never picks one.
- An account whose `auth_method` is not `oauth` (an app password reads mail over IMAP, never the Gmail API) fails,
  naming `cc-gmail -a <nickname> auth --method oauth`.

### 2.3 The token, and when it is renewed

- The check reads `accounts/<nickname>/token.json` only: google-auth's `Credentials.to_json()`, whose `token`,
  `refresh_token`, `token_uri`, `client_id`, `client_secret`, `scopes` and `expiry` it uses. `credentials.json` is
  never read: cc-gmail's own refresh (`load_credentials`) takes the client id and secret from token.json, and G0 read
  google-auth's code to prove it.
- No token.json: fails naming `cc-gmail -a <nickname> auth` (cc-gmail signs in when there is no token). A token.json
  that is not a JSON object, whose `expiry` is not a time, or that lacks `refresh_token`, `client_id`, `client_secret`
  or `token_uri`: fails naming `auth --force` (with a token present, cc-gmail's `auth` needs `--force`). A `token_uri`
  other than exactly `https://oauth2.googleapis.com/token` fails too, and is never followed.
- **The scope the mode needs:** observe needs `gmail.readonly` or `gmail.modify`; live needs `gmail.modify`. A token
  without it fails naming `auth --force`. `scopes` may be a list or one space-separated text, as google-auth reads it.
- The token is renewed only when `token` is missing or empty, `expiry` is missing, or `expiry` is within 5 minutes.
  `expiry` is read as google-auth reads it: the text before any fraction, without the `Z`, as UTC.

### 2.4 The refresh and the write-back

- The refresh is the standard OAuth refresh: `POST https://oauth2.googleapis.com/token` with the form
  `grant_type=refresh_token`, `client_id`, `client_secret` and `refresh_token`, and nothing else - no `scope`, so
  Google grants the token's existing scopes. It goes through its own guard, `google_token_request()`, which refuses
  any other address and any other form before the network.
- Google refusing it (HTTP 400 or 401 with an `error`, such as `invalid_grant`) fails the channel naming `auth --force`,
  with the error code on the detail line. Google out of reach, any other HTTP status, an answer that is not JSON, or one
  without an `access_token` and a positive whole-number `expires_in`: fails the channel naming the Google sign-in
  service.
- **The write-back** is exactly as cc-gmail would leave the file: the same keys in the same order; `token` and `expiry`
  replaced (`expiry` as google-auth writes it: UTC in `isoformat()`, then `Z`); `refresh_token` replaced only when Google
  sent a new one; every other key unchanged. It is written to a temporary file in the same folder, made owner-only
  (0600) on Linux and macOS, then moved over token.json with `os.replace` - one move, so a reader of token.json sees
  the old file or the new one, never half of it. A test watches the move: one `os.replace`, from a file in the same
  folder onto token.json. google-auth's own
  `Credentials.from_authorized_user_file` reads the written file (a test proves it; google-auth is used by the tests
  only).
- A write-back that fails (the folder cannot be written, the disk is full) fails the channel and leaves token.json as
  it was; the check removes its own temporary file.
- **Not done, on purpose:** the check never writes `token-refresh-error.json`, never deletes anything in the store, and
  never puts a token in the memory, the output or standard error.

## 3. What the check may send to Google (R3)

cc-gmail's token **can** send mail, write drafts and trash or change any message (its scopes include `gmail.send`,
`gmail.compose` and `gmail.modify`; G0 saw them). So the guard is the whole of the promise. Every Gmail API call goes
through one function, `gmail_request()`, which refuses all of the following **before anything reaches the network**:

- any host but `https://gmail.googleapis.com` with no port and no user name; any version but `/gmail/v1/`; any user but
  `me`; an address with a fragment; a path holding `..`, `%` or `\`;
- any space or control character anywhere in the address as written, checked **before** parsing (Python's `urlsplit`
  silently removes tab, carriage return and line feed). Once decoded, a query parameter's **value** may hold a space -
  both searches need one (`after:<s> has:attachment`, `to:<a> OR cc:<a> OR bcc:<a>`) - but no control character, and a
  parameter's **name** may hold neither;
- every method but `GET` and `POST`, including a lower-case `post`;
- every `GET` but these two, named exactly:
  - `GET .../gmail/v1/users/me/messages`, the list, with only the parameters `labelIds` (`INBOX` or `SENT`), `q`,
    `maxResults` and `pageToken`;
  - `GET .../gmail/v1/users/me/messages/{id}`, one message, with `format` (`metadata` or `minimal`, and it must be
    there, so the whole message is never read) and `metadataHeaders` (`From`, `Subject`, `To`, `Cc` or `Bcc`) only;
  - a `GET` with a body;
- every `POST` but `POST .../gmail/v1/users/me/messages/{id}/modify`, **in live mode only**, with a JSON body of
  **exactly** `{"removeLabelIds": ["INBOX"]}` (archive) or `{"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}`
  (junk), in that key order, with no query. **Observe mode never sends a modify**, whatever the body.
- So these are all refused: send, drafts (and their send), trash, untrash, delete, batchDelete, batchModify, import,
  insert, threads, settings, labels (read or created), history, the profile, attachments, any other label in a modify,
  another user, another host, another version.
- `{id}` must be letters and digits only. G0 found every Gmail id to be 16 lower-case hexadecimal characters.

Its only other network traffic is the refresh of section 2.4. The tests send every refused shape and assert that the
fake Gmail API's log is empty for each; they send the allowed shapes too, to show the log is a working instrument.

## 4. The cursor, the first run and the batches (R4)

- **The cursor is Gmail's own**, and cannot be taken for Outlook's:
  `{"provider": "gmail", "newestMs": <internalDate of the newest reported, in milliseconds>, "idsAtNewest": [<the ids
  reported at exactly that millisecond>]}` - exactly those three keys. Outlook's `{"newest", "idsAtNewest"}` on a Gmail
  channel, this one on an Outlook channel, or any other shape fails that channel: `the channel <address> has a
  'cursor' this check did not write`. Not `historyId`: it expires after about a week.
- **The cursor keeps milliseconds and the output does not.** `receivedUtc` is internalDate written as
  `YYYY-MM-DDTHH:MM:SSZ` (UTC, whole seconds), the same shape as Outlook's.
- **First run** (no cursor): `labelIds=INBOX`, `q=after:<now less 30 days, in seconds>`, at most 200 ids - the most
  recent 200 or those of the last 30 days, whichever is fewer (D18).
- **Later runs:** `labelIds=INBOX`, `q=after:<the cursor's second less one>`, every page; then, in code, only messages
  at or after the cursor's millisecond and not among `idsAtNewest`. So a batch cut inside one millisecond loses nothing
  and repeats nothing.
- **Reading the oldest first.** The list comes newest first. **This is a premise, not a measurement:** Gmail's API
  reference describes `internalDate` as the field that determines ordering in the Inbox, and G0 measured that two lists
  give the same ids in the same order, but not that the order follows `internalDate`. Everything "nothing skipped"
  promises rests on it: were a message listed out of order, newer than ones read after it, a cursor past it would leave
  it out on every later run (review observation 1). Phase 6, on the real mailbox, checks the order of one list against
  `internalDate` once. The check reads metadata
  from the oldest end until it has one more new message than a run can report, at most 60 reads a run. An id already
  in the key table was reported, so it is skipped unread: that is how a burst of mail inside the cursor's last second
  never stalls the cursor (a test sends 100 messages inside two seconds). More than 40 already-reported messages in that
  second would cost a read each; more than 60 would stall the channel, and nothing measured comes near it.
- **More than 10,000 new Inbox messages** (20 pages of 500) fails the channel rather than guess which are oldest.
- **Filing keeps the message id** (unlike Graph's move). A retried archive is a harmless no-op; a 404 is
  `already-gone`. A modify failing any other way fails the channel and leaves its whole queue for the next run, as in
  phase 2's 3.4. Modify has never been sent to a real mailbox (G0 did not, on purpose): Gmail's answer to an id already
  gone is the fake's 404 until phase 6 sees it once.
- **The short keys, the key table, the filing queue, the stops and `more`** are phase 2's, unchanged.

A realistic run of 10 new messages from 10 senders is 22 Gmail calls (G0 measured 5.2 seconds): one list, 10 metadata
reads, one attachment search, and one Sent search per distinct sender. A test counts them. Each sender the search
finds costs one more read, to confirm it (section 5).

## 5. The fields (R5)

| Field | From |
|---|---|
| `sender`, `senderName` | the `From` header, parsed with `email.utils.parseaddr`; the address in lower case. No `From`: both empty, and `knownSender` false. An address not written in ASCII is never searched for in the Sent mail, so it is never known: the Kelvin sign (U+212A) is the one character outside ASCII whose lower case is ASCII (review observation 4). |
| `subject` | the `Subject` header with RFC 2047 encoded words decoded (decoding is not rewording; a character set Python does not know is kept as it came), then cleaned as phase 2's 4.3. G0 saw 0 of 20 subjects still encoded: the decoding is kept for mail that arrives otherwise. |
| `preview` | Gmail's `snippet`, HTML entities unescaped (G0: 10 of 20 snippets hold one), then cleaned as 4.3, **then** cut to 200 characters. An entity that stands for an invisible character is unescaped first, so it is stripped too. |
| `hasAttachments` | whether the id is in **one** list a run of the same window with `has:attachment` added (G0: `format=metadata` carries no parts, and the MIME type disagreed with this search on 6 of 36 messages). It is Gmail's own definition of an attachment, which may differ from Outlook's at the edges (an inline picture). No new mail, no search. |
| `knownSender` | "we have written to this sender": one search per distinct new sender, `labelIds=SENT`, `q=to:<a> OR cc:<a> OR bcc:<a>`, `maxResults=1` (G0: 2.4 seconds for 10 senders, against 123 seconds to build a 500-message cache). It covers the whole Sent history. Nothing is kept. **A Gmail channel has no `sentCache`**: one there is a field this check did not write, and fails that channel. Two guards, because the `From` address is the sender's to write and a known sender skips phase 3's spam screen (the Tech Lead's ruling on G0 review finding 1): **(1)** only an address that, once `email.utils` has taken it out of the header and put it in lower case, is plain - a local part of letters, digits and `.` `_` `+` `-`, then `@`, then labels of letters, digits and `-` with dots between (`GMAIL_PLAIN_ADDRESS`) - is searched for; anything else is not known and costs no call; **(2)** a hit counts only when a read of the found message (`format=metadata`, `To`, `Cc`, `Bcc`) shows exactly that address among its parsed recipients, so a look-alike hit (a longer address holding it) is not known. The G0 probe's pattern also allows `%` in the local part; this check follows the ruling's words and does not. |

## 6. The failures

Every Gmail failure belongs to its channel: the channel's section carries `"failure": {"sentence", "detail"}`, its
entry in the memory comes back exactly as it was, and every other channel is read. Every sentence is in phase 2's
failure table (`docs/qa/mail-desk/phase-2/CONTRACT.md` 1.2, the rows marked Gmail) and is at most 165 characters with
a 31-character address, a 15-character computer name, a 20-character nickname and an error of more than 50 characters:
tests measure every sign-in sentence, and every Gmail API sentence at each of the four steps. For that, the error in
"the Gmail API could not be reached" is cut to 41 characters, not 50 (review observation 3).

**Every unexpected answer from Google fails its channel only.** A token-service answer that is not a JSON object, a page
token holding a control character, a space or anything outside printable ASCII, and a Sent message whose payload or
headers are not an object and a list, are each that channel's failure sentence, never the run's (review finding 2).
Each names cc-gmail's real command: `cc-gmail accounts add <name>`, `cc-gmail -a <nickname> auth`, `... auth --force`,
`... auth --method oauth`, `cc-gmail accounts remove <name>`. A detail line names the store, the file or the Gmail API
address with any message id replaced by `{id}`; never a token, a sender, a subject or a snippet.

`<step>` for Gmail is one of `reading the Inbox`, `reading the Sent mail`, `archiving a message`, `moving a message to
Spam`.

## 7. The tests

```
uv run --no-project --with pytest --with "msal>=1.28" --with google-auth python -m pytest \
    docs/qa/mail-desk/phase-2/tests docs/qa/mail-desk/gmail/tests -q
```

The Gmail tests build on phase 2's rig (a fresh `screen.py`, every `CC_` variable removed, a scratch `HOME` and a
scratch account-record home, the fake Graph and the fake MSAL sign-in) and add a fake Gmail API and a fake Google
sign-in service, which stand in for `urllib.request.urlopen` together with the fake Graph and log every request, and a
fake cc-gmail store in cc-gmail's own shape. No network, no port, no real mailbox, no real token. The folder has no
`conftest.py` on purpose: phase 2's tests import from a module named `conftest`, and pytest gives both folders' files
that one name when they run together, so the fixtures are in `gmail_rig.py`.

| File | What it proves |
|---|---|
| `test_gmail_request_guard.py` | Every refused shape of section 3, in observe and in live mode, raises before the network and leaves the log empty; observe mode refuses even the two filing bodies; the reads and the two filings reach the network; spaces inside query values pass and control characters and spaces in names do not; the refresh guard refuses any other address or form; nothing in the Gmail reader goes round the guards; a bad id in the key table is refused per item and the rest filed. |
| `test_gmail_filing.py` | Observe mode with a queue sends no modify and keeps the queue; live mode sends exactly the two bodies; 404 is `already-gone`; a retried archive keeps the id; a failing modify fails the channel with its queue whole; a decision other than the two is refused. |
| `test_gmail_cursor_and_batches.py` | The first run (30 days or 200); milliseconds kept in the cursor and not in the output; a cut inside one millisecond; the query one second early and filtered in code; catching up over many runs; a burst of 100 inside two seconds; each cursor refused on the other provider's channel; Gmail and Outlook sharing the 10-message stop oldest first; the byte budget; two Gmail channels; one section shape. |
| `test_gmail_sign_in.py` | Where it looks (not `HOME`, not `LOCALAPPDATA` on Linux, not `instances/*`); case; the nickname premise read from cc-gmail; none, two, app password, no token, each missing key, a refused refresh, each missing scope, another `token_uri`, an unreadable or foreign token file, unreadable settings, the sign-in service failing four ways and the Gmail API failing five: each one channel only, the Outlook channel read and its cursor moved, nothing secret on standard error; every sentence within 165 characters, and every Gmail API sentence at every step; unexpected answers from Google (a JSON list for a token, a bad page token, a Sent message without headers) each one channel only; a free name in `accounts add`; no refresh while valid, a refresh within 5 minutes with exactly the four fields; the write-back (keys and order, 0600, read by google-auth, one `os.replace` from the same folder, a new refresh token kept, a folder that cannot be written). |
| `test_gmail_fields.py` | Sender parsing; encoded subjects decoded and cleaned; entities unescaped; padding stripped before the 200 cut; `hasAttachments` from one search a run, and none with no new mail; a realistic run is 22 calls; `knownSender` by one search per sender; every hostile `From` of the G0 review (spaces, `OR`, a quoted local part, `*`, angle brackets, a tab, a comment in the domain) not known and never searched; a `From` that parses to a plain address searched for that address only; a look-alike hit not known; a Kelvin-sign address never searched; a `sentCache` on a Gmail channel fails it; standard error holds no token, sender, subject, snippet or id. |

Phase 2's tests stay as they were, except where Gmail now exists: the unknown-provider tests name `imap` instead of
`gmail` and their sentence lists `outlook, gmail`; the source test counts three guards calling `transport()` (Graph's,
the Gmail API's, the Google sign-in service's) instead of one; and the test that keeps Outlook inside its reader expects
`READERS` to hold both readers and the run to name neither.

**The tests can fail.** The check was broken on purpose twelve ways, each put back after, and each turned the suite red
(the first ten against the suite of 597, the last two against the suite of 614):

| Break | Red | A test that turned red |
|---|---|---|
| The guard lets a modify through in observe mode | 2 | `test_GmailRequest_ObserveMode_RefusesEvenTheTwoFilings` |
| The guard lets trash through (its path accepted beside modify) | 1 | `test_GmailRequest_RefusedShape_RaisesAndNothingReachesTheNetwork` (trash carrying a filing body, live) |
| The cursor drops ids at the cursor's own millisecond | 1 | `test_Cursor_ACutInsideOneMillisecond_NothingSkippedNothingTwice` |
| Junk is filed as archive | 1 | `test_LiveMode_ArchiveAndJunk_AreSentWithExactlyTheTwoBodies` |
| The write-back loses a key (`universe_domain`) | 1 | `test_WriteBack_SameKeysInOrder_TokenAndExpiryReplaced_Mode0600_ReadByGoogleAuth` |
| The snippet's entities are not unescaped | 2 | `test_Preview_HtmlEntitiesUnescaped` |
| A valid token is refreshed anyway | 2 | `test_AValidToken_IsUsedWithoutARefresh_AndTheFileIsNotTouched` |
| The guard lets a control character through in a query value | 6 | `test_GmailRequest_RefusedShape_RaisesAndNothingReachesTheNetwork` |
| `hasAttachments` is never set from the search | 1 | `test_HasAttachments_IsGmailsOwnAttachmentSearch_OneListARun` |
| A `sentCache` on a Gmail channel is accepted | 1 | `test_KnownSender_ASentCacheOnAGmailChannel_IsAFieldThisCheckDidNotWrite` |
| Any sender address is searched for (no plain-address guard) | 8 (of 614) | `test_KnownSender_AnAddressThatIsNotPlain_IsNotKnownAndNeverSearched` (the other 3 hostile senders parse to no address at all) |
| A search hit is taken without confirming the recipients | 1 (of 614) | `test_KnownSender_ALookAlikeHit_IsNotKnown` |

The review (`docs/qa/mail-desk/gmail/reviews/reader-review.md`) broke it sixteen more ways; fifteen turned it red. The
one that did not - token.json written in place instead of by `os.replace` - now does, through the test that watches
the move. Each fix made for the review was then undone on its own against the suite of 633, and each turned it red:

| Fix undone | Red (of 633) | A test that turned red |
|---|---|---|
| Finding 1: the part before the `@` named even when it is taken | 2 | `test_NoAccount_TheNameToAddIsTakenAlready_AFreeOneIsNamed` |
| Finding 2: the token service's answer read before it is checked | 1 | `test_TheSignInServiceFailing_FailsThatChannelSayingSo[a-json-list]` |
| Finding 2: any page token accepted | 1 | `test_TheGmailApiFailing_FailsThatChannelAndTheSameMailIsReadNextRun[bad-page-token]` |
| Finding 2: a Sent message's payload not checked | 1 | `test_ASentMessageWithoutUsableHeaders_FailsThatChannelOnly` |
| Observation 3: the error cut to 50 again | 4 | `test_EveryGmailApiSentence_FitsTheLimitWithLongNamesAndALongError` |
| Observation 2: token.json written in place | 1 | `test_WriteBack_IsOneOsReplaceOfATemporaryFileInTheSameFolder` |
| Observation 4: a Kelvin-sign address searched | 1 | `test_KnownSender_AnAddressWrittenWithTheKelvinSign_IsNeverSearched` |

The trash break first stayed green: every trash call the tests sent had no body, so the body rule refused it even with
the path rule broken. Trash, untrash, delete, send, batchModify, a thread's modify and a draft, each carrying a valid
filing body, were added to the refused shapes, so the path rule alone must refuse them; then it turned red.
