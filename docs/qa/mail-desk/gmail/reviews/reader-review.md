# Review of the Gmail reader in `screen.py` (pull request 4335, branch `mail-desk-gmail-reader`)

Reviewer: Mail Desk - Reviewer - Gmail reader in screen.py. Date: 21 October 2026. Machine: devlinux.
Reviewed commit: `65e6c80c5` (the tip of `mail-desk-gmail-reader`), against `mail-desk-gmail`.

## Verdict

**Approve, with two findings.** I could not get any call past the request guard that the rulings forbid, and
observe mode cannot send a modify. Sign-in, write-back, cursor and Outlook all match the rulings. The two findings are
small. One is a fix sentence that names a command cc-gmail refuses. The other is three answers from Google that escape
their channel and stop the whole run. Neither one weakens the guard.

## Scope

Read:
- `docs/qa/mail-desk/gmail/RULINGS.md` (R1 to R7), `g0/README.md`, `reviews/g0-review.md` and `reviews/g0-answers.md`.
- The whole diff: the Gmail reader and every changed line of `screen.py`, `gmail/CONTRACT.md`, the six files of
  `gmail/tests/`, and the changes to phase 2's contract and four test files.
- On origin/main: `tools/cc-gmail/src/cli.py` (the `-a` option, `accounts add`, `accounts remove`, `auth` with
  `--force` and `--method`) and `tools/cc-gmail/src/auth.py` (`load_account_config`). `tools/cc-gmail` and
  `tools/cc_storage` are the same on origin/main and on this branch.
- Phase 2's README and screen review, for what was already decided about redirects and sentence lengths.

Ran:
- The tests as the brief gives them: `679 passed in 11.46s` (Python 3.11.17).
- About forty hand-made requests sent straight into `gmail_request()` and `google_token_request()`, with the transport
  replaced by a recorder (results under "The request guard").
- 200,000 random `From` and `Subject` headers through `read_gmail_message`, including encoded words in unknown,
  binary and two-byte character sets. Not one made the message unusable, so I found no sender who can stall a
  channel through those two headers.
- Four probe tests, built on the branch's own rig, in a scratch folder outside the repository (results under the
  findings and "Observations").
- Sixteen deliberate breakages of `screen.py` in my worktree, each with the unchanged suite, each put back after
  (table under "What the tests catch"). The worktree is clean.
- Every sentence length worked out with a 31-character address, a 15-character computer name, a 20-character nickname
  and a 50-character error.
- A search of the whole diff for addresses, token shapes and the owner's names.

Could not reach, by the brief's rule: the real mailbox and the network. So what Gmail itself does (the order of its
list, redirects, the exact matching of its search) was not measured by me.

## The request guard

Every shape below was refused before the transport and left the recorder empty:

- Hosts and addresses: another host, `GMAIL.googleapis.com`, a trailing dot on the host, a port, a user name,
  `//gmail/...`, `/./messages`, `;x` on the path or on an id, `..`, `%`, `\`, a fragment with content, and a raw space,
  tab, carriage return or line feed anywhere (checked before parsing).
- Paths: send, drafts, trash, untrash, delete, batchModify, batchDelete, import, insert, threads, labels, settings,
  history, the profile and attachments. The ones that are POSTs were also sent with a valid filing body, so the path
  rule refuses them by itself.
- Methods: PUT, DELETE and a lower-case `post`.
- The modify: in observe mode (both bodies), with a query, with the keys in the other order, with an empty
  `addLabelIds` added, with `INBOX` twice, and with a tuple in place of a list.
- GETs: with a body (including an empty one), without `format`, with `format=raw` or `full` (also beside an allowed
  `format`), and with `alt`, `$.xgafv` or any parameter not on the list, including when the parameter's name is
  percent-encoded. Also `labelIds` other than `INBOX` or `SENT`, a `metadataHeaders` other than the five, a decoded
  control character in any value, and a space in a parameter's name.
- The refresh: `?x=1` or a trailing `/` on the token address, and any form but the four fields.

A few shapes pass that look odd, and none of them can do harm:
- `.../modify?` and `.../modify#` (an empty query and an empty fragment) reach the same endpoint with the same body.
- An upper-case `HTTPS://` is lowered by `urlsplit`, and the host must still be `gmail.googleapis.com`.
- `GET .../messages/send?format=metadata`: `send` is letters, so it passes as an id. A GET cannot send; Gmail answers
  404, which the reader turns into a failed channel.
- `maxResults=1%26alt%3Dmedia`: the value decodes to `1&alt=media`, but it is sent encoded, so Google reads it as one
  value, not a second parameter.

The search cannot be shaped by the sender. Only an address that fully matches `GMAIL_PLAIN_ADDRESS` reaches `q`, so it
has no space, quote, colon, bracket or capital `OR`. And a hit counts only when one of our own Sent messages lists that
exact address in To, Cc or Bcc, which a sender cannot write. Breaking either guard turns the suite red (breakages 12 and 13).

