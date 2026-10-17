# Review of pull request 4113 - a refused report and a silent machine are visible machine statuses

Reviewed at the pull request head `0b1f49b33`, branch `license-refused-and-silent-machines`, merge base `08dec1140`.
Origin/main has since moved by two commits (`2065c9184`, a Gateway unit test change to the scripted terminal, and
`6c783e3d4`, the release notes correction). Neither touches `src/CcDirector.Gateway/Licenses`.

## Verdict

One finding that must change, two notes the Developer should read, and nothing else found within the scope below.

The change does what the brief asks in every case where one machine is reported by one Director for its whole life.
It does not survive the one way the Director behind a machine changes in ordinary operation: the key holder
handover that contract section 2.4 defines. Then the page shows one machine twice with contradicting statuses, a
refusal row stays open in the log forever, and a machine whose reports are being refused can also be written up as
silent, which the proof file says cannot happen.

## Finding 1 (must change): a refusal is keyed and cleared by Director identifier, but the machine's Director changes

**What the code does.**

- A refusal row is opened and counted by `LicenseStore.RecordRefusal` for one `directorId`
  (`LicenseStore.cs:880`), and it is ended only by `RecordAccepted` for that same `directorId` (`LicenseStore.cs:942`).
  Nothing else ever ends a refusal row.
- A machine row is keyed by machine name and operating system user, and every accepted report overwrites its
  `DirectorId` with the reporter's (`LicenseStore.cs:164`).
- The page matches a refusal to a machine by the machine row's CURRENT `DirectorId` (`LicenseDisplayFold.cs:409`,
  `LicenseSnapshotSource.cs:23`), the engine's `ReportsRefused` is set the same way (`LicenseSnapshotSource.cs:131`),
  and the silence check asks the same question (`LicenseSwitchService.cs:576`).
- A refusal whose Director is not the current `DirectorId` of any machine row becomes a stand-in machine row
  (`LicenseDisplayFold.cs:76` and `:463`), labelled with the machine name and the sentence "No report from it has
  been accepted yet".

**Why the Director behind a machine changes.** `LicenseKeyHolderLock` (contract section 2.4): several Directors run
on one machine as one operating system user, exactly one holds the key, and only the key holder reports
(`LicenseUsageReporter.cs:173-175`). When the holder exits, "the next Director to try takes it". Each Director build
has its own persistent identifier per executable path (`DirectorIdStore.cs`, whose own comment says operators
"routinely run multiple Director builds concurrently"). A reinstall or an upgrade that moves the executable also gives
the Director a new identifier. So the identifier a machine reports under changes whenever the key passes, and the
first thing anyone does with a Director whose reports are refused is stop it.

**What breaks.** Take the incident this piece is for: machine M, Director D1 holds the key, every report is refused
("an account held twice" - a fault in the shared config, so any holder is refused the same way).

