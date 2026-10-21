# Mail Desk Gmail support, proof G0: the Gmail sign-in, the refresh, and the mailbox read-only

Date: 21 October 2026. Machine: devlinux. Built from the phase branch `mail-desk-gmail` at `bff2295cf`
(origin/main plus the rulings); `tools/cc-gmail` and `tools/cc_storage` there are identical to origin/main, and the
installed cc-gmail (`~/.local/share/cc-director/pyenv`) has the same `auth.py`. Binding rulings:
`docs/qa/mail-desk/gmail/RULINGS.md`. No product code changed. The review (`../reviews/g0-review.md`) and the answers
to it (`../reviews/g0-answers.md`) sit beside this folder.

## The rule, in plain words

- **One documented place.** cc-gmail's store is `<home>/.cc-director/config/gmail` on Linux,
  `<home>/Library/Application Support/cc-director/config/gmail` on macOS, and `%LOCALAPPDATA%\cc-director\config\gmail`
  on Windows. On Linux and macOS `<home>` comes from the operating system's account record (`pwd`), never from
  `HOME`, and `LOCALAPPDATA` is ignored. There is no other place, no fallback, and no look into `instances/*`.
- **The account is found by its address.** It is the directory under `accounts/` whose `config.json` `email` equals
  the channel address, without regard to case. The nickname is that directory's name, which is what cc-gmail's `-a`
  takes: `resolve_account` returns an explicit name unchanged and `get_account_dir` joins it to `accounts/`
  (`tools/cc-gmail/src/auth.py`, `resolve_account` and `get_account_dir`).
- **None found: fail, naming the fix. More than one: fail, naming every directory. Never pick one.**
- **token.json only.** Not oauth, no token.json, a token.json that cannot be read, no refresh token, a missing `client_id`, `client_secret` or
  `token_uri`, a `token_uri` other than `https://oauth2.googleapis.com/token`, or no scope for the mode: fail, naming
  the cc-gmail command that fixes it. `credentials.json` is never read.
- **Refresh** when the access token is missing or expires within five minutes; **write back** to a temporary file in
  the same folder, then `os.replace`, mode 0600, with `token` and `expiry` replaced (and `refresh_token` only if Google
  sent a new one) and every other key unchanged, in the same order.

`gmail_probe.py` implements exactly this with the standard library only (plain `urllib`). Every Gmail call goes
through one guarded GET; the probe has no code path that can POST to Gmail.

## The verdict

| Point | Verdict |
|---|---|
| a. Where it looked and what it found | **PROVEN** |
| b. Forced refresh, write-back 0600, other keys unchanged, cc-gmail still Ready; google-auth refreshes from token.json | **PROVEN** |
| c. The token's scopes | **The token CAN send mail** (`gmail.send`, `gmail.compose`, `gmail.modify`). The guard is the whole promise. |
| d. Inbox, last 30 days | **Measured**: 200 (the cap), two pages of 100, about 0.3 seconds a page |
| e. Metadata, attachments, encoding, entities | **Measured**. `format=metadata` cannot tell attachments. Subjects arrive decoded. Half the snippets carry HTML entities. |
| f. knownSender both ways | **Measured**. Recommendation: one search per sender, only for a plain address, and a hit confirmed against the found message's recipients |
| g. Message ids | **16 lowercase hexadecimal characters, stable** |
| h. Rate | **No HTTP 429 in 604 calls**; a realistic run of 10 messages: 5.8 seconds, 24 calls |

All numbers below come from `transcript.txt`: the final run, 11:42 UTC, made from commit `bca238290` with
`gmail_probe.py` byte-for-byte as committed there. The transcript's first lines say so: they carry the probe's SHA-256
(`224e1826...`) and `run_proof.sh`'s check that it equals the committed file (`git hash-object` against
`HEAD:docs/qa/mail-desk/gmail/g0/gmail_probe.py`). The probe has not changed since that commit. Three earlier runs that
morning, from uncommitted working copies, gave the same picture; one stopped at the probe's own guard (see "Found on
the way").

## a. Where it looked and what it found

Three runs, every one with `CC_` variables absent (`"ccVariableNames": []`):

