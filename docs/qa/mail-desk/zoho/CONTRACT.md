# The Mail Desk Zoho reader: contract

The `zoho` provider reads a Zoho mailbox through Zoho's own API with `dt-zoho`. It is the class `ZohoReader` in
`src/CcDirector.Gateway/MailDesk/Template/checks/screen.py`. Everything in phase 2's contract
(`docs/qa/mail-desk/phase-2/CONTRACT.md`) and phase 3's (one mailbox failing never stops the others) holds for it
unchanged; this file says only what is particular to Zoho.

The `imap` provider (`docs/qa/mail-desk/imap/CONTRACT.md`) can read the same mailbox over IMAP. The `zoho` provider
needs neither the mailbox's IMAP switch nor an app password: one Zoho key signs in for mail, calendar and admin.

## What a machine needs

- `dt-zoho` and `cc-secrets` on the PATH of the check. cc-secrets starts dt-zoho, so the check looks for dt-zoho
  itself first and says plainly when it is missing.
- dt-zoho's settings with the organisation's data centre (`dt-zoho settings set --data-centre ca`). dt-zoho keeps them,
  with its sealed access token, under the folder `CC_DIRECTOR_ROOT` names, else cc-storage's. The check runs with no
  `CC_` variable, so it looks in the Director's folder, cc-storage's user folder when that differs (`dt_email_bases`,
  the same search as dt-email's), and every `instances/*` under them that holds `config/dt-zoho/config.json`, and runs
  every dt-zoho command with `CC_DIRECTOR_ROOT` set to that folder. Settings in two of them fail the channel: keep one.
- The Zoho key in cc-secrets on that machine, allowed for `run`: a Self Client of the mailbox's organisation, in the
  entries `zoho-api-client-secret`, `zoho-api-client-id` and `zoho-api-refresh-token` (dt-zoho's README). One key
  reaches every mailbox of its organisation that `dt-zoho accounts` lists, aliases included. When cc-secrets refuses
  an entry (missing, not allowed for run, or one it cannot hide in output), cc-secrets's own reason is the sentence.

## The key

It never passes through the check. Every dt-zoho command runs as
`cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token --timeout 60 --json --
dt-zoho ...`, so cc-secrets puts the key in dt-zoho's three variables for that one command and removes it from what
the command prints. The check names the entries, never a value. Nothing about the key is in the memory, the output,
standard error, the command lines or on the Gateway (`test_TheKey_NeverPassesThroughTheCheck`).

## The guard

`check_dt_zoho()` is the only way to dt-zoho. It lets out exactly:

- `accounts --json`;
- `folders --account <address> --json`;
- `list` with only `--folder`, `--start` and `--count`, each once and each a number, then `--account <address> --json`;
- in live mode only, once the folders are read: `move <one id> --to <the Archive or the Spam folder's id>
  --from-folder <the Inbox's id>`, then `--account <address> --json`.

dt-zoho can send, reply, delete, mark spam, change admin settings (aliases, the IMAP switch, forwarding, the vacation
reply) and create calendar events with meeting links, so this guard is what keeps the Mail Desk from doing any of
them; a test names each of those and proves it refused, in live mode too. A process is started in exactly one place,
`run_dt_tool()`, which only the IMAP and Zoho readers' `tool()` call, each after its guard.

## The folders

`dt-zoho folders` is read once per run, when signing in. Zoho makes `/Inbox`, `/Sent`, `/Archive` and `/Spam` itself;
each is found by its path being `/<its type>`, because a folder the owner makes is given the type Inbox too. A
missing one fails the channel naming it. Every list and move names a folder by its id.

## The cursor and ids

- A message's id is Zoho's message id (digits).
- Zoho lists a folder newest first and has no call for what came after an id, so the cursor is Outlook's:
  `{"provider": "zoho", "newest": "<UTC time>", "idsAtNewest": ["<id>", ...]}` - the arrival time of the newest message
  reported, and every id reported at exactly that time. Zoho's times are to the second, so two messages can share one.
- dt-zoho prints an arrival time in the account's own zone with its offset; the check turns it into UTC.
- First run: the most recent 200 Inbox messages, or those of the last 30 days, whichever is fewer (one `list --count
  200`), reported oldest first, 10 at a time; the rest come in the following runs.
- Later runs: the Inbox is read newest first, 200 at a time (`--start 1`, `201`, ...), back to a page that holds a
  message older than the cursor's time, or to the end; every page is read whole. New mail is what arrived after the
  cursor's time, or at that time and is not among `idsAtNewest`, oldest first. A message read twice (new mail moved it
  down a page while the Inbox was read) is counted once. More than 20 pages without reaching the cursor fails the
  channel for this run rather than starting anywhere.
- As with Outlook, mail moved into the Inbox later with an arrival time before the cursor's is not reported.

## Filing

- `archive`: `dt-zoho move <id> --to <Archive> --from-folder <Inbox>`.
- `junk`: `dt-zoho move <id> --to <Spam> --from-folder <Inbox>`.
- Never `dt-zoho spam` and never `dt-zoho archive`. dt-zoho's README: Zoho's spam moves every message in the
  conversation into Spam, the owner's own sent replies too. A move names one message (`test_LiveMode_JunkOnAConversation
  TheOwnerAnswered_TheSentReplyStaysInSent`). That a move leaves the rest of a real conversation where it is was not
  run live: the live run was in watch mode.
- `--from-folder` makes dt-zoho read where Zoho places the message (its details call answers with the real folder,
  whatever folder is in the path; seen live) and move nothing unless it is the Inbox. "message <id> is not in /Inbox"
  - the owner moved it, deleted it, or Zoho no longer has it ("messageId is invalid", seen live) - is `already-gone`.
  dt-zoho checks just before it moves; a message moved by hand in between those two calls is moved.
- Any other dt-zoho error fails the channel for this run, so its queue comes back whole.

## Sentences

Every failure sentence is at most 165 characters (SENTENCE_LIMIT). An error from dt-zoho or cc-secrets is cut to what
is left after the address, the machine and the step (`with_error`); the whole error is on the detail line.

## Known senders

Like Outlook, a Sent-folder cache read once a day: the To and Cc of the most recent 500 messages in `/Sent`. dt-zoho
does not show Bcc.

## Tests

`docs/qa/mail-desk/zoho/tests`, on fakes of dt-zoho and cc-secrets at the process boundary (no process, no network,
no port), answering in dt-zoho's own shapes. They run on Linux, as phase 2's rig does:

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/zoho/tests -q

dt-zoho's own `move --from-folder` is tested in `tools/dt-zoho/tests/test_dt_zoho_write.py`.
