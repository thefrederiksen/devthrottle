using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE USAGE REPORT GATE (License Switching, contract sections 2.1 and 5.2): only the active connection, only a higher
/// sequence from that connection, never a malformed report; a skewed clock's ages are judged from the receive time.
/// </summary>
public sealed class LicenseUsageIngestTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseUsageIngest _ingest;

    private static readonly TenantId Tenant = new("acct-a");

    public LicenseUsageIngestTests()
    {
        _store = new LicenseStore(_harness.Open());
        _ingest = new LicenseUsageIngest(_store);
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-1");
    }

    public void Dispose() => _harness.Dispose();

    private LicenseIngestOutcome Push(long seq, double fiveHour, string conn = "conn-1", LicenseUsageIngest? ingest = null)
        => (ingest ?? _ingest).Ingest(Tenant, "dir-1", conn, seq, Report("m1", Holding(A, true, fiveHour)), T0);

    private double? StoredFiveHour() => new LicenseReadModel(_store).Read(Tenant, T0).Accounts.SingleOrDefault()?.Freshest?.FiveHourUtilization;

    [Fact]
    public void Ingest_ActiveConnectionHigherSequence_IsAccepted()
    {
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(1, 10));
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(2, 20));
        Assert.Equal(20, StoredFiveHour());
    }

    [Fact]
    public void Ingest_NotTheActiveConnection_IsRefused_AndStoresNothing()
    {
        Assert.Equal(LicenseIngestOutcome.NotActiveConnection, Push(1, 10, conn: "conn-never-said-hello"));
        Assert.Null(StoredFiveHour());
    }

    [Fact]
    public void Ingest_FromASupersededConnection_IsRefused_EvenAfterItSaysHelloAgain()
    {
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-2");
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-1");   // the old connection's periodic Hello

        Assert.Equal(LicenseIngestOutcome.NotActiveConnection, Push(100, 99, conn: "conn-1"));
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(1, 12, conn: "conn-2"));
        Assert.Equal(12, StoredFiveHour());
    }

    [Fact]
    public async Task Ingest_AReconnectDuringAWrite_DoesNotWait_AndTheNewReportLandsAfterTheOldOne()
    {
        // Pull request 3871 reviews: Hello must not wait for a database write, and a report checked before a reconnect
        // must never land on top of the new connection's first report. Every step is lined up by a signal, not a delay.
        using var oldInsideWrite = new ManualResetEventSlim();
        using var releaseOldWrite = new ManualResetEventSlim();
        using var newAtWriteGate = new ManualResetEventSlim();
        using var newInsideWrite = new ManualResetEventSlim();
        var ingest = new LicenseUsageIngest(_store,
            waitingForWriteGate: conn => { if (conn == "conn-2") newAtWriteGate.Set(); },
            insideWrite: conn =>
            {
                if (conn == "conn-2") newInsideWrite.Set();
                if (conn == "conn-1") { oldInsideWrite.Set(); releaseOldWrite.Wait(TimeSpan.FromSeconds(30)); }
            });
        ingest.RegisterConnection(Tenant, "dir-1", "conn-1");

        var oldReport = Task.Run(() => Push(1, 10, "conn-1", ingest));
        Assert.True(oldInsideWrite.Wait(TimeSpan.FromSeconds(10)), "the old report never reached its write");

        // The old report is checked and inside its write. A reconnect's Hello completes anyway.
        var hello = Task.Run(() => ingest.RegisterConnection(Tenant, "dir-1", "conn-2"));
        await hello.WaitAsync(TimeSpan.FromSeconds(10));   // a watchdog: a Hello that waited on the write would hang here

        // The new connection's first report reaches the write gate while the old write still holds it.
        var newReport = Task.Run(() => Push(1, 20, "conn-2", ingest));
        Assert.True(newAtWriteGate.Wait(TimeSpan.FromSeconds(10)), "the new report never reached the write gate");
        // With the gate held, the new report cannot be inside its write, so this probe is false on correct code whatever
        // the timing; its bound only gives a missing gate the chance to show itself.
        var newWroteWhileOldHeldTheGate = newInsideWrite.Wait(TimeSpan.FromMilliseconds(500));
        releaseOldWrite.Set();

        Assert.Equal(LicenseIngestOutcome.Accepted, await oldReport);
        Assert.Equal(LicenseIngestOutcome.Accepted, await newReport);
        Assert.False(newWroteWhileOldHeldTheGate);
        Assert.Equal(20, StoredFiveHour());
        Assert.Equal(LicenseIngestOutcome.NotActiveConnection, Push(2, 99, "conn-1", ingest));
    }

    [Fact]
    public void Ingest_AReconnectWithTheDirectorIdInAnotherCase_SupersedesTheOldConnection()
    {
        // Pull request 3871 re-review, finding 1: Director identifiers compare without regard to case, as in
        // PushedRepositoryStore, so a differently-cased reconnect takes the same Director over.
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(1, 10));
        _ingest.RegisterConnection(Tenant, "DIR-1", "conn-2");

        Assert.Equal(LicenseIngestOutcome.NotActiveConnection,
            _ingest.Ingest(Tenant, "dir-1", "conn-1", 2, Report("m1", Holding(A, true, 99)), T0));
        Assert.Equal(LicenseIngestOutcome.Accepted,
            _ingest.Ingest(Tenant, "Dir-1", "conn-2", 1, Report("m1", Holding(A, true, 30)), T0));
        Assert.Equal(30, StoredFiveHour());
    }

    [Fact]
    public void Ingest_SameOrLowerSequence_IsRefused_AndKeepsTheNewerNumbers()
    {
        Push(5, 50);

        Assert.Equal(LicenseIngestOutcome.StaleSequence, Push(5, 60));
        Assert.Equal(LicenseIngestOutcome.StaleSequence, Push(4, 40));
        Assert.Equal(50, StoredFiveHour());
    }

    [Fact]
    public void Ingest_ANewConnection_StartsAFreshSequenceBaseline()
    {
        Push(50, 50);
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-2");

        Assert.Equal(LicenseIngestOutcome.Accepted, Push(1, 15, conn: "conn-2"));
        Assert.Equal(15, StoredFiveHour());
    }

    [Theory]
    [InlineData("two-active")]
    [InlineData("held-twice")]
    [InlineData("no-machine")]
    [InlineData("no-email")]
    [InlineData("bad-switch-id")]
    public void Ingest_MalformedReport_IsRefused(string fault)
    {
        var report = Report("m1", Holding(A, true), Holding(B, false));
        switch (fault)
        {
            case "two-active": report.Holdings[1].IsActive = true; break;
            case "held-twice": report.Holdings[1].AccountEmail = "ACCOUNT-A@example.com"; break;
            case "no-machine": report.MachineName = " "; break;
            case "no-email": report.Holdings[1].AccountEmail = ""; break;
            case "bad-switch-id": report.SwitchInProgressId = "not-a-switch-id"; break;
        }

        Assert.Equal(LicenseIngestOutcome.Malformed, _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, report, T0));
        Assert.Null(StoredFiveHour());
    }

    // ---- a number or a read time that cannot be a reading ----

    public static TheoryData<string, double, double?, int> ReadingsThatCannotBe() => new()
    {
        // what, the 5-hour number, the extra usage number, seconds the read time is after the sent time
        { "not a number", double.NaN, null, 0 },
        { "infinite", double.PositiveInfinity, null, 0 },
        { "minus infinite", double.NegativeInfinity, null, 0 },
        { "the reviewer's -1e308", -1e308, null, 0 },
        { "the reviewer's 1e308", 1e308, null, 0 },
        { "just below nought", -0.001, null, 0 },
        { "just above the most", LicenseUsageIngest.MaxUtilization + 0.001, null, 0 },
        { "an extra usage number that is infinite", 10, double.PositiveInfinity, 0 },
        { "read three minutes after it was sent", 10, null, 180 },
        { "read one second more than two minutes after it was sent", 10, null, 121 },
    };

    [Theory]
    [MemberData(nameof(ReadingsThatCannotBe))]
    public void Ingest_AUsageNumberOrAReadTimeThatCannotBeAReading_IsRefused_AndStoresNothing(string what, double fiveHour, double? extraUsage, int readSecondsAfterSent)
    {
        var holding = Holding(A, true, fiveHour, readAt: T0.AddSeconds(readSecondsAfterSent));
        holding.ExtraUsage = new CcDirector.Gateway.Contracts.LicenseExtraUsage { IsEnabled = true, Utilization = extraUsage };

        Assert.True(LicenseIngestOutcome.Malformed == _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", holding, Holding(B, false)), T0), what);
        Assert.Null(StoredFiveHour());
    }

    [Fact]
    public void Ingest_TheSevenDayNumberIsHeldToTheSameRule()
    {
        Assert.Equal(LicenseIngestOutcome.Malformed,
            _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", Holding(A, true, 10, sevenDay: double.PositiveInfinity)), T0));
        Assert.Null(StoredFiveHour());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(103.5)]                            // the provider can pass 100 a little
    [InlineData(LicenseUsageIngest.MaxUtilization)]
    public void Ingest_AUsageNumberAtEitherEdgeOfWhatIsAllowed_IsAccepted(double fiveHour)
    {
        Assert.Equal(LicenseIngestOutcome.Accepted, _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", Holding(A, true, fiveHour)), T0));
        Assert.Equal(fiveHour, StoredFiveHour());
    }

    [Fact]
    public void Ingest_AReadTimeIsJudgedAgainstTheReportsOwnSentTime_AtEachEdge()
    {
        // Read at the very moment it was sent, and read exactly the longest allowed before it: both are readings.
        // (The other edge, a read time AHEAD of the sent time, is the theory below.)
        Assert.Equal(LicenseIngestOutcome.Accepted, _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", Holding(A, true, 10, readAt: T0)), T0));
        Assert.Equal(LicenseIngestOutcome.Accepted,
            _ingest.Ingest(Tenant, "dir-1", "conn-1", 2, Report("m1", Holding(A, true, 11, readAt: T0 - LicenseUsageIngest.MaxReadingAge)), T0));
        // One second older than that is not.
        Assert.Equal(LicenseIngestOutcome.Malformed,
            _ingest.Ingest(Tenant, "dir-1", "conn-1", 3, Report("m1", Holding(A, true, 12, readAt: T0 - LicenseUsageIngest.MaxReadingAge - TimeSpan.FromSeconds(1))), T0));
        Assert.Equal(11, StoredFiveHour());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(90)]      // a clock stepped back a minute and a half by time synchronisation
    [InlineData(120)]     // exactly the two minutes allowed
    public void Ingest_AReadTimeALittleAheadOfTheSentTime_IsAccepted_BecauseAClockSteppedBackIsNotADefect(int readSecondsAfterSent)
    {
        // The spare was read before the clock was stepped back, so its kept read time is ahead of the report.
        var report = Report("m1", Holding(A, true, 10), Holding(B, false, 4, readAt: T0.AddSeconds(readSecondsAfterSent)));

        Assert.Equal(LicenseIngestOutcome.Accepted, _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, report, T0));
        // The machine is heard from, and stays switchable: it is not stale and its spare is listed.
        var machine = new LicenseReadModel(_store).Read(Tenant, T0).Machines.Single();
        Assert.False(machine.IsStale);
        Assert.Equal(new[] { B }, machine.Spares);
    }

    [Fact]
    public void Ingest_AReadTimeALittleAheadOfTheSentTime_IsKeptInTheHistoryAtNow_NeverLaterThanTheGatewaysClock()
    {
        var history = new LicenseUsageHistory();
        var ingest = new LicenseUsageIngest(_store, history);
        ingest.RegisterConnection(Tenant, "dir-1", "conn-1");

        Assert.Equal(LicenseIngestOutcome.Accepted,
            ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", Holding(A, true, 10, readAt: T0.AddSeconds(90))), T0));

        Assert.Equal(T0, history.Read(Tenant, T0).Single().FiveHour.Single().AtUtc);
        Assert.Empty(history.Read(Tenant, T0 + LicenseUsageHistory.Kept + TimeSpan.FromSeconds(1))); // and it ages out
    }

    [Fact]
    public void Ingest_AMachineWhoseWholeClockIsWrong_IsStillAccepted_BecauseItsReadTimeIsNotAfterItsOwnSentTime()
    {
        // A clock 400 days fast moves the sent time and the read time together. That is a wrong clock, which the
        // skew correction handles - not a defect, and not refused.
        var fast = T0.AddDays(400);

        Assert.Equal(LicenseIngestOutcome.Accepted,
            _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", fast, Holding(A, true, 10, readAt: fast.AddSeconds(-2))), T0));
        Assert.False(new LicenseReadModel(_store).Read(Tenant, T0.AddSeconds(1)).Accounts.Single().IsStale);
        Assert.True(new LicenseReadModel(_store).Read(Tenant, T0.AddMinutes(7)).Accounts.Single().IsStale); // and it does go stale
    }

    [Theory]
    [InlineData(false)]   // the read time in the last minutes of the year 9999, sent on time
    [InlineData(true)]    // the same with the whole clock there: the sum of the read time and the correction
    public void Ingest_AReadTimeAtTheEdgeOfTheCalendar_IsRefusedOrCorrected_AndNeverThrows(bool wholeClock)
    {
        var edge = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).AddMinutes(-3);
        var sent = wholeClock ? edge.AddMinutes(1) : T0;

        var outcome = _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", sent, Holding(A, true, 50, readAt: edge)), T0);

        Assert.Equal(wholeClock ? LicenseIngestOutcome.Accepted : LicenseIngestOutcome.Malformed, outcome);
        if (wholeClock)
            Assert.Equal(T0.AddMinutes(-1), new LicenseReadModel(_store).Read(Tenant, T0).Accounts.Single().Freshest!.JudgedReadAtUtc);
    }

    [Fact]
    public void Ingest_AReadTimeAtTheStartOfTheCalendar_IsRefused_AndNeverThrows()
    {
        var start = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc).AddMinutes(3);

        // Sent on time, and sent from a clock that is itself years slow: the sum would pass the start of the calendar.
        Assert.Equal(LicenseIngestOutcome.Malformed, _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("m1", Holding(A, true, 50, readAt: start)), T0));
        Assert.Equal(LicenseIngestOutcome.Malformed,
            _ingest.Ingest(Tenant, "dir-1", "conn-1", 2, Report("m1", T0.AddYears(-20), Holding(A, true, 50, readAt: start)), T0));
        Assert.Null(StoredFiveHour());
    }

    [Fact]
    public void Ingest_NullReport_IsRefused()
        => Assert.Equal(LicenseIngestOutcome.Malformed, _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, null, T0));

    [Fact]
    public void ReadAtShift_WithinTwoMinutes_IsZero()
        => Assert.Equal(TimeSpan.Zero, _ingest.ReadAtShift(Tenant, Report("m1", T0.AddMinutes(-2)), T0));

    [Fact]
    public void ReadAtShift_SkewedClock_JudgesAgeFromTheReceiveTime()
    {
        // The machine's clock is an hour behind: it says it read at 02:10 and sent at 02:12; the Gateway got it at 03:12.
        var report = Report("m1", T0.AddHours(-1), Holding(A, true, readAt: T0.AddHours(-1).AddMinutes(-2)));

        _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, report, T0);

        var reading = new LicenseReadModel(_store).Read(Tenant, T0).Accounts[0];
        Assert.Equal(T0.AddMinutes(-2), reading.Freshest!.JudgedReadAtUtc);
        Assert.False(reading.IsStale);
    }
}
