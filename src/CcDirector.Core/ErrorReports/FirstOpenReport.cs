using System.Reflection;
using System.Runtime.InteropServices;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// One report, the first time DevThrottle opens on a machine that has not signed in (issue #3722).
///
/// Between "the installer finished" and "the machine is connected" we were blind: a person who installed and
/// never opened the app, and a person who opened it and stopped at the sign-in screen, looked the same - no
/// report, no device. This sends a single <see cref="Step"/> report on the installer's public route
/// (<c>POST /install-reports</c>, no credential needed), under the machine's install id, so the per-install
/// story on the Gateway reads: installer started, installer finished, app opened, signed in.
///
/// Once per machine: a marker file beside the install id is written when the Gateway accepts the report. A
/// machine that already has a Gateway credential is not a first open before sign-in, so it only gets the
/// marker. A report that cannot be delivered leaves no marker and is tried again at the next start - it is a
/// copy for us, never something the app waits on.
///
/// What it sends is the same as an installer report: operating system, its version, the processor
/// architecture, the product version and the install id. No machine name, no user name. The install id is the
/// one the installer reported under, which is what joins this report to the install (and, through the
/// installer's report, to the install tag).
/// </summary>
public sealed class FirstOpenReport
{
    public const string Step = "director-opened";
    public const string MarkerFileName = "first-open-reported";

    private readonly string _machineRoot;
    private readonly Func<GatewayConfig> _config;
    private readonly Func<string> _gatewayUrl;
    private readonly HttpClient _http;
    private readonly string _productVersion;

    public FirstOpenReport(string machineRoot, Func<GatewayConfig> config, Func<string> gatewayUrl, HttpClient http,
        string productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineRoot);
        _machineRoot = machineRoot;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _gatewayUrl = gatewayUrl ?? throw new ArgumentNullException(nameof(gatewayUrl));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _productVersion = productVersion;
    }

    /// <summary>Start the report off the startup path. Never throws and never delays the window.</summary>
    public static void StartInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "unknown";
                var report = new FirstOpenReport(CcStorage.MachineRoot(), GatewayConfig.Load, HostedGateway.ResolveUrl,
                    http, version);
                await report.SendIfFirstAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[FirstOpenReport] FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Send the report if this is the first open before sign-in. Returns true only when a report was sent and
    /// accepted.
    /// </summary>
    public async Task<bool> SendIfFirstAsync(CancellationToken ct)
    {
        var marker = Path.Combine(_machineRoot, MarkerFileName);
        if (File.Exists(marker)) return false;

        if (_config().HasCredential)
        {
            FileLog.Write("[FirstOpenReport] already signed in; marking the first open as seen without a report");
            WriteMarker(marker);
            return false;
        }

        var payload = new InstallReportPayload(
            InstallId: InstallId.ReadOrCreate(_machineRoot),
            Installer: "director",
            Component: "director",
            Step: Step,
            Message: "DevThrottle opened for the first time on this machine, before sign-in.",
            Diagnostics: "",
            Os: OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
            OsVersion: Environment.OSVersion.Version.ToString(),
            Arch: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ProductVersion: _productVersion);
        var url = _gatewayUrl();
        var outcome = await InstallReportClient.PostAsync(_http, url, payload, ct).ConfigureAwait(false);
        FileLog.Write($"[FirstOpenReport] {Step} -> {(outcome.Accepted ? "accepted" : outcome.Status?.ToString() ?? outcome.Failure)}");
        if (!outcome.Accepted) return false;
        WriteMarker(marker);
        return true;
    }

    private static void WriteMarker(string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
    }
}

/// <summary>
/// The anonymous install tag (issue #3722, website issue devthrottle_internal #2404). The website puts a short
/// random tag in the install command it hands a visitor, as <c>DEVTHROTTLE_INSTALL_TAG=...</c>; the installer
/// repeats it in its reports, so a website visit can be joined to the install it led to.
/// It identifies a browser in our own analytics, never a person. Anything that is not 4 to 16 lower-case
/// letters and digits is ignored, so the variable cannot carry other text into a report.
/// </summary>
public static class InstallTag
{
    public const string EnvironmentVariable = "DEVTHROTTLE_INSTALL_TAG";

    public static string? FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static string? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 4 || value.Length > 16) return null;
        foreach (var c in value)
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9')) return null;
        return value;
    }

    /// <summary>The message with " [tag xxxx]" on the end when there is a tag.</summary>
    public static string Append(string message, string? tag) => tag is null ? message : $"{message} [tag {tag}]";
}
