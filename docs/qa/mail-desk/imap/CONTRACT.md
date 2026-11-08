# The Mail Desk IMAP reader: contract

The `imap` provider reads any mailbox that speaks IMAP (Zoho, Purelymail, Fastmail, Migadu, any IMAP host) with
`dt-email`. It is the class `ImapReader` in `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py`. Everything in
phase 2's contract (`docs/qa/mail-desk/phase-2/CONTRACT.md`) and phase 3's (one mailbox failing never stops the
others) holds for it unchanged; this file says only what is particular to IMAP.

## What a machine needs

- `dt-email` and `cc-secrets` on the PATH of the check.
- A dt-email account for the mailbox (`dt-email account add <name> --email <address> --imap-host <host> --smtp-host
  <host> --secret-entry <entry> --password-env <VARIABLE>`), saved under the Director's folder or any of its instance
  folders. A Director session sets `CC_DIRECTOR_ROOT` to its instance folder, so an account added from a session is
  saved there; the check runs with no `CC_` variable, so it looks in the Director's folder and every
  `instances/*` folder that holds `config/dt-email/config.json`, and runs every dt-email command with
  `CC_DIRECTOR_ROOT` set to the one that holds the account. The account in two of them fails the channel.
- The password in cc-secrets on that machine, under the entry the account names.

## The password

It never passes through the check. Every dt-email command but `account list` runs as
`cc-secrets run <entry> --via env --env-name <the account's password variable> --timeout 60 --json -- dt-email ...`,
so cc-secrets puts the password in that variable for that one command, and removes it from what the command prints.
Nothing about the password is in the memory, the output, standard error or on the Gateway.

## The guard

`check_dt_email()` is the only way to dt-email. It lets out exactly:

- `account list --json`;
- `list` with only `--folder`, `--count`, `--after-uid`, `--since`, `--oldest`, `--preview`, then `--account <name>
  --json`;
- `folders --account <name> --json`;
- in live mode only: `archive <one UID>` (from the Inbox) and `move <one UID> --to <the junk folder dt-email folders
  names>`.

dt-email can send, reply, forward, draft, mark and delete, so this guard is what keeps the Mail Desk from doing any of
them. A process is started in exactly one place, after the guard.

## The cursor and ids

- A message's id is `<the Inbox's UIDVALIDITY>.<UID>`.
- The cursor is `{"provider": "imap", "uidValidity": n, "lastUid": n, "newest": "<UTC time>"}`: the highest UID
  reported, under the UIDVALIDITY it was read with, and the latest arrival time ever reported. In a mailbox whose mail
  was moved or imported a higher UID can carry an earlier arrival time (seen live on a migrated Zoho mailbox), so
  `newest` keeps the latest of the cursor's and the new ones, and never goes back.
- First run: the most recent 200 Inbox messages, or those of the last 30 days, whichever is fewer. The lowest UID among
  them is found (`list --since --count 200`), then they are read from it, oldest first.
- Later runs: `list --after-uid <lastUid> --oldest --count 10 --preview`. `more` is true when the Inbox holds more above
  the cursor than were read.
- A renumbered Inbox (another UIDVALIDITY): the UIDs mean nothing any more, so new mail is found again by arrival time
  from the cursor's `newest`. The message reported last may be reported once more.
- New mail is ordered by UID, not arrival time: each message carries `orderedAt`, the latest arrival time up to it, so
  mail moved into the Inbox late never reports before a lower UID that is not reported yet, and the cursor never jumps
  over a UID. The context's `receivedUtc` is always the real arrival time.
- `--preview` reads each reported message with BODY.PEEK, so nothing is marked read.

## Filing

- `archive`: `dt-email archive <UID>`, to the folder dt-email marks as archive.
- `junk`: `dt-email move <UID> --to <the junk folder>`.
- A UID the Inbox no longer holds ("no message with UID") is `already-gone`.
- A UID under an older UIDVALIDITY is `already-gone` without any command: moving it would file another message.

## Known senders

Like Outlook, a Sent-folder cache read once a day: the To and Cc of the most recent 500 messages in the folder
dt-email marks as sent. dt-email does not show Bcc.

## Tests

`docs/qa/mail-desk/imap/tests`, on fakes of dt-email and cc-secrets at the process boundary (no process, no network,
no port). They run on Linux, as phase 2's rig does:

    uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/imap/tests -q
