using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;

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
    public const string Path = "/install-reports";

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

    internal sealed record Payload(
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

    /// <summary>Send one failure. Returns whether the Gateway accepted it.</summary>
    public async Task<bool> ReportAsync(string component, string step, string message, string? diagnostics, CancellationToken ct = default)
    {
        try
        {
            var payload = BuildPayload(component, step, message, diagnostics);
            var url = _gatewayUrl().TrimEnd('/') + Path;
            using var resp = await _http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
            EngineLog.Write($"[InstallFailureReporter] {component}/{step} -> HTTP {(int)resp.StatusCode} ({url})");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[InstallFailureReporter] {component}/{step} NOT delivered ({ex.GetType().Name}): {ex.Message}");
            return false;
        }
    }

    internal Payload BuildPayload(string component, string step, string message, string? diagnostics) => new(
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

    /// <summary>One random id per machine, created on first use and kept beside the install.</summary>
    internal string InstallId()
    {
        var path = System.IO.Path.Combine(_layout.LocalRoot, "install-id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        var id = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(_layout.LocalRoot);
        File.WriteAllText(path, id);
        return id;
    }

    private static string ProductVersion()
    {
        var asm = typeof(InstallFailureReporter).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    }
}
