using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// The wire shape of <c>POST /gateway/director-errors</c> (issue #3311): one batch of errors from one
/// Director or launcher. Shared by the sender (<see cref="ErrorReporter"/>) and the Gateway, so the two
/// cannot drift apart.
/// </summary>
public sealed record ErrorReportBatch(
    [property: JsonPropertyName("reports")] IReadOnlyList<ErrorReportItem>? Reports);

/// <summary>
/// One error. Everything here is written by our own code on the reporting machine: which component, which
/// class logged it, the message, the exception type and stack, and what the machine is. Never prompt text,
/// terminal content, repository content or a credential - and the Gateway scrubs again on receipt.
/// </summary>
public sealed record ErrorReportItem(
    [property: JsonPropertyName("component")] string? Component,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("exception_type")] string? ExceptionType,
    [property: JsonPropertyName("stack")] string? Stack,
    [property: JsonPropertyName("repeat_count")] int RepeatCount,
    [property: JsonPropertyName("first_seen_utc")] DateTime FirstSeenUtc,
    [property: JsonPropertyName("last_seen_utc")] DateTime LastSeenUtc,
    [property: JsonPropertyName("product_version")] string? ProductVersion,
    [property: JsonPropertyName("os")] string? Os,
    [property: JsonPropertyName("os_version")] string? OsVersion,
    [property: JsonPropertyName("arch")] string? Arch,
    [property: JsonPropertyName("machine_id")] string? MachineId);

/// <summary>The words and limits both sides agree on.</summary>
public static class ErrorReportLimits
{
    public const string Director = "director";
    public const string Launcher = "launcher";
    public const string Install = "install";

    /// <summary>The components a device credential may report as. The installer reports through the
    /// separate, credential-less <c>/install-reports</c> route.</summary>
    public static readonly IReadOnlySet<string> DeviceComponents = new HashSet<string>(StringComparer.Ordinal) { Director, Launcher };

    public const int MaxReportsPerBatch = 25;
    public const int MaxShortField = 120;
    public const int MaxMessage = 2000;
    public const int MaxStack = 8000;
}

/// <summary>
/// The machine id in an error report: a one-way hash of the machine's name, never the name. The same
/// name always gives the same id, so the owner can ask "errors from devthrottle-mac-mini" by name and the
/// Gateway hashes it to match - without the name itself ever being stored.
/// </summary>
public static class ErrorReportMachineId
{
    /// <summary>16 lower-case hex characters of SHA-256 over the upper-cased machine name.</summary>
    public static string Of(string machineName)
    {
        ArgumentNullException.ThrowIfNull(machineName);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machineName.Trim().ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>True when <paramref name="value"/> already has the shape <see cref="Of"/> produces.</summary>
    public static bool IsHash(string value)
        => value.Length == 16 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
