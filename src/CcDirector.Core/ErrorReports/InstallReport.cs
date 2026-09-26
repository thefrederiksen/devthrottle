using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// The wire shape of the hosted Gateway's public <c>POST /install-reports</c> (issue #3311). It was the
/// installer's alone; the launcher and the Director now send through it too, for the errors they log before
/// the machine has signed in and for the "Send logs to DevThrottle" action. One definition, here, so the
/// installer and the desktop cannot drift apart. The Gateway reads the same field names
/// (<c>InstallReportEndpoints.InstallReportPost</c>).
/// </summary>
public sealed record InstallReportPayload(
    [property: JsonPropertyName("install_id")] string InstallId,
    [property: JsonPropertyName("installer")] string Installer,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("diagnostics")] string Diagnostics,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("os_version")] string OsVersion,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("product_version")] string ProductVersion);

/// <summary>The limits <c>POST /install-reports</c> enforces, mirrored so a sender can stay inside them
/// instead of learning them from a refusal. The Gateway's own constants are the authority.</summary>
public static class InstallReportLimits
{
    public const string Path = "/install-reports";

    /// <summary>Reports one install id may send in an hour. Shared by EVERYTHING on the machine - the
    /// installer, the launcher and every Director - because they all read the one install-id file.</summary>
    public const int MaxReportsPerInstallPerHour = 10;

    public const int MaxMessage = 4000;
    public const int MaxDiagnostics = 16000;
}

/// <summary>
/// The machine's install id: one random identifier per machine, created on first use and kept in the file
/// <c>install-id</c> at the machine root. The installer's failure reports, the launcher's and the Director's
/// errors before sign-in, and a "send logs" all carry it, so the owner can put one machine's story together
/// without it ever having signed in.
/// </summary>
public static class InstallId
{
    public const string FileName = "install-id";

    /// <summary>Read the id from <paramref name="machineRoot"/>, creating it when absent or unreadable.</summary>
    public static string ReadOrCreate(string machineRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineRoot);
        var path = System.IO.Path.Combine(machineRoot, FileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        var id = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(machineRoot);
        File.WriteAllText(path, id);
        return id;
    }
}

/// <summary>
/// Posts one <see cref="InstallReportPayload"/> and says what the Gateway answered. Never throws except for
/// cancellation: an unreachable Gateway is an answer (<see cref="InstallReportOutcome.NotDelivered"/>), and the
/// caller decides what to keep.
/// </summary>
public static class InstallReportClient
{
    public static async Task<InstallReportOutcome> PostAsync(HttpClient http, string gatewayUrl, InstallReportPayload payload, CancellationToken ct)
    {
        var url = gatewayUrl.TrimEnd('/') + InstallReportLimits.Path;
        try
        {
            using var response = await http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
            return new InstallReportOutcome(response.StatusCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InstallReportOutcome(null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>What one post came back with: an HTTP status, or the reason no answer arrived.</summary>
public sealed record InstallReportOutcome(HttpStatusCode? Status, string? Failure)
{
    public bool Accepted => Status is { } s && (int)s is >= 200 and < 300;
    public bool RateLimited => Status == HttpStatusCode.TooManyRequests;
    public bool RouteMissing => Status == HttpStatusCode.NotFound;

    public override string ToString() => Status is { } s ? $"HTTP {(int)s}" : $"not delivered ({Failure})";
}
