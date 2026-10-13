using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// One tenant's license picture read at one moment: what the Licenses page lists (<see cref="Overview"/>), what the
/// decision engine and the forecast judge (<see cref="Snapshot"/>), and the switch log rows still open
/// (<see cref="OpenSwitches"/>). All three come from one read, so a page built from them cannot show a machine as
/// switchable in one place and mid-switch in another.
/// </summary>
public sealed record LicensePicture(
    LicenseOverview Overview,
    LicenseSnapshot Snapshot,
    IReadOnlyList<LicenseSwitchLogEntity> OpenSwitches);

/// <summary>
/// THE ONE PLACE A <see cref="LicenseSnapshot"/> IS BUILT FROM THE STORE (License Switching). The minute check, Switch
/// now, the forecast and the Licenses page all take the engine's picture from here, so they judge the same facts:
/// the freshest reading of every account and every machine from <see cref="LicenseReadModel"/>, whether a machine has
/// an open switch from the switch log, and the bars still in force.
///
/// The open "no account has room" events are an INPUT, not read here: the minute check owns where they are kept. A
/// caller that does not judge them (the forecast, the Licenses page) passes an empty list.
/// </summary>
public sealed class LicenseSnapshotSource
{
    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;

    public LicenseSnapshotSource(LicenseStore store, LicenseReadModel reads)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
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
            var overview = _reads.Read(tenant, nowUtc);
            var open = _store.ListOpenSwitches(tenant);
            var bars = _store.ListActiveBars(tenant, nowUtc);
            var snapshot = Build(overview, open, bars, openExhaustions, nowUtc);
            return new LicensePicture(overview, snapshot, open);
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
    /// machine has an open switch when the log holds a <c>pending</c> or <c>unknown</c> row for it.
    /// </summary>
    public static LicenseSnapshot Build(LicenseOverview overview, IReadOnlyList<LicenseSwitchLogEntity> openSwitches,
        IReadOnlyList<LicenseBarEntity> bars, IReadOnlyList<LicenseExhaustion> openExhaustions, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(openSwitches);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(openExhaustions);

        var accounts = overview.Accounts
            .Select(a => new LicenseAccountReading(
                a.AccountEmail,
                Window(a.Freshest?.FiveHourUtilization, a.Freshest?.FiveHourResetsAtUtc),
                Window(a.Freshest?.SevenDayUtilization, a.Freshest?.SevenDayResetsAtUtc),
                a.Freshest?.JudgedReadAtUtc,
                a.Freshest?.FromActiveHolding ?? false))
            .ToList();

        var machinesMidSwitch = openSwitches
            .Where(r => r.Kind == LicenseLogKinds.Switch && LicenseLogStates.IsOpen(r.State))
            .Select(r => r.MachineId)
            .ToHashSet();
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
                m.Holdings.FirstOrDefault(h => h.IsActive)?.SignInExpiresAtUtc))
            .ToList();

        // The store hands over only the bars still in force, and the engine ignores one whose time has passed.
        var inForce = bars
            .Select(b => new LicenseBar(b.AccountEmail, b.Window, b.UntilUtc))
            .ToList();

        return new LicenseSnapshot(nowUtc, accounts, machines, inForce, openExhaustions);
    }

    private static LicenseWindowReading? Window(double? utilization, DateTime? resetsAtUtc)
        => utilization is { } used ? new LicenseWindowReading(used, resetsAtUtc) : null;
}
