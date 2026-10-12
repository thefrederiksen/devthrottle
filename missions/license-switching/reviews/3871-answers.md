# Answers to the pull request 3871 review

**1. Must fix - the reconnect race between the ownership check and the write. Accepted and fixed.**
The hub no longer samples `IsActiveConnection` before the ingest runs. `LicenseUsageIngest` now owns which connection
may report for each Director: Hello registers the connection with it (`RegisterConnection`, the epoch rule copied from
`PushedRepositoryStore`, so a superseded connection saying Hello again never reclaims ownership), and disconnect
unregisters it. `Ingest` checks ownership, checks the sequence and applies the report while holding that Director's
lock, and `RegisterConnection` takes the same lock - so a reconnect cannot land between the check and the write; it
waits for the report in flight, and the old connection is refused from then on.
Test: `Ingest_AReconnectDuringTheWrite_WaitsForIt_AndThenTheOldConnectionIsRefused` starts a reconnect from inside the
write, asserts it has not completed while the write holds the lock, then asserts the old connection's next report is
refused and the stored numbers are the first report's. Proven by mutation: `RegisterConnection` locking a different
object turns exactly that test red (1 failed, 13 passed); restored. Also added:
`Ingest_FromASupersededConnection_IsRefused_EvenAfterItSaysHelloAgain`.

**2. Should fix - the capability is offered when there is no ingest. Accepted and fixed.**
Making the constructor parameter required would touch every existing hub construction for a dependency most of them do
not exercise, so the offer follows the dependency instead: `CapabilitiesFor` leaves `PushLicenseUsage` out of
`HubMethods` when the hub has no ingest. A Director only sends when `GatewaySupports("PushLicenseUsage")` (contract
section 1), so it is never invited to send into a method that drops every report. Test:
`GatewayCapabilities_WithNoLicenseIngest_DoNotOfferPushLicenseUsage`.

**3. Should fix - the public reads have no logs. Accepted and fixed.**
`FindSwitch`, `ListOpenSwitches`, `ListLog` and `ListActiveBars` go through one helper that logs the entry, the result
(found or not, or the count) and a failure before rethrowing; `LicenseReadModel.Read` logs the same three.

# Answers to the re-review (`3871-re-review.md`)

**1. Director identifier case. Accepted and fixed.** Owners are now kept per tenant in a dictionary keyed with
`StringComparer.OrdinalIgnoreCase`, the comparer `PushedRepositoryStore` uses. Test:
`Ingest_AReconnectWithTheDirectorIdInAnotherCase_SupersedesTheOldConnection` (a reconnect as `DIR-1` refuses the old
`dir-1` connection and accepts the new one as `Dir-1`). Proven by mutation: the comparer set to `Ordinal` turns exactly
that test red (1 failed, 14 passed); restored.

**2. The in-memory lock held over the database write. Accepted and fixed.** Two locks per Director now:
- the ownership lock - taken by Hello, disconnect and the check, held only over a few fields, never over input or
  output;
- a write gate - taken only by reports, held over the ownership check and the database write.
How the ordering is kept: a report is checked against ownership inside the write gate, immediately before its write,
so a connection superseded before its turn is refused; a report that passed its check before a reconnect holds the gate
until its write is done, so the new connection's first report cannot even be checked until then, and the newer facts
always land last; the accepted sequence is recorded only if the connection generation (its epoch) is still the one the
check saw, so a write that straddled a reconnect cannot overwrite the new connection's fresh baseline. Hello and
disconnect therefore never wait for the database. A slow database delays only that Director's next report, which is
the one thing that must wait for it.

**3. A deterministic interleaving test. Accepted and fixed.** `Ingest_AReconnectDuringAWrite_DoesNotWait_AndTheNewReportLandsAfterTheOldOne`
lines every step up with a signal: the old report signals from inside its write and waits there; the reconnect's Hello
is then run and must complete while the write is held; the new report signals at the write gate; only then is the old
write released; and the stored numbers must be the new report's. The two-second wait in it never decides a pass on
correct code - it only gives a broken gate time to show itself. Proven by mutation: the write gate replaced by a fresh
object (no gate) turns exactly that test red (1 failed, 14 passed); restored. The earlier test, which relied on a
500-millisecond window, is removed.