- Run 1 and run 2: `env -i PATH=/usr/bin:/bin:~/.local/bin` - `"HOME": null`, `"LOCALAPPDATA": null`.
- Run 3: the same, plus `HOME=<scratch>/home` and `LOCALAPPDATA=<scratch>/localappdata`, each holding a decoy store
  with an account `decoy` for the same address (an app-password account, so using it would have failed naming
  `decoy` before any network call). Run 3 found the real account, not either decoy.

In every run: `"store": "~/.cc-director/config/gmail"`, `"accountDirectoriesExamined": 2`, `"accountNickname":
"personal"`, `"tokenFile": "~/.cc-director/config/gmail/accounts/personal/token.json"`. The second account directory
(`consulting`, a different address, no token) did not match and was not touched.

## b. The refresh, the write-back, and cc-gmail afterwards

- **Forced refresh worked**, twice (run 1 and run 3): `"renewed": true`, `"tokenChanged": true`,
  `"refreshTokenChanged": false` (Google sent no new refresh token), `"expiryAfterMinutesFromNow": 60`, and a
  read-only GET with the new token worked (`"readOnlyGetWorked": true`).
- **Written back with mode 0600**: `"modeBefore": "0o600"`, `"modeAfter": "0o600"`, and the file's modification time
  moved (1792582350 before, 1792582922 after run 1, 1792583069 after run 3).
- **Every other key unchanged**: `"sameKeysSameOrder": true` (the nine keys `token, refresh_token, token_uri,
  client_id, client_secret, scopes, universe_domain, account, expiry`), and the value hash prefixes of `refresh_token`
  (`95c32992`), `token_uri`, `client_id`, `client_secret`, `scopes`, `universe_domain` and `account` are identical
  before and after every run; only `token` and `expiry` changed.
- **Run 2 did not refresh** (`"forced": false`, `"renewed": false`): the token was 60 minutes from expiry, so the
  five-minute rule left the file alone.
- **cc-gmail still reads the account as Ready**: `cc-gmail accounts status personal` after run 1 and after run 3
  shows `Authenticated  Yes`, `Gmail Scopes  OK`; `cc-gmail accounts list` shows `personal ... Ready`. `accounts
  status` loads the token with google-auth's `Credentials.from_authorized_user_file`, so google-auth itself parsed the
  file we wrote, including our `expiry`. The token file's modification time did not move when cc-gmail read it
  (cc-gmail rewrites only when it had to refresh), so cc-gmail found the token valid as written.
- **The unit tests** additionally read the written file with google-auth's `Credentials.from_authorized_user_file`
  and assert it is valid and not expired.

**google-auth refreshes from token.json, never from credentials.json.** google-auth 2.62.0, in cc-gmail's own
environment (`~/.local/share/cc-director/pyenv/lib/python3.12/site-packages/google/oauth2/credentials.py`):

```
488        keys_needed = set(("refresh_token", "client_id", "client_secret"))
...
512        return cls(
513            token=info.get("token"),
514            refresh_token=info.get("refresh_token"),
515            token_uri=_GOOGLE_OAUTH2_TOKEN_ENDPOINT,  # always overrides
516            scopes=scopes,
517            client_id=info.get("client_id"),
518            client_secret=info.get("client_secret"),
```

`info` is the parsed token.json (`from_authorized_user_file` at line 528 opens the file and calls
`from_authorized_user_info`). The refresh (`_perform_refresh_token`, from line 382) uses only those fields:

```
419        if (
420            self._refresh_token is None
421            or self._token_uri is None
422            or self._client_id is None
423            or self._client_secret is None
424        ):
...
437        ) = reauth.refresh_grant(
438            request,
439            self._token_uri,
440            self._refresh_token,
441            self._client_id,
442            self._client_secret,
```

