# Live exchange proof - Windows and the Mac, 11 October 2026

`../live_exchange_proof.py` was run once on a Windows machine and once on the Mac, against the license code
merged in pull request 3857, after the review in `../review-3869-live-proof.md` was answered. Both runs passed.
Each output file is exactly what the script printed. The script itself replaces account addresses with
invented `account-...@example.com` addresses, and replaces machine names, folders and time zones with labels.

## What each run shows

A `claude -p` task (Claude Code 2.1.296, the cheapest model) is started on an isolated folder that stands in
for the machine's default Claude config. The task pauses inside a tool call for 40 seconds. Five seconds into
that pause, `cc-devthrottle license switch` exchanges the active sign-in with the spare's. When the pause
ends, the same running process makes its next call.

Every call reports the window reset times of the account that served it. Before the task, the script reads
each account's own reset times from the provider with that account's own key. It passes only when the call
before the exchange matches the source account's own reset times, and the call after it matches the target
account's own:

| Machine | Call before the exchange matched | Call after the exchange matched (the same running process) |
|---|---|---|
| Windows (key in `.credentials.json`) | account A (weekly window resets 17 Oct 19:00 UTC) | account C (weekly window resets 13 Oct 12:00 UTC), and the task finished with `SECOND CALL OK` |
| The Mac (key in the Keychain) | account C (weekly window resets 13 Oct 12:00 UTC) | account B: `rejected`, with account B's own weekly limit and reset time (15 Oct 21:00 UTC) |

The Mac refusal is the expected answer: account B had used its whole week. After each run the script exchanged
back, then read the folder back. Both runs verified the folder was on the source account again, and the
proof depends on that check.

## Setup (not in the output files)

- **Isolated folders only.** Every folder sits inside a test root marked beforehand by a separate
  `--init-test-root` run. The machine's real default Claude login was never touched.
- **No key was copied.** Each folder was signed in with its own `claude auth login`.
- **The spare was moved, not copied.** It went into the isolated store with
  `cc-devthrottle license add <folder>`, after which the source folder no longer held the sign-in.
- **One spelling per folder.** Folders were given in the store's one canonical form: absolute, no trailing
  separator, and backslashes on Windows.
- **The code under test came from the checkout.** It was run from `tools/cc-devthrottle/src`, with the
  installed tool's own Python environment.

## Timing note

In the first round of runs (before the review), each `license` command on Windows took 13 to 32 seconds when
run from a freshly copied package in the temporary folder. In these runs the same commands took about a second
on Windows and under half a second on the Mac, so that delay was an effect of the environment and not of the
store.
