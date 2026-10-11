# Answers to the review of pull request 3869 (live exchange proof)

The review is `review-3869-live-proof.md`, beside this file. I accepted all five findings and fixed each one in
`live_exchange_proof.py`. After the fixes the proof was run again on Windows and on the Mac, and both passed.
The new outputs replace the old ones in `proof/`.

## High - reset timestamps do not prove which account answered: ACCEPTED, fixed

- **Each account's own reset times are now read first.** Before the task starts, the script reads them from
  the provider (`/api/oauth/usage`) with that account's own key, through the merged key store
  (`HeldAccount.read_key().access_token()`). The key goes only into the Bearer header. The
  `license usage` command was not on main yet, so the script does not use it.
- **The verdict is now a positive match, not a difference.** It passes only when every call made before the
  tool result matches the SOURCE account's own reset times and every call made after it matches the
  TARGET account's own.
  - "Matches" means the weekly reset time is equal to within a minute, and the five-hour reset time is too
    wherever both sides give one.
  - A call with no reset times matches nothing.
  - If the two accounts' reset times cannot be told apart, the run refuses to start.
- **"After" is now decided by request order, not by when an event arrives.** It means after the tool result
  is delivered (the `user` / `tool_result` event), which is the request that carries the paused call's
  result. A separate check fails the proof unless the exchange had returned before that tool result.
- In the Mac run, account B had no use in its five-hour window, so the provider gave no five-hour reset time.
  Its match therefore rests on the weekly reset time (15 Oct 21:00 UTC) and the `rejected` status, as the
  rule above allows.

## High - rollback is neither guaranteed nor part of the pass result: ACCEPTED, fixed

- The switch-back now runs in a `finally`, after a timeout, an error or an interruption too.
- Its result is checked, and the folder is read back.
- The verdict has a check "the folder was switched back and verified on the source account", so a failed
  switch-back fails the proof and the exit code.
- The README now says the folders were left as they were only because that check passed in both runs.

## High - the helper does not enforce isolated config folders: ACCEPTED, fixed

- **A proof run needs a marked test root.** `--test-root` must hold a marker file. Both the default
  config folder and the store must be absolute folders inside that root, or the run refuses before
  anything else.
- **Only a separate run writes the marker.** That is `--init-test-root <folder>`, which writes the marker
  and stops. It refuses the home folder, a drive root, any folder above the home folder, and any folder
  that holds or sits inside the real default Claude login.
- **A proof run never creates the marker.** This replaces the one-path denylist.
- **Checked before the real runs:** a run on an unmarked root refused, and marking the home folder refused.

## Medium - no subprocess has a deadline: ACCEPTED, fixed

- Every `license` command has a 120-second timeout and `claude auth status` has 60 seconds.
- The task has 300 seconds. Its output is read on a thread, and on timeout the task is killed.
- The `finally` then exchanges back. A timeout during setup stops the run with `PROOF FAILED`.

## Medium - redaction does not guarantee that emails or paths stay out: ACCEPTED, fixed

- **An alias is required for every account the run touches.** That means the source account, the target
  account, and every account `license list` prints. Otherwise the run refuses to start. Checked: a run
  missing one alias refused, and the unaliased address printed as `<unlisted email>`.
- **Paths and addresses are redacted as a class:**
  - the test root, the tool source and the home folder, in every spelling;
  - anything after them, since a spare's folder name is made from its account's email;
  - then any remaining email address, Windows path or macOS/Linux home path.
- **Time zones are redacted.** The provider's refusal text names the account's time zone, which says
  roughly where its owner lives, so a Region/City time zone is replaced too.
- **Output is plain ASCII.** Any character outside ASCII becomes `-`.
- Reset times and usage percentages are kept, as the Tech Lead ruled: they identify no one, and the proof
  depends on them.
