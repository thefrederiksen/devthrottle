# dt-email

Email over standard IMAP and SMTP, for any provider that offers them (Zoho, Purelymail, Fastmail,
Migadu, any IMAP host). It holds only what the two standard protocols can do; anything that exists only
in one provider's own API belongs in that provider's tool (dt-zoho, cc-gmail, cc-outlook).

Microsoft 365 does not accept a password over IMAP, so an Outlook account uses cc-outlook, never this.

## The password comes from cc-secrets, never a file

An account's settings (hosts, ports, sign-in name, and WHICH cc-secrets entry and variable hold the
password) are kept in `<cc-director config>/dt-email/config.json`. The password is never stored there.
Every command that touches a mailbox reads it from the named variable, which cc-secrets supplies:

```
dt-email account add soren --email soren@devthrottle.com \
    --imap-host imap.zohocloud.ca --smtp-host smtp.zohocloud.ca \
    --secret-entry zoho-mail-admin --password-env ZOHO_ADMIN_PASSWORD --default
cc-secrets run zoho-mail-admin -- dt-email account check
cc-secrets run zoho-mail-admin -- dt-email list
```

A missing variable fails with the exact `cc-secrets run ...` command to use.

## Test mode: allowed recipients

```
dt-email allowed-recipients set soren@devthrottle.com email@devthrottle.com
dt-email allowed-recipients show
dt-email allowed-recipients clear
```

While a list is set, any To, CC or BCC not on it - in send, reply, reply-all, forward and draft send -
is refused with an error before any connection is opened. Nothing is sent.

## Commands

| Command | What it does |
|---|---|
| `folders` | Folders with their special use (drafts, sent, trash, junk, archive) and counts |
| `folder create\|rename\|delete` | Manage folders. A server that keeps a deleted folder in Trash (Zoho) is reported, with the name it chose |
| `list [--folder F] [--count N] [--unread] [--after-uid N] [--since DATE] [--oldest] [--preview]` | Newest messages first, or the oldest with `--oldest`. `--json` carries the folder's `uidvalidity` and each message's `received` time in UTC; `--preview` adds the first 200 characters of text and the attachment count, read from the first 64 KB without marking it read |
| `search [TEXT] [--from --to --subject --body --since --before --unread --flagged]` | Search on the server |
| `read UID [--folder F] [--html] [--full]` | One message; does not mark it read |
| `attachments UID` / `download UID --out DIR [--index N] [--overwrite]` | List and save attachments |
| `send --to --cc --bcc --subject --body\|--body-file [--html-file] [--attach F] [--from ALIAS]` | Send |
| `reply UID [--all]` | Reply in the thread (In-Reply-To and References) |
| `forward UID --to ...` | Forward with the original attached whole, keeping References |
| `draft save [--replace UID]\|list\|send UID\|delete UID` | Drafts in the Drafts folder |
| `mark UID... --as read\|unread\|flagged\|unflagged` | Flags |
| `move UID... --to F` / `archive UID...` `[--expect-uidvalidity N]` | Move; Archive is found by its flag, else by its name. With `--expect-uidvalidity`, nothing moves unless the folder's uidvalidity is still N, checked on the connection that moves |
| `delete UID... [--permanent]` | To Trash, or removed for good (only those UIDs are expunged) |

Every command takes `--account NAME` and `--json` (every field, same shape whether filtered or not).
List output follows the AXI shape (`docs/axi-standard.md`): a `count:` line, a list block, `help[]`.

## What a server must offer

IMAP4rev1 with MOVE (to move, archive or delete to Trash) and UIDPLUS (to delete permanently). Without
them those commands fail with a clear error; dt-email never does them another way.

## Things learned on a real server (Zoho)

- A server's search index can lag a write by about a minute; commands report the UID the server
  returned instead of searching for what they just wrote.
- Zoho saves sent mail to Sent itself. An account is `--no-save-sent` by default; pass `--save-sent`
  only for a server that does not, or every message is stored twice.
- Zoho refuses a From that is not the account's own address or an alias at the DATA step with
  `550 5.7.1 Sender is not allowed to relay emails`, then closes the connection.
- Zoho offers no THREAD extension; dt-email builds the thread headers itself.

## Tests

`python -m pytest tools/dt-email/tests` - an in-memory IMAP server and a real `smtplib.SMTP` with only
the wire replaced (`tests/fakes.py`). No test touches the network.
