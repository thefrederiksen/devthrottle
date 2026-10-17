using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Licenses;

/// <summary>One account's freshest usage reading, and the machine and holding it came from.</summary>
public sealed record LicenseReadingView(
    string MachineName,
    string OsUser,
    bool FromActiveHolding,
    double? FiveHourUtilization,
    DateTime? FiveHourResetsAtUtc,
    double? SevenDayUtilization,
    DateTime? SevenDayResetsAtUtc,
    bool? ExtraUsageEnabled,
    double? ExtraUsageUtilization,
    DateTime JudgedReadAtUtc,
    string? ReadError,
    string? ReadErrorDetail);

/// <summary>One account as the engine and the Licenses page read it.</summary>
public sealed record LicenseAccountView(
    string AccountEmail,
    string? Plan,
    string? RateLimitTier,
    LicenseReadingView? Freshest,
    bool IsStale,
    IReadOnlyList<string> ActiveOnMachines,
    IReadOnlyList<string> SpareOnMachines,
    DateOnly? RenewsOn = null);

/// <summary>One sign-in a machine holds, with its sign-in expiry and the verdict the Cockpit shows as given.</summary>
public sealed record LicenseHoldingView(
    string AccountEmail,
    bool IsActive,
    DateTime? SignedInAtUtc,
    DateTime? SignInExpiresAtUtc,
    string ExpiryVerdict,
    bool IsReadingStale,
    string? ReadError,
    DateTime? RenewalFailedAtUtc = null,
    string? RenewalError = null,
    string? RenewalErrorDetail = null);

/// <summary>One machine as the engine and the Licenses page read it.</summary>
public sealed record LicenseMachineView(
    Guid MachineId,
    string MachineName,
    string OsUser,
    string DirectorId,
    string? ActiveAccountEmail,
    IReadOnlyList<string> Spares,
    IReadOnlyList<LicenseHoldingView> Holdings,
    DateTime LastReportAtUtc,
    bool IsStale,
    string? SwitchInProgressId,
    double ClockSkewSeconds,
    string? CannotSwitchReason = null);

/// <summary>An account's whole License Switching picture at one moment.</summary>
public sealed record LicenseOverview(IReadOnlyList<LicenseAccountView> Accounts, IReadOnlyList<LicenseMachineView> Machines);

/// <summary>The sign-in expiry verdict of contract section 5.7, folded here and shown by the Cockpit as given.</summary>
public static class LicenseSignInExpiry
{
    public const string Expired = "expired";
    public const string ExpiresSoon = "expires soon";
    public const string Fine = "fine";
    public const string NotKnown = "not known";

    /// <summary>Expired at or before now; expires soon within <see cref="LicenseContract.SignInExpiryWarning"/>; fine after
    /// that; not known when the Director did not say. A missing expiry is never read as fine or as expired.</summary>
    public static string Verdict(DateTime? signInExpiresAtUtc, DateTime nowUtc) => signInExpiresAtUtc switch
    {
        null => NotKnown,
        { } at when at <= nowUtc => Expired,
        { } at when at - nowUtc <= LicenseContract.SignInExpiryWarning => ExpiresSoon,
        _ => Fine,
    };
}

/// <summary>The staleness rules of contract section 5.2, in one place.</summary>
public static class LicenseStaleness
{
    /// <summary>A holding's numbers are stale when never read, or older than 6 minutes (active) or 30 minutes (spare),
    /// measured from the time the Gateway judges the read by.</summary>
    public static bool IsHoldingStale(bool isActive, DateTime? judgedReadAtUtc, DateTime nowUtc)
        => judgedReadAtUtc is not { } read
           || nowUtc - read > (isActive ? LicenseContract.ActiveHoldingStaleAfter : LicenseContract.SpareHoldingStaleAfter);

    /// <summary>A machine is stale when its last accepted report is older than 6 minutes.</summary>
    public static bool IsMachineStale(DateTime lastReportAtUtc, DateTime nowUtc)
        => nowUtc - lastReportAtUtc > LicenseContract.MachineStaleAfter;
}

/// <summary>
/// THE LICENSE READ MODEL (License Switching): for one account (tenant), every Claude subscription with its freshest
/// reading and whether that is stale, and every machine with its active account, its spares and whether it is stale.
/// The decision engine and the Licenses page both read this, so they cannot disagree about a fact. Pure: <see cref="Fold"/>
/// takes rows and a clock and touches nothing.
///
/// An account's numbers are its freshest reading from ANY machine (contract section 5.2). The account is stale when
/// that freshest reading is stale by the rule of the holding it came from - so an account fresh as a spare on one
/// machine is not made stale by an older reading where it is active elsewhere; the older reading is simply not the
/// freshest.
/// </summary>
public sealed class LicenseReadModel
{
    private readonly LicenseStore _store;

