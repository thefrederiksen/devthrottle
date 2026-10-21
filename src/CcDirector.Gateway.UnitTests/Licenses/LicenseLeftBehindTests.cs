using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A MACHINE WITH LICENSE SWITCHING TURNED OFF (issue #4327). On 21 October 2026 an account reached 97%; two machines
/// switched and a third, whose Director had switching off, stayed on the tired account - and nothing said so, because
/// that Director sent no report at all. Now it reports that switching is off
/// (<see cref="LicenseCannotSwitchReasons.SwitchingOff"/>), with what it holds and no numbers. These hold what the
/// Gateway does with that: the machine's card says so and how to turn it on, it is not sent a switch, and the minute
/// check that switches the others writes a row naming it as left behind - once, not every minute. Over the real store
/// on a throwaway SQLite file, with a fake Director that switches and a clock that is the test's own.
/// </summary>
public sealed class LicenseLeftBehindTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId Tenant = new("acct-a");
    private static readonly DateTime LogStart = T0.AddDays(-1);

    private const string Windows = "the-windows-machine";
    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSwitchService _service;
    private readonly List<LicenseSwitchRequest> _sent = new();
    private DateTime _now = T0;
    private long _sequence;

    public LicenseLeftBehindTests()
    {
        _store = new LicenseStore(_harness.Open());
        _service = new LicenseSwitchService(_store, new LicenseSnapshotSource(_store, new LicenseReadModel(_store)), Send,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)), () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private Task<DirectorCommandResult?> Send(string directorId, DirectorCommand command, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)
            ?? throw new InvalidOperationException("The command carried no switch request.");
        lock (_sent) _sent.Add(request);
        return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(new LicenseSwitchResult
        {
            SwitchId = request.SwitchId,
            Outcome = LicenseSwitchOutcomes.Switched,
            Message = "The Director switched.",
            PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
            ActiveAccountEmailAfter = request.TargetAccountEmail,
            VerifiedByUsageRead = true,
            StartedAtUtc = _now,
            FinishedAtUtc = _now,
        }, Web)));
    }

    /// <summary>The Windows machine reports with switching on: on A, whose weekly limit is at 98%, holding B with room.</summary>
    private void WindowsReports(string active)
        => _store.ApplyReport(Tenant, "dir-" + Windows, ++_sequence,
            Report(Windows, _now, Holding(A, active == A, 20, 98, _now), Holding(B, active == B, 10, 30, _now)), _now, TimeSpan.Zero);

    /// <summary>The Mac reports with switching off: what it holds, read from its account files, with no numbers.</summary>
    private void MacReportsSwitchingOff()
    {
        var report = Report(Mac, _now, new LicenseHolding { AccountEmail = A, IsActive = true }, new LicenseHolding { AccountEmail = B });
        report.CanSwitch = false;
        report.CannotSwitchReason = LicenseCannotSwitchReasons.SwitchingOff;
        _store.ApplyReport(Tenant, "dir-" + Mac, ++_sequence, report, _now, TimeSpan.Zero);
    }

    private List<LicenseSwitchLogEntity> Rows(string kind)
        => _store.ListLog(Tenant, LogStart).Where(r => r.Kind == kind).OrderBy(r => r.CreatedAtUtc).ToList();

    private LicenseMachineDto MachineDto(string name)
    {
        var picture = new LicenseSnapshotSource(_store, new LicenseReadModel(_store)).ReadPicture(Tenant, _now, Array.Empty<LicenseExhaustion>());
        return LicenseDisplayFold.Overview(picture).Machines.Single(m => m.MachineName == name);
    }

    [Fact]
    public void AMachineWithSwitchingOff_ItsCardSaysSoAndHowToTurnItOn()
    {
        WindowsReports(A);
        MacReportsSwitchingOff();

        var mac = MachineDto(Mac);

        Assert.Equal(LicenseMachineStatuses.CannotSwitch, mac.Status);
        Assert.Equal("Cannot switch: license switching is turned off on its Director, although it holds spares", mac.StatusText);
        Assert.Contains(LicenseDisplayFold.TurnSwitchingOnCommand, mac.StatusDetail);
        Assert.Equal(LicenseTones.Warning, mac.StatusTone);
    }

    [Fact]
    public async Task TickAsync_AThresholdSwitch_NamesTheMachineItLeftBehind_Once()
    {
        WindowsReports(A);
        MacReportsSwitchingOff();

        await _service.TickAsync(Tenant, CancellationToken.None);

        var sent = Assert.Single(_sent);
        Assert.Equal(B, sent.TargetAccountEmail);
        var left = Assert.Single(Rows(LicenseLogKinds.LeftBehind));
        Assert.Equal(Mac, left.MachineName);
        Assert.Equal(A, left.FromAccountEmail);
        Assert.Equal(LicenseCannotSwitchReasons.SwitchingOff, left.ReasonCode);
        Assert.Contains("license switching is turned off on its Director", left.Message);

        // A minute later the Windows machine is on B and the Mac is still on the tired account: no new switch, so the
        // Mac is not named again every minute.
        _now = T0.AddMinutes(1);
        WindowsReports(B);
        MacReportsSwitchingOff();
        await _service.TickAsync(Tenant, CancellationToken.None);

        Assert.Single(_sent);
        Assert.Single(Rows(LicenseLogKinds.LeftBehind));
    }

    [Fact]
    public async Task TheLog_ShowsTheLeftBehindRowAsAWarning()
    {
        WindowsReports(A);
        MacReportsSwitchingOff();
        await _service.TickAsync(Tenant, CancellationToken.None);

        var entry = LicenseDisplayFold.LogEntry(Rows(LicenseLogKinds.LeftBehind).Single());

        Assert.Equal("Not switched", entry.KindText);
        Assert.Equal("Left behind", entry.StateText);
        Assert.Equal(LicenseTones.Warning, entry.StateTone);
        Assert.Contains(Mac, entry.Summary);
    }

    [Fact]
    public async Task TickAsync_EveryMachineTakesPart_NoLeftBehindRow()
    {
        WindowsReports(A);

        await _service.TickAsync(Tenant, CancellationToken.None);

        Assert.Single(_sent);
        Assert.Empty(Rows(LicenseLogKinds.LeftBehind));
    }
}
