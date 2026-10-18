# Review of pull request 4114 - keep the active sign-in alive when no Claude Code session uses it

Reviewer: a separate review session (Track B, License Switching). Head reviewed: 386447adf, based on
08dec1140. Branched beside 4106; the only overlap is `CcDevthrottleLicenseCommandLine.ReadUsageAsync`,
which the pull request body already names.

## Scope

Read in full: the `license_usage.py` changes (the session registry reader, the process-start readers for
Windows, Linux and the `ps` fallback, Claude Code's two lock folders, `refresh_key`,
`_refresh_active_holding_the_lock`, the re-read under the lock, `_renew` and its write path),
`license_usage_ops.py` and `cli.py`, the Director's `ReadUsageAsync` change, the contract.md and
spare-refresh.md amendments, the proof file, the pull request body, and every test in the diff.

Checked the measurement against the installed Claude Code binary, version 2.1.302 at
`~/.local/bin/claude.exe` (290 MB, dated 16 October), by locating each cited string and reading the code
around it. Confirmed: the pre-expiry check is "now plus five minutes is at or past expiresAt"; the refresh
lock is a mkdir lock named `.oauth_refresh.lock` inside the config folder with stale 60000 and update
5000, plus a legacy lock named by the config folder's real path with `.lock` appended; after taking the
lock the session reads the stored key again and, if the access token differs from the one it started
with, uses the stored one and posts nothing; on contention it retries five times one to two seconds
apart, then watches the lock's modification time over a short window, and only attempts a take-over
through the owner-record path. With no owner record (which is what our lock leaves) the answer is
`no_owner_record` and it does not take over, returning a busy or timeout result without refreshing. On a
401 it reads the stored key again and adopts it when it differs and is not itself near expiry, reporting
the recovery. The session registry under `sessions/` with `pid`, `procStart` and `startedAt` is present
in the binary; I did not repeat the Developer's live run that showed a killed session's record left
behind.

Ran: `test_license_usage.py` on the branch: 96 passed, 5 failed. The 5 failures are the command-line
tests, all `ValueError: stderr not separately captured` from this machine's Click 8.1.8, which is below
the tool's floor of 8.2.1, the condition the Developer named in the 4106 proof. They are not failures of
this change; every renewal, lock and session-registry test passed. Grepped the diff for real names: none.

Could not reach: I did not build or run the .NET tests. I did not run a real signed-in Claude Code against
a key renewed under it; the proof says the same, and the rule does not depend on it.

## What I checked hardest, and found sound

- **Exactly one copy.** The renewal reads the key through the store handle, posts, and writes back with
  `LicenseStore.store_refreshed_key` into the folder that holds the account now (an atomic same-folder
  replace, or the Keychain item). Nothing else is written. The test searches every file under the test
  machine for the four tokens and finds the new ones in one file and the old ones nowhere.
- **Under Claude Code's own lock, and the store lock.** The three locks are held from the first read to
  the write; the first Claude lock is given back when the second is busy (tested). Claude Code's lock
  library treats a lock untouched for 60 seconds as stale; this tool holds it for one HTTP call with a 20
  second timeout plus a local write, so a session never sees it as stale. A session that meets the lock
  waits roughly ten seconds, finds no owner record, and gives up without refreshing; on its next call it
  takes the lock, re-reads, and finds the renewed key. That is the behaviour the rule relies on, and it is
  in the binary.
- **The deviation: an expired token renewed with idle sessions registered.** Safe by what the binary
  does. A session holding a dead token cannot be mid-turn on it. Before its next call it sees the token
  past expiry, takes the lock, reads the stored key, finds a different access token and uses it without
  posting; if it had already sent a request, the 401 path reads the stored key and adopts it. The spent
  refresh token is never posted by the session because it re-reads before posting.
- **The registry.** A record counts only while its process is alive and started no later than the record
  says (five seconds' slack), so a killed session's record and a reused process number do not block the
  renewal; an unreadable record counts as in use, so not knowing never reads as "no session". A process
  whose start cannot be read answers 0.0 and counts as alive (fails safe). Tested against the real
  process table for the test's own process.
- **A session the registry does not show.** The remaining exposure is a reader of the credentials file
  that does not register in `sessions/`: a Claude Code older than the registry, or a third-party tool
  using the same OAuth file. Its in-memory access token dies the moment this renewal lands. A Claude Code
  session recovers on its 401 by reading the stored key (confirmed above); a third-party tool has no such
  path. The mandate was Claude Code sessions, and the Director's own sessions are current Claude Code, so
  this is a scope note, not a defect.
- **A session starting between the registry check and the write.** The check is made before Claude
  Code's lock is taken and not repeated under it. A session that starts in that window reads the old key
  and, if it has sent a request, gets a 401 and recovers from disk. Small window, recoverable, and the
  same window exists for a session's own renewal racing another session's; no change asked for.
- **Failure is reported.** A refused refresh is the active row's `refresh-failed`, and the Director's
  words are "The stored sign-in could not be renewed; the account needs a new sign-in." (tested on both
  sides).

## Findings

**1. (low) On Linux, `process_started_at` can raise `FileNotFoundError` and bring down the whole usage
read.** `process_started_at` checks `Path(f"/proc/{pid}/stat").is_file()` and then calls `read_text()`
outside any `try`. A process that exits between the two calls makes `read_text()` raise
`FileNotFoundError`, which `claude_sessions_in_use` does not catch; it propagates out of
`_refresh_active_holding_the_lock`, past the `RefreshError` handlers in `read_with_refresh`, and the
tool exits with a traceback, so that round sends no report. Concrete case: a Linux Director whose active
token is under an hour from expiry while a registered Claude Code session is exiting at the moment of the
two-minute read. Rare and self-correcting next round, but it is the one place in the new code where a
race turns into a crash rather than an answer; the intended answer for a vanished process is "not
running".

## Not findings, noted for the record

- `license refresh` (the command) now refuses the active account with a message that names the usage
  read as the only renewer; the `--refresh` flag's help was updated to match. The AXI shapes are
  unchanged.
- The spare path and the active path now share `_read_for_refresh`, `_not_renewed` and `_renew`; the
  spare's `force` behaviour is unchanged (tests still pass).
- The pull request body records the default gate green and 318 Python tests passed; no Gateway code is
  touched, so no parked run is owed.
