# Mail Desk: Gmail support

Date: 21 October 2026. Tech Lead: session 137 ("Mail Desk - Tech Lead - Gmail support"), on devlinux, opened by the
Mail Desk Implementation Lead (session 120). Phase branch `mail-desk-gmail`, cut from origin/main at `1e3daab40` and
brought up to origin/main at `6be9548ab` before the gate.

## Decision D25 (the owner, 21 October 2026)

> "Can you continue and get Gmail support implemented?"

It builds on decision D20 in `docs/missions/mail-desk-2026-10-18/MISSION.md`: every channel carries a provider
(`outlook` now, `gmail` next), nothing may assume Outlook beyond the reader in `checks/screen.py`, and Gmail is a new
reader on the existing cc-gmail sign-in, not a redesign. The first Gmail mailbox is the owner's own, signed in with
cc-gmail under the nickname `personal` on devlinux. The owner's Erik Laursen factory already reads that mailbox with
its own check; the Mail Desk will screen it in observe mode beside the two Outlook mailboxes. Its address is given only
to the Gateway and to the owner's report, never to this repository (handover ruling 7).

## What was built

| Part | Pull request into the phase branch | What it does |
|---|---|---|
| G0, the proof | 4333 | A read-only probe that finds the Gmail sign-in with every environment variable stripped, renews it and writes it back, and measures the real mailbox. `g0/README.md`. |
| G1, the reader | 4335 | `src/CcDirector.Gateway/MailDesk/Template/checks/screen.py` reads a channel whose provider is `gmail` through the Gmail API, beside the Outlook reader. `CONTRACT.md` in this folder. |
| G2, the Gateway | 4336 | The Gateway accepts `gmail` (label `Gmail`) and still refuses any other provider; every sentence that named `--provider outlook` now names both; the command and the client render the Gateway's words. |

The rulings every Developer worked to are in `RULINGS.md` (R1 to R7). No database migration was needed: the provider
is a text column.

## The proof, in plain words

**Where the check finds the sign-in (R1, the equivalent of handover ruling 4).** cc-gmail keeps one store per
operating-system user, not one per Director instance (issue 3011). The check looks in exactly one place: on Linux
`<home>/.cc-director/config/gmail`, on macOS `<home>/Library/Application Support/cc-director/config/gmail`, on Windows
`%LOCALAPPDATA%\cc-director\config\gmail`, where `<home>` comes from the operating system's account record, never from
`HOME`, and `LOCALAPPDATA` is ignored outside Windows. The account is the folder under `accounts/` whose `config.json`
names the channel's address, compared without regard to case. None found: that mailbox fails, naming the fix. More than
one: it fails, naming every folder; it never picks. There is no fallback. On devlinux the Erik factory's own
`LOCALAPPDATA` points at a folder whose `cc-director` is a symbolic link to `~/.cc-director`, so there is one store, not
two. G0 ran the probe against the real mailbox with an empty environment, and again with `HOME` and `LOCALAPPDATA`
pointing at decoy stores holding the same address; both times it found only the real one.

**The client id and secret, and the write-back (R2).** cc-gmail renews a sign-in with google-auth's
`Credentials.from_authorized_user_file(token.json)`, which takes the client id and secret from `token.json` itself;
`credentials.json` is read only by cc-gmail's interactive sign-in (G0 quotes the library's lines). So the check reads
`token.json` alone, and a `token.json` missing any of the four fields fails that mailbox naming `auth --force`, with no
fallback to `credentials.json`. It renews with a plain POST to `https://oauth2.googleapis.com/token` (no other address
is ever followed), writes the token back through a temporary file and one rename, readable by its owner only, with
every other key unchanged and in order. G0 forced a renewal on the real file: mode 0600, every other value unchanged by
hash, and cc-gmail itself still reported the account Ready afterwards.

**The token can send mail.** Its scopes include `gmail.send`, `gmail.compose` and `gmail.modify`. Exactly as with
Outlook (handover ruling 3), the promise that the Mail Desk never sends rests only on the check's request guard: every
Gmail call goes through one function that lets out only the reads the check needs (`GET` of the message list and of one
message), and in live mode only, `POST .../messages/{id}/modify` with exactly `{"removeLabelIds": ["INBOX"]}` (archive)
or `{"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}` (junk). Send, drafts, trash, delete, batch changes, any
other label, another user or host, and every path or encoding trick the reviewers tried are refused before anything
reaches the network, and observe mode never calls modify. The tests send every refused shape and assert that the fake
Gmail API's log is empty.

**The cursor (R4).** The newest message's `internalDate` in milliseconds plus the ids reported at exactly that time,
the same pattern as Outlook; not Gmail's history id, which expires after about a week. The batches, the short keys, the
output and the rule that one mailbox failing never stops the others are the Outlook contract's.

**Two fields measured on the real mailbox (R5).** The cheap message form carries no attachment information, so
`hasAttachments` comes from one `has:attachment` search per mailbox per run. Whether we have written to a sender comes
from one search of the Sent mail per new sender (2.4 seconds for 10 senders, against 123 seconds to build a cache of
the last 500 sent messages). Because the sender writes its own `From` header, the search runs only for a strict plain
address, and a hit counts only when the found message's To, Cc or Bcc holds that exact address (G0 review finding 1).

