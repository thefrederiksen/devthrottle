# dt-zoho

Zoho Mail, Calendar and admin through Zoho's own API. It holds what only Zoho's API can do, beside the
standard tools: `dt-email` (IMAP and SMTP) and `dt-calendar` (CalDAV) work with Zoho too.

This first version reads mail: accounts, folders, labels, message lists, search, one message and its
attachments. Writing mail, the calendar and the admin calls come in the next versions, and only what was
proven to work against a real Zoho organisation (8 November 2026) is built.

## The key comes from cc-secrets, never a file

dt-zoho signs in with a Zoho Self Client key: a client id, a client secret and a refresh token, stored in
cc-secrets. Every command that reaches Zoho runs under `cc-secrets run`:

```
dt-zoho settings set --data-centre ca
cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token -- dt-zoho signin check
cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token -- dt-zoho list
```

A missing variable fails with that exact command. The settings file
(`<cc-director config>/dt-zoho/config.json`) holds only the data centre.

**Data centre.** Zoho keeps each organisation in one data centre and a key made in one is refused by the
others: `ca` (Canada, zohocloud.ca), `com`, `eu`, `in`, `com.au`, `jp`. Only `ca` has been run against a
real organisation.

**The access token is kept for its hour, sealed.** Zoho gives out only about ten access tokens per ten
minutes, so dt-zoho does not ask for one per command: it keeps the token in
`<cc-director config>/dt-zoho/token.sealed` and reuses it until five minutes before it expires. The file is
encrypted with AES-256-GCM under a key derived (HKDF-SHA256) from the client secret and the refresh token,
which cc-secrets supplies on every run and never writes to disk; the client id and data centre are bound in,
so a file from another key or data centre does not open. If Zoho revokes the token early, dt-zoho asks for
a new one once and repeats the call. `dt-zoho signin forget` deletes the file.

## Commands (reading)

| Command | What it does |
|---|---|
| `settings set --data-centre C` / `settings show` | Choose Zoho's data centre, show the servers |
| `signin check` / `signin forget` | Sign in and show the account and whether the token was reused; delete the kept token |
| `accounts` | The mail accounts the key reaches, with every address each receives |
| `folders` | Folders, nested ones by path, and whether each is visible over IMAP |
| `labels` | Labels (Zoho's tags) |
| `list [--folder F] [--unread\|--read] [--flagged] [--label L] [--thread T] [--include-archive]` | Messages, newest first; with --label or --thread and no --folder, every folder |
| `search QUERY [--unread\|--read] [--flagged]` | Zoho's search words: `sender:` `subject:` `to:` `cc:` `entire:` `has:attachment` `in:Sent`, joined by `::` (and) or `::or:` |
| `read ID [--folder F] [--html] [--headers]` / `read ID --raw [--save FILE]` | One message as text; the whole original message |
| `attachments ID [--folder F]` / `download ID [--folder F] [--attachment A] --out-dir D` | List or download attachments; never overwrites a file |

`--folder` takes a name, a path (`/Inbox/Sub`) or an id; a name two folders share must be a path. The
default is Inbox. `--count` (1 to 200) and `--start` page through; a full page says how to get the next.
Every command takes `--json`; the mail commands take `--account <address>`.

## What Zoho does that you should know

- **Search ignores `is:unread`, `is:read` and `is:flagged`** and returns the same messages without them.
  dt-zoho refuses those words and filters with `--unread`, `--read` and `--flagged` instead, over the
  messages fetched; the output says how many the filter left out.
- **Text attachments come back with CRLF line endings** even when sent with LF. Binary files come back
  byte for byte. `download` says so when a text file has CRLF endings.
- Reading a message through the API does not mark it read.
