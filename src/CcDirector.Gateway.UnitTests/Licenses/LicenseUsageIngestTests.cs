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
        var ingest = new LicenseUsageIngest(_store,
            waitingForWriteGate: conn => { if (conn == "conn-2") newAtWriteGate.Set(); },
            insideWrite: conn => { if (conn == "conn-1") { oldInsideWrite.Set(); releaseOldWrite.Wait(TimeSpan.FromSeconds(30)); } });
        ingest.RegisterConnection(Tenant, "dir-1", "conn-1");

        var oldReport = Task.Run(() => Push(1, 10, "conn-1", ingest));
        Assert.True(oldInsideWrite.Wait(TimeSpan.FromSeconds(10)), "the old report never reached its write");

        // The old report is checked and inside its write. A reconnect's Hello completes anyway.
        var hello = Task.Run(() => ingest.RegisterConnection(Tenant, "dir-1", "conn-2"));
        Assert.Same(hello, await Task.WhenAny(hello, Task.Delay(TimeSpan.FromSeconds(10))));

        // The new connection's first report reaches the write gate while the old write still holds it.
        var newReport = Task.Run(() => Push(1, 20, "conn-2", ingest));
        Assert.True(newAtWriteGate.Wait(TimeSpan.FromSeconds(10)), "the new report never reached the write gate");
        var newFinishedBeforeOld = await Task.WhenAny(newReport, Task.Delay(TimeSpan.FromSeconds(2))) == newReport;
        releaseOldWrite.Set();

        Assert.Equal(LicenseIngestOutcome.Accepted, await oldReport);
        Assert.Equal(LicenseIngestOutcome.Accepted, await newReport);
        Assert.False(newFinishedBeforeOld);
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
