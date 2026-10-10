using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Sends a failed install step to the hosted Gateway's <c>POST /install-reports</c> (issue #3311), so an
/// install that fails on somebody's machine is visible to us without asking them for screenshots or logs.
///
/// It needs no sign-in: installation happens before the machine has any credential. What it sends is
/// written by the installer itself - the step, the error message, the diagnostics the step collected, the
/// operating system, its version, the processor architecture and the product version. All of it is scrubbed
/// with the rules Director errors use (<see cref="Scrub"/>) before anything leaves the machine. No machine name, no user name, no file contents
/// other than the tail of our own log that a step chose to attach.
///
/// The install id is one random identifier per machine, kept beside the install, so a failure and the
/// re-run after it can be told apart from two different machines.
///
/// The install has ALREADY failed when this runs, and the person is already looking at the reason on their
/// own screen. So a report that cannot be delivered is logged and the installer carries on showing that
/// reason: the report is a copy for us, never a step the install depends on.
/// </summary>
public sealed class InstallFailureReporter
{
    public const string Path = InstallReportLimits.Path;

    private readonly InstallLayout _layout;
    private readonly string _installer;
    private readonly HttpClient _http;
    private readonly Func<string> _gatewayUrl;

    /// <param name="installer">Which installer is reporting, e.g. "setup-wizard" or "setup-cli".</param>
    public InstallFailureReporter(InstallLayout layout, string installer, HttpClient? http = null, Func<string>? gatewayUrl = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentException.ThrowIfNullOrWhiteSpace(installer);
        _installer = installer;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _gatewayUrl = gatewayUrl ?? HostedGateway.ResolveUrl;
    }

    /// <summary>Send one failure. Returns whether the Gateway accepted it.</summary>
    public async Task<bool> ReportAsync(string component, string step, string message, string? diagnostics, CancellationToken ct = default)
    {
        try
        {
            var payload = BuildPayload(component, step, message, diagnostics);
            var url = _gatewayUrl();
            var outcome = await InstallReportClient.PostAsync(_http, url, payload, ct).ConfigureAwait(false);
            EngineLog.Write($"[InstallFailureReporter] {component}/{step} -> {outcome} ({url.TrimEnd('/')}{Path})");
            return outcome.Accepted;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[InstallFailureReporter] {component}/{step} NOT delivered ({ex.GetType().Name}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The report as it will be sent, inside every limit the Gateway enforces: the message and the diagnostics
    /// cut to their field limits AFTER everything a caller appended and after scrubbing, and the serialized
    /// body measured in bytes against the Gateway's body limit, with the diagnostics giving way until it fits.
    /// A report over a limit is not a report with less detail; it is a 413 and nothing stored.
    /// </summary>
    internal InstallReportPayload BuildPayload(string component, string step, string message, string? diagnostics)
    {
        var payload = new InstallReportPayload(
            InstallId: InstallId(),
            Installer: _installer,
            Component: component,
            Step: step,
            Message: Cut(Scrub(message), InstallReportLimits.MaxMessage),
            Diagnostics: Cut(Scrub(diagnostics ?? ""), InstallReportLimits.MaxDiagnostics),
            Os: OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
            OsVersion: Environment.OSVersion.Version.ToString(),
            Arch: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ProductVersion: ProductVersion());
        while (BodyBytes(payload) > InstallReportLimits.MaxBodyBytes && payload.Diagnostics.Length > 200)
            payload = payload with { Diagnostics = Cut(payload.Diagnostics, payload.Diagnostics.Length * 4 / 5) };
        return payload;
    }

    /// <summary>The bytes of the body as the client serializes it.</summary>
    internal static int BodyBytes(InstallReportPayload payload) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload));

    /// <summary>The text within <paramref name="max"/> characters, and a note where it was cut.</summary>
    internal static string Cut(string text, int max)
    {
        const string note = "\n(cut to fit the report)";
        if (text.Length <= max) return text;
        return max > note.Length ? text[..(max - note.Length)] + note : text[..Math.Max(0, max)];
    }

    /// <summary>
    /// Make the text safe to leave the machine with the rules every Director and launcher error uses
    /// (<see cref="ErrorTextScrubber.ScrubOnThisMachine(string)"/>, issue #3644): home folders to "~", credential-shaped
    /// values redacted, the names taken out of <c>id</c> output, and this machine's user name and machine name
    /// replaced. The Gateway scrubs again on receipt, but only the sending machine knows its own names.
    /// </summary>
    public static string Scrub(string text) => ErrorTextScrubber.ScrubOnThisMachine(text ?? "");

    /// <summary>One random id per machine, created on first use and kept beside the install. The launcher and
    /// the Director read the same file (<see cref="CcDirector.Core.ErrorReports.InstallId"/>).</summary>
    internal string InstallId() => CcDirector.Core.ErrorReports.InstallId.ReadOrCreate(_layout.LocalRoot);

    private static string ProductVersion()
    {
        var asm = typeof(InstallFailureReporter).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    }
}
