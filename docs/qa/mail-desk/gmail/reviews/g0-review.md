# Review of the G0 proof (pull request 4333, branch `mail-desk-gmail-g0-proof`)

Reviewer: Mail Desk - Reviewer - Gmail G0 sign-in proof. Date: 21 October 2026. Machine: devlinux.
Reviewed commit: `82bc12b7d` (the tip of `mail-desk-gmail-g0-proof`), against `mail-desk-gmail` at `bff2295cf`.

## Verdict

**Approve, with four findings.** The evidence for R1 and R2 is sound and I found nothing in it that is stronger than
what was measured. None of the findings undoes the sign-in, refresh or write-back proof. Finding 1 should be settled
before the Tech Lead picks a knownSender design, because the recommended design brings a risk the proof did not
weigh. Findings 2 to 4 are small and are fixed by a sentence or a line each.

## Scope

Read:
- `docs/qa/mail-desk/gmail/RULINGS.md` (R1 to R7), and every file of the diff: `g0/README.md`, `g0/gmail_probe.py`,
  `g0/run_proof.sh`, `g0/tests/test_gmail_probe.py`, `g0/transcript.txt`.
- The phase 0 standard: `docs/qa/mail-desk/phase-0/a-sign-in/README.md` on origin/main.
- On origin/main: `tools/cc-gmail/src/auth.py` (`resolve_account`, `get_account_dir`, `describe_token`,
  `load_credentials`, `save_credentials`, `get_auth_status`, `check_token_scopes`) and the `accounts status` command in
  `tools/cc-gmail/src/cli.py`. `tools/cc-gmail` and `tools/cc_storage` are identical on origin/main and on
  `mail-desk-gmail` (checked with `git diff`).
- The phase 3 contract on origin/main (`docs/qa/mail-desk/phase-3/CONTRACT.md`, row 3b) for what knownSender decides.
- google-auth 2.62.0 in cc-gmail's own environment: every line the README quotes from `oauth2/credentials.py`
  (488, 512 to 518, 419 to 424, 437 to 442, 528) and `oauth2/reauth.py` (324 to 325) is quoted correctly.

Ran:
- The tests as the brief gives them: `49 passed in 0.47s`.
- Seven deliberate breakages of a scratch copy of the probe, each with the unchanged tests (results under the
  findings and in "What the tests catch").
- The probe's `main` against a scratch store whose token.json is cut short (finding 3), with a transport that stops
  the run if it is ever called.
- `email.utils.parseaddr` on hand-made `From` values (finding 1). No Gmail call of any kind.
- A search of the whole diff for addresses, domains, 16-character hexadecimal ids, token shapes and the owner's names.

Could not reach, by the brief's rule: the real mailbox. I did not run the probe or any Gmail search, so what Gmail's
search does with the addresses in finding 1 is not measured by me either.

## What holds

- **R1, one documented place, HOME and LOCALAPPDATA ignored on Linux: proven.** `gmail_store` takes the home folder
  from `pwd`, never `HOME`, and reads `LOCALAPPDATA` only on Windows. Run 3 set `HOME` and `LOCALAPPDATA` to scratch
  folders each holding a decoy account for the same address, and the probe still found `personal` under
  `~/.cc-director/config/gmail`. The unit test `test_main_ignores_home_and_localappdata_on_linux` does the same with
  decoys that would have worked, so a probe that used either would have reported a different nickname.
- **R1, fail on none and on several, never pick: proven by the tests.** Breaking the more-than-one rule (taking the
  first match) fails two tests. The nickname rule is right: `resolve_account` returns an explicit name unchanged and
  `get_account_dir` joins it to `accounts/`.
- **R2, token.json only: proven.** The probe never opens credentials.json; the tests put one beside a token missing
  each client field and the probe still fails naming `auth --force`. The README's reading of google-auth is accurate.
- **R2, atomic write-back, 0600, other keys unchanged in order: proven.** Breaking the key order fails a test.
  The transcript's hash prefixes show only `token` and `expiry` changed across two forced refreshes.
