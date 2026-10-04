# gateway-load

Measures what one more account costs the hosted Gateway in memory and data out, by running a separate copy of the
Gateway on this machine and connecting simulated accounts to it in steps. It never touches production: the host
refuses to start with a real database, stats store, public address or database provider set, and `run-load.ps1`
clears those for the processes it starts and restores the terminal's settings when it ends.

The copy listens on every network interface (as the Gateway always does) on a port the operating system picks, with
authentication on, so it is reachable from the local network for the length of a step.

## How it works

Two processes, so the Gateway's memory is measured without the load generator's:

- `gateway-load host` starts the real Gateway code in hosted mode on a throwaway folder (`CC_DIRECTOR_ROOT` must be the
  run's own `root` folder, or it refuses to start), enrolls N accounts directly (each with one device key per Director
  and one for a phone), writes `accounts.json`, and samples its own memory into `memory.csv` every 5 seconds until
  `stop.txt` appears. When the driver asks (at the end of its hold, accounts still connected) it takes one more
  sample after a full, compacting collection into `collected.csv`: that is LIVE memory, and the regular samples,
  which force nothing and so include garbage, are an upper bound. The Gateway's log goes to the run's `logs` folder.
- `gateway-load drive` connects the first N accounts: each Director over the real `/director-stream` tunnel with its
  device key, pushes a snapshot of sessions built from `session-template.json` (a real roster row, every string
  scrubbed to x's of the same length), then pushes changes as sessions take turns, and makes the Director's regular
  reads (session list, skills twice, workflows, injected text, triggers, account status). A share of the accounts also
  polls `GET /sessions` every 2 seconds as an open phone does. A failed read or push is printed and counted, and the
  loop carries on. At the end it saves each account's own `/diag/traffic` rows (`traffic.json`) and what it did
  (`drive.json`); the summary refuses a step that had any failure.

`run-load.ps1` runs one or more steps; each step is a fresh Gateway in its own folder, and a folder that already
holds a run is refused rather than reused. `summarize.py` turns a results folder into the memory per step, the slope
per account for each profile, and data out per account per day (phone traffic charged per open phone times the
profile's share of the day, because a whole number of phones cannot match the share at every step).

```powershell
.\run-load.ps1 -Profile fleet -Steps 0,5,10,20 -HoldMinutes 15
# one step per call, into one folder, to stay inside a 10-minute foreground limit:
.\run-load.ps1 -Profile light -Steps 50 -HoldMinutes 8 -SkipBuild -RunRoot D:\load\light
python summarize.py D:\load\light
```

## Profiles

Rates are in `DriveMode.cs` (`LoadProfile`), each with its source.

| Profile | Directors | Sessions each | Phone open | Source |
|---|---|---|---|---|
| fleet | 3 | 10 | 8 hours a day | the owner's account on the traffic meter, 4 October 2026 |
| light | 1 | 5 | 3 hours a day | the owner's DEFINITION (4 October 2026), not a measurement - nobody else's traffic is visible; its "voice on for one session" is not simulated |

`-Today` uses the Director's session-list rate before the desktop release that carries pull request 3519.

## What it does not measure

- **Stored history.** A simulated account is minutes old. What a real account accumulates and the Gateway keeps in
  memory (turn history, transcripts, stores) is not created, so the slope is the cost of a CONNECTED account, not of
  an account with months of history.
- **Agents' own reads.** Agents calling `session list` and reading screens with a session key are not simulated.
- **Everything else a real Director sends.** Repository snapshots, conversation turns and session key registration
  travel over the same tunnel and are not simulated.
- **The database.** This runs on SQLite; production runs on PostgreSQL. On SQLite a hosted Gateway has no statistics
  store either, so that work is absent from these figures.
- **The session-change rates are estimates.** Measured against the owner's real fleet on a Sunday evening, the
  simulated tunnel traffic was about five times the real one, so read the fleet tunnel bytes as an upper bound.
- **Phones are whole numbers.** The share of accounts with the phone open cannot be exact at every step (fleet runs
  2 of 5, 3 of 10 and 7 of 20 against a third), and at least one phone is always open. Data out is corrected for this
  in the summary; the memory steps are not, so they carry slightly uneven phone load.
- **Platform differences.** This runs on Windows; production runs in a Linux container. Both use workstation garbage
  collection. Compare slopes, not absolute sizes.
- **New accounts have factory agents off,** so their Director's triggers read is answered 404, as it is in production
  for a new account. The driver counts that as expected traffic, not a fault.