and cc-gmail's `load_credentials` (`tools/cc-gmail/src/auth.py`) is `Credentials.from_authorized_user_file(str(token_path),
SCOPES)` then `creds.refresh(Request())` then `save_credentials`. Nothing in that path opens credentials.json. The
ruling R2 holds.

Three small differences from cc-gmail's own refresh, both deliberate and neither visible in the file:

- google-auth ignores token.json's `token_uri` and always posts to Google's endpoint (line 515, "always overrides").
  The probe instead requires the stored `token_uri` to be exactly that endpoint and fails otherwise, as R2 says.
- google-auth adds `scope=<the requested scopes>` to the refresh body (`reauth.py` lines 324-325, since cc-gmail
  passes `SCOPES`). The probe sends the four fields R2 names and no `scope`; Google then grants the token's existing
  scopes. The probe does not touch the `scopes` key, so the file's scopes stay as cc-gmail wrote them.
- cc-gmail's `save_credentials` writes the file in place (`write_text` then `chmod`); the probe's write is atomic.

## c. The scopes

```
https://www.googleapis.com/auth/gmail.readonly
https://www.googleapis.com/auth/gmail.send
https://www.googleapis.com/auth/gmail.compose
https://www.googleapis.com/auth/gmail.modify
https://www.googleapis.com/auth/calendar
https://www.googleapis.com/auth/contacts
```

**This token can send mail, write drafts, and change or trash any message.** Nothing about the token limits what the
check can do. The promise that the Mail Desk never sends mail and never changes anything but the Inbox label rests
entirely on the request guard (R3).

## d. The Inbox

`labelIds=INBOX`, `q=after:<30 days ago in seconds>`, `maxResults=100`: **200 messages** (the cap was reached: this
mailbox has more than 200 Inbox messages in 30 days), in **two pages of 100**. The two list calls took 274 and 303
milliseconds.

## e. Metadata for 20 recent Inbox messages

`GET .../messages/{id}?format=metadata&metadataHeaders=From&metadataHeaders=Subject`:

- **Top-level keys**, the same for all 20: `historyId, id, internalDate, labelIds, payload, sizeEstimate, snippet,
  threadId`.
- **payload keys**: only `headers` and `mimeType`. `headers` holds exactly the two asked for (`From`, `Subject`).
  One message's shape: `{"mimeType": "str(21)", "headers": {"listLength": 2, "first": {"name": "str(4)", "value":
  "str(40)"}}}`.
- **Response size**: 434 to 1,114 bytes, median 627.
- **Time**: median 242 milliseconds per GET (223 to 280), 4.8 seconds for the 20, one after another.

**hasAttachments cannot be read from `format=metadata`.** No message had `payload.parts` (0 of 20) and so no
filename (0 of 20). `format=minimal` is no better: it has no `payload` at all (keys `historyId, id, internalDate,
labelIds, sizeEstimate, snippet, threadId`), median 233 milliseconds per message.

Two candidates were compared over 36 messages (the 20 plus every Inbox message of the 30 days the search called
attached):

| | `payload.mimeType` is `multipart/mixed` | is not |
|---|---|---|
| `has:attachment` search says attached | 12 | 5 |
| search says not attached | 1 | 18 |

`mimeType` disagrees with Gmail's own attachment search on 6 of 36 messages (types seen: `multipart/alternative` 15,
`multipart/mixed` 13, `multipart/related` 5, `text/html` 3), so it is a guess, not an answer.

**Recommendation for hasAttachments:** one extra list call per run, `labelIds=INBOX&q=after:<same seconds> has:attachment`
(paged, `maxResults=500`), and `hasAttachments` is whether the message id is in that set. It costs one call per run,
not per message: **261 milliseconds** for the whole 30-day window (17 ids). It is Gmail's own definition of an
attachment, which may differ at the edges from Outlook's (an inline picture, for example); the contract should say
so. It needs no new GET shape: it is the list GET the guard already allows.

**Subjects arrive decoded:** 20 Subject headers seen, **0** still holding an RFC 2047 encoded word (`=?charset?B|Q?`).
Decoding stays in the check (R5) as a no-op guard for mail that arrives otherwise.

**Snippets carry HTML entities:** **10 of 20** snippets hold at least one (`&amp;`, `&#39;` and the like). Unescaping
them (R5) is necessary, not cosmetic.

## f. knownSender, both ways

