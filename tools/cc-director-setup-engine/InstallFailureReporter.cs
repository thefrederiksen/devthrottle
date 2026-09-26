using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Sends a failed install step to the hosted Gateway's <c>POST /install-reports</c> (issue #3311), so an
/// install that fails on somebody's machine is visible to us without asking them for screenshots or logs.
///
/// It needs no sign-in: installation happens before the machine has any credential. What it sends is
/// written by the installer itself - the step, the error message, the diagnostics the step collected, the
/// operating system, its version, the processor architecture and the product version. Home-folder paths are
/// reduced to "~" before anything leaves the machine. No machine name, no user name, no file contents
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

    private static readonly Regex MacHome = new(@"/Users/[^/\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex LinuxHome = new(@"/home/[^/\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex WindowsHome = new(@"[A-Za-z]:\\Users\\[^\\\s""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    internal InstallReportPayload BuildPayload(string component, string step, string message, string? diagnostics) => new(
        InstallId: InstallId(),
        Installer: _installer,
        Component: component,
        Step: step,
        Message: Scrub(message),
        Diagnostics: Scrub(diagnostics ?? ""),
        Os: OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
        OsVersion: Environment.OSVersion.Version.ToString(),
        Arch: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        ProductVersion: ProductVersion());

    /// <summary>Reduce every home folder in the text to "~".</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = MacHome.Replace(text, "~");
        s = LinuxHome.Replace(s, "~");
        return WindowsHome.Replace(s, "~");
    }

    /// <summary>One random id per machine, created on first use and kept beside the install. The launcher and
    /// the Director read the same file (<see cref="CcDirector.Core.ErrorReports.InstallId"/>).</summary>
    internal string InstallId() => CcDirector.Core.ErrorReports.InstallId.ReadOrCreate(_layout.LocalRoot);

    private static string ProductVersion()
    {
        var asm = typeof(InstallFailureReporter).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    }
}
