# Mail Desk phase 2: the contract of `checks/screen.py`

What the screening check reads, what it writes back, and what it prints. Phase 3 (the Gateway's `MailDeskService`)
builds against this. The check is `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py`, and its tests are in
`docs/qa/mail-desk/phase-2/tests/`. Where this document and the code disagree, the tests decide, and one of the two is
a defect.

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md`, sections 5.4 to 5.7, with the phase 0 findings.

## 1. How it runs

- The Director runs it as a definition check every 5 minutes: `uv run --script checks/screen.py`, Python 3.10 or
  later, with MSAL 1.28 or later as its only package (declared in its PEP 723 header). Every `CC_` variable is
  stripped except the `CC_CHECK_` ones.
- It reads one variable: `CC_CHECK_STATE_FILE`, the memory file. It takes no arguments and no secrets.
- Exit 0: it printed one output (section 4) and rewrote the memory (section 3).
- Exit 1: a failed check. The reason is on standard error, starting `check failed: `. **The memory file is not
  touched**, so the Gateway keeps the memory it had, and the next run reads the same mail again.

## 2. Limits

| What | Value | Why |
|---|---|---|
| Context | at most 7,168 bytes of UTF-8 | Phase 0 (b). The Director keeps at most 8 KB. |
| Messages in one run | at most 10, across all channels together | Phase 0 (b). |
| Filing results in one run | at most half the context, 3,584 bytes (about 60 results) | So filing never starves new mail. |
| Output | under 16,384 bytes | `TriggerCheckLimits.OutputCap`. Checked before printing. |
| Memory | at most 65,536 bytes of UTF-8 | `TriggerCheckLimits.StateCapBytes`. Above it the Director keeps no memory at all, so the check fails instead of writing it. |
| Preview | 200 characters, after cleaning | Mission 5.5. |
| First run | the most recent 200 Inbox messages, or those of the last 30 days, whichever is fewer | Mission D18. |
| Sent-folder cache | refreshed when older than 1 day; the most recent 500 sent messages, at most 400 addresses | Mission 5.5. |
| Short-key table | per channel: every queued key, plus the 40 most recently reported that are under 7 days old | Section 3.3. |

**Measured, one channel at its caps** (the 40 keys of 152-character ids, 400 sent addresses, a cursor of one id): the
channel's memory entry is **15,118 bytes**. So about **three channels** fit in the 65,536-byte memory with room for the
Gateway's own fields and a filing queue; a fourth needs smaller caps. The check fails loudly ("the memory would be N
bytes, over the Director's 65536") rather than lose the memory.

**Messages per run.** A message entry with every text field empty is 162 bytes; the context around one channel is 139
bytes. So the 10-message stop binds for every message up to about 700 bytes, and the 7 KB budget binds only above that.
Phase 0 measured a message at 446 bytes typical and 680 at worst, **counting its 152-character Graph id**. This check
reports a 16-character key instead, so the same messages are about 310 and 544 bytes here. **Both give 10 messages a
run** (test `test_Batch_TypicalAndWorstMeasuredSizes_TenPerRun` builds messages of exactly 446 and 680 bytes in this
format and gets 10 a run). Messages of 900 bytes give 7 a run. The mission's "roughly 20 a run" does not hold: the
10-message stop makes it 10 at most.

## 3. The memory (`CC_CHECK_STATE_FILE`)

One JSON object, UTF-8. **The Gateway writes the channel list; the check owns only the fields marked "check" below,
plus removing attempted items from `filingQueue`.** The check writes the whole object back, compact, and keeps every
field it does not own exactly as it was - at the top level and inside each channel - so the Gateway may keep its own
fields there (the tests prove a channel id, a machine name and nested objects come back unchanged).

### 3.1 Shape

```json
{
  "channels": [
    {
      "address": "email@devthrottle.com",
      "provider": "outlook",
      "mode": "observe",
      "nickname": "devthrottle",
      "filingQueue": [
        { "key": "3f9c1e0a7b2d4c68", "to": "archive" },
        { "key": "a01b77c2e9f04d13", "to": "junkemail" }
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
| `channels` | Gateway | One entry per mailbox, in the order they are reported. Must be a non-empty list: an empty or missing list is a failed check ("the memory holds no 'channels'"), and so is an empty memory file. There is no default. |
| `address` | Gateway | The mailbox, **lower case, exactly as registered in cc-outlook**: the saved sign-in's file name is made from it. Two entries with the same address (in any case) are a failed check. It is never hard-coded in the check. |
| `provider` | Gateway | `outlook`. Any other value is a failed check naming it: `provider gmail has no reader in this version of screen.py (channel ...)`. All channels are checked before any network call. |
| `mode` | Gateway | `observe` or `live`. Anything else is a failed check. |
| `nickname` | Gateway, optional | The cc-outlook nickname, used only in the fix of a sign-in failure. Without it the fix names the address (`cc-outlook -a <address> auth`, which cc-outlook accepts, `resolve_account` in `tools/cc-outlook/src/auth.py`). The check never guesses a nickname. |
| `filingQueue` | Gateway adds; check removes | Items `{"key": <short key>, "to": "archive" or "junkemail"}`, oldest first. See 3.4. Missing means empty. |
| `cursor` | check | The newest receive time reported, and the Graph ids reported at exactly that time (the phase 0 pattern). Missing means this channel has never reported a message: the next run is a first run. The Gateway must not write it. |
| `sentCache` | check | When the Sent folder was last read, and a 12-hex-digit hash (SHA-256 of the lower-case address) of each address written to. **No address is kept in the memory.** |
| `keys` | check | The short-key table: key to Graph id and when it was reported. |

### 3.3 Short keys

- A message is reported by a **short key**: the first 16 hexadecimal digits of SHA-256 of `<channel address>\n<Graph
  message id>`. The same message id in two mailboxes has two different keys, so a key names its channel.
- The table maps the key back to the id, so the Gateway never handles a Graph id.
- **Pruning** happens at the end of every successful run, per channel: a key is kept while it is in that channel's
  `filingQueue`, or while it is one of the **40 most recently reported** and was reported **under 7 days ago**.
  Everything else is dropped.
- **What the Gateway must do with it:** queue a filing item within 4 runs of the report (at least 10 messages a run
  reach 40 keys in 4 runs). The Gateway decides at report time and writes the queue into the kept memory then, so this
  is met by design. A queued key no longer in the table is not guessed at: it is reported `refused` with the reason
  `no such key in this channel's key table`, and dropped from the queue.

### 3.4 The filing queue

- **Observe mode:** the queue is **not carried out**. No move request is made at all, the queue is left exactly as it
  is (and its keys are kept in the table), and the channel's report says so (4.2, `filingNotCarriedOut`).
- **Live mode:** filing comes before reading new mail. Items are attempted oldest first while their results fit in the
  filing half of the context; the rest stay queued, in order, for the next run. Each attempted item is removed from
  the queue, whatever its result:
  - `moved` - Graph moved it.
  - `already-gone` - Graph answered 404: the message is no longer there (deleted, or moved already).
  - `refused` - not sent, with a `reason`: the destination is not `archive` or `junkemail`, the key is not in the
    table, or the request guard refused it (for example an id holding a character a Graph id never has).
- Any other Graph answer to a move (500, 403, unreachable) is a **failed check**. The memory is not rewritten, so the
  whole queue is still there next run. A move that succeeded before the failure is therefore tried again, and is then
  `already-gone`, because Graph gives a moved message a new id. **The Gateway must read `already-gone` as "not in the
  Inbox any more", not as "moved by us".** Nothing is moved twice.

### 3.5 The cursor and the first run

- **First run** (no `cursor`): the Inbox is read newest first, received in the last 30 days, at most 200 messages,
  then reported oldest first.
- **Later runs:** Inbox messages received at or after `cursor.newest`, leaving out the ids in `cursor.idsAtNewest`,
  oldest first.
- The cursor advances **only past messages actually reported**. After a run it is the receive time of the newest
  message reported, and every id reported at exactly that time (plus the ids already held when that time did not
  change). So a batch cut between messages of the same second loses none and repeats none (test 5).
- A channel that reported nothing keeps its cursor; a channel that never reported anything stays a first run.

## 4. The output

Standard output is exactly one line:

```json
{"count": 3, "context": "<the context, a JSON document as a string>"}
```

- `count` = messages reported + filing items attempted (moved, already gone or refused). It is **0 only when nothing
  new was reported and nothing was filed**. A filing queue not carried out in observe mode does not count.
- `context` is a string holding JSON (4.1), at most 7,168 bytes of UTF-8. It is always present, even when `count` is 0.
- Text is UTF-8, written as characters, not `\u` escapes.

### 4.1 The context

```json
{
  "version": 1,
  "channels": [
    {
      "address": "email@devthrottle.com",
      "provider": "outlook",
      "mode": "live",
      "messages": [
        {
          "key": "c41d7e0b9a2f6e15",
          "received": "2026-10-18T09:41:07Z",
          "from": "billing@example.com",
          "name": "Example Billing",
          "subject": "Your invoice for October",
          "hasAttachments": true,
          "knownSender": false,
          "preview": "Your invoice for October is ready. The total is 42 dollars and it is due on the first of next month."
        }
      ],
      "filing": [
        { "key": "3f9c1e0a7b2d4c68", "to": "archive", "result": "moved" },
        { "key": "a01b77c2e9f04d13", "to": "junkemail", "result": "already-gone" },
        { "key": "ffffffffffffffff", "to": "archive", "result": "refused", "reason": "no such key in this channel's key table" }
      ],
      "more": false
    },
    {
      "address": "second@example.org",
      "provider": "outlook",
      "mode": "observe",
      "messages": [],
      "filing": [],
      "filingNotCarriedOut": { "count": 2, "reason": "the channel is in observe mode" },
      "more": true
    }
  ]
}
```

(Shown indented; the check writes it compact.)

### 4.2 The fields

| Field | Meaning |
|---|---|
| `version` | `1`. A change to this shape raises it. |
| `channels` | One section per channel in the memory, in the memory's order, always present even with nothing in it. |
| `address`, `provider`, `mode` | The channel, as the memory gave them (address lower case). |
| `messages` | New mail reported in this run, oldest first. Across all channels together the messages are taken oldest first, so one busy mailbox does not hold back another's older mail. |
| `messages[].key` | The short key (3.3). The Gateway uses it to queue filing. |
| `messages[].received` | Graph's `receivedDateTime`, as Graph wrote it (UTC, `Z`). |
| `messages[].from` | The sender address, lower case. Empty when Graph gave none. |
| `messages[].name` | The sender's display name, cleaned (4.3). |
| `messages[].subject` | The subject, cleaned (4.3). |
| `messages[].hasAttachments` | Graph's `hasAttachments`. |
| `messages[].knownSender` | True when the sender is among the addresses in the Sent-folder cache (to, cc or bcc of the most recent 500 sent messages). |
| `messages[].preview` | Graph's `bodyPreview`, cleaned (4.3), **then** cut to 200 characters. |
| `messages[].trimmed` | Present, and `true`, only on a message that alone did not fit the budget (4.4). |
| `filing` | Live mode: the result of each item attempted (3.4), in queue order. Empty otherwise. |
| `filingNotCarriedOut` | Present only when the channel is in observe mode and its queue is not empty: how many items, and why. |
| `more` | True when this channel has more new mail than this run reported; the next run continues. |

### 4.3 Cleaning text

Subject, name and preview are cleaned the same way, **before** the preview is cut:

1. Every character of Unicode category `Cc` (control), `Cf` (format: zero-width space, zero-width joiner and
   non-joiner, word joiner, soft hyphen, byte order mark, direction marks), `Cs` and `Co` is removed, and so are the
   invisible letters senders pad with: U+034F, U+115F, U+1160, U+3164, U+FFA0, U+2800.
2. Every run of whitespace (including tabs, line breaks and no-break spaces) becomes one space; the ends are trimmed.
3. The preview is then cut to 200 characters.

Phase 0 (c) found 8 of 19 real previews were mostly padding; cutting first would have left 46 to 99 visible
characters. Nothing else is changed: no word is rewritten or translated.

### 4.4 A message larger than the budget

If the first message of a run does not fit in what is left of the context on its own, it is reported **trimmed**: its
preview is emptied, then its subject is shortened, then its name, until it fits, and it carries `"trimmed": true`.
Its key, time, sender address, `hasAttachments` and `knownSender` are never cut. The cursor then moves past it as
usual, so one huge message never stalls the check. A message that does not fit **after** others in the same run is
left for the next run, where it is first.

## 5. What the check may send to Microsoft Graph

Every Graph call goes through one function, `graph_request()`. It refuses, **before anything reaches the network**:

- every method but `GET` and `POST` (so `PATCH`, `DELETE`, `PUT`, `HEAD`, and a lower-case `post`);
- every `POST` but `POST https://graph.microsoft.com/v1.0/me/messages/{id}/move` with a body of exactly
  `{"destinationId": "archive"}` or `{"destinationId": "junkemail"}` - so `sendMail`, `send`, `reply`, `replyAll`,
  `forward`, `createReply`, `copy`, a new draft, another mailbox's path, the `beta` version, a move with any other
  destination, any extra key, no body, or a query, are all refused;
- an id holding anything but letters, digits, `-`, `_` and `=` (so no `/`, `.`, or `%`), and any path holding `..`,
  `%` or `\`;
- any host but `https://graph.microsoft.com` with no port and no user, and any address with a fragment;
- a `GET` with a body, or outside `/v1.0/`.

The saved sign-in's token **can** send mail (phase 0 finding), so this guard is the whole of the promise that the Mail
Desk never sends. The tests send each of these shapes and assert the fake Graph's log is empty for each.

The only other network traffic is MSAL's own renewal of the sign-in, to `login.microsoftonline.com`, asking for
`https://graph.microsoft.com/Mail.ReadWrite` only.

## 6. The sign-in (Outlook)

- Looked for only under the Director's per-user base folder - Linux `~/.local/share/cc-director`, macOS
  `~/Library/Application Support/cc-director`, Windows `%LOCALAPPDATA%\cc-director` - at
  `instances/*/config/outlook/tokens/<name>_msal.json`, where `<name>` is the address with `@` and `.` turned into `_`
  (cc-outlook's `get_msal_token_path`). On Linux and macOS the home comes from the operating system's account record,
  never from `HOME`. There is no fallback to `~/.cc-director` or to `HOME`.
- cc-outlook's Azure app (`f575e3f4-8f62-4bfb-8f30-f776e55b51bd`) and authority (`https://login.microsoftonline.com/common`).
- Failed checks, each naming the fix:
  - none found: `<address> is not signed in on <machine> (looked for ... and found none): run cc-outlook -a <nickname or address> auth`;
  - more than one: `<address> is signed in more than once on <machine> ...: <every path>. Keep one ...` - it never picks one;
  - the cache holds no account for the address: `<address> is not signed in on <machine> ...: run cc-outlook ...`;
  - the silent renewal fails: `the sign-in for <address> on <machine> has expired and could not be renewed (<error>): run cc-outlook ...`.
- A renewed sign-in is written back to the same file, owner-only (0600) on Linux and macOS, exactly as cc-outlook does.
  It never goes into the memory or the output.
- **A failure of any one channel fails the whole run**, so no channel's memory moves (section 1). One mailbox whose
  sign-in has expired therefore stops the screening of every mailbox until it is signed in again.

## 7. Standard error

The check's only log. Plain words, one line each, prefixed `screen.py: `: the channels and their modes, filing
results by key, a queue not carried out, a Sent-folder refresh, how many messages were reported and whether more
waits. It never holds a token, a sender, a subject, a name or any body text (a test asserts this). On failure the last
line is `check failed: <reason>`.

## 8. The tests

```
uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v
```

They run with every `CC_` variable removed, a scratch home and a scratch Director base folder, a fake MSAL sign-in and
a fake Graph that stands in for `urllib.request.urlopen` and logs every request. No real mailbox, no real token, no
network, no port.

| Brief | What it proves | File |
|---|---|---|
| 1 | Nothing but GET and the two moves leaves the guard; the log is empty for each refused shape; only `transport()` reaches the network | `test_request_guard.py` |
| 2 | Observe mode makes no move with a queue present; the queue survives untouched | `test_filing.py` |
| 3 | Live mode moves to Archive and Junk and reports it; a vanished message is `already-gone`; a long queue is filed over several runs | `test_filing.py` |
| 4 | 10-message and 7 KB stops; catch-up over several runs, nothing skipped or twice; the oversized message | `test_batches_and_cursor.py` |
| 5 | The cursor never passes what was reported, including a cut inside one second | `test_batches_and_cursor.py` |
| 6 | No sign-in, more than one, no account, renewal failing: non-zero exit naming the fix, memory byte for byte unchanged | `test_sign_in_and_failures.py` |
| 7 | Graph 500, 503, unreachable, not JSON, a page without values: non-zero exit, memory unchanged, the same mail next run | `test_sign_in_and_failures.py` |
| 8 | Invisible characters stripped before the 200-character cut, with phase 0's kind of padding | `test_text_channels_and_keys.py` |
| 9 | Two channels in one run, signed in separately, sharing the 10-message stop; an unknown provider fails naming it | `test_text_channels_and_keys.py` |
| 10 | Non-Latin subjects and names survive; the budget counts bytes, not characters | `test_batches_and_cursor.py` |
