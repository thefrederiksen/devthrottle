# Pull request 3871 round 3 review - Gateway license storage and usage ingest

Verdict: approve

Reviewed: the license files changed between 0aae5def5 (the head round 2 reviewed) and 067426456. The branch was rebased
in between, so the raw range also carries unrelated main commits; only `src/CcDirector.Gateway/Licenses`,
`DirectorHub.cs` and the license tests were judged. The branch is one commit behind origin/main, and that commit touches
none of these files.

## Verdict on each answer

**1. Director identifier case - accepted.** Owners are now held per tenant in a dictionary built with
`StringComparer.OrdinalIgnoreCase` (`LicenseUsageIngest.cs:80-81`), the comparer `PushedRepositoryStore` uses. Every
place the ingest keys a Director goes through `OwnersFor`: `RegisterConnection` (line 88), `UnregisterConnection`
(line 114) and `Ingest` (line 139), so there is no second, case-sensitive path. Connection identifiers stay ordinal,
which is right - they are SignalR-generated. The store keys a machine by machine and user name, not by Director
identifier, so a differently-cased reconnect does not split a machine's row there either (it only records the casing
last seen in `DirectorId`). `Ingest_AReconnectWithTheDirectorIdInAnotherCase_SupersedesTheOldConnection` covers the
reconnect in a third casing for both the refusal and the acceptance.

**2. The in-memory lock no longer spans the database write - accepted, with the property it now guarantees stated
plainly.** Lock order is always write gate, then ownership monitor, and only `Ingest` takes the write gate;
`RegisterConnection` and `UnregisterConnection` take only the ownership monitor and hold it over field updates (and a
log line), never over the database. Nothing inside the write gate calls back into register or unregister, and
`ApplyReport` takes no ingest lock. So Hello and disconnect can never hold or wait for the write gate, and there is no
lock cycle - no deadlock.

Interleavings walked (A = the old connection, B = the reconnect):

- A's report waiting at the gate, B registers, A gets the gate: the check sees B active, A is refused. Closed.
- A's report checked and writing, B registers: B's Hello completes at once (the point of the change). A's write lands.
  A's sequence is not recorded because the epoch moved, so B's fresh baseline survives. B's first report blocks on the
  gate until A's write is finished, then is checked against B and lands on top. Newer facts always land last.
- A's report checked and writing, A disconnects: A's write lands; ownership is already cleared; the next Hello starts
  a new epoch and baseline. Harmless.
- A's report checked and writing, B registers and A then sends another report: it waits for the gate, then is refused.
- Two reports from one connection racing: the sequence check and the sequence record are both inside the gate, so the
  higher one wins and the lower one is refused as stale, in either order.
- A database failure inside the write: the `lock` statements release both locks, the sequence is not recorded, and
  the hub contains the exception.

The round 1 finding was literally "a superseded connection's in-flight report must never be written after a newer
connection registered". Under this design a report that passed its ownership check BEFORE the reconnect can still be
written after the reconnect's Hello returns - that is the cost of not making Hello wait on the database. What is still
closed, and what actually mattered in round 1, is that old facts can never overwrite newer ones: no report from the new
connection can be checked or written until the old in-flight write is finished, and a report from the old connection
that had not yet been checked is refused. The only visible difference is that, if the new connection sends nothing,
the stored row is the old connection's last report - which is the same state as if that write had finished a moment
before the Hello, and it is the Director's real reading. I accept that trade. The class comment states it correctly
(lines 34-42); the hub comment does not (finding 1).

**3. A deterministic interleaving test - accepted.** `Ingest_AReconnectDuringAWrite_DoesNotWait_AndTheNewReportLandsAfterTheOldOne`
orders every step with signals: the old report signals from inside its write and parks there; Hello must complete
while it is parked; the new report signals at the gate; only then is the old write released. On correct code no
assertion depends on timing - the two-second window can only ever end with the new report still blocked. It fails on
each regression that matters: with the ownership monitor held over the write again (the round 2 design), Hello cannot
complete within ten seconds; with the gate removed, the new report finishes inside the window AND the stored number
ends as the old report's 10 instead of 20. I did not repeat the author's mutation run (tracked files are not to be
edited); this is from reading the code against the test.

## New findings

1. **Low - `src/CcDirector.Gateway/Streaming/DirectorHub.cs:394`.** The comment still says the license ingest decides
   ownership "under the same lock that applies a report". That was the round 2 design and is now false: Hello takes the
   ownership lock, and reports apply under the separate write gate, which Hello never takes. A reader trusting this
   comment would conclude Hello waits for a database write. Reword it to match the class comment.

Nothing else in the license change is broken that I could find.

## Verification

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` at 067426456 - passed, 45
  passed, 0 failed, 0 skipped.
