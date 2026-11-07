using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// One tenant's license picture read at one moment: what the Licenses page lists (<see cref="Overview"/>), what the
/// decision engine and the forecast judge (<see cref="Snapshot"/>), and the switch log rows still open
/// (<see cref="OpenSwitches"/>). All three come from one read, so a page built from them cannot show a machine as
/// switchable in one place and mid-switch in another. <see cref="MachineHealth"/> is every open
/// <see cref="LicenseLogKinds.ReportRefused"/> and <see cref="LicenseLogKinds.MachineSilent"/> row
/// (<see cref="LicenseStore.ListOpenMachineHealth"/>): a machine whose reports are refused, or that has gone silent.
/// <see cref="TestUsage"/> is set while the owner's 82% test switch is on (<see cref="LicenseTestUsage"/>).
/// </summary>
public sealed record LicensePicture(
    LicenseOverview Overview,
    LicenseSnapshot Snapshot,
    IReadOnlyList<LicenseSwitchLogEntity> OpenSwitches,
    IReadOnlyList<LicenseSwitchLogEntity> MachineHealth,
    LicenseTestUsageOn? TestUsage = null)
{
    /// <summary>The snapshot with the real numbers: the one the forecast takes its pace from, so the test switch never
    /// feeds the pace it is judged by. The same as <see cref="Snapshot"/> while the switch is off.</summary>
    public LicenseSnapshot RealSnapshot => TestUsage?.RealSnapshot ?? Snapshot;

    /// <summary>The open refusal of <paramref name="machine"/>'s reports, whichever of its Directors sent them, or null
    /// when they are not being refused (<see cref="LicenseStore.IsRefusalOf"/>).</summary>
    public LicenseSwitchLogEntity? RefusalOf(LicenseMachineView machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return MachineHealth.FirstOrDefault(r => r.Kind == LicenseLogKinds.ReportRefused
                                                 && LicenseStore.IsRefusalOf(r, machine.MachineName, machine.OsUser, machine.DirectorId));
    }
}

/// <summary>
/// THE ONE PLACE A <see cref="LicenseSnapshot"/> IS BUILT FROM THE STORE (License Switching). The minute check, Switch
/// now, the forecast and the Licenses page all take the engine's picture from here, so they judge the same facts:
/// the freshest reading of every account and every machine from <see cref="LicenseReadModel"/>, whether a machine has
/// an open switch from the switch log, whether it is held because its switch keeps failing, and the bars still in force.
///
/// The open "no account has room" events are an INPUT, not read here: the minute check owns where they are kept. A
/// caller that does not judge them (the forecast, the Licenses page) passes an empty list.
/// </summary>
public sealed class LicenseSnapshotSource
{
    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;
    private readonly Func<TenantId, Guid?>? _testUsageMachine;

    /// <param name="testUsageMachine">The machine the owner's 82% test switch is on for, per tenant
    /// (<see cref="LicenseTestUsage.MachineFor"/>); null when this source has no test switch.</param>
    public LicenseSnapshotSource(LicenseStore store, LicenseReadModel reads, Func<TenantId, Guid?>? testUsageMachine = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _testUsageMachine = testUsageMachine;
    }

    /// <summary>The engine's picture of <paramref name="tenant"/> at <paramref name="nowUtc"/>.</summary>
    public LicenseSnapshot Read(TenantId tenant, DateTime nowUtc, IReadOnlyList<LicenseExhaustion> openExhaustions)
        => ReadPicture(tenant, nowUtc, openExhaustions).Snapshot;