Observe mode never reaches modify, for two separate reasons. `read_channel` files only when the channel is live, and
the guard checks `live` again on every POST. With either one broken, the other still stops it, and the tests catch a
broken guard (the contract's own table, first row).

## What holds

- **The sign-in place (R1.1).** On Linux, `gmail_store` takes the home folder from `pwd`, never from `HOME`, and reads
  `LOCALAPPDATA` only on Windows. Making it use `HOME` fails 94 tests (breakage 4).
- **One account, never picked (R1.2).** None found and more than one found each fail the channel. Taking the first
  match fails 2 tests (breakage 15).
- **The commands named in the sentences are cc-gmail's real ones.** `-a` is the global option, `accounts add <name>`
  and `accounts remove <name>` take the name as an argument, and `auth` has `--force` and `--method`. See finding 1
  for the one case where the command is right but cannot work.
- **The write-back (R2).** The temporary file goes in the same folder, is set to 0600 and is moved over with
  `os.replace`. Keys keep their order, google-auth reads the result, and the temporary file is removed when the write
  fails. A temporary file left at 0644 fails a test (breakage 14). The tests cannot see whether the write is atomic
  (see "Observations").
- **The cursor (R4).** Gmail's cursor and Outlook's cannot be confused. The query starts one second early and the code
  filters to the millisecond. A cut inside one millisecond loses nothing and repeats nothing, because the ids already
  reported at that millisecond are kept in the cursor and skipped next time. Removing the millisecond filter fails 2
  tests (breakage 6).
- **The Outlook path behaves as before.** Its cursor check and its cursor update moved into `OutlookReader` without
  change. The new `shown_url` rule only touches Gmail addresses. `error_answer` only reads the body of an error. A
  first run still means no more mail is waiting. Phase 2's tests change only where Gmail now exists.
- **Standard error.** Every Gmail address is logged through `shown_url`, which drops the query (the Sent search holds
  the sender's address) and replaces the message id with `{id}`. No log line holds a token, sender, subject or snippet.
- **The public repository (R6).** The only addresses are at `example.com`, `example.org`, `example.net`, plus Google's
  own hosts. `email@devthrottle.com` is phase 2's existing test mailbox, and it appears only in an unchanged line. No
  token shape and no owner's name.

## Findings

### 1. With an account already named after the address's local part, the "not signed in" fix names a command cc-gmail refuses

When no account holds the address, `find_account` (line 824) fails with
`<address> is not signed in on <machine>: run cc-gmail accounts add <the part before the @> and give it this address`.
It never checks whether `accounts/<that part>` already exists.

cc-gmail's `accounts_add` (origin/main `tools/cc-gmail/src/cli.py`, line 649) calls `load_account_config(name)`.
When that account already has an `auth_method`, it prints `Account '<name>' already exists` and returns without adding
anything. So the command the owner is told to run does nothing.

This is not rare. It happens whenever two Gmail mailboxes share a local part, such as `info@` or a first name at two
domains: the first gets the account named after it, and the second is then told to add that same name. I proved it on
the branch's rig. With account `desk` holding `desk@example.com` and a channel for `desk@example.org`, the sentence is
`desk@example.org is not signed in on testmachine: run cc-gmail accounts add desk and give it this address`, and
`accounts/desk/config.json` exists.

Why it must change: the sentence is the only help the owner gets, and here it leads nowhere. The G0 review's second
finding was the same kind of fault: a command that cannot be run as written.

What should change: when `accounts/<local part>` already exists, suggest a name that is free, or tell the owner to pick
a new name and name the one that is taken. Add a test with two addresses that share a local part.

### 2. Three unexpected answers from Google escape their channel and stop every channel

Phase 3 section 6.1 and this contract (sections 1 and 6, and the sign-in test row: "the Gmail API failing five
ways: each one channel only") promise that one channel failing never stops another. `read_channel` and `sign_in`
are wrapped in `except CheckFailed`. But three paths raise something else, which goes up to `main`. The run then exits
1, no channel is reported and the memory is not written:

| Where | The answer | What escapes |
|---|---|---|
| `known_hashes`, line 1061 | a Sent message's metadata whose `payload` is not an object (`"payload": "x"`) | `AttributeError` |
| `refresh`, line 903 | the token service answering a JSON list | `AttributeError`: `(answer or {}).get` runs before the `isinstance(answer, dict)` check two lines later |
| `message_ids`, line 962 | a `nextPageToken` holding a control character | `RefusedRequest`, raised by the guard on the next page's address |

I proved the first one end to end on the rig. With an Outlook channel and a Gmail channel together, and the fake
answering `"payload": "unexpected"` for the confirming GET, the result is exit 1 and
`screen.py stopped on an error it does not handle: AttributeError: 'str' object has no attribute 'get'`. The Outlook
channel was not reported. I proved the other two by calling `refresh` and `message_ids` directly with the transport
replaced.

Why it matters, and how much: none of these can come from a sender. Each needs Google to answer in a shape it does not
use today. But if it does, every mailbox goes unread, not just the Gmail one, and this repeats every 5 minutes for as
long as the answer stays like that. That is exactly the failure that section 6.1 exists to prevent. The Inbox path
already checks every shape (`message_ids`, `read_gmail_message`); these three paths do not.

What should change: check `isinstance(raw.get("payload"), dict)` in `known_hashes` and `isinstance(answer, dict)`
before the `.get` in `refresh`, and turn each into the channel's failure sentence. Treat a `nextPageToken` holding a
control character as "a list without usable message ids". Or, more simply, catch `RefusedRequest` beside `CheckFailed`
in `read_channel` and `sign_in`. Add a test for each with an Outlook channel beside it.

## Observations (not findings: no harm I could prove)

- **The cursor depends on Gmail listing messages newest first by `internalDate`. G0 did not measure that.** `oldest()`
  reads from the end of the list and treats unread ids as newer than every message it read. If the order were ever
  different, a message older than the new cursor would be filtered out on every later run and never reported. On the
  rig, with the oldest of 12 messages moved from the end of the list to the front, 3 runs reported 11 of the 12. G0
  shows the order is the same between two calls, not that it follows `internalDate`. Gmail's API reference describes `internalDate` as the
  field that "determines ordering in the inbox", so the premise is probably right. But it is the one thing the
  "nothing skipped" promise rests on, and G0 already had the data to check it (200 listed ids, 20 with
  `internalDate`). The contract should call it a premise, or a measurement should back it.
- **The tests cannot see whether the write-back is atomic.** Replacing `os.replace` with an in-place `write_text`
  leaves all 679 tests green (breakage 5). The code is right today. A test that reads token.json from another thread
  during the write would be hard to make reliable, so a line in the contract saying it is checked by reading the code
  is enough.
- **"Every sentence is at most 165 characters" is true for the sign-in sentences, but not for the "cannot be
  reached" sentences at the sizes the contract tests.** With a 31-character address, a 15-character computer name and
  a 50-character error, `the Gmail API could not be reached ... while moving a message to Spam: ...` is 174
  characters (167 to 174 across the four steps). Every other Gmail sentence fits. Outlook's matching sentence is 181
  at the same sizes, and phase 2 accepted that ("an address much longer than the 21 and 18 characters tested here
  also eats into the 165"). With phase 2's sizes the Gmail sentence is 164. A Gmail sentence is always a channel's
  failure, which travels in the context and not through the 200-character window. So the harm is only that section 6
  of the contract claims more than the code does. Either cut the error to 41 characters or narrow the claim.
- **The Kelvin sign.** `"K".lower()` is `k`, and it is the only character outside ASCII whose lower case is
  ASCII. So `From: <Kate@example.com>` is shown as `kate@example.com` and is known if we have written to
  `kate@example.com` (checked on the rig). This gives a sender nothing beyond writing `kate@example.com` in `From`,
  which anyone can do and which knownSender was never meant to defend against. Not a finding. A check of
  `address.isascii()` before the lower case would close it at no cost, if the Tech Lead wants it.
- **Redirects.** `urllib` follows a 301, 302 or 303 on a GET, and on a POST it turns them into a GET without the
  body. The bearer token goes with it, even to another host. Phase 2's screen review weighed this for Graph and
  accepted it: only Google itself can send the redirect, and no redirect can become a write. The same holds here,
  including for the refresh.
- **`cc-gmail accounts remove <name>`** in the "more than one account" sentence is a placeholder, unlike G0 review
  finding 2. Which account to keep is the owner's choice, and the detail line lists every directory, so this is right.

## What the tests catch

Each breakage was applied alone to `screen.py` in my worktree and run with the unchanged suite of 679, then put back:

| # | Breakage | Result |
|---|---|---|
| 0 | The modify accepts a query | 1 fails |
| 1 | A GET of one message skips the id check | 4 fail |
| 2 | Any host ending in `googleapis.com` | 4 fail |
| 3 | A GET of one message skips its query check (any `format`, any parameter) | 10 fail |
| 4 | The store uses `HOME` | 94 fail |
| 5 | The write-back writes token.json in place instead of `os.replace` | **all pass** (observation 2) |
| 6 | The cursor filter drops the millisecond test | 2 fail |
| 7 | A GET may carry a body | 2 fail |
| 8 | The refresh goes to any `https://` address | 4 fail |
| 9 | Any `metadataHeaders` value | 2 fail |
| 10 | Any `labelIds` value | 2 fail |
| 11 | No check for spaces and control characters before parsing | 7 fail |
| 12 | Every message read is accepted (no cursor or first-run filter) | 2 fail |
| 13 | A Sent hit is taken without confirming the recipient | 1 fails |
| 14 | The temporary file is made 0644 | 1 fails |
| 15 | More than one matching account is not refused | 2 fail |

Together with the contract's own twelve breakages, everything the guard refuses is covered by a test that fails
when that rule is removed.
