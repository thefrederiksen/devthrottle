using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// Issue #4327: a machine that holds spares while license switching is off on its Director is no longer invisible.
/// It reads what it holds from the key store's account files (<see cref="LicenseHeldOnDisk"/>) - never a key, never the
/// license tool - and sends a report saying it cannot switch, <see cref="LicenseCannotSwitchReasons.SwitchingOff"/>.
/// </summary>
public sealed class LicenseSwitchedOffReportTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLicenseCommandLine _tool = new();
    private readonly FakeReportSink _sink = new();
    private readonly LicenseKeyHolderLock _holder;
    private readonly LicenseUsageReporter _reporter;
    private readonly string _store;
    private readonly string _home;

    public LicenseSwitchedOffReportTests()
    {
        _store = Path.Combine(_folder.Path, "store");
        _home = Path.Combine(_folder.Path, "home");
        _holder = new LicenseKeyHolderLock(Path.Combine(_folder.Path, "director"));
        _reporter = new LicenseUsageReporter(_tool, _holder, _clock.UtcNow, "the-mac", "owner");
        _reporter.AttachSink(_sink);
    }

    public void Dispose()
    {
        _holder.Dispose();
        _folder.Dispose();
    }

    private LicenseHeldOnDisk HeldOnDisk(string? defaultConfigDir = null) => new(defaultConfigDir, _store, _home);

    private void HoldActive(string email) => LicenseSwitchingSettingTests.WriteAccount(_home, email);

    private void HoldSpare(string email) => LicenseSwitchingSettingTests.WriteAccount(Path.Combine(_store, "spares", email.Split('@')[0]), email);

    [Fact]
    public void Holdings_ActiveFromTheHomeAccountFileThenEachSpare_LowerCased()
    {
        HoldActive("Account-A@Example.com");
        HoldSpare("account-c@example.com");
        HoldSpare("account-b@example.com");

        var holdings = HeldOnDisk().Holdings();

        Assert.Equal(new[] { "account-a@example.com", "account-b@example.com", "account-c@example.com" }, holdings.Select(h => h.AccountEmail));
        Assert.Equal(new[] { true, false, false }, holdings.Select(h => h.IsActive));
        Assert.All(holdings, h => Assert.Null(h.ReadAtUtc));   // nothing was read from the provider
        Assert.True(HeldOnDisk().HoldsSpare());
    }

    [Fact]
    public void Holdings_AnIsolatedDefaultFolder_NamesTheActiveAccountFromItsOwnAccountFile()
    {
        var isolated = Path.Combine(_folder.Path, "isolated-default");
        LicenseSwitchingSettingTests.WriteAccount(isolated, "account-z@example.com");
        HoldActive("account-a@example.com");

        Assert.Equal("account-z@example.com", HeldOnDisk(isolated).ActiveEmail());
    }

    [Fact]
    public void HoldsSpare_AFolderWithNoReadableAccountFile_HoldsNothing()
    {
        Directory.CreateDirectory(Path.Combine(_store, "spares", "empty"));
        Directory.CreateDirectory(Path.Combine(_store, "spares", "broken"));
        File.WriteAllText(Path.Combine(_store, "spares", "broken", LicenseHeldOnDisk.AccountFile), "{");

        Assert.False(HeldOnDisk().HoldsSpare());
        Assert.Empty(HeldOnDisk().Holdings());
    }

    [Fact]
    public async Task TickSwitchedOffAsync_SendsWhatIsHeldSayingSwitchingIsOff_AndGivesTheLockBack()
    {
        HoldActive("account-a@example.com");
        HoldSpare("account-b@example.com");

        var report = await _reporter.TickSwitchedOffAsync(HeldOnDisk(), CancellationToken.None);

        Assert.NotNull(report);
        Assert.False(report!.CanSwitch);
        Assert.Equal(LicenseCannotSwitchReasons.SwitchingOff, report.CannotSwitchReason);
        Assert.Equal(new[] { "account-a@example.com", "account-b@example.com" }, report.Holdings.Select(h => h.AccountEmail));
        Assert.True(report.Holdings[0].IsActive);
        Assert.Equal("the-mac", report.MachineName);
        Assert.Same(report, _sink.Sent.Single());
        Assert.False(_holder.IsHeld);              // a Director with switching on can still become the key holder
        Assert.Equal(0, _tool.ListCalls);          // the license tool never ran
        Assert.Empty(_tool.UsageCalls);
    }

    [Fact]
    public async Task TickSwitchedOffAsync_NothingChangedBeforeTheInterval_SendsNothingMore_ThenAChangeIsSentAtOnce()
    {
        HoldActive("account-a@example.com");
        HoldSpare("account-b@example.com");
        Assert.NotNull(await _reporter.TickSwitchedOffAsync(HeldOnDisk(), CancellationToken.None));

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await _reporter.TickSwitchedOffAsync(HeldOnDisk(), CancellationToken.None));

        HoldSpare("account-c@example.com");
        var changed = await _reporter.TickSwitchedOffAsync(HeldOnDisk(), CancellationToken.None);
        Assert.Equal(LicenseReportTriggers.Changed, changed!.Trigger);

        _clock.Advance(LicenseContract.ReportInterval);
        var interval = await _reporter.TickSwitchedOffAsync(HeldOnDisk(), CancellationToken.None);
        Assert.Equal(LicenseReportTriggers.Interval, interval!.Trigger);
    }

    [Fact]
    public async Task TickSwitchedOffAsync_AnotherDirectorHoldsTheLock_SendsNothing()
    {
        HoldSpare("account-b@example.com");
        using var other = new LicenseKeyHolderLock(Path.Combine(_folder.Path, "director"));
        Assert.True(other.TryAcquire());

        Assert.Null(await _reporter.TickSwitchedOffAsync(HeldOnDisk(), CancellationToken.None));
        Assert.Empty(_sink.Sent);
    }

    [Fact]
    public async Task TickSwitchedOffAsync_NoGatewayToSendTo_SendsNothingAndTakesNoLock()
    {
        HoldSpare("account-b@example.com");
        _sink.CanSend = false;

        Assert.Null(await _reporter.TickSwitchedOffAsync(HeldOnDisk(), CancellationToken.None));
        Assert.False(_holder.IsHeld);
    }
}