| | Calls | Time |
|---|---|---|
| (a) Sent cache: list the 500 most recent SENT (one page of 500), then a metadata GET (To, Cc, Bcc) for each | 501 | **119 seconds** (median 235 milliseconds per GET); 116 distinct addresses |
| (b) one search per sender: `labelIds=SENT&q=to:<a> OR cc:<a> OR bcc:<a>&maxResults=1`, then for a hit one metadata GET (To, Cc, Bcc) of the found message to confirm it | 10 searches and 2 confirming GETs for 10 senders | **2.8 seconds** in all (searches median 226 milliseconds, confirming GETs median 248) |

Outcomes of (b) for the 10 senders: 0 not a plain address, 8 no hit, 2 hits confirmed, 0 hits not confirmed. Both ways
said the same for all 10 (`"sendersWhereTheyDiffer": 0`).

**Recommendation: (b), one search per distinct new sender, under two conditions (the Tech Lead's ruling on review
finding 1):**

1. **Search only for a plain address.** The `From` header is written by the sender, and `parseaddr` does not turn it
   into a plain address (the review shows values that come out as `gmail.com or x@...` or `*@...`). The address goes
   into `q` only when it matches a strict pattern: letters, digits and `. _ % + -` before the `@` (not starting with a
   hyphen, which Gmail reads as "not"), and dot-separated labels of letters, digits and hyphens after it. No spaces,
   quotes, parentheses, braces, brackets, asterisks or colons. Anything else is **not known**, with no search made.
2. **Confirm a hit.** A search hit counts only if the found Sent message's `To`, `Cc` or `Bcc` header, parsed with
   `email.utils.getaddresses`, holds exactly that address (case-blind). A hit that does not confirm is **not known**.

The probe does exactly this (`PLAIN_ADDRESS` and `known_by_search` in `gmail_probe.py`), and the tests prove that none
of nine hostile or malformed addresses reaches the network, that a look-alike recipient is not a confirmation, and
that a no-hit makes no second call. The cost is one search per new sender plus one GET per hit: about 2.8 seconds for
10 senders, against two minutes of sequential GETs whenever a Sent cache is rebuilt (Gmail's batch endpoint is outside
the guard, and parallel GETs were not measured). The search also covers the whole Sent history, where the cache sees
only the latest 500. The answer could be remembered per sender for a day (hashed); that is an optimisation, not a need.

## g. Message ids

All 200 ids are **16 characters, lowercase hexadecimal** (so letters and digits, as the guard requires). A second
list of the same query minutes later returned **the same ids in the same order** (`"sameIdsInSecondList": true`).
155 of the 200 have `id == threadId` (the first message of its thread).

Worth knowing for R4 and R6: a Gmail message id **looks exactly like a short key** (16 hexadecimal digits of SHA-256).
Anything that prints either must say which it is, and the public-repository check cannot tell them apart by shape.

## h. The rate

**No HTTP 429** in 604 calls made one after another (median 236 milliseconds, slowest 478); every answer was 200.
A realistic run of 10 messages - one list, 10 metadata GETs, one `has:attachment` list, and for each of the 10 distinct
senders one SENT search under the conditions of section f (plus a confirming GET per hit) - took **5.8 seconds for 24
calls**. A token refresh, when due, adds one POST.

## Found on the way

- The first measuring run stopped in the probe's own guard: it refused the attachment search because the query value
  `after:<n> has:attachment` holds a space. The guard now refuses spaces in the path and in parameter names only
  (values are URL-encoded) and control characters everywhere; the tests cover both. The real guard (R3) will need the
  same distinction, since both recommended searches have spaces in `q`.
- The review's findings 2 to 4 changed the probe after the first committed version: a refused refresh now names the
  account (`run cc-gmail -a personal auth --force`, not the placeholder `<nickname>`); a token.json that cannot be read
  (for example one caught half-written by cc-gmail, whose `save_credentials` writes in place) is a failed channel naming
  `cc-gmail -a <nickname> auth --force`, not a crash; knownSender way (b) gained the two conditions of section f; and
  the transcript carries the probe's SHA-256. The final run above was made after these changes, from the commit that
  holds them.
