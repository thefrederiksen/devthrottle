using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>License Switching contract 2.3 and 2.4: when the usage report is sent, what it carries, and that only the
/// key holder sends it.</summary>
public sealed class LicenseUsageReporterTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLicenseCommandLine _tool;
    private readonly FakeReportSink _sink = new();
    private readonly LicenseKeyHolderLock _holder;
    private readonly LicenseUsageReporter _reporter;

    public LicenseUsageReporterTests()
    {
        _tool = new FakeLicenseCommandLine { UtcNow = () => _clock.Now }
            .Hold("account-a@example.com", active: true)
            .Hold("account-b@example.com");
        _tool.FiveHour["account-a@example.com"] = 96;
        _tool.FiveHour["account-b@example.com"] = 4;
        _holder = new LicenseKeyHolderLock(_folder.Path);
        _reporter = new LicenseUsageReporter(_tool, _holder, _clock.UtcNow, "the-windows-machine", "owner");
        _reporter.AttachSink(_sink);
    }

    public void Dispose()
    {
        _holder.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task TickAsync_FirstTick_TakesTheLockAndSendsACompleteConnectedReport()
    {
        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.NotNull(report);
        Assert.True(_holder.IsHeld);
        Assert.Equal(LicenseReportTriggers.Connected, report!.Trigger);
        Assert.Equal("the-windows-machine", report.MachineName);
        Assert.Equal("owner", report.OsUser);
        Assert.Equal(new[] { "account-a@example.com", "account-b@example.com" }, report.Holdings.Select(h => h.AccountEmail));
        Assert.True(report.Holdings[0].IsActive);
        Assert.Equal(96, report.Holdings[0].FiveHour!.Utilization);
        Assert.Equal(4, report.Holdings[1].FiveHour!.Utilization);
        Assert.Same(report, _sink.Sent.Single());
    }

    [Fact]
    public async Task TickAsync_AnotherDirectorHoldsTheLock_SendsNothing()
    {
        using var other = new LicenseKeyHolderLock(_folder.Path);
        Assert.True(other.TryAcquire());

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Null(report);
        Assert.False(_holder.IsHeld);
        Assert.Empty(_sink.Sent);
        Assert.Empty(_tool.UsageCalls);
    }

    [Fact]
    public async Task TickAsync_HolderExits_NextDirectorTakesOverAndSendsConnected()
    {
        var other = new LicenseKeyHolderLock(_folder.Path);
        Assert.True(other.TryAcquire());
        Assert.Null(await _reporter.TickAsync(CancellationToken.None));

        other.Dispose();
        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Connected, report!.Trigger);
    }

    [Fact]
    public async Task TickAsync_GatewayCannotTakeIt_SendsNothingAndReadsNothing()
    {
        _sink.CanSend = false;

        Assert.Null(await _reporter.TickAsync(CancellationToken.None));

        Assert.Empty(_tool.UsageCalls);
        Assert.Equal(0, _tool.ListCalls);
    }

    [Fact]
    public async Task TickAsync_IntervalElapsed_SendsIntervalReadingOnlyTheActive()
    {
        await _reporter.TickAsync(CancellationToken.None);
        _clock.Advance(LicenseContract.ReportInterval);

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Interval, report!.Trigger);
        Assert.Equal(new[] { false, true }, _tool.UsageCalls);   // first report read all, this one the active only
        Assert.Equal(2, report.Holdings.Count);                  // still complete
    }

    [Fact]
    public async Task TickAsync_SpareNotDue_CarriesTheSparesLastNumbersWithTheirOwnReadTime()
    {
        await _reporter.TickAsync(CancellationToken.None);
        var firstRead = _clock.Now;
        _tool.FiveHour["account-b@example.com"] = 50;   // the provider would now say 50, but the spare is not due
        _clock.Advance(LicenseContract.ReportInterval);

        var report = await _reporter.TickAsync(CancellationToken.None);

        var spare = report!.Holdings.Single(h => !h.IsActive);
        Assert.Equal(4, spare.FiveHour!.Utilization);
        Assert.Equal(firstRead, spare.ReadAtUtc);
        Assert.Equal(_clock.Now, report.Holdings.Single(h => h.IsActive).ReadAtUtc);
    }

    [Fact]
    public async Task TickAsync_SpareReadIntervalElapsed_ReadsEverySpareAgain()
    {
        await _reporter.TickAsync(CancellationToken.None);
        _tool.FiveHour["account-b@example.com"] = 50;
        _clock.Advance(LicenseContract.SpareReadInterval);

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(new[] { false, false }, _tool.UsageCalls);
        Assert.Equal(50, report!.Holdings.Single(h => !h.IsActive).FiveHour!.Utilization);
    }

    [Fact]
    public async Task TickAsync_ReadFails_KeepsPreviousNumbersAndOldReadTimeAndSetsTheError()
    {
        await _reporter.TickAsync(CancellationToken.None);
        var firstRead = _clock.Now;
        _tool.ReadErrors["account-a@example.com"] = LicenseReadErrors.Network;
        _clock.Advance(LicenseContract.ReportInterval);

        var report = await _reporter.TickAsync(CancellationToken.None);

        var active = report!.Holdings.Single(h => h.IsActive);
        Assert.Equal(96, active.FiveHour!.Utilization);
        Assert.Equal(firstRead, active.ReadAtUtc);
        Assert.Equal(LicenseReadErrors.Network, active.ReadError);
        Assert.Equal(LicenseUsageReporter.ReadErrorSentence(LicenseReadErrors.Network), active.ReadErrorDetail);
        Assert.DoesNotContain(FakeLicenseCommandLine.ToolTextMarker, active.ReadErrorDetail);
    }

    [Fact]
    public async Task TickAsync_SignInExpiryRead_ReportCarriesItAndKeepsItThroughAFailedRead()
    {
        var expires = _clock.Now.AddDays(29);
        _tool.SignInExpires["account-b@example.com"] = expires;
        var first = await _reporter.TickAsync(CancellationToken.None);
        Assert.Equal(expires, first!.Holdings.Single(h => !h.IsActive).SignInExpiresAtUtc);
        Assert.Null(first.Holdings.Single(h => !h.IsActive).SignedInAtUtc);

        _tool.SignInExpires.Remove("account-b@example.com");
        _tool.ReadErrors["account-b@example.com"] = LicenseReadErrors.Network;
        _clock.Advance(LicenseContract.SpareReadInterval);
        var second = await _reporter.TickAsync(CancellationToken.None);

        var spare = second!.Holdings.Single(h => !h.IsActive);
        Assert.Equal(LicenseReadErrors.Network, spare.ReadError);
        Assert.Equal(expires, spare.SignInExpiresAtUtc);
    }

    [Fact]
    public async Task TickAsync_NeverReadAndReadFails_NumbersAreUnknownNotZero()
    {
        _tool.ReadErrors["account-b@example.com"] = LicenseReadErrors.Unauthorized;

        var report = await _reporter.TickAsync(CancellationToken.None);

        var spare = report!.Holdings.Single(h => !h.IsActive);
        Assert.Null(spare.FiveHour);
        Assert.Null(spare.ReadAtUtc);
        Assert.Equal(LicenseReadErrors.Unauthorized, spare.ReadError);
        Assert.DoesNotContain(FakeLicenseCommandLine.ToolTextMarker, spare.ReadErrorDetail);
        Assert.Equal(LicenseReadErrors.Unauthorized, _reporter.LastReadErrorFor("Account-B@example.com"));
    }

    [Fact]
    public async Task TickAsync_NothingChangedBeforeTheInterval_SendsNothing()
    {
        await _reporter.TickAsync(CancellationToken.None);
        _clock.Advance(LicenseUsageReporter.ChangeCheckInterval);

        Assert.Null(await _reporter.TickAsync(CancellationToken.None));
        Assert.Single(_sink.Sent);
    }

    [Fact]
    public async Task TickAsync_SpareAddedByHand_SendsChangedAtOnce()
    {
        await _reporter.TickAsync(CancellationToken.None);
        _tool.Hold("account-c@example.com");
        _clock.Advance(LicenseUsageReporter.ChangeCheckInterval);

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Changed, report!.Trigger);
        Assert.Contains(report.Holdings, h => h.AccountEmail == "account-c@example.com");
    }

    [Fact]
    public async Task TickAsync_ActiveChangedByHand_SendsChangedAtOnce()
    {
        await _reporter.TickAsync(CancellationToken.None);
        _tool.Held[0] = ("account-a@example.com", false);
        _tool.Held[1] = ("account-b@example.com", true);
        _clock.Advance(LicenseUsageReporter.ChangeCheckInterval);

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Changed, report!.Trigger);
        Assert.Equal("account-b@example.com", report.Holdings[0].AccountEmail);
        Assert.True(report.Holdings[0].IsActive);
    }

    [Fact]
    public async Task TickAsync_SpareRemoved_ReportNoLongerCarriesIt()
    {
        await _reporter.TickAsync(CancellationToken.None);
        _tool.Held.RemoveAt(1);
        _clock.Advance(LicenseUsageReporter.ChangeCheckInterval);

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Changed, report!.Trigger);
        Assert.Single(report.Holdings);
        Assert.Null(_reporter.LastReadErrorFor("account-b@example.com"));
    }

    [Fact]
    public async Task TickAsync_AfterSwitchRequested_SendsAfterSwitchBeforeTheInterval()
    {
        await _reporter.TickAsync(CancellationToken.None);
        _reporter.RequestReport(LicenseReportTriggers.AfterSwitch);

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.AfterSwitch, report!.Trigger);
    }

    [Fact]
    public async Task TickAsync_SwitchRunning_ReportCarriesItsId()
    {
        _reporter.SwitchInProgressId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal("6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40", report!.SwitchInProgressId);
    }

    [Fact]
    public async Task TickAsync_ToolGivesNoAnswer_SendsNoPartialReport()
    {
        _tool.FailWith = "cc-devthrottle is not installed";

        Assert.Null(await _reporter.TickAsync(CancellationToken.None));
        Assert.Empty(_sink.Sent);

        _tool.FailWith = null;
        var report = await _reporter.TickAsync(CancellationToken.None);
        Assert.Equal(LicenseReportTriggers.Connected, report!.Trigger);
    }

    // ---- a hand sign-in the key store healed (contract 2.6) ----

    private static LicenseHandSwitch Healed(string email) => new() { AccountEmail = email, HealedAtUtc = new DateTime(2026, 10, 11, 3, 1, 0, DateTimeKind.Utc) };

    private void SignedInByHand(string email)
    {
        // What the key store leaves after it healed: the account active and held once, and the heal in the answer.
        _tool.Held.Clear();
        _tool.Hold(email, active: true);
        _tool.Healed.Add(Healed(email));
    }

    [Fact]
    public async Task TickAsync_TheStoreHealedAHandSignIn_TheNextReportSaysSoOnceAndHoldsTheAccountOnce()
    {
        // The report after the heal is sent at once ("changed"), holds the account once, as active, and says the
        // machine was switched by hand; the report after it no longer says so.
        await _reporter.TickAsync(CancellationToken.None);
        SignedInByHand("account-b@example.com");
        _clock.Advance(TimeSpan.FromSeconds(30));

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Changed, report!.Trigger);
        Assert.Equal(new[] { ("account-b@example.com", true) }, report.Holdings.Select(h => (h.AccountEmail, h.IsActive)));
        Assert.Equal("account-b@example.com", report.SwitchedByHand!.AccountEmail);

        _clock.Advance(LicenseContract.ReportInterval);
        var next = await _reporter.TickAsync(CancellationToken.None);
        Assert.Null(next!.SwitchedByHand);
        Assert.Single(_sink.Sent, r => r.SwitchedByHand is not null);
    }

    [Fact]
    public async Task TickAsync_ASendThatFails_KeepsTheHandSwitchForTheNextReport()
    {
        await _reporter.TickAsync(CancellationToken.None);
        SignedInByHand("account-b@example.com");
        var failing = new ThrowingSink();
        _reporter.AttachSink(failing);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<IOException>(() => _reporter.TickAsync(CancellationToken.None));

        _reporter.AttachSink(_sink);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal("account-b@example.com", report!.SwitchedByHand!.AccountEmail);
    }

    [Fact]
    public async Task TickAsync_TheMachineMovedOnBeforeTheReport_TheHandSwitchIsDroppedNotSentAsAMalformedReport()
    {
        // A switch made another account active before the report went: naming the hand-signed account would make the
        // whole report malformed at the Gateway, so the sentence is dropped and the report goes without it.
        await _reporter.TickAsync(CancellationToken.None);
        _tool.Healed.Add(Healed("account-b@example.com"));   // healed, but account-a is active again by the time it is read
        _clock.Advance(TimeSpan.FromSeconds(30));

        var report = await _reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Changed, report!.Trigger);
        Assert.Null(report.SwitchedByHand);
        Assert.Equal("account-a@example.com", report.Holdings.Single(h => h.IsActive).AccountEmail);
    }

    private sealed class ThrowingSink : ILicenseReportSink
    {
        public bool CanSend => true;

        public Task SendAsync(LicenseUsageReport report, CancellationToken cancellationToken) => throw new IOException("the tunnel dropped");
    }
}