    public LicenseReadModel(LicenseStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public LicenseOverview Read(TenantId tenant, DateTime nowUtc)
    {
        // QUIET ON PURPOSE, like LicenseSnapshotSource.ReadPicture, which calls this once a minute for every
        // DevThrottle account: no entry line and no result line. A read that FAILS still logs. By the same ruling
        // (the Tech Lead, on the review of pull request 3936, finding 9).
        try
        {
            var (accounts, machines, holdings) = _store.ReadAll(tenant);
            return Fold(accounts, machines, holdings, nowUtc);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseReadModel] Read FAILED: {ex.Message}");
            throw;
        }
    }

    public static LicenseOverview Fold(IReadOnlyList<LicenseAccountEntity> accounts, IReadOnlyList<LicenseMachineEntity> machines,
        IReadOnlyList<LicenseHoldingEntity> holdings, DateTime nowUtc)
    {
        var machineById = machines.ToDictionary(m => m.Id);
        var live = holdings.Where(h => machineById.ContainsKey(h.MachineId)).ToList();

        var accountViews = accounts
            .OrderBy(a => a.AccountEmail, StringComparer.Ordinal)
            .Select(a =>
            {
                var mine = live.Where(h => h.AccountEmail == a.AccountEmail).ToList();
                var freshest = mine.Where(h => h.JudgedReadAtUtc is not null)
                    .OrderByDescending(h => h.JudgedReadAtUtc)
                    .ThenByDescending(h => h.IsActive)
                    .FirstOrDefault();
                var reading = freshest is null ? null : ToView(freshest, machineById[freshest.MachineId]);
                var stale = freshest is null || LicenseStaleness.IsHoldingStale(freshest.IsActive, freshest.JudgedReadAtUtc, nowUtc);
                return new LicenseAccountView(a.AccountEmail, a.Plan, a.RateLimitTier, reading, stale,
                    MachineNames(mine.Where(h => h.IsActive), machineById),
                    MachineNames(mine.Where(h => !h.IsActive), machineById),
                    a.RenewsOn);
            })
            .ToList();

        var machineViews = machines
            .OrderBy(m => m.MachineKey, StringComparer.Ordinal).ThenBy(m => m.OsUserKey, StringComparer.Ordinal)
            .Select(m => new LicenseMachineView(m.Id, m.MachineName, m.OsUser, m.DirectorId, m.ActiveAccountEmail,
                live.Where(h => h.MachineId == m.Id && !h.IsActive).Select(h => h.AccountEmail).OrderBy(e => e, StringComparer.Ordinal).ToList(),
                live.Where(h => h.MachineId == m.Id)
                    .OrderByDescending(h => h.IsActive).ThenBy(h => h.AccountEmail, StringComparer.Ordinal)
                    .Select(h => new LicenseHoldingView(h.AccountEmail, h.IsActive, h.SignedInAtUtc, h.SignInExpiresAtUtc,
                        LicenseSignInExpiry.Verdict(h.SignInExpiresAtUtc, nowUtc),
                        LicenseStaleness.IsHoldingStale(h.IsActive, h.JudgedReadAtUtc, nowUtc), h.ReadError,
                        h.RenewalFailedAtUtc, h.RenewalError, h.RenewalErrorDetail))
                    .ToList(),
                m.LastReportAtUtc, LicenseStaleness.IsMachineStale(m.LastReportAtUtc, nowUtc), m.SwitchInProgressId, m.ClockSkewSeconds,
                m.CannotSwitchReason))
            .ToList();

        return new LicenseOverview(accountViews, machineViews);
    }

    private static LicenseReadingView ToView(LicenseHoldingEntity h, LicenseMachineEntity m)
        => new(m.MachineName, m.OsUser, h.IsActive, h.FiveHourUtilization, h.FiveHourResetsAtUtc, h.SevenDayUtilization,
            h.SevenDayResetsAtUtc, h.ExtraUsageEnabled, h.ExtraUsageUtilization, h.JudgedReadAtUtc!.Value, h.ReadError, h.ReadErrorDetail);

    private static IReadOnlyList<string> MachineNames(IEnumerable<LicenseHoldingEntity> holdings, IReadOnlyDictionary<Guid, LicenseMachineEntity> machines)
        => holdings.Select(h => machines[h.MachineId].MachineName).OrderBy(n => n, StringComparer.Ordinal).ToList();
}
