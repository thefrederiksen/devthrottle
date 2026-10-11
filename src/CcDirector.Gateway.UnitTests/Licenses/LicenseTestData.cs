using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>Reports for the License Switching tests. Accounts are "account A / account B"; no real names.</summary>
internal static class LicenseTestData
{
    public const string A = "account-a@example.com";
    public const string B = "account-b@example.com";
    public const string C = "account-c@example.com";

    public static readonly DateTime T0 = new(2026, 10, 11, 3, 12, 0, DateTimeKind.Utc);

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
