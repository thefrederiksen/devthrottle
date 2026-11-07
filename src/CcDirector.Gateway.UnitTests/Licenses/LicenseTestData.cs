using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>Reports for the License Switching tests. Accounts are "account A / account B"; no real names.</summary>
internal static class LicenseTestData
{
    public const string A = "account-a@example.com";
    public const string B = "account-b@example.com";
    public const string C = "account-c@example.com";

    public static readonly DateTime T0 = new(2026, 10, 11, 3, 12, 0, DateTimeKind.Utc);

    /// <summary>The Licenses page of <paramref name="picture"/> with no pace known (<see cref="CcDirector.Gateway.Licenses.LicenseNextLicenseFold"/>
    /// takes ready licenses as lasting), for a test that is not about the pace.</summary>
    public static LicenseOverviewResponse OverviewOf(CcDirector.Gateway.Licenses.LicensePicture picture)
        => CcDirector.Gateway.Licenses.LicenseDisplayFold.Overview(picture, NextOf(picture));

    /// <summary>Every machine's next-license verdict on <paramref name="picture"/> with no pace known.</summary>
    public static IReadOnlyList<CcDirector.Gateway.Licenses.LicenseNextLicense> NextOf(CcDirector.Gateway.Licenses.LicensePicture picture)
        => CcDirector.Gateway.Licenses.LicenseNextLicenseFold.Judge(picture, _ => null);

    /// <summary>The coverage grid of <paramref name="picture"/> with no pace known.</summary>
    public static CcDirector.Gateway.Licenses.LicenseCoverage CoverageOf(CcDirector.Gateway.Licenses.LicensePicture picture)
        => CcDirector.Gateway.Licenses.LicenseCoverageFold.Judge(picture, NextOf(picture));

    /// <summary>The owner's night never falls, for an alarm service whose test is not about the night.</summary>
    public static bool NeverNight(CcDirector.Core.Tenancy.TenantId tenant, DateTime utc) => false;

    public static LicenseHolding Holding(string email, bool active, double fiveHour = 10, double sevenDay = 20, DateTime? readAt = null,
        string? readError = null)
        => new()
        {
            AccountEmail = email,
            IsActive = active,
            Plan = "max",
            RateLimitTier = "default_claude_max_20x",
            FiveHour = new LicenseWindow { Utilization = fiveHour, ResetsAtUtc = T0.AddHours(2) },
            SevenDay = new LicenseWindow { Utilization = sevenDay, ResetsAtUtc = T0.AddDays(4) },
            ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
            ReadAtUtc = readAt ?? T0,
            ReadError = readError,
        };

    public static LicenseUsageReport Report(string machine, params LicenseHolding[] holdings)
        => Report(machine, T0, holdings);

    public static LicenseUsageReport Report(string machine, DateTime sentAt, params LicenseHolding[] holdings)
        => new()
        {
            MachineName = machine,
            OsUser = "owner",
            SentAtUtc = sentAt,
            Trigger = LicenseReportTriggers.Interval,
            Holdings = holdings.ToList(),
        };
}
