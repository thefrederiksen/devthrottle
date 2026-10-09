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
///
/// The fields after <c>machine_id</c> came with the Error Logging mission (issue #3675). Every one is OPTIONAL
/// on the wire, so a Director built before them keeps reporting unchanged:
///   user_visible   - true when the error reached a screen the user was looking at.
///   surface        - which screen: "session terminal", "phone chat", "settings".
///   action         - what the user was trying to do, in plain words: "send a prompt to the session".
///   correlation_id - the command or delivery id, so every row one failure caused reads as one incident.
///   http_status    - the Gateway answer that caused it, when one did.
///   error_code     - the machine-readable code from that answer, when one did.
///   session_id     - the DevThrottle session it concerned.
/// There is deliberately no fingerprint field here: the Gateway computes it once, on receipt (rule 7 - the
/// client is dumb), so two clients can never group the same failure differently.
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
    [property: JsonPropertyName("machine_id")] string? MachineId,
    [property: JsonPropertyName("user_visible")] bool? UserVisible = null,
    [property: JsonPropertyName("surface")] string? Surface = null,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId = null,
    [property: JsonPropertyName("http_status")] int? HttpStatus = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null);

/// <summary>The words and limits both sides agree on.</summary>
public static class ErrorReportLimits
{
    public const string Director = "director";
    public const string Launcher = "launcher";
    public const string Install = "install";
    /// <summary>Every cc-* command line tool, cc-devthrottle included.</summary>
    public const string Tool = "tool";
    public const string Cockpit = "cockpit";
    public const string Mobile = "mobile";
    /// <summary>The hosted Gateway's own errors, written by the Gateway into its own store.</summary>
    public const string Gateway = "gateway";
    /// <summary>The self-hosted Gateway app a user runs on their own machine, reporting to the hosted Gateway.</summary>
    public const string GatewayApp = "gateway-app";
    public const string Website = "website";

    /// <summary>
    /// EVERY component an error report may carry, in one place (issue #3675). The Gateway's routes, the read
    /// filters and <c>cc-devthrottle errors</c> all check against this list, and the mission's end-to-end proof
    /// reads it from here so that a new surface cannot be left out of the proof. Adding a surface is one line here.
    /// </summary>
    public static readonly IReadOnlyList<string> Components = [Director, Launcher, Install, Tool, Cockpit, Mobile, Gateway, GatewayApp, Website];

    /// <summary>The components the desktop <see cref="ErrorReporter"/> runs as: a Director or a launcher process.</summary>
    public static readonly IReadOnlySet<string> DeviceComponents = new HashSet<string>(StringComparer.Ordinal) { Director, Launcher };

    /// <summary>The components a credentialed sender may file through <c>POST /gateway/director-errors</c>: every
    /// component except two. The installer reports through the separate, credential-less <c>/install-reports</c>
    /// route, because the machine has no account yet. The hosted Gateway's own errors are written by the Gateway into
    /// its own store, so no caller may claim to be the Gateway. A self-hosted Gateway app is a caller like any other
    /// and reports as <see cref="GatewayApp"/>.</summary>
    public static readonly IReadOnlySet<string> ReportedComponents =
        new HashSet<string>(Components.Where(c => c is not Install and not Gateway), StringComparer.Ordinal);

    /// <summary>True when <paramref name="component"/> is one of <see cref="Components"/>.</summary>
    public static bool IsComponent(string component) => Components.Contains(component, StringComparer.Ordinal);

    public const int MaxReportsPerBatch = 25;
    public const int MaxShortField = 120;
    /// <summary>What the user was doing, in plain words - longer than a name, still one line.</summary>
    public const int MaxAction = 200;
    public const int MaxMessage = 2000;
    public const int MaxStack = 8000;
}

/// <summary>
/// The machine id in an error report: a one-way hash of the machine's name, never the name. The same
/// name always gives the same id, so the owner can ask "errors from devthrottle-mac-mini" by name and the
/// Gateway hashes it to match - without the name itself ever being stored.
///
/// It is NOT a privacy guarantee. Machine names are short and guessable, so anyone who can read the store and
/// holds a list of candidate names can recover the name by hashing each one. It keeps the literal name out of
/// the record; it does not make the machine anonymous.
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