- **R2, cc-gmail still Ready: proven, and more strongly than it may look.** `cc-gmail accounts status` calls
  `get_auth_status`, which calls `load_credentials`, which is `Credentials.from_authorized_user_file` and then
  `creds.valid`. "Authenticated Yes" with the token file's modification time unmoved therefore means google-auth parsed
  the written file and found it valid without refreshing it.
- **The measurements** of the Inbox, the metadata shape, the encoded subjects, the entities, the message ids and the
  rate match the transcript. "No HTTP 429" is sound: any answer other than 200 would have stopped the run, and it ran
  to the end. The has:attachment recommendation is stated with its limit (Gmail's own definition, compared with
  `mimeType` on 36 messages only), and the README says so in "What is NOT proven".

## Findings

### 1. The recommended knownSender search puts the sender's own address, unchecked, into a Gmail search, and a known sender skips the spam screen

What the proof does: `measure` builds the search as `q=to:<a> OR cc:<a> OR bcc:<a>` where `<a>` is
`parseaddr(From)[1].lower()` (`gmail_probe.py` lines 437, 510 to 511, 558 to 559), and the README recommends exactly
this, way (b), for the real check.

Why it matters: the `From` header is written by whoever sends the mail. `parseaddr` does not make it a plain address:

```
'<a b@c.com>'                         -> 'a b@c.com'
'Bank <gmail.com OR x@evil.example>'  -> 'gmail.com or x@evil.example'
'"x OR label:sent"@example.com'       -> '"x OR label:sent"@example.com'
'<*@example.com>'                     -> '*@example.com'
```

Each of these becomes search words and operators inside `q`, not one address. In phase 3 a known sender is never
asked the person-or-spam question (`CONTRACT.md` row 3b; `phase-3/reviews/layers-settings-review.md` point 2). So a
sender who can shape the search so that it finds any Sent message skips the sender screen. Way (a), the Sent cache,
compares exact addresses in a set and has no such exposure.

The proof cannot show the search is safe or exact: it compared the two ways on 10 ordinary senders, 2 of them known
(`"sendersWhereTheyDiffer": 0`). That cannot reveal what the search does with a hostile address, nor whether Gmail's
`to:` matches only the whole address or also a longer one that contains it. I did not run Gmail, so I cannot say that
a given `From` does flip the answer; the harm I can prove is that the recommendation adds an input the sender controls
to a decision that bypasses the spam screen, and the comparison did not weigh it.

What should change: the README's recommendation should carry the condition, and "What is NOT proven" should name it.
At least: the real check searches only when the address matches a strict plain-address pattern (no spaces, quotes,
braces, brackets, asterisks or other search characters) and otherwise treats the sender as not known; and either the
search's exactness is measured on look-alike addresses, or a hit is confirmed against the found message's To, Cc and
Bcc headers. Or the Tech Lead picks way (a) knowing this. Either way it is the Tech Lead's call, made with this on the
table.

### 2. A refresh Google refuses names the placeholder `<nickname>`, not the account

`post_token_refresh` (line 217) fails with `run cc-gmail -a <nickname> auth --force`, the literal text `<nickname>`.
R1.4 says the failed channel names `cc-gmail -a <nickname> auth` with `--force`, where the nickname is the directory
name; every other failure in `load_token` names the real nickname. devlinux has two Gmail accounts (`personal` and
`consulting`), so the owner is handed a command that cannot be run as written and has to work out which account it means.

The test cannot see this: `test_refused_refresh_fails_naming_auth_force_and_leaves_file_unchanged` matches only
`invalid_grant.*auth --force`. I removed the nickname from the message entirely and all 49 tests still passed. The
README's claim that a refused refresh "fails naming `auth --force`" is true only in that weak sense.

What should change: pass the nickname into the refresh failure, and have the test match `cc-gmail -a personal auth
--force`. The real check copies this probe's messages, so the fix belongs here.