## The reviews

Every piece was reviewed by a separate reviewing session on the agent handover ruling 9 names, and every finding was answered by the Developer who
built it, accepted or declined with a reason, in the file beside the review.

| Review | Verdict | Findings | Answers |
|---|---|---|---|
| `reviews/g0-review.md` | Approve | 4 | `reviews/g0-answers.md`: all accepted |
| `reviews/reader-review.md` | Approve | 2, and 5 observations (one, a sentence over 165 characters, treated as a finding) | `reviews/reader-answers.md`: findings fixed; observations answered |
| `reviews/gateway-review.md` | Approve | 3 | `reviews/gateway-answers.md`: all accepted |

## The check, run by the Tech Lead

On the final phase tree (`fad6dca80`, the phase branch with origin/main `6be9548ab` merged in):

| What | Command | Result |
|---|---|---|
| The screening check, Outlook and Gmail, and G0's probe | `uv run --no-project --with pytest --with "msal>=1.28" --with google-auth python -m pytest docs/qa/mail-desk/phase-2/tests docs/qa/mail-desk/gmail -q` | 698 passed |
| The `cc-devthrottle` command, every test | in `tools/cc-devthrottle`: `uv run --no-project --with pytest --with ../cc_storage --with ../cc_shared --with-requirements <its other dependencies> python -m pytest tests -q` | 5,190 passed, 2 skipped |
| The Mail Desk page | in `packages/client-core`: `npx vitest run src/maildesk --minWorkers=1 --maxWorkers=2` | 36 passed |
| The Gateway's Mail Desk tests | `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~MailDesk"` (run on the trial merge of G2 with the reader) | 578 passed |
| The repository gate, with the parked suites | `pwsh ./scripts/test-local.ps1 -Parked`, with `EnableWindowsTargeting=true`, `DOTNET_USE_POLLING_FILE_WATCHER=1` and a scratch `LOCALAPPDATA` | see below |

**The gate on Linux (handover ruling 8).** `-Parked` cannot be clean on devlinux: the script's verdict was NEVER RAN on
both runs, because the two Windows-only installer suites print no summary and the Launcher, Reclaim, Terminal and part
of the Core suites depend on Windows behaviour. So it was run twice, on the phase branch and on unchanged origin/main at
the same commit (`6be9548ab`), and compared test by test from the result files (`gate/`):

- The phase branch: 107 failing tests in 9 suites. Unchanged main: 108 in the same 9 suites.
- **No test fails on the phase branch that does not also fail on main.** The one test failing only on main is
  `GatewayStatsStoreMidChainContainmentTests.ContainedOpen_ChangesNothingOnDisk`, which this phase does not touch.
- Every suite's counts are identical on both, except the Gateway unit tests, which have 27 more tests on the branch,
  all passing (G2's additions).
- `gate/suites-branch.txt` and `gate/suites-main.txt` hold each suite's line; `gate/failures-branch.tsv` and
  `gate/failures-main.tsv` hold every failing test by name.

**No Windows gate ran.** `test-database.ps1` was not run: this phase adds no migration.

## Adding the mailbox (for the Implementation Lead, after the deploy)

```
cc-devthrottle maildesk channel add <the Gmail address> --provider gmail --machine devlinux --why "<why>"
```

A new channel always starts in observe mode. The check needs nothing new on devlinux: the cc-gmail sign-in it reads is
the one already there, and its only package is still MSAL. If the sign-in is missing or refused, the mailbox's row on
the Mail Desk page shows a sentence naming the cc-gmail command that fixes it.

## What is NOT proven

- **Never run as a Mail Desk check under a Director, against the real mailbox.** G0's probe ran from a shell with an
  empty environment; the reader was proven against a fake Gmail API only. Phase 6's production run is the first real
  one. Phase 0 found `uv` missing from the live Director's PATH on devlinux; that applies to Gmail exactly as to Outlook.
- **Modify was never called on any real mailbox**, so archive and junk on Gmail, and Gmail's answer to a retried
  archive or an id already gone, are proven against the fake only. Live mode stays off in production (D17).
- **Gmail listing newest first** is a premise the reader relies on to report oldest first; G0 did not measure it.
  Phase 6 should check the order once on the real mailbox (`CONTRACT.md` section 4).
- **More than 60 already-reported messages in the cursor's last second** would stall that mailbox (`CONTRACT.md`).
- **The search's exactness on look-alike addresses** was not tried against Gmail; the confirmation against the found
  message's recipients is what makes the answer exact, proven by the tests and seen on 2 real hits.
- **Two writers of one token at once.** The check and cc-gmail (and the Erik factory through it) renew the same
  `token.json`. The check's write is atomic; cc-gmail's is not (it empties the file, then writes it), so the check can
  read a half-written file. That fails the mailbox for one run, naming `auth --force`, and the next run reads the whole
  file. That the final state is always a valid token is reasoning, not proof.
- **macOS and Windows** store paths are tested as path computations only.
- **The token cannot be narrowed.** It can send; nothing but the check's own guard stops that.
- **No Windows gate ran**, and the Linux gate is a comparison with main, not a clean verdict.