    /// <summary>The engine's picture together with the overview and the open switch rows it was built from.</summary>
    public LicensePicture ReadPicture(TenantId tenant, DateTime nowUtc, IReadOnlyList<LicenseExhaustion> openExhaustions)
    {
        ArgumentNullException.ThrowIfNull(openExhaustions);
        // QUIET ON PURPOSE. The once-a-minute check reads this for every DevThrottle account, every minute, for
        // good, and the Licenses page reads it on every poll; an entry line and a result line here are thousands of
        // lines a day that say nothing happened. The callers log what they decided, and a read that FAILS still logs.
        // This departs from the rule that every public method logs its entry and exit, BY RULING (the Tech Lead, on
        // the review of pull request 3936, finding 9): a read that runs every minute logs failures only.
        try
        {
            // The owner's 82% test switch, when it is on, raises one license's weekly use here, so every reader sees it.
            var real = _reads.Read(tenant, nowUtc);
            var testMachine = _testUsageMachine?.Invoke(tenant);
            var overview = LicenseTestUsage.Apply(real, testMachine);
            var open = _store.ListOpenSwitches(tenant);
            var bars = _store.ListActiveBars(tenant, nowUtc);
            var holds = _store.ListHolds(tenant, nowUtc);
            var health = _store.ListOpenMachineHealth(tenant);
            var snapshot = Build(overview, open, bars, openExhaustions, nowUtc, holds, health);
            var testUsage = ReferenceEquals(real, overview) || testMachine is not { } machineId
                ? null
                : new LicenseTestUsageOn(machineId, LicenseStore.NormalizeEmail(real.Machines.First(m => m.MachineId == machineId).ActiveAccountEmail!),
                    Build(real, open, bars, openExhaustions, nowUtc, holds, health));
            return new LicensePicture(overview, snapshot, open, health, testUsage);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseSnapshotSource] ReadPicture FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The snapshot, as pure code. An account's reading is its freshest one, judged by the time the store corrected
    /// for clock skew; an account never read has no windows and no read time, which the engine reads as stale. A
    /// machine has an open switch when the log holds a <c>pending</c> or <c>unknown</c> row for it, a hold when
    /// <paramref name="holds"/> - <see cref="LicenseStore.ListHolds"/> - has a row for it, and its reports are refused
    /// when <paramref name="machineHealth"/> - <see cref="LicenseStore.ListOpenMachineHealth"/> - has an open refusal
    /// for its Director.
    /// </summary>
    public static LicenseSnapshot Build(LicenseOverview overview, IReadOnlyList<LicenseSwitchLogEntity> openSwitches,
        IReadOnlyList<LicenseBarEntity> bars, IReadOnlyList<LicenseExhaustion> openExhaustions, DateTime nowUtc,
        IReadOnlyList<LicenseSwitchLogEntity>? holds = null, IReadOnlyList<LicenseSwitchLogEntity>? machineHealth = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(openSwitches);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(openExhaustions);

        var accounts = overview.Accounts
            .Select(a => new LicenseAccountReading(
                a.AccountEmail,
                FiveHourWindow(a.Freshest),
                Window(a.Freshest?.SevenDayUtilization, a.Freshest?.SevenDayResetsAtUtc),
                a.Freshest?.JudgedReadAtUtc,
                a.Freshest?.FromActiveHolding ?? false,
                a.NoPaidPlanSinceUtc))
            .ToList();

        var machinesMidSwitch = openSwitches
            .Where(r => r.Kind == LicenseLogKinds.Switch && LicenseLogStates.IsOpen(r.State))
            .Select(r => r.MachineId)
            .ToHashSet();
        var holdByMachine = (holds ?? Array.Empty<LicenseSwitchLogEntity>())
            .Where(r => r.Kind == LicenseLogKinds.Switch && r.HeldUntilUtc is not null)
            .GroupBy(r => r.MachineId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAtUtc).First());
        var refusals = (machineHealth ?? Array.Empty<LicenseSwitchLogEntity>())
            .Where(r => r.Kind == LicenseLogKinds.ReportRefused)
            .ToList();
        var machines = overview.Machines
            .Select(m => new LicenseMachineState(
                m.MachineName,
                m.OsUser,
                m.ActiveAccountEmail,
                m.Holdings.Where(h => !h.IsActive)
                    .Select(h => new LicenseSpare(h.AccountEmail, h.ReadError, h.SignInExpiresAtUtc))
                    .ToList(),
                m.LastReportAtUtc,
                machinesMidSwitch.Contains(m.MachineId),
                m.SwitchInProgressId,
                m.Holdings.FirstOrDefault(h => h.IsActive)?.SignInExpiresAtUtc,
                holdByMachine.TryGetValue(m.MachineId, out var held)
                    ? new LicenseHold(DateTime.SpecifyKind(held.HeldUntilUtc!.Value, DateTimeKind.Utc), held.FromAccountEmail, held.ToAccountEmail, held.ReasonCode)
                    : null,
                refusals.Any(r => LicenseStore.IsRefusalOf(r, m.MachineName, m.OsUser, m.DirectorId)),
                m.CannotSwitchReason))
            .ToList();

        // The store hands over only the bars still in force, and the engine ignores one whose time has passed.
        var inForce = bars
            .Select(b => new LicenseBar(b.AccountEmail, b.Window, b.UntilUtc))
            .ToList();

        return new LicenseSnapshot(nowUtc, accounts, machines, inForce, openExhaustions);
    }

    private static LicenseWindowReading? Window(double? utilization, DateTime? resetsAtUtc)
        => utilization is { } used ? new LicenseWindowReading(used, resetsAtUtc) : null;

    /// <summary>
    /// The 5-hour window of a reading. A reading that carries the weekly number but no 5-hour number is one whose
    /// provider answer said <c>"five_hour": null</c>: the reader refuses an answer that carries neither window, so the
    /// weekly number proves the answer was read. That is a 5-hour window not running
    /// (<see cref="LicenseWindowReading.NotRunningWindow"/>). A reading with neither number is not known, as before.
    /// </summary>
    internal static LicenseWindowReading? FiveHourWindow(LicenseReadingView? freshest)
    {
        if (freshest is null)
            return null;
        if (freshest.FiveHourUtilization is null && freshest.SevenDayUtilization is not null)
            return LicenseWindowReading.NotRunningWindow;
        return Window(freshest.FiveHourUtilization, freshest.FiveHourResetsAtUtc);
    }
}
