using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The installer failure channel (issue #3311, first slice): when an install step fails on somebody's
/// machine, the installer sends what went wrong HERE, so the failure does not exist only on their screen.
///
///   POST /install-reports                 - an installer reports one failed step. No credential: the
///                                           person has not signed in yet, which is exactly when we were blind.
///   GET  /gateway/admin/install-reports   - the recent reports, newest first, for an administrator.
///
/// WHY IT IS PUBLIC. Installation happens before the machine has any Gateway credential, so a report that
/// needed one could never be sent for the failures that matter most - the ones that stop the install before
/// sign-in. The route is exact-match public in <c>AuthMiddleware.PublicPaths</c>, like the enrollment front
/// doors, and carries its own bounds instead of an identity:
///   - the body is capped at <see cref="MaxBodyBytes"/> and read no further,
///   - every field is length-capped and control characters are removed,
///   - each install id may report <see cref="MaxReportsPerInstallPerHour"/> times an hour, and the whole
///     route accepts <see cref="MaxReportsPerHour"/> an hour, so a loop or a stranger cannot flood the log,
///   - home-folder paths are reduced to <c>~</c> here as well as in the installer, so a user name does not
///     reach the log even from an installer that forgot to scrub.
///
/// WHAT IS RECORDED. Unlike <see cref="ClientErrorEndpoints"/>, whose input is free text a person may have
/// typed a secret into, this input is written by our own installer: the step, its error message and the
/// diagnostics it collected (for the macOS launcher, launchd's state for our job and the tail of our own
/// error log). So the durable record carries the content - a record without it would leave us exactly as
/// blind as before.
///
/// THE DURABLE RECORD is <see cref="ErrorReportStore"/>, the same files on the Gateway's durable storage that
/// Director and launcher errors go to, readable at <c>/gateway/admin/director-errors?component=install</c>.
/// This slice first called its "[InstallReport] {json}" FileLog line the durable record, saying it landed on
/// the persistent share. On hosted it does not: <c>GatewayEntryPoint</c> keeps the process log on the
/// container's temporary disk, so every such line was lost on the next deploy. The line is still written,
/// for a reader of the live log; the in-memory ring behind this route's admin read is still a convenience
/// that a restart empties.
///
/// No database table on purpose: a schema change is the owner's decision.
/// </summary>
internal static class InstallReportEndpoints
{
    /// <summary>The report route. Exact-match public in <c>AuthMiddleware</c>.</summary>
    public const string Path = "/install-reports";

    /// <summary>The administrator read. Exact-match public in <c>AuthMiddleware</c>; carries the
    /// administrator service-token gate (<see cref="AdminTrialEndpoint.ServiceTokenDenial"/>).</summary>
    public const string AdminPath = "/gateway/admin/install-reports";

    internal const int MaxBodyBytes = 64 * 1024;
    internal const int MaxReportsPerInstallPerHour = 10;
    internal const int MaxReportsPerHour = 300;
    private const int RingCapacity = 500;

    internal const int MaxShortField = 100;
    internal const int MaxMessage = 4000;
    internal const int MaxDiagnostics = 16000;

    internal sealed record InstallReportPost(
        [property: JsonPropertyName("install_id")] string? InstallId,
        [property: JsonPropertyName("component")] string? Component,
        [property: JsonPropertyName("step")] string? Step,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("diagnostics")] string? Diagnostics,
        [property: JsonPropertyName("os")] string? Os,
        [property: JsonPropertyName("os_version")] string? OsVersion,
        [property: JsonPropertyName("arch")] string? Arch,
        [property: JsonPropertyName("product_version")] string? ProductVersion,
        [property: JsonPropertyName("installer")] string? Installer);

    internal sealed record InstallReportRecord(
        [property: JsonPropertyName("at_utc")] DateTime AtUtc,
        [property: JsonPropertyName("install_id")] string InstallId,
        [property: JsonPropertyName("installer")] string Installer,
        [property: JsonPropertyName("component")] string Component,
        [property: JsonPropertyName("step")] string Step,
        [property: JsonPropertyName("os")] string Os,
        [property: JsonPropertyName("os_version")] string OsVersion,
        [property: JsonPropertyName("arch")] string Arch,
        [property: JsonPropertyName("product_version")] string ProductVersion,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("diagnostics")] string Diagnostics);

    private static readonly object RingLock = new();
    private static readonly LinkedList<InstallReportRecord> Ring = new();

    private static readonly object RateLock = new();
    private static readonly Queue<DateTime> RouteWindow = new();
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> InstallWindows = new(StringComparer.Ordinal);

    private static readonly Regex InstallIdShape = new("^[A-Za-z0-9-]{8,64}$", RegexOptions.CultureInvariant);

    // A home folder in a path names the person. Reduced to "~" - the rest of the path stays, because the
    // rest is what makes a report useful ("~/Library/Application Support/cc-director/logs").
    private static readonly Regex MacHome = new(@"/Users/[^/\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex LinuxHome = new(@"/home/[^/\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex WindowsHome = new(@"[A-Za-z]:\\Users\\[^\\\s""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions LineJson = new() { WriteIndented = false };

    public static void Map(IEndpointRouteBuilder app, ErrorReportStore? store = null, Func<DateTime>? nowUtc = null)
    {
        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            try
            {
                if (ctx.Request.ContentLength is > MaxBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                var raw = await ReadCappedAsync(ctx.Request.Body, MaxBodyBytes, ctx.RequestAborted).ConfigureAwait(false);
                if (raw is null)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                InstallReportPost? post;
                try
                {
                    post = JsonSerializer.Deserialize<InstallReportPost>(raw);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "the request body is not readable JSON" });
                }

                return Handle(post, clock(), store);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[InstallReportEndpoints] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the report could not be recorded" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet(AdminPath, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                var limit = 100;
                if (int.TryParse(ctx.Request.Query["limit"].ToString(), out var requested))
                    limit = Math.Clamp(requested, 1, RingCapacity);
                return Results.Json(new { reports = Recent(limit) });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[InstallReportEndpoints] GET {AdminPath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "the reports could not be read" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[InstallReportEndpoints] mapped {Path} (public, bounded) + {AdminPath} (service-token authorized)");
    }

    /// <summary>Validate, bound, scrub and record one report. Internal so every branch is testable
    /// without standing a host up.</summary>
    internal static IResult Handle(InstallReportPost? post, DateTime nowUtc, ErrorReportStore? store = null)
    {
        if (post is null)
            return Results.BadRequest(new { error = "the request body is empty" });

        var installId = (post.InstallId ?? "").Trim();
        if (!InstallIdShape.IsMatch(installId))
            return Results.BadRequest(new { error = "install_id must be 8 to 64 letters, digits or hyphens" });

        var message = Clean(post.Message, MaxMessage);
        if (message.Length == 0)
            return Results.BadRequest(new { error = "message is required" });

        if (!TryAdmit(installId, nowUtc))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        var record = new InstallReportRecord(
            AtUtc: nowUtc,
            InstallId: installId,
            Installer: Clean(post.Installer, MaxShortField),
            Component: Clean(post.Component, MaxShortField),
            Step: Clean(post.Step, MaxShortField),
            Os: Clean(post.Os, MaxShortField),
            OsVersion: Clean(post.OsVersion, MaxShortField),
            Arch: Clean(post.Arch, MaxShortField),
            ProductVersion: Clean(post.ProductVersion, MaxShortField),
            Message: message,
            Diagnostics: Clean(post.Diagnostics, MaxDiagnostics));

        FileLog.Write(DurableLine(record));
        store?.Append(new[] { ToStored(record) });

        lock (RingLock)
        {
            Ring.AddFirst(record);
            while (Ring.Count > RingCapacity) Ring.RemoveLast();
        }

        return Results.Json(new { recorded = true }, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>The installer report in the shape every stored error shares. It belongs to no account: the
    /// machine has none yet.</summary>
    internal static ErrorReportRecord ToStored(InstallReportRecord r) => new()
    {
        ReceivedUtc = r.AtUtc,
        Component = CcDirector.Core.ErrorReports.ErrorReportLimits.Install,
        Account = "",
        Device = r.InstallId,
        ProductVersion = r.ProductVersion,
        Os = r.Os,
        OsVersion = r.OsVersion,
        Arch = r.Arch,
        Source = r.Component,
        Kind = "install-step",
        Message = r.Message,
        Installer = r.Installer,
        Step = r.Step,
        Diagnostics = r.Diagnostics.Length > 0 ? r.Diagnostics : null,
    };

    /// <summary>The one log line. JSON on one line, so newlines in diagnostics cannot forge a second
    /// log entry, and so the line can be read back as data.</summary>
    internal static string DurableLine(InstallReportRecord record)
        => "[InstallReport] " + JsonSerializer.Serialize(record, LineJson);

    internal static IReadOnlyList<InstallReportRecord> Recent(int limit)
    {
        lock (RingLock) return Ring.Take(limit).ToList();
    }

    /// <summary>Scrub a home folder out of a path, drop control characters other than newline and tab,
    /// and cap the length.</summary>
    internal static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var scrubbed = ScrubHomePaths(value);
        var sb = new StringBuilder(Math.Min(scrubbed.Length, max));
        foreach (var c in scrubbed)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(c) && c != '\n' && c != '\t') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    internal static string ScrubHomePaths(string value)
    {
        var s = MacHome.Replace(value, "~");
        s = LinuxHome.Replace(s, "~");
        return WindowsHome.Replace(s, "~");
    }

    /// <summary>A sliding one-hour window, per install id and for the whole route.</summary>
    internal static bool TryAdmit(string installId, DateTime nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromHours(1);
        lock (RateLock)
        {
            while (RouteWindow.Count > 0 && RouteWindow.Peek() < cutoff) RouteWindow.Dequeue();
            if (RouteWindow.Count >= MaxReportsPerHour)
            {
                FileLog.Write("[InstallReportEndpoints] route limit reached; report dropped");
                return false;
            }

            var mine = InstallWindows.GetOrAdd(installId, _ => new Queue<DateTime>());
            while (mine.Count > 0 && mine.Peek() < cutoff) mine.Dequeue();
            if (mine.Count >= MaxReportsPerInstallPerHour)
            {
                FileLog.Write("[InstallReportEndpoints] per-install limit reached; report dropped");
                return false;
            }

            mine.Enqueue(nowUtc);
            RouteWindow.Enqueue(nowUtc);

            // Prune empty windows so a stream of one-off install ids cannot grow the dictionary for ever.
            if (InstallWindows.Count > 10_000)
                foreach (var (key, queue) in InstallWindows)
                    if (queue.Count == 0 || queue.Peek() < cutoff) InstallWindows.TryRemove(key, out _);

            return true;
        }
    }

    /// <summary>Test seam: forget every report and every rate window.</summary>
    internal static void ResetForTests()
    {
        lock (RingLock) Ring.Clear();
        lock (RateLock) { RouteWindow.Clear(); InstallWindows.Clear(); }
    }

    /// <summary>Read at most <paramref name="max"/> bytes; null when the body is longer.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
