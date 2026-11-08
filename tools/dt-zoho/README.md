# dt-zoho

Zoho Mail, Calendar and admin through Zoho's own API. It holds what only Zoho's API can do, beside the
standard tools: `dt-email` (IMAP and SMTP) and `dt-calendar` (CalDAV) work with Zoho too.

This version reads and writes mail (accounts, folders, labels, lists, search, reading, attachments,
sending, replying, drafts, flags, moves, archive, delete, spam, threads, scheduled send) and works the
calendar (calendars and sharing, events, invitations, reminders, repeats, Zoho Meeting). The admin calls
come next, and only what was proven to work against a real Zoho organisation (8 November 2026) is built.

## The key comes from cc-secrets, never a file

dt-zoho signs in with a Zoho Self Client key: a client id, a client secret and a refresh token, stored in
cc-secrets. Every command that reaches Zoho runs under `cc-secrets run`:

```
dt-zoho settings set --data-centre ca
cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token -- dt-zoho signin check
cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token -- dt-zoho list
```

A missing variable fails with that exact command. The settings file
(`<cc-director config>/dt-zoho/config.json`) holds the data centre and the test-mode list, never a secret.

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

## Test mode: allowed recipients

```
dt-zoho allowed-recipients set soren@devthrottle.com email@devthrottle.com
dt-zoho allowed-recipients show
dt-zoho allowed-recipients clear
```

While a list is set, any To, CC or BCC not on it - in `send` and `reply` - is refused before anything
reaches Zoho: no attachment is uploaded and nothing is sent. A reply also checks the original's From and
Reply-To, and `reply --all` every address on the original, because Zoho copies the original CC itself, the
account's own aliases included. The list is kept in the settings file. A
draft is never sent, so `draft save` is not checked.

## Commands (writing)

| Command | What it does |
|---|---|
| `send --to A [--cc] [--bcc] --subject S --body T\|--body-file F\|--html-file F [--attach F] [--from ALIAS] [--at "YYYY-MM-DD HH:MM" [--time-zone Z]]` | Send now, or at a set time |
| `reply ID [--all] --body T\|--body-file F\|--html-file F [--attach F] [--from ALIAS]` | Reply, or reply to all, in the same thread |
| `draft save ...` / `draft list` / `draft delete ID...` | Save a draft (the send options), list drafts, delete them for good |
| `mark ID... --as read\|unread\|important\|info\|followup\|unflagged` | Read state and flag |
| `move ID... --to F` / `archive ID...` / `unarchive ID... [--to F]` | Move; archive; move back out of Archive (default Inbox) |
| `delete ID... [--folder F] [--permanent]` | To Trash, or with `--permanent` for good; refused unless Zoho places each message in that folder |
| `spam ID...` / `not-spam ID...` | Into Spam; back to Inbox |
| `label create NAME [--color #RRGGBB]` / `label rename L [--name N] [--color C]` / `label delete L` | Labels |
| `label apply ID... --label L` / `label remove ID... --label L` / `label clear ID...` | Labels on messages |
| `folder create NAME [--parent F]` / `rename F --name N` / `move F --to-top` / `mark-read F` / `empty F` / `imap F --show\|--hide` / `delete F` | Folders. `empty` moves the messages to Trash; `delete` moves the folder into Trash, and deleting it again there (`/Trash/<name>`) removes it for good |
| `thread mark T... --as ...` / `thread label T... --apply L\|--remove L` | Every message in a thread at once |

Ids may be space or comma separated. Every command takes `--json` and `--account <address>`.

## What Zoho's API cannot do, and what to use instead

- **Forward**: Zoho answers 500 and sends nothing. `dt-zoho forward` says so; use `dt-email forward`.
- **Filters**: there is no filter call. `dt-zoho filters` says so; filters are made in Zoho Mail's web settings.
- **Send or change a saved draft**: there is no call. Send with `dt-zoho send`, then `dt-zoho draft delete`;
  to change one, delete it and save it again.
- **Unarchive**: Zoho's unarchive call answers OK and leaves the message in Archive, so `unarchive` moves it.
- **A scheduled message cannot be listed or cancelled** once sent with `--at`.
- **One body**: Zoho sends plain text or HTML, not both, so `--body`, `--body-file` and `--html-file` are one
  or the other.
