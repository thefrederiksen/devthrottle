using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>Stands in for <c>cc-devthrottle license</c>: holds accounts by email, answers usage from a table, and
/// exchanges the active account with a spare. No sign-in exists anywhere in it - only emails and numbers.</summary>
internal sealed class FakeLicenseCommandLine : ILicenseCommandLine
{
    /// <summary>Planted in every sentence the fake tool writes, so a test can prove none of it reaches the Gateway.</summary>
    public const string ToolTextMarker = "TOOL-TEXT-MARKER-5e2a";

    public List<(string Email, bool Active)> Held { get; } = new();
    public Dictionary<string, double> FiveHour { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ReadErrors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTime> SignInExpires { get; } = new(StringComparer.Ordinal);
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public int ListCalls { get; private set; }
    public List<bool> UsageCalls { get; } = new();
    public List<(string Target, string Expected)> SwitchCalls { get; } = new();

    /// <summary>When set, the next calls fail as a tool that gave no readable answer.</summary>
    public string? FailWith { get; set; }

    /// <summary>When set, SwitchAsync answers this instead of exchanging.</summary>
    public LicenseExchangeAnswer? SwitchAnswer { get; set; }

    /// <summary>When set, SwitchAsync waits for this before it exchanges.</summary>
    public TaskCompletionSource? SwitchGate { get; set; }

    public FakeLicenseCommandLine Hold(string email, bool active = false)
    {
        Held.Add((email, active));
        return this;
    }

    public string Active => Held.FirstOrDefault(h => h.Active).Email ?? "";

    public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<LicenseHolding>>(Held
            .OrderByDescending(h => h.Active)
            .Select(h => new LicenseHolding { AccountEmail = h.Email, IsActive = h.Active, Plan = "max" })
            .ToList());
    }

    public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        UsageCalls.Add(activeOnly);
        ThrowIfFailing();
        var now = UtcNow();
        return Task.FromResult<IReadOnlyList<LicenseHolding>>(Held
            .Where(h => !activeOnly || h.Active)
            .OrderByDescending(h => h.Active)
            .Select(h => ReadErrors.TryGetValue(h.Email, out var error)
                ? new LicenseHolding
                {
                    AccountEmail = h.Email, IsActive = h.Active, ReadError = error, ReadErrorDetail = ToolTextMarker,
                    SignInExpiresAtUtc = SignInExpires.TryGetValue(h.Email, out var errorExpiry) ? errorExpiry : null,
                }
                : new LicenseHolding
                {
                    AccountEmail = h.Email,
                    IsActive = h.Active,
                    Plan = "max",
                    RateLimitTier = "default_claude_max_20x",
                    FiveHour = new LicenseWindow { Utilization = FiveHour.GetValueOrDefault(h.Email), ResetsAtUtc = now.AddHours(2) },
                    SevenDay = new LicenseWindow { Utilization = 10, ResetsAtUtc = now.AddDays(3) },
                    ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
                    ReadAtUtc = now,
                    SignInExpiresAtUtc = SignInExpires.TryGetValue(h.Email, out var expiry) ? expiry : null,
                })
            .ToList());
    }

    public async Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken)
    {
        SwitchCalls.Add((targetAccountEmail, expectedActiveAccountEmail));
        if (SwitchGate is not null) await SwitchGate.Task;
        if (SwitchAnswer is not null) return SwitchAnswer;

        var previous = Active;
        for (var i = 0; i < Held.Count; i++)
            Held[i] = (Held[i].Email, Held[i].Email == targetAccountEmail);
        return new LicenseExchangeAnswer
        {
            Outcome = LicenseSwitchOutcomes.Switched,
            PreviousActiveAccountEmail = previous,
            ActiveAccountEmailAfter = Active,
            ExchangeMilliseconds = 5,
        };
    }

    private void ThrowIfFailing()
    {
        if (FailWith is not null) throw new LicenseCommandLineException(FailWith);
    }
}

/// <summary>Collects what would have gone up the tunnel.</summary>
internal sealed class FakeReportSink : ILicenseReportSink
{
    public bool CanSend { get; set; } = true;
    public List<LicenseUsageReport> Sent { get; } = new();

    public Task SendAsync(LicenseUsageReport report, CancellationToken cancellationToken)
    {
        Sent.Add(report);
        return Task.CompletedTask;
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class FakeClock
{
    public DateTime Now { get; set; } = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A temporary folder for one test, removed after it.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "license-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
