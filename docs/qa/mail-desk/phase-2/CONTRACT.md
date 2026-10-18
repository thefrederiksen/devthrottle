# Mail Desk phase 2: the contract of `checks/screen.py`

This is what the screening check reads, what it writes back, and what it prints. Phase 3 (the Gateway's
`MailDeskService`) builds against it. The check is `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py`, and its
tests are in `docs/qa/mail-desk/phase-2/tests/`. Where this document and the code disagree, the tests decide, and one
of the two is a defect.

It follows the mission document (`docs/missions/mail-desk-2026-10-18/MISSION.md`, sections 5.4 to 5.7, with the phase 0
findings) and uses phase 1's words (`docs/qa/mail-desk/phase-1/CONTRACT.md`): the modes `observe` and `live`, the
decisions `archive` and `junk`, and `messageKey`, `sender` and `receivedUtc` as in phase 1's decision row.

## 1. How it runs

- The Director runs it as a definition check every 5 minutes with `uv run --script checks/screen.py`. It needs Python
  3.10 or later, and MSAL 1.28 or later is its only package (declared in its PEP 723 header). Every `CC_` variable is
  stripped except the `CC_CHECK_` ones.
- It reads one variable, `CC_CHECK_STATE_FILE`, the memory file. It takes no arguments and no secrets.
- **Exit 0:** it printed one output (section 4), rewrote the memory (section 3), and wrote its log to standard error.
- **Exit 1:** a failed check, and **the memory file is not touched**. The Gateway keeps the memory it had, and the next
  run reads the same mail again. Standard error is then:
  1. **line 1: one plain sentence** saying what is wrong and how to fix it, **at most 165 characters** (1.1). It never
     carries a path list or a stack trace, and it never names a Graph id;
  2. line 2, when there is one: the detail (the next step of the fix, where the check looked, the error the sign-in
     service gave, the Graph address that failed with any message id replaced by `{id}`);
  3. then the run's log so far (section 7);
  4. last, and only for a failure the check did not foresee: the Python traceback.
- **No failure is ever a traceback first.** Anything the check did not foresee is still one sentence on line 1:
  `screen.py stopped on an error it does not handle: <kind>: <message, cut to 50 characters>`, with
  `in <function>, screen.py line <n>` on line 2.

### 1.1 What the Gateway keeps of a failure: the reason window

**Phase 3 must build on this, not on the raw stream.** For a non-zero exit, the Gateway on `origin/main`
(`TriggerCheckContract.Read`) does not look for a line or a prefix. It takes **all** of standard error, joins every line
with one space (`OneLine`: split on carriage return, line feed and tab, trim, drop empty pieces), cuts it at **200
characters** (`QuotedOutputLimit`), adding `...` when it cut, and stores `exit code 1: <that>` as the run's reason. That
reason is the trigger's `LastReason`, which phase 1's signed-in label quotes verbatim, and which the Open problems card
and the 08:00 email show.

- **The reason does not start with the check's sentence.** It starts with `exit code 1: `, and the Director's process
  is `uv run --script checks/screen.py` (`DefinitionCheckRunner`), so whatever uv writes to standard error comes before
  the check's own first line. Phase 3 must not assume any prefix, and must not try to strip one.
- **What uv writes**, measured on devlinux with uv 0.12.23: nothing at all when the script's environment is already
  built (the usual run); `Installed 10 packages in 95ms` (29 characters) when it builds the environment from a warm
  package cache (after the script changes, or the cache was pruned); and `Downloading cryptography (4.5MiB)
  Downloaded cryptography Installed 10 packages in 16ms` (85 characters, flattened) on a machine whose package cache is
  empty.
- **The check's rule:** every sentence is at most `SENTENCE_LIMIT` = 200 - 35 = **165 characters**, so the whole
  sentence, fix and all, is inside the window behind a uv line of up to 34 characters. The machine is named without
  its domain. A failing Graph call is named by mailbox and step ("while reading the Inbox"), never by its address.
- **What the rule does not cover:** on a machine whose package cache is empty, uv's 85 characters can cut a long
  sentence on that first run. The failure repeats every 5 minutes and uv is silent from the second run, so the next
  reason is whole. An address much longer than the 21 and 18 characters tested here also eats into the 165.
- The tests judge every failure by the Gateway's own rule applied after a stand-in uv line
  (`test_what_the_gateway_keeps.py`), never by the raw stream.

