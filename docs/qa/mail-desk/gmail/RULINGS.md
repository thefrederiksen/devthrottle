# Mail Desk Gmail support - the Tech Lead's rulings (21 October 2026)

Binding on every Developer of this phase. Where a ruling says "G0 decides", the G0 proof's measurement wins and the
Tech Lead forwards it. Read from origin/main, never the shared checkout. Phase branch: `mail-desk-gmail`.

## R1. Where the check finds the Gmail sign-in (the equivalent of handover ruling 4)

Facts read from origin/main `tools/cc-gmail/src/auth.py` and `tools/cc_storage/storage.py`:
- cc-gmail keeps ONE store per operating-system user: `CcStorage.user_tool_config("gmail")` =
  `_user_base()/config/gmail`, where `_user_base()` is `%LOCALAPPDATA%\cc-director` when LOCALAPPDATA is set, else
  `~/Library/Application Support/cc-director` on macOS, else `~/.cc-director`. CC_DIRECTOR_ROOT does NOT move it
  (issue 3011).
- Each account is `accounts/<nickname>/` holding `config.json` (`{"email", "auth_method"}`, no secrets),
  `credentials.json` (the OAuth client, used only by cc-gmail's interactive sign-in), `token.json` (google-auth's
  `Credentials.to_json()`: token, refresh_token, token_uri, client_id, client_secret, scopes, universe_domain,
  account, expiry), and possibly `token-refresh-error.json`.
- On devlinux the store is `~/.cc-director/config/gmail`; the Erik factory's `LOCALAPPDATA` points at
  `~/erik-factory/localappdata`, whose `cc-director` is a symbolic link to `~/.cc-director` - one store, not two.