- **Spam acts on the whole conversation.** `spam` on one message moves every message in its thread into
  Spam, your own sent replies too, and `not-spam` then puts them all in Inbox. Move the sent ones back with
  `dt-zoho move <id> --to Sent` (seen on 8 November 2026).
- **Reply to all copies the original CC itself**, own aliases included, whatever CC dt-zoho sends.
- **On a thread, Zoho marks unread and labels only the received messages**; your sent replies stay read and
  unlabelled. Read and flag reach every message.
- **Moving a folder under another folder** was not proven, so only `folder move --to-top` is offered.
- `--fields` does not apply to `--json`, which always carries every field; giving both is refused.

## Commands (calendar)

| Command | What it does |
|---|---|
| `calendars` | Your calendars and those shared with you |
| `calendar create NAME [--color] [--time-zone] [--description]` / `rename CAL [--name] [--color]` / `delete CAL` | Calendars; your default calendar is never deleted |
| `calendar share CAL --with A` / `unshare CAL --with A` / `shares CAL` | Share for viewing (the level proven), stop, list |
| `event list [--calendar C] [--from D] [--to D] [--instances]` | Events, earliest first; `--instances` lists each occurrence with its id |
| `event show UID` | Times, invitees and their answers, reminders, repeat rule, changed and deleted occurrences, meeting links |
| `event create --title T --start "YYYY-MM-DD HH:MM" [--end\|--duration] [--time-zone] [--location] [--description] [--invite A] [--reminder email:30] [--repeat RULE] [--meeting]` | Create; `--meeting` adds a Zoho Meeting and prints the guest join link and the host start link |
| `event change UID [--occurrence ID] [--title] [--start] [--end\|--duration] [--location] [--description] [--invite] [--uninvite] [--reminder\|--no-reminders]` | Change; Zoho replaces the whole event, so dt-zoho sends every field again. `--start` is read in the event's own time zone; all-day events are refused |
| `event delete UID [--occurrence ID]` | Delete an event, a series, or one occurrence |
| `event respond` | Says Zoho's API cannot answer an invitation; use `dt-calendar respond` |

Times are in the calendar's time zone unless `--time-zone`. Test mode checks every invitee and every share
before anything reaches Zoho - on a change everyone still invited and anyone removed, on a delete every
invitee - because Zoho mails each of them (an invitation, or a "Cancelled" notice).

## What Zoho's calendar does that you should know

- **Invitations come from noreply@zohocalendar.ca** (Reply-To the organizer), and **Zoho keeps only invitees
  inside its organisation**, dropping others - and the organizer's own aliases - without an error.
  `event create` and `event change` read the event back and exit 1 naming whoever was dropped.
- **A Zoho Meeting** is `--meeting`. The guest join link is in the invitation; the host start link is only
  in dt-zoho's output. A change keeps the same meeting and links.
- **Repeating events**: Zoho answers with the rule alone (INTERVAL=1 added). Changing a whole series was not
  proven - and a change sent without the rule turned a test series into a single event - so dt-zoho changes
  one occurrence (`--occurrence`) and refuses a series change. Once an occurrence changed, the event's own
  answer lists each changed occurrence (with its own values and etag), each deleted one, then the series. A
  series delete takes the series' etag; a changed occurrence answers only to its own (the series' drew 409
  CONFLICT_EVENT), and a second change to it starts from its current values. A deleted occurrence cannot be
  changed. If the answer has no series entry, or no etag, dt-zoho refuses.
- **A change replaces the whole event.** Leaving the location, the description or the invitees out of the
  request removes them, so dt-zoho always sends them again. An invitee's answer (accepted) survives a change.
- **All-day events** (made in Zoho Calendar) are shown and can be deleted; dt-zoho does not create them, and
  changing one was not proven, so it is refused.
- **Deleting a calendar** mailed no "Cancelled" notice to an invitee of an event in it (run 8 November 2026,
  checked five minutes after), so it is not checked against test mode.
- **Free/busy** needs a scope the key does not have, and is not built.
- An empty range comes back as a "No events found." message; dt-zoho shows `count: 0`.
