# Mail Desk, phase 0: review of the proofs (pull request 4214, branch `mail-desk-phase-0`)

Date: 18 October 2026. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), the phase 0 row of
section 6 and section 7. The review seat is a different agent family from the writer, as repository rule 5a
requires when the first seat in the order cannot start.

## Verdict

**Accept. No finding.** Each proof shows what its README claims, every NOT PROVEN statement is honest and
precise, the corrected section 5.4 is right and fails closed, the scripts only read the mailbox, and nothing
committed carries an address other than email@devthrottle.com, a token, a message id or body text from a real
email. No assistant attribution anywhere on the branch. Observations for the Delivery Lead follow the verdict;
none of them blocks the merge.

## Scope: what was read, what was run, what was not reached

**Read, all from origin (never the shared checkout's working tree):**

- The whole branch diff, `git diff origin/main...origin/mail-desk-phase-0` (nine commits, 34 files, merge base
  equals origin/main at `46258cb4d`): the four README files and the top-level one, the four scripts
  (`sign_in_probe.py`, `run_proof.py`, `measure_mailbox.py`, `model_sample.py`), `run_classify.sh`, both test
  files, the published definition, the prompts, the invented email set, every committed result file, both
  transcripts, and the amendment to `MISSION.md` (the 5.2 row and the 5.4 findings).
- The mission document on origin/main: sections 1 to 4, 5.4, 5.5, 5.10, 6 and 7.
- On origin/main, to check the 5.4 corrections against the code they describe: `tools/cc-outlook/src/auth.py`
  (`CONFIG_DIR`, `TOKENS_DIR`, `get_msal_token_path`, `_save_cache`, `_harden_file_permissions`) and
  `tools/cc_storage/storage.py` (`_base`, `_user_base`, `tool_config`, `user_tool_config`).
- This session's own environment, as a second witness to two findings: it was started by the owner's live
  Director on devlinux, carries `CC_DIRECTOR_ROOT=/home/<user>/.local/share/cc-director/instances/default`, and
  its PATH is exactly the one `run_proof.py` names as the live Director's, with no `~/.local/bin` and no `uv`.

**Run, in a worktree cut from `origin/mail-desk-phase-0`, with uv from `~/.local/bin`:**

- The sign-in tests: `uv run --no-project --with pytest --with "msal>=1.28" python -m pytest
  docs/qa/mail-desk/phase-0/a-sign-in/tests -v` - 16 passed, as the README says.
- The model tests: `uv run --script docs/qa/mail-desk/phase-0/c-model/test_model_sample.py` - 31 passed, as the
  README says.
- A mutation check the README describes: with the more-than-one rule in `find_sign_in` disabled in a scratch
  copy, exactly the two more-than-one tests failed and the other 14 passed. The tests have teeth.
- Both suites run with no network to Microsoft and no sign-in: the sign-in tests remove every `CC_` variable
  and point the lookup at scratch folders of dummy files; the model tests use the fake endpoint only.

**Not reached, deliberately or because it could not be:**

- Nothing was re-run against the real mailbox, the owner's sign-in, the owner's Director, the throwaway rig or
  any model endpoint. The transcripts and result files were taken as the evidence of those runs and checked for
  internal consistency and against the code that produced them.
- Windows and macOS branches of the lookup: not run here either; the README says the same.
- The hosted Gateway's switch state for the owner's account: not asked, as proof (d) also did not.

## Does each proof show what its README claims?

**(a) The sign-in premise - yes.** Every claim in the verdict table has its evidence line in `transcript.txt`:

- All three runs saw exactly four `CC_` variables (`CC_CHECK_FACTORY`, `CC_CHECK_SEAT`, `CC_CHECK_SECRET_VARS`,
  `CC_CHECK_STATE_FILE`); the control lookup from the scratch `HOME` found nothing; the lookup from the account
  record found exactly one file, at `instances/default/config/outlook/tokens/email_devthrottle_com_msal.json`.
- `silentToken: yes`, `tokenSource: identity_provider` and `inboxTotalItemCount: 19` in all three runs.
- `scp` lists `Mail.Send` beside `Mail.ReadWrite` in all three runs, with `force_refresh=True` in the code, so
  the NOT PROVEN on point 3 is exactly right: the token carries the scope whatever is asked for.
- The cache's modification time moved 14:46:36, 14:54:36, 14:57:05, 14:59:35, each run's `cacheBefore` equal to
  the previous run's `cacheAfter`; mode `0o600` before and after every write; hash prefix changed; `cc-outlook
  accounts list` shows `Yes` before and after. The write-back copies cc-outlook's `_save_cache` (write only when
  the state changed, same path, then owner-only), which I confirmed against `auth.py` on origin/main.
- The test Director exited with code 0 on its lifecycle request file; no process was left from the rig folder;
  the real `~/.local/bin` links were unchanged.
- `transcript-live-path.txt` holds the uv finding end to end: the publish warning, the failed run row with the
  reason "the check declares packages and uv is not on this machine", and the Director log line
  `FindOnPath: name=uv, found=(none)`. This session's own PATH confirms the live Director's PATH as recorded.

**(b) Read-only measurements - yes.** `measurements.json` holds counts, sizes, times and distributions only: the
one address in it is our own; there is no message id, subject or preview. Inbox 19 and 4 unread before and
after, 17 Graph calls, 38 distinct Sent recipients, 0 known senders in the sample - all as the README states.
The guard (`GetOnlyGraphSession`) refuses every method but GET and every host but graph.microsoft.com before
sending, with redirects off, and the self-test runs before every measurement.

**(c) The model on a sample - yes, and the headline verdict is the honest one.** The README leads with NOT
PROVEN against the real model and says why. The committed runs are marked `"endpoint": "fake"` and
`"spent_is_notional": true`; 19 and 18 emails; 7 and 5 off-list answers recorded as `unsure`; the spend-stop
run stopped at $0.000160 against a $0.0003 limit. The threshold proposal is labelled a reasoned proposal, not a
measurement. Every committed result file holds short keys, sender kinds, labels, confidences and costs; none
holds an at sign or any string longer than a label. The invented set uses reserved example domains only, with
the local part and domain kept apart.

**(d) Where the switch belongs - yes.** A reading of origin/main at a named commit, with file and line
references; the recommendation copies an existing, tested design, and what it did not settle is listed.

## Are the NOT PROVEN statements honest?

Yes, and they are the strongest part of the work. Each README ends with a "What was NOT proven" list that is
specific (an expired sign-in as opposed to a missing one; paging at scale; a sender found in Sent; anything about
the real model; Windows and macOS), and the top-level README carries the same list unsoftened. The one point
the mission's phase 0 row expected to pass and did not (a `Mail.ReadWrite`-only token) is reported as NOT PROVEN
with its consequence stated, rather than reworded into a pass.

## Is the corrected section 5.4 right and safe?

**Right.** I checked each correction against the code it describes, on origin/main:

- `CONFIG_DIR = CcStorage.tool_config('outlook')` in `auth.py`, and `tool_config` goes through `config()` and
  `_base()`, which returns `CC_DIRECTOR_ROOT` when it is set. So the first version of the 5.2 row ("resolved from
  the user's own folder, never from a `CC_` variable") described `user_tool_config`, which cc-outlook does not
  use. The correction is accurate.
- A Director's session does carry `CC_DIRECTOR_ROOT=<base>/instances/<instance>`: this session has exactly that.
- cc-outlook's fallback without the variable is `_user_base()`, which on Linux is `~/.cc-director`: accurate, and
  the lookup rightly refuses to look there.
- The file name rule is `get_msal_token_path`, with `@` and `.` turned into `_`: accurate, and a test fails if that
  line in cc-outlook changes.
- The scope finding matches the token's `scp` claim in all three runs.

**Safe.** The lookup fails closed in every uncertain case: none found, more than one found, a cache with no
account, a silent renewal refused. It never picks one of several and never falls back to another folder. The
request guard described for `screen.py` (GET, and `POST /me/messages/{id}/move` with `destinationId` `archive`
or `junkemail`, nothing else) is the right place for the no-send promise once the token cannot carry it, and the
design says phase 2 must prove it with tests.

## Do the scripts only read the mailbox?

Yes. Every Graph call in all three mailbox scripts goes through a guard that refuses anything but GET against
graph.microsoft.com before a request is made; none of them contains a move, a flag, a mark-read, a category
change, a delete or a send; none contains an interactive or device-code sign-in. The Inbox unread count was
unchanged by proof (b), which shows that reading did not mark anything read. The only write any script makes
outside the repository is proof (a)'s write-back of the renewed sign-in cache to its own file, which is what the
phase 0 row asked it to prove. In `model_sample.py` the one POST is to the model endpoint, whose host is fixed in
the code, never to Graph.

## Is anything committed that should not be?

- **Addresses:** the only address in the added files is email@devthrottle.com. The owner's address appears once
  in `MISSION.md` line 4, which was already on origin/main and is unchanged by this branch. The invented set uses
  example domains. The transcripts' rig replaces any other address, the user name, the rig and home paths and any
  token-shaped string before writing.
- **Tokens:** none. Of the saved sign-in the transcripts record only time, size, mode and a twelve-character hash
  prefix. No token-shaped string, no `dt_live_` or `dt_test_` value, no bearer value anywhere.
- **Body text and message ids:** none from real email. Result files carry twelve-character hashes of ids and
  counts of visible characters, never the text.
- **Attribution:** none in the nine commit messages (author and committer are the owner's identity), none in any
  document. The agent type and model name in `factory.yaml` are product configuration values for the throwaway
  definition, and the transcripts list inherited environment variable names and the `~/.local/bin` links as part
  of the evidence, as the README explains. None of that is a signature on the work.

## Observations (none blocks the merge)

1. **Sender domains of real email are committed.** `fake_run_real19/per_email.json` and `model_answers.json`
   carry `sender_kind` values that are the real senders' domains (two of them named in the (c) README). The phase
   0 row forbids addresses and body text, and these are neither, so the rule is kept to the letter. The Delivery
   Lead may still want to decide whether a domain of a service the account uses is something the public
   repository should hold, and set the rule for later phases now.
2. **A comment and the committed layout disagree.** `model_sample.py` says the fake model's answers "stay in the
   raw folder, never in the committed results", yet `results/fake_run_*/model_answers.json` are committed. They
   are harmless (keys, kinds, labels, notional costs), and the README's run instructions write them elsewhere; the
   comment is simply out of date.
3. **One (c) claim rests on the code, not on committed evidence.** The README says the cache file's time, size and
   hash were identical before and after the (c) read; no committed file records that. The code supports it (the
   cache is deserialised and never serialised back), and proof (b) recorded the same check for its own read.
4. **The more-than-one rule is fail-closed but brittle in production.** If any second instance under the owner's
   per-user base folder is ever signed in to cc-outlook for this mailbox, the production check fails until one
   sign-in is removed. That is the design's intent and it is safe; phase 2 and phase 6 should know it, and the
   failure text already names what to do.
5. **The account-record rule has one edge the README names.** A Director started with a `HOME` that differs from
   the account record, whose sessions did the sign-in, would have its sign-in under that `HOME` and the check would
   fail naming a fix that does not reach it. Not the owner's devlinux setup (there `HOME` and the account record
   agree); fail-closed; worth a sentence in phase 2's README.