The rule:
1. The ONE documented place: Linux `<home>/.cc-director/config/gmail`, macOS
   `<home>/Library/Application Support/cc-director/config/gmail`, Windows `%LOCALAPPDATA%\cc-director\config\gmail`.
   On Linux and macOS `<home>` comes from the operating system's account record (`pwd`), never `HOME`, and
   `LOCALAPPDATA` is ignored (it is not the operating system's variable there). No fallback anywhere else, and no
   look into `instances/*`.
2. The account is the directory under `accounts/` whose `config.json` `email` equals the channel address without
   regard to case. None: a failed channel naming the fix. More than one: a failed channel naming every directory;
   never pick.
3. An account whose `auth_method` is not `oauth` (an app password reads IMAP, not the Gmail API): failed channel
   naming the fix `cc-gmail -a <nickname> auth --method oauth`.
4. No `token.json`, or one without a refresh token, or a refresh Google refuses: failed channel naming
   `cc-gmail -a <nickname> auth` (`--force` when a token exists and was refused). The nickname is the directory name
   (cc-gmail's `-a`); check in cc-gmail's `resolve_account` that `-a <directory name>` is what it accepts and say so.
5. A token without the scope the mode needs (observe: `gmail.readonly` or `gmail.modify`; live: `gmail.modify`):
   failed channel naming `cc-gmail -a <nickname> auth --force`.

## R2. The client id and secret, the refresh, and the write-back

- cc-gmail's own refresh (`load_credentials`) is `Credentials.from_authorized_user_file(token.json)` then
  `creds.refresh()`, which takes client_id and client_secret FROM token.json. `credentials.json` is only read by the
  interactive sign-in. So the check reads token.json only; a token.json missing client_id, client_secret,
  refresh_token or token_uri is a failed channel naming `auth --force`. No fallback to credentials.json.
  G0 confirms this by reading google-auth's code and proving it; if G0 finds otherwise, it says so and the Tech Lead
  rules again.
- The refresh is the standard OAuth refresh: POST to the token's `token_uri`, which must be exactly
  `https://oauth2.googleapis.com/token` (anything else is a failed channel, never followed), form body
  `grant_type=refresh_token`, client_id, client_secret, refresh_token. Plain `urllib`, no Google library (the
  check's only package stays msal). It goes through its own narrow function, guarded like the Gmail calls.
- Refresh only when the access token is missing or expires within 5 minutes (expiry parsed as google-auth writes it).
- Write-back exactly as cc-gmail would leave it: the same keys, `token` and `expiry` replaced (and `refresh_token`
  if Google returned a new one), every other key unchanged, written to a temporary file in the same folder then
  `os.replace`, mode 0600 on Linux and macOS. The test: google-auth's own `Credentials.from_authorized_user_file`
  reads the written file (use it in the tests only). The check never writes `token-refresh-error.json` and never
  deletes anything in the store (documented as not done).

## R3. The request guard (the equivalent of handover ruling 3)

The token CAN send (its scopes include gmail.send and gmail.compose - seen on devlinux). The guard is the whole
promise. Every Gmail call goes through one function. It allows ONLY:
- `GET https://gmail.googleapis.com/gmail/v1/users/me/messages` (list, with query parameters) and
  `GET .../users/me/messages/{id}` (with `format` metadata or minimal and `metadataHeaders`), and any other GET
  G0 proves necessary - named exactly, never a prefix.
- In LIVE mode only: `POST .../users/me/messages/{id}/modify` with a JSON body of EXACTLY
  `{"removeLabelIds": ["INBOX"]}` (archive) or `{"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}` (junk).
- Refused before the network: send, drafts, trash, untrash, delete, batchDelete, batchModify, import, insert,
  any other label, threads, settings, labels.create, history writes, another user than `me`, any other host or
  version, any id that is not letters and digits, any body on a GET, any query on the POST, a fragment, a port,
  user info, spaces or control characters (checked before parsing). Observe mode never calls modify.
- Tests send every refused shape and assert the fake Gmail API's log is empty for each.

## R4. The cursor, the keys, the batches

- Same contract as Outlook (phase 2 CONTRACT.md as amended by phase 3 section 6.1): at most 10 messages and 7,168
  bytes across all channels together, oldest first; the same short keys (16 hexadecimal digits of SHA-256 of
  `<address>\n<message id>`); the same failure rules (one channel failing never stops the others); the same output.
- The cursor is `internalDate` (milliseconds) of the newest message reported, plus the ids reported at exactly that
  time - the phase 0 pattern. Not historyId: it expires after about a week, and a mailbox not read for a week would
  then need a different first-run path. Query: `labelIds=INBOX` and `q=after:<seconds>` one second before the cursor,
  then filtered in code. First run: the last 30 days, at most 200 messages (D18).
- `receivedUtc` is internalDate written as `YYYY-MM-DDTHH:MM:SSZ` (UTC). Say in the contract that the cursor keeps
  milliseconds and the output does not.
- The cursor stored must be distinguishable from Outlook's, so a channel's provider changing can never read the
  other's cursor as its own (for example `{"provider": "gmail", "newestMs": ..., "idsAtNewest": [...]}`), and a
  cursor this check did not write fails that channel as today.
- Filing in Gmail keeps the message id (unlike Graph's move). A retried archive is a harmless no-op; a 404 is
  `already-gone`. Say so in the contract.

## R5. The message fields

- sender and senderName: the `From` header (parsed with `email.utils`), address in lower case.
- subject: the `Subject` header, RFC 2047 encoded words decoded (decoding is not rewording), then cleaned as 4.3.
- preview: Gmail's `snippet`, HTML entities unescaped, cleaned as 4.3, then cut to 200 characters.
- hasAttachments: G0 decides how from the real mailbox (for example `format=metadata` parts with a filename, or a
  second `format=minimal` call); the cost per message is measured and recorded.
- knownSender: "we have written to this sender", from the SENT label. G0 measures both ways and the Tech Lead picks:
  (a) a Sent cache like Outlook's (most recent 500 sent messages' To, Cc and Bcc, hashed, refreshed daily) and
  (b) one search per distinct new sender, `labelIds=SENT`, `q=to:<a> OR cc:<a> OR bcc:<a>`, `maxResults=1`.

## R6. The public repository (handover ruling 7)

No real address (not the Gmail mailbox's address, not any sender's), no token, no message id of real mail, no
subject, no snippet, no body text in anything committed. Counts and timings only; ids may be shown as hashes.
Example domains only in test data (`example.com`, `example.org`, `example.net`).

## R7. Working rules

- Your own worktree, your own branch; never the shared checkout; never `git pull` it.
- Commit with `git -c user.name="thefrederiksen" -c user.email="soren@duksrevo.com"`, check it landed, push, open a
  pull request INTO `mail-desk-gmail` (not main). No assistant name, no Co-Authored-By, no "Generated with", anywhere.
  Plain English, no abbreviations.
- Never send an email, never change the real mailbox (no modify on the real mailbox, ever, even in G0), never touch
  the hosted Gateway, Azure or Microsoft 365, never deploy.
- devlinux: cc-* tools at ~/.local/bin, node at ~/.local/share/node/bin, .NET 10 at ~/.dotnet (not /usr/bin/dotnet),
  uv at ~/.local/bin. Gateway tests: DOTNET_USE_POLLING_FILE_WATCHER=1, LOCALAPPDATA to a scratch folder,
  -p:EnableWindowsTargeting=true. vitest: --minWorkers=1 --maxWorkers=2.
- Doorbells fail often: whenever you are idle, run `cc-devthrottle message inbox` yourself.