### 1.2 Every failure sentence

`<machine>` is the computer's name without its domain; `<address>` is the channel's address in lower case.

| Failure | Line 1 |
|---|---|
| No saved sign-in, or it has expired, or it holds no account for the address | `<address> is not signed in on <machine>: run cc-outlook -a <nickname or address> auth` (section 6) |
| No cc-outlook account list on the machine holds the address | `<address> is not signed in on <machine>: first run cc-outlook accounts add <address> -c f575e3f4-8f62-4bfb-8f30-f776e55b51bd` (line 2 begins `then run cc-outlook -a <address> auth`; once it is added, the next run's failure names the sign-in) |
| More than one saved sign-in | `<address> is signed in more than once on <machine>: keep one and sign out of the others with cc-outlook` |
| The sign-in service cannot be reached | `could not reach the Microsoft sign-in service from <machine> for <address>: <error kind>` (for example `ConnectionError`, `ProxyError`, `Timeout`; the whole error on line 2) |
| The sign-in service answers with an error | `the Microsoft sign-in service failed on <machine> for <address>: <its message, cut to 50 characters>` |
| Graph answers an HTTP error | `Microsoft Graph answered HTTP <status> for <address> while <step>` |
| Graph cannot be reached | `Microsoft Graph could not be reached from <machine> for <address> while <step>: <error, cut to 50 characters>` |
| Graph answers something that is not JSON | `Microsoft Graph answered something that is not JSON for <address> while <step>` |
| A page without a `value` list | `Microsoft Graph answered a page without a 'value' list for <address> while <step>` |
| A message without a usable id or receive time, or a text field that is not text | `Microsoft Graph answered a message without a usable id or receive time for <address> while reading the Inbox` |
| An unknown provider | `provider <name> has no reader in this version of screen.py (channel <address>); this version reads: outlook` |
| The memory would outgrow the Director's limit | `the Mail Desk check's memory would be <n> bytes, over the Director's 65536, so nothing was written: give this factory fewer channels` |
| The memory is missing, empty, not JSON, unreadable, or holds a field this check did not write | one sentence naming the file or the channel and the field (3.2) |
| Anything else | `screen.py stopped on an error it does not handle: <kind>: <message>` |

`<step>` is one of `reading the Inbox`, `reading the Sent folder`, `moving a message to archive`, `moving a message to
junkemail`.

## 2. Limits

| What | Value | Why |
|---|---|---|
| Context | at most 7,168 bytes of UTF-8 | Phase 0 (b). The Director keeps at most 8 KB. |
| Messages in one run | at most 10, across all channels together | Phase 0 (b). |
| Filing results in one run | at most half the context, 3,584 bytes (about 50 results) | So filing never starves new mail. |
| Output | under 16,384 bytes | `TriggerCheckLimits.OutputCap`. Checked before printing. |
| Memory | at most 65,536 bytes of UTF-8 | `TriggerCheckLimits.StateCapBytes`. Above that the Director keeps no memory at all, so the check fails instead of writing it. |
| Preview | 200 characters, after cleaning | Mission 5.5. |
| First run | the most recent 200 Inbox messages, or those of the last 30 days, whichever is fewer | Mission D18. |
| Sent-folder cache | refreshed when older than 1 day: the most recent 500 sent messages, at most 400 addresses | Mission 5.5. |
| Short-key table | per channel, every queued key, plus the 40 most recently reported keys that are under 7 days old | Section 3.3. |

**One channel at its caps, measured:** 40 keys of 152-character ids, 400 sent addresses and a cursor of one id make a
channel's memory entry of **15,131 bytes**. So about **three channels** fit in the 65,536-byte memory, with room left
for the Gateway's own fields and a filing queue. A fourth would need smaller caps. When the memory would be too large,
the check fails and says so ("... over the Director's 65536, so nothing was written"), rather than lose it.

**Messages per run.** A message entry with every text field empty is 180 bytes, and the context around one channel is
139 bytes. So the 10-message stop binds for every message up to about 700 bytes, and the 7 KB budget binds only above
that. Phase 0 measured one message at 446 bytes typical and 680 bytes at worst, **counting its 152-character Graph id**.
This check reports a 16-character key instead, so the same mail is about 136 bytes smaller here. **Both sizes give 10
messages a run.** The test `test_Batch_TypicalAndWorstMeasuredSizes_TenPerRun` builds messages of exactly 446 and 680
bytes in this format and gets 10 a run. Messages of 900 bytes give 7 a run. The mission's "roughly 20 a run" does not
hold: the 10-message stop makes it 10 at most.

## 3. The memory (`CC_CHECK_STATE_FILE`)

The memory is one JSON object in UTF-8. **The Gateway writes the channel list. The check owns only the fields marked
"check" below, and it may remove attempted items from `filingQueue`.** The check writes the whole object back in compact
form. Every field it does not own stays exactly as it was, at the top level and inside each channel, so the Gateway may
keep its own fields there (for example the channel row's `id`, `kind`, `machines`, `factory` and `seat`). The tests
prove that a channel id, a machine name and nested objects come back unchanged.

### 3.1 Shape

```json
{
  "channels": [
    {
      "id": "c-0001",
      "address": "email@devthrottle.com",
      "provider": "outlook",
      "mode": "live",
      "machines": ["devlinux"],
      "filingQueue": [
        { "messageKey": "3f9c1e0a7b2d4c68", "decision": "archive" },
        { "messageKey": "a01b77c2e9f04d13", "decision": "junk" }
      ],
      "cursor": {
        "newest": "2026-10-18T09:41:07Z",
        "idsAtNewest": ["AAMkAGI2...152 characters...AAA="]
      },
      "sentCache": {
        "refreshedUtc": "2026-10-18T06:00:12Z",
        "hashes": ["5d41402abc4b", "7d793037a076"]
      },
      "keys": {
        "3f9c1e0a7b2d4c68": { "id": "AAMkAGI2...152 characters...AAA=", "reportedUtc": "2026-10-18T09:45:02Z" },
        "a01b77c2e9f04d13": { "id": "AAMkAGI2...152 characters...BBB=", "reportedUtc": "2026-10-18T09:45:02Z" }
      }
    }
  ]
}
```

### 3.2 The fields

| Field | Written by | Meaning |
|---|---|---|
| `channels` | Gateway | One entry per mailbox, in the order they are reported. It must be a list with at least one entry. An empty or missing list is a failed check, and so is an empty memory file. There is no default. |
| `address` | Gateway | The mailbox, as in phase 1's channel row. The check reads it in lower case. The saved sign-in's file name is made from it and matched **without regard to case**, because cc-outlook names the file after the address as it was typed when the account was added (6). Two entries with the same address, in any case, are a failed check. The check never hard-codes an address. |
| `provider` | Gateway | `outlook`. Any other value is a failed check that names it: `provider gmail has no reader in this version of screen.py (channel ...); this version reads: outlook`. Every channel is checked before any network call. |
| `mode` | Gateway | `observe` or `live`. Anything else is a failed check. |
| `id` | Gateway, optional | Copied into the channel's section of the report (4.2), so the Gateway can match it to its channel row. |
| `filingQueue` | Gateway adds; check removes | Items `{"messageKey": <short key>, "decision": "archive" or "junk"}`, oldest first. See 3.4. A missing queue means an empty one. |
| `cursor` | check | The newest receive time reported, and the Graph ids reported at exactly that time (the phase 0 pattern). A missing cursor means this channel has never reported a message, so the next run is a first run. The Gateway must not write it. |
| `sentCache` | check | When the Sent folder was last read, and a 12-hex-digit hash of each address written to (the SHA-256 of the lower-case address). **No address is kept in the memory.** |
| `keys` | check | The short-key table: each key, the Graph id it stands for, and when it was reported. |

The channel row carries **no nickname**. The sign-in fix finds the nickname for itself (section 6).

**The check's own fields are checked before any network call.** A `cursor` that is not `{"newest": <text>,
"idsAtNewest": [<text>, ...]}`, a `keys` table that is not an object of `{"id": <text>, "reportedUtc": <text>}`, or a
`sentCache` that is not `{"refreshedUtc": <text>, "hashes": [<text>, ...]}` is a failed check naming the channel and the
field (`the channel <address> has a 'keys' table this check did not write`). A memory file that cannot be read, or is
not UTF-8, is a failed check too. None of them is ever a traceback.

### 3.3 Short keys

- A message is reported by a **short key**: the first 16 hexadecimal digits of the SHA-256 of `<channel address>\n<Graph
  message id>`. The same message id in two mailboxes gets two different keys, so a key names its channel.
- The table maps each key back to its id, so the Gateway never handles a Graph id.
- **Pruning** happens at the end of every successful run, for each channel. A key is kept while it is in that
  channel's `filingQueue`, or while it is one of the **40 most recently reported** and was reported **under 7 days ago**.
  Every other key is dropped.
- **What the Gateway must do with it:** queue a filing item within 4 runs of the report, because at 10 messages a run
  the table reaches 40 keys in 4 runs. The Gateway decides at report time and writes the queue into the kept memory
  then, so the design meets this. A queued key that is no longer in the table is never guessed at. It is reported
  `refused` with the reason `no such key in this channel's key table`, and it is dropped from the queue.

### 3.4 The filing queue

- **Observe mode:** the queue is **not carried out**. No move request is made at all. The queue is left exactly as it
  is, its keys stay in the table, and the channel's section says so (`filingNotCarriedOut`, 4.2).
- **Live mode:** filing comes before new mail is read. Items are attempted oldest first while their results fit in the
  filing half of the context. The rest stay queued, in order, for the next run. Each attempted item is removed from the
  queue, whatever its result:
  - `moved`: Graph moved it (`archive` to the Archive folder, `junk` to Junk Email).
  - `already-gone`: Graph answered 404, so the message is no longer there (it was deleted, or moved already).
  - `refused`: nothing was sent, and a `reason` says why. The decision is not `archive` or `junk`, the key is not in
    the table, or the request guard refused the call (for example, an id holding a character a Graph id never has, a
    space or a control character). The reason never quotes the Graph id. A refusal is per item: the rest of the queue
    is still carried out and the run succeeds.
- Any other Graph answer to a move (500, 403, or no answer at all) is a **failed check**. The memory is not rewritten,
  so the whole queue is still there next run. A move that succeeded before the failure is therefore tried again, and
  this time it comes back `already-gone`, because Graph gives a moved message a new id. **The Gateway must read
  `already-gone` as "not in the Inbox any more", not as "moved by us".** Nothing is moved twice.
- **Not yet observed on a real mailbox:** that Graph answers 404 to a move of an id it has already moved. The fake Graph
  does; phase 6 should see it once on the real one. If the real Graph kept the old id usable, the retried move would
  be a second move into the same folder, which changes nothing.

### 3.5 The cursor and the first run

- **First run** (no `cursor`): the Inbox is read newest first, over the last 30 days and at most 200 messages, and then
  reported oldest first.
- **Later runs:** the Inbox messages received at or after `cursor.newest`, leaving out the ids in `cursor.idsAtNewest`,
  oldest first.
- The cursor advances **only past messages actually reported**. After a run it holds the receive time of the newest
  message reported, and every id reported at exactly that time (plus the ids it already held, when that time did not
  change). So when a batch is cut between messages of the same second, none is lost and none is repeated (test 5).
- A channel that reported nothing keeps its cursor. A channel that has never reported anything stays a first run.
- **What a receive-time cursor cannot see:** a message that enters the Inbox with a receive time older than the cursor
  (moved back from Junk Email or Archive, restored after deletion, or delivered late by the server) is never reported.
  This comes with the cursor the mission chose (the phase 0 pattern). Phase 3 must not promise that every message that
  is ever in the Inbox is screened.

## 4. The output

Standard output is exactly one line:

```json
{"count": 3, "context": "<the context, a JSON document as a string>"}
```

- `count` is the number of messages reported plus the number of filing items attempted (moved, already gone or
  refused). It is **0 only when nothing new was reported and nothing was filed**. A filing queue that was not carried
  out because of observe mode does not count.
- `context` is a string that holds JSON (4.1), at most 7,168 bytes of UTF-8. It is always present, even when `count` is
  0.
- Text is written as UTF-8 characters, not as `\u` escapes.

### 4.1 The context

```json
{
  "version": 1,
  "channels": [
    {
      "id": "c-0001",
      "address": "email@devthrottle.com",
      "provider": "outlook",
      "mode": "live",
      "messages": [
        {
          "messageKey": "c41d7e0b9a2f6e15",
          "receivedUtc": "2026-10-18T09:41:07Z",
          "sender": "billing@example.com",
          "senderName": "Example Billing",
          "subject": "Your invoice for October",
          "hasAttachments": true,
          "knownSender": false,
          "preview": "Your invoice for October is ready. The total is 42 dollars and it is due on the first of next month."
        }
      ],
      "filing": [
        { "messageKey": "3f9c1e0a7b2d4c68", "decision": "archive", "result": "moved" },
        { "messageKey": "a01b77c2e9f04d13", "decision": "junk", "result": "already-gone" },
        { "messageKey": "ffffffffffffffff", "decision": "archive", "result": "refused",
          "reason": "no such key in this channel's key table" }
      ],
      "more": false
    },
    {
      "address": "second@example.org",
      "provider": "outlook",
      "mode": "observe",
      "messages": [],
      "filing": [],
      "more": true,
      "filingNotCarriedOut": { "count": 2, "reason": "the channel is in observe mode" }
    }
  ]
}
```

(It is shown indented here; the check writes it compact.)

### 4.2 The fields

| Field | Meaning |
|---|---|
| `version` | `1`. Any change to this shape raises it. |
| `channels` | One section per channel in the memory, in the memory's order. Every channel has a section, even one with nothing in it. |
| `id` | The channel's `id` from the memory, present only when the memory has one. |
| `address`, `provider`, `mode` | The channel, as the memory gave them (the address in lower case). |
| `messages` | New mail reported in this run, oldest first. Messages are taken oldest first across all channels together, so one busy mailbox does not hold back another's older mail. |
| `messages[].messageKey` | The short key (3.3). The Gateway uses it to queue filing. |
| `messages[].receivedUtc` | Graph's `receivedDateTime`, exactly as Graph wrote it (in UTC, ending in `Z`). |
| `messages[].sender` | The sender address in lower case. It is empty when Graph gave none. |
| `messages[].senderName` | The sender's display name, cleaned (4.3). |
| `messages[].subject` | The subject, cleaned (4.3). |
| `messages[].hasAttachments` | Graph's `hasAttachments`. |
| `messages[].knownSender` | True when the sender is among the addresses in the Sent-folder cache (the to, cc or bcc of the most recent 500 sent messages). |
| `messages[].preview` | Graph's `bodyPreview`, cleaned (4.3), **then** cut to 200 characters. |
| `messages[].trimmed` | Present, and `true`, only on a message that did not fit the budget on its own (4.4). |
| `filing` | Live mode only: the result of each item attempted (3.4), in queue order. It is empty otherwise. |
| `filingNotCarriedOut` | Present only when the channel is in observe mode and its queue is not empty. It says how many items were waiting, and why they were not carried out. |
| `more` | True when this channel has more new mail than this run reported. The next run continues from there. |

### 4.3 Cleaning text

The subject, the sender's name and the preview are cleaned the same way, and the cleaning comes **before** the preview
is cut:

1. Every character of Unicode category `Cc` (control), `Cf` (format: the zero-width space, joiner and non-joiner, the
   word joiner, the soft hyphen, the byte order mark, direction marks), `Cs` and `Co` is removed. So are the invisible
   letters that senders pad with: U+034F, U+115F, U+1160, U+3164, U+FFA0 and U+2800.
2. Every run of whitespace (including tabs, line breaks and no-break spaces) becomes one space, and spaces at the ends
   are removed.
3. Only then is the preview cut to 200 characters.

Phase 0 (c) found that 8 of 19 real previews were mostly padding. Cutting first would have left only 46 to 99 visible
characters. Nothing else is changed: no word is rewritten or translated.

### 4.4 A message larger than the budget

When the first message of a run does not fit in what is left of the context, it is reported **trimmed**. Its preview
is emptied, then its subject is shortened, then its sender's name, until it fits, and it carries `"trimmed": true`. Its
key, its time, the sender address, `hasAttachments` and `knownSender` are never cut. The cursor then moves past it as
usual, so one huge message never stalls the check. A message that does not fit **after** others in the same run is left
for the next run, where it comes first.

## 5. What the check may send to Microsoft Graph

Every Graph call goes through one function, `graph_request()`. It refuses all of the following **before anything
reaches the network**:

- every method other than `GET` and `POST`, including `PATCH`, `DELETE`, `PUT`, `HEAD` and a lower-case `post`;
- every `POST` other than `POST https://graph.microsoft.com/v1.0/me/messages/{id}/move` with a body of exactly
  `{"destinationId": "archive"}` or `{"destinationId": "junkemail"}`. So these are all refused: `sendMail`, `send`,
  `reply`, `replyAll`, `forward`, `createReply`, `copy`, a new draft, another mailbox's path, the `beta` version, a move
  to any other folder, a body with an extra key, no body at all, and a move with a query;
- an id holding anything but letters, digits, `-`, `_` and `=` (so no `/`, `.` or `%`), and any path holding `..`, `%`
  or `\`;
- any address holding a space or a control character, checked **before** the address is parsed, because Python's
  `urlsplit` silently removes tab, carriage return and line feed and would otherwise hand the rules above a clean
  path;
- any host other than `https://graph.microsoft.com` with no port and no user name, and any address with a fragment;
- a `GET` with a body, or a `GET` outside `/v1.0/`.

The saved sign-in's token **can** send mail (phase 0 finding), so this guard is the whole of the promise that the Mail
Desk never sends. The tests send every one of these shapes and assert that the fake Graph's log is empty for each.

The check's only other network traffic is MSAL's own renewal of the sign-in, to `login.microsoftonline.com`, asking for
`https://graph.microsoft.com/Mail.ReadWrite` only.

## 6. The sign-in (Outlook)

- The check looks for the saved sign-in only under the Director's per-user base folder: on Linux
  `~/.local/share/cc-director`, on macOS `~/Library/Application Support/cc-director`, and on Windows
  `%LOCALAPPDATA%\cc-director`. Inside it, the file is `instances/*/config/outlook/tokens/<name>_msal.json`, where
  `<name>` is the address with `@` and `.` turned into `_` (cc-outlook's `get_msal_token_path`). On Linux and macOS the
  home folder comes from the operating system's account record, never from `HOME`. There is no fallback to
  `~/.cc-director` or to `HOME`.
- It signs in with cc-outlook's Azure app (`f575e3f4-8f62-4bfb-8f30-f776e55b51bd`) and authority
  (`https://login.microsoftonline.com/common`).
- **The fix names cc-outlook's own nickname, never a guessed one.** The check reads cc-outlook's account list in every
  instance, `instances/*/config/outlook/profiles.json` (`PROFILES_FILE` in `tools/cc-outlook/src/auth.py`):
  - The nickname is the one stored for the account, or else the one cc-outlook derives from the domain
    (`get_nickname` and `derive_nickname`: `email@devthrottle.com` gives `devthrottle`).
  - The nickname is used only when cc-outlook's own nickname lookup (`_build_nickname_map`, in which the last account
    wins) leads back to this address. Otherwise, or when instances disagree, the fix names the address, because
    cc-outlook always matches an exact address first (`resolve_account`).
  - When no instance lists the account, the fix says how to add it.
- The first line of each failure:

| Case | Line 1 (one sentence) | Line 2 |
|---|---|---|
| No saved sign-in | `<address> is not signed in on <machine>: run cc-outlook -a <nickname> auth` | where it looked |
| The sign-in cannot be renewed (expired) | the same sentence | `the saved sign-in at <path> has expired and could not be renewed (<error>)` |
| The saved file holds no account for the address, or is not a sign-in cache | the same sentence | what was wrong with the file |
| The account is in no cc-outlook account list | `<address> is not signed in on <machine>: first run cc-outlook accounts add <address> -c f575e3f4-8f62-4bfb-8f30-f776e55b51bd` | `then run cc-outlook -a <address> auth`, then where it looked. Once the account is added, the next run fails with the row above, naming the sign-in. |
| More than one saved sign-in | `<address> is signed in more than once on <machine>: keep one and sign out of the others with cc-outlook` | every path found. The check never picks one. |
| The sign-in service cannot be reached | `could not reach the Microsoft sign-in service from <machine> for <address>: <error kind>` | the whole error |

`<machine>` is this computer's name as the operating system gives it, without its domain. Every sentence is in 1.2.

- **Case.** cc-outlook keeps an account under the address exactly as it was typed (`save_profile`), names the sign-in
  file after it (`get_msal_token_path`), and `-a <address>` matches it exactly and with case (`resolve_account`); only
  nicknames are matched without case, and they are stored in lower case. So the check finds the file without regard to
  case, MSAL finds the account without regard to case (`get_accounts`), and when the fix must name the address it names
  it as cc-outlook holds it. Two sign-in files differing only in case are "more than once".
- **The sign-in service out of reach.** MSAL calls `login.microsoftonline.com` when it is set up (the authority's
  configuration) and when it renews an expired token. When it cannot, the check fails with one sentence naming the
  machine, the address and the kind of error, and the whole error on line 2.

- A renewed sign-in is written back to the same file, readable by its owner only (mode 0600) on Linux and macOS, exactly
  as cc-outlook does it. It never goes into the memory or the output.
- **When any one channel fails, the whole run fails**, so no channel's memory moves (section 1). One mailbox whose
  sign-in has expired therefore stops the screening of every mailbox until it is signed in again.

## 7. Standard error

Standard error is the check's only log. It is written in plain words, one line each, every line starting
`screen.py: `. It names the channels and their modes, the filing results by key, any queue not carried out, any
Sent-folder refresh, how many messages were reported and whether more mail is waiting. It never holds a token, a
sender, a subject, a name or any body text (a test asserts this). On a failure, the sentence and its detail come first
and the log follows (section 1). Only the first 200 characters, flattened, reach the Gateway (1.1); the rest is for a
person reading the Director's own record of the run.

## 8. The tests

```
uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v
```

The tests run with every `CC_` variable removed, a scratch home and a scratch Director base folder, a fake MSAL
sign-in, and a fake Graph that stands in for `urllib.request.urlopen` and logs every request. They use no real mailbox,
no real token, no network and no port.

To show the tests can fail, the check was broken on purpose six ways, and each break turned at least one test red: the
guard letting `PATCH` through; observe mode filing anyway; the preview cut before cleaning; the cursor forgetting the
ids it held at its time; the nickname collision ignored; and `junk` filed to Archive. For the review
(`docs/qa/mail-desk/phase-2/reviews/`), the whole suite was also run against the check as it stood before the review
was answered (commit `200156d4d`): 92 of its 208 tests fail there, every one of them a test added or changed for a
finding or an observation, and all 208 pass on the answered check.

| Brief | What it proves | File |
|---|---|---|
| 1 | Nothing but GET and the two moves leaves the guard. The log is empty for each refused shape, and only `transport()` reaches the network. | `test_request_guard.py` |
| 2 | Observe mode makes no move when a queue is present, and the queue survives untouched. | `test_filing.py` |
| 3 | Live mode moves mail to Archive and Junk Email and reports it. A vanished message is `already-gone`, and a long queue is filed over several runs. | `test_filing.py` |
| 4 | The 10-message and 7 KB stops; catching up over several runs with nothing skipped and nothing twice; the oversized message. | `test_batches_and_cursor.py` |
| 5 | The cursor never passes what was reported, including a cut inside one second. | `test_batches_and_cursor.py` |
| 6 | No sign-in, more than one, no account, or renewal failing: a non-zero exit whose first line names the machine and the fix, with the nickname taken from cc-outlook's own list, and the memory unchanged byte for byte. | `test_sign_in_and_failures.py` |
| 7 | Graph answering 500 or 503, unreachable, not JSON, or a page without values: a non-zero exit, the memory unchanged, and the same mail read next run. | `test_sign_in_and_failures.py` |
| 8 | Invisible characters are stripped before the 200-character cut, with the kind of padding phase 0 found. | `test_text_channels_and_keys.py` |
| 9 | Two channels in one run, signed in separately and sharing the 10-message stop; an unknown provider fails and names itself. | `test_text_channels_and_keys.py` |
| 10 | Non-Latin subjects and names survive, and the budget counts bytes, not characters. | `test_batches_and_cursor.py` |
| Review 1 | Every kind of failure, with one channel and two, in observe and live mode: the whole first sentence is inside what the Gateway keeps, after a uv line, judged by the Gateway's own rule; a real-length Graph id cannot push it out. | `test_what_the_gateway_keeps.py` |
| Review 2 | The sign-in service out of reach (in setup and in renewal), a memory field this check did not write, a memory that is not UTF-8, a message field of the wrong kind, and anything unforeseen: one sentence first, never a traceback, memory unchanged. | `test_no_traceback.py` |
| Review 3 | A space or a control character in an id is refused by the guard itself, and per item: the rest of the queue is filed. | `test_request_guard.py` |
| Observation 1 | A sign-in file named with capitals is found; two differing only in case are "more than once"; the fix names the address as cc-outlook holds it; the premise is read from cc-outlook's source. | `test_sign_in_and_failures.py` |