### 3. A token.json that cannot be parsed crashes the probe instead of failing the channel, and cc-gmail can leave one

`load_token` (line 155) is `json.loads(token_path.read_text())` with nothing around it, and `main` catches only
`ChannelFailed`. With a token.json cut short I got a `json.decoder.JSONDecodeError` traceback, no `failed` line and
no fix named. (`find_account` handles the same case for config.json correctly, which shows it was meant to be handled.)

This state is reachable in normal use, not only by damage: cc-gmail's `save_credentials` is
`token_path.write_text(creds.to_json())`, which empties the file and then writes it. A check that reads during
cc-gmail's own refresh reads a partial file. In the real check, an exception escaping one channel's sign-in step can
stop the other channels unless something above catches it, which R4 forbids ("one channel failing never stops the
others").

It also corrects a sentence in "What is NOT proven": "Both write a whole valid file" is true of the probe's write
(`os.replace`) but not of cc-gmail's. The last-writer-wins reasoning still holds for the final state; the moment in
between does not.

What should change: an unreadable token.json is a failed channel naming `cc-gmail -a <nickname> auth --force`, with
a test, and the two-writers item in "What is NOT proven" says that cc-gmail's write is not atomic, so a reader can
see a partial file.

### 4. Nothing ties the transcript to the committed probe

The transcript is stamped `commit bff2295cf` (line 1), which is the base branch: it does not contain `gmail_probe.py`
at all. "Found on the way" says the guard was changed between runs. So the probe that produced the final transcript
was an uncommitted working copy, and nothing in the folder says it is byte-for-byte the committed one. The phase 0
standard says this explicitly ("with the check, rig and tests exactly as committed here"). Without it, a reader
cannot tell whether the committed guard is the one that ran 600 calls.

The output keys in the transcript match the committed `main` and `measure`, so I have no reason to think they differ;
the gap is that the proof does not say so.

What should change: one sentence in the README that the final run used `gmail_probe.py` exactly as committed (if that
is true), and in future let `run_proof.sh` print the SHA-256 of the probe into the transcript.

## What the tests catch

Each breakage applied alone to a scratch copy, with the unchanged tests:

| Breakage | Result |
|---|---|
| Take the first of several matching accounts | 2 tests fail |
| `gmail_store` uses `HOME` | 1 test fails |
| `main` uses `HOME` | 2 tests fail |
| Write the keys in a different order | 1 test fails |
| Temporary file mode 0644 (final chmod kept) | all pass: harmless, the final chmod still makes 0600 |
| No final chmod (temporary file kept 0600) | all pass: harmless, `mkstemp` already makes 0600 |
| The refused refresh names no account | all pass: finding 2 |

The two mode breakages are each covered by the other line, and the test asserts the outcome (a 0644 file comes out
0600), so a real check that writes some other way would be caught. Not a finding.

## The public repository rule (R6)

Searched the whole diff. No real address, no real domain (only `example.com`, `example.org`, `example.net` and
Google's own endpoints), no token, no snippet, no subject, no message id of real mail. The one 16-character
hexadecimal id, `19a2b3c4d5e6f708`, is test data and plainly made up. The owner's names do not appear. `run_proof.sh`
replaces anything holding "@" before the transcript is written.

Checked and judged harmless, so not findings:
- The transcript carries 8-character SHA-256 prefixes of `token`, `refresh_token` and `client_secret`. Thirty-two bits
  of a hash of a long random secret reveal nothing usable. `account` is the empty string (its prefix `12ae32cb` is the
  hash of `""`), so no address is hashed.
- The cc-gmail tables keep their column padding after the address is replaced, so they show how long each address
  is. A length is not an address.

## What "What is NOT proven" should also name

Beyond the two items in findings 1 and 3:
- **Not run as a factory check under a Director.** The phase 0 proof ran its check inside a real Director, which is
  how it found uv missing from the live Director's PATH. G0 ran the probe from a shell with `env -i`. That is enough
  for R1 and R2, but the README should say the Director's environment was not part of this proof.