- The guard also now allows list parameters (`labelIds`, `q`, `maxResults`, `pageToken`) only on the list, and
  `format` (metadata or minimal) and `metadataHeaders` only on a message GET, which must name its format.

## The unit tests

`tests/test_gmail_probe.py`: scratch stores, fake transports, no network, every `CC_` variable removed.
65 tests, all passing:

```
uv run --no-project --with pytest --with google-auth python -m pytest docs/qa/mail-desk/gmail/g0/tests -v
65 passed
```

They cover: none found (other address, no store at all), two found (names both, picks neither, case-blind), an
unreadable config.json, an app-password account, no token.json, a token.json that is cut short, empty or not an object
(fails naming `cc-gmail -a personal auth --force`, and through `main` reports `failed` with no network call), no
refresh token, a missing `client_id`, `client_secret` or `token_uri` (with a credentials.json present that is never
read), another `token_uri`, the scope rule for each mode, the five-minute window, the exact refresh form posted to the
one address, mode 0600 and every other key unchanged in order, a new refresh token kept, google-auth reading the
written file as valid, no refresh and no write when the token is fresh, a forced refresh, a refused refresh (fails
naming `cc-gmail -a personal auth --force`, file unchanged, no `token-refresh-error.json`), every refused request shape
with an empty transport log, the whole probe with `HOME` and `LOCALAPPDATA` pointing at decoy stores on Linux, and
knownSender way (b): nine addresses that are not plain never searched, a confirmed hit, a look-alike hit not known, and
no second call on no hit.

Checked against broken copies of the probe: with the refusal naming `<nickname>` again, 1 test fails; with the plain
address pattern allowing anything, 9 fail; with every hit counted as confirmed, 1 fails.

## How it was run

```
bash docs/qa/mail-desk/gmail/g0/run_proof.sh personal
```

`run_proof.sh` reads the address from the account's config.json by nickname, runs the three probe runs and the two
cc-gmail reads, and writes `transcript.txt`, replacing anything holding "@" with `<address>`, the home folder with `~`
and its scratch folder with `<scratch>`. Before the first run the token file was copied, owner-only, to a scratch
folder; the copy was deleted once cc-gmail confirmed the account Ready.

## What is NOT proven

- **macOS and Windows were not run.** Their store paths are tested as path computations only.
- **A refresh Google refuses was not produced on the real account.** It is proven against the fake transport only.
- **Modify was never called**, on the real mailbox or anywhere: archive and junk, and Gmail's answer to a retried
  archive or an id already gone, are not measured here.
- **The token cannot be narrowed.** Nothing was tried to obtain a token without send; the guard is the only promise.
- **Parallel GETs** were not measured; every timing is one call after another from devlinux.
- **The has:attachment search's definition** was compared with `mimeType` on 36 messages only, and not with what a
  person would call an attachment.
- **Two writers at once** (cc-gmail and the check refreshing the same token.json in the same second) was not tried.
  The probe's write is atomic (`os.replace`), but **cc-gmail's is not**: its `save_credentials` empties the file and
  then writes it, so a reader in between can see a partial file. The probe fails that channel naming `auth --force`
  rather than crashing (tested), and the next run reads the whole file again. The final state should be the last
  writer's valid token, since the refresh token does not change; that is reasoning, not proof.
- **The search's exactness on look-alike addresses was not measured.** Whether Gmail's `to:` matches only the whole
  address, or also a longer address that contains it, was not tried against Gmail. The confirmation against the found
  message's recipients is what makes the answer exact; it is proven by the tests only, and on the real mailbox it ran
  on 2 hits, both confirmed.
- **Not run as a factory check under a Director.** The probe ran from a shell with `env -i`. That is enough for R1 and
  R2, but the Director's own environment (its PATH, and whether uv is on it, as phase 0 found it was not on devlinux)
  was not part of this proof.
- **One mailbox**: a different mailbox (fewer messages, other senders, other languages) may show other counts of
  encoded subjects and entities.
- **Nothing was changed in the mailbox and nothing was sent**: the only POST made was the token refresh, to
  `https://oauth2.googleapis.com/token`.
