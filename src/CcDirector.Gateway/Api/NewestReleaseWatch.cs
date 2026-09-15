using CcDirector.Core.Update;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Gateway.Api;

/// <summary>What the Gateway knows about the newest published release at one moment.</summary>
/// <param name="Version">The newest release version, or null while it has never been read.</param>
/// <param name="CheckedAtUtc">When the last read finished, successfully or not.</param>
/// <param name="Error">Why the last read failed, or null when it succeeded. A failure keeps the version read
/// before it, and says so - it never clears a version that was true, and never invents one.</param>
internal sealed record NewestReleaseSnapshot(string? Version, DateTime? CheckedAtUtc, string? Error);

/// <summary>
/// The newest published release, read by the Gateway (fleet maintenance, devthrottle_internal#2020).
///
/// WHY THE GATEWAY READS IT AT ALL. Every Director asks GitHub for the latest release on its own and keeps the
/// answer to itself, so the Cockpit could show a version on every machine and could not say whether any of them
/// was behind. This is the one place that answer now lives for the fleet view.
///
/// A READ NEVER WAITS ON THE NETWORK. <see cref="Current"/> returns what is known and starts a refresh when the
/// answer is older than an hour (five minutes after a failure). The fleet view is polled every few seconds, and a
/// slow GitHub must not hold a page open.
/// </summary>
internal sealed class NewestReleaseWatch
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryFailureAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(3);

    private readonly Func<CancellationToken, Task<string>> _fetchVersion;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private NewestReleaseSnapshot _current = new(null, null, null);
    private DateTime? _lastAttemptUtc;
    private Task? _refresh;

    public NewestReleaseWatch(Func<CancellationToken, Task<string>> fetchVersion, Func<DateTime>? utcNow = null)
    {
        _fetchVersion = fetchVersion ?? throw new ArgumentNullException(nameof(fetchVersion));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// The production watch: the same release source the launcher and the installer read, with its answer cache
    /// kept in the temporary folder so a hosted container needs no install layout.
    /// </summary>
    public static NewestReleaseWatch FromGitHub() => new(async ct =>
    {
        var cache = new ReleaseInfoCache(Path.Combine(Path.GetTempPath(), "devthrottle-gateway", "newest-release-cache.json"));
        var release = await new ReleaseSource(cache: cache).FetchLatestAsync(ct);
        return release.Manifest.Version;
    });

    /// <summary>What is known now. Starts a refresh in the background when one is due; never waits for it.</summary>
    public NewestReleaseSnapshot Current()
    {
        lock (_gate)
        {
            var now = _utcNow();
            var waitBetween = _current.Error is null && _current.Version is not null ? FreshFor : RetryFailureAfter;
            var due = _refresh is null && (_lastAttemptUtc is null || now - _lastAttemptUtc.Value >= waitBetween);
            if (due)
            {
                FileLog.Write($"[NewestReleaseWatch] Current: starting a check (known={_current.Version ?? "(none)"})");
                _lastAttemptUtc = now;
                _refresh = Task.Run(RefreshAsync);
            }
            return _current;
        }
    }

    /// <summary>The refresh in flight, or a completed task when there is none. For tests.</summary>
    internal Task WaitForRefreshAsync()
    {
        lock (_gate) return _refresh ?? Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(FetchTimeout);
            var version = (await _fetchVersion(cts.Token))?.Trim() ?? "";
            if (UpdateService.TryParseTag(version) is null)
                throw new InvalidOperationException($"the newest release reports a version that cannot be read: '{version}'");

            lock (_gate)
            {
                _current = new NewestReleaseSnapshot(version, _utcNow(), null);
                _refresh = null;
            }
            FileLog.Write($"[NewestReleaseWatch] RefreshAsync: newest release is {version}");
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _current = _current with { CheckedAtUtc = _utcNow(), Error = ex.Message };
                _refresh = null;
            }
            FileLog.Write($"[NewestReleaseWatch] RefreshAsync FAILED: {ex.Message}");
        }
    }
}