1. D1 is stopped. D2 takes the key and reports. D2 is refused too: a second open refusal row, keyed D2, with
   `MachineId = Guid.Empty` (no machine row carries D2, `LicenseStore.cs:891`). The page now lists machine M twice:
   the real row says "Report refused" (D1's row still matches it), and a stand-in row for D2 says "Report refused"
   and "No report from it has been accepted yet", which is false of that machine.
2. The config is fixed. D2's report is accepted: the machine row's `DirectorId` becomes D2, D2's refusal row ends,
   one "Reports accepted" row is written. D1's refusal row is never ended: D1 will never report again. From now on,
   forever, the page lists machine M twice - "Reporting" and a stand-in "Report refused" whose detail reads "Its
   Director is still sending reports: N reports refused since ..., the latest 3 days ago" - and the Licenses log
   carries an open "Refused" row for it on every read (`ListLog` returns every open row however old). There is no
   operation that clears it. Tone bad, on the page, for good.
3. The other order is worse for the brief's own rule. D1 was accepted (machine row `DirectorId = D1`), D1 is
   stopped, D2 takes the key and is refused. `RefusalOf(D1)` is null, so the real row is judged as a plain machine:
   "Reporting" for six minutes, then "Not reporting since ...", and after ten minutes `WatchSilence` writes a
   "machine-silent" row for it, because the refusal check at `LicenseSwitchService.cs:576` asks about D1. The
   proof file says "A machine whose reports are refused is never also called silent"; here it is called both, in
   two rows on the page and two open rows in the log, while its Director is alive and sending every two minutes.

None of this sends a wrong switch: the real row is judged by its own `DirectorId`, so the engine's decision is the
one the pre-existing code would make. The harm is the page and the log, which this piece exists to make truthful.

**Why it must change rather than be accepted.** The pull request's own text says the refusal "is about the MACHINE".
The thing that is about the machine must be keyed, matched and cleared by the machine, which is machine name and
operating system user. The refused report names both in every case but two (`no-machine-name`, `name-too-long`),
and `RecordRefusal` already resolves a machine row for the report; the end condition that is missing is "a report
from this machine and user was accepted", however the Director is called. The Director-only key is right only for
the two cases where the report names no machine. The in-tree tests never change the Director identifier
(`LicenseRefusedAndSilentMachineTests` registers `dir-1` only), which is why none of them sees it. A two-Director
test on one machine and user - D1 refused, D2 accepted - is the test that would.

## Note 1 (no change required, read it): "the same refusal" is judged on the sentence, and the sentence carries data

`RecordRefusal` counts a report on the open row only when the code AND the message are equal. Five of the fourteen
messages embed something from the report: the duplicated email (the first duplicated group in holdings order), the
list of in-use emails, and the email of the first unreadable holding (`LicenseUsageIngest.cs:279`). Holdings come in
the order the license tool's `list` answer gives them, active first. If that order ever varies between two reports
of the same fault, each flip ends a row and opens one - a row per report, the thing the brief forbids. I could not
show the order varying (it is one JSON answer from our own tool), so this is a dependency to know about, not a
defect. It also bounds the answer to "can a stream of malformed reports grow the log": in memory, nothing per
report; in the log, one row per change of (code, sentence), which a bound Director can force to one row per report
by varying the content - 720 rows a day, from an authenticated member of the tenant. That is the brief's own rule
("another if the reason changes") and I do not ask for a change.

## Note 2 (no change required): what the ingest now does inside the write gate

Before this change a malformed report was refused before the per-Director write gate; now it waits for the gate
and writes one row inside it. The gate is per Director (`owner.WriteGate`), so a refused report serialises only
with that Director's own reports, exactly as an accepted one does. Hello takes `lock (owner)`, which is never held
across a database write, so a refusal cannot hold up Hello or another Director's accepted write. `RecordAccepted`
adds one query per accepted report inside the gate (open refusals for this Director); cheap, and it is what makes
the "accepted again" row exact. If `RecordAccepted` throws after `ApplyReport` succeeded, the hub contains it, the
sequence is not advanced, and `_history.Record` is skipped for that one report; the next report is taken normally.
Not a defect, just the shape.

## What I checked and found sound

- **Lock order and concurrency**: above. The ownership and sequence checks are unchanged, and the shape check moving
  after them is correct - a superseded connection can no longer mark a machine refused.
- **One log entry per change across Gateway restarts and reconnects**: the state is the open row in the database,
  keyed by tenant and Director; a restart or reconnect finds it and counts on it. Holds, with the exception in
  Finding 1.
- **Two operating system users on one machine**: two machine rows, two key holders, two Director identifiers; the
  cases never touch. Holds.
- **Tenant isolation**: every read and write goes through `_db.CreateContext(tenant)`, whose deny-by-default query
  filter scopes `LicenseSwitchLog` by `TenantId`; `TenantId = ctx.ActiveTenant` is set on every new row. The
  isolation test uses the SAME Director identifier in two tenants and proves neither the page, the health list, nor
  the "accepted again" row leaks. Holds.
- **The refused machine is never sent a switch**: `SplitMachines` skips it before staleness, and all three callers
  (the minute decision, Switch now, and the machine list) go through `SplitMachines`. The open-switch re-ask in
  `PlanTick` already requires a report newer than the last send, which a refused machine never produces. Holds, for
  the machine row whose current Director is the refused one (see Finding 1, step 3, for the window where it is not).
- **The client is dumb**: the Cockpit adds one line that draws `statusDetail` when it is not null and decides
  nothing; the new status, text, tone and sentence are all stamped by `LicenseDisplayFold`. The mobile app has no
  Licenses page. `LicenseSignInFold` throws on an unknown machine row and refuses Add a spare on a stand-in. Holds.
- **No real machine name, email or account in the diff**: the only addresses are `alice@example.com` and
  `account-a@example.com`; the machines are `machine-a`, `the-mac`, `the-windows-machine`; the Directors `dir-1`.
  Clean.
- **Column limits**: every new row's reason code is at most 20 characters (column 32), the refusal message is capped
  at 500 (`MaxMessageLength`), names are only stored when at most 128. Holds.
- **The silence check**: runs in the existing minute check, writes one row per silence, closes on "a report accepted
  after the row was written", never on a machine whose current Director is refused. The one race (a report landing
  during the tick) costs one spurious silent-and-reporting-again pair for a machine that really was silent ten
  minutes; acceptable.

## Scope

**Read**: the full diff of pull request 4113 (18 files), the whole of `LicenseUsageIngest.Ingest`, `ApplyReport`,
`ListLog`, `ApplySwitchReadBack` and the new store methods, `LicenseSwitchService.PlanTick` and `WatchSilence`, the
engine's `SplitMachines` and its callers, `DirectorHub.PushLicenseUsage`, the tenant query filter in
`GatewayDbContext`, the Director side (`LicenseUsageReporter`, `LicenseKeyHolderLock`, `DirectorIdStore`,
`LicenseCommandLine.ReadListedAccounts`), the new test class in full, the Developer's brief and proof file, and the
API document diff.

**Ran**: nothing. I built nothing and executed no test, in this worktree or out of it. Finding 1 is reasoned from
the code paths cited line by line; the two-Director test described under it is how to confirm it in-tree.

**Could not reach**: the hosted Gateway, a live Director, and the claim in the proof file that the six failing
Gateway unit tests also fail on origin/main - out of my scope and not verified here.
