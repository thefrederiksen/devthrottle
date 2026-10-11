# Live exchange proof - Windows and the Mac, 11 October 2026

`../live_exchange_proof.py` was run once on a Windows machine and once on the Mac, against the license code
merged in pull request 3857. Both runs passed. Each output file is the script's own output. Before commit,
every email address was replaced with an invented `account-...@example.com` address, and the machine names
and folders were replaced with neutral labels.

## What each run shows

A `claude -p` task (Claude Code 2.1.296, the cheapest model) is started on an isolated folder that stands in
for the machine's default Claude config. The task pauses inside a tool call for 40 seconds. Five seconds into
that pause, `cc-devthrottle license switch` exchanges the active sign-in with the spare's. When the pause
ends, the same running process makes its next call.

Every call reports the window reset times of the account that served it, so the reset times show which
account answered:

| Machine | Before the exchange | After the exchange (the same running process) |
|---|---|---|
| Windows (key in `.credentials.json`) | account A: weekly window resets 17 Oct 19:00 UTC | account C: weekly window resets 13 Oct 12:00 UTC, and the task finished with `SECOND CALL OK` |
| The Mac (key in the Keychain) | account C: weekly window resets 13 Oct 12:00 UTC | account B: `rejected`, with account B's own weekly limit and reset time (15 Oct 21:00 UTC) |

The Mac refusal is the expected answer: account B had used its whole week, and the refusal carries account
B's own reset time, not account C's. After each run the script exchanged back, so the folders were left as
it found them.

## Setup (not in the output files)

- Only isolated folders were used, never the machine's real default Claude login. Each folder was signed in
  with its own `claude auth login`, so no sign-in key was copied.
- The spare was moved into an isolated store with `cc-devthrottle license add <folder>`. After that, the
  source folder no longer held the sign-in.
- Folders were given in the store's one canonical form: absolute, no trailing separator, and backslashes on
  Windows.
- The code under test was run from the checkout's `tools/cc-devthrottle/src`, with the installed tool's own
  Python environment.

## Timing note

On Windows, each `license` command in the output takes 13 to 32 seconds. That is the cost of running a fresh
copy of the code out of the temporary folder: the first run from a new copy took 31 seconds, later runs 13 to
17 seconds. The same command run from a copy made earlier took 1.3 seconds. On the Mac each command took
under half a second.
