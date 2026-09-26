using System.Reflection;
using System.Runtime.InteropServices;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// "Send logs to DevThrottle" (issue #3311, B5): the person asks for it - from the launcher's tray or the
/// Director's Help menu - and the recent tail of this process's own log goes to the hosted Gateway. Before this,
/// the only way to get a log to us was a GitHub account or an email with an attachment, and a person stuck on a
/// fresh install has neither to hand.
///
/// It uses the public <c>POST /install-reports</c>, signed in or not, so it works in exactly the state where
/// it is needed most. The report carries the machine's install id, and the person is shown the first eight
/// characters of it as a reference they can quote. What leaves the machine is our own log only, with home
/// folders reduced to "~" and credential-shaped values redacted by <see cref="ErrorTextScrubber"/>, cut to the
/// most recent lines that fit the route's diagnostics field.
/// </summary>
public static class LogSender
{
    public const string Step = "send-logs";

    // Below the route's cap, so the Gateway's own scrub never cuts what we believed was whole.
    internal const int MaxLogChars = InstallReportLimits.MaxDiagnostics - 1000;

    /// <summary>What happened, in words to show the person.</summary>
    public sealed record Result(bool Sent, string Reference, string Detail);

    /// <summary>Send the tail of this process's log. Never throws except for cancellation.</summary>
    /// <param name="component">"director" or "launcher".</param>
    /// <param name="trigger">Where the person asked, e.g. "tray menu" or "Help menu".</param>
    public static Task<Result> SendAsync(string component, string trigger, CancellationToken ct)
        => SendAsync(component, trigger, FileLog.CurrentLogPath,
            () => InstallId.ReadOrCreate(CcStorage.MachineRoot()), HostedGateway.ResolveUrl,
            SharedHttp.Value, ProductVersion(), ct);

    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

    internal static async Task<Result> SendAsync(string component, string trigger, string logPath,
        Func<string> installId, Func<string> gatewayUrl, HttpClient http, string productVersion, CancellationToken ct)
    {
        FileLog.Write($"[LogSender] SendAsync: component={component}, trigger={trigger}, log={logPath}");
        string id, url, tail;
        try
        {
            id = installId();
            url = gatewayUrl();
            tail = ReadTail(logPath, MaxLogChars);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FileLog.Write($"[LogSender] SendAsync FAILED before sending ({ex.GetType().Name}): {ex.Message}");
            return new Result(false, "", $"The log could not be prepared: {ex.Message}");
        }

        var reference = id.Length >= 8 ? id[..8] : id;
        var payload = new InstallReportPayload(
            InstallId: id,
            Installer: component,
            Component: component,
            Step: Step,
            Message: $"The person sent the {component}'s log from the {trigger}.",
            Diagnostics: tail,
            Os: OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
            OsVersion: ErrorTextScrubber.Clean(RuntimeInformation.OSDescription, ErrorReportLimits.MaxShortField),
            Arch: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ProductVersion: productVersion);

        var outcome = await InstallReportClient.PostAsync(http, url, payload, ct).ConfigureAwait(false);
        FileLog.Write($"[LogSender] SendAsync: {tail.Length} chars, reference={reference} -> {outcome}");
        if (outcome.Accepted)
            return new Result(true, reference, $"Your log was sent to DevThrottle. If you write to us, quote the reference {reference}.");
        if (outcome.RateLimited)
            return new Result(false, reference, "DevThrottle has had as many reports from this machine as it takes in an hour. Please try again later.");
        return new Result(false, reference, $"The log could not be sent ({outcome}). Check the internet connection and try again.");
    }

    /// <summary>
    /// The most recent whole lines of <paramref name="path"/> that fit in <paramref name="maxChars"/> once
    /// scrubbed. Read with write sharing, because this process is still writing the file.
    /// </summary>
    internal static string ReadTail(string path, int maxChars)
    {
        if (!File.Exists(path)) return "(this process has not written a log file yet)";
        var lines = new List<string>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
        }

        var kept = new LinkedList<string>();
        var length = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var clean = ErrorTextScrubber.Scrub(lines[i]);
            if (length + clean.Length + 1 > maxChars)
            {
                if (kept.Count == 0) kept.AddFirst(clean[^Math.Min(clean.Length, maxChars)..]);
                break;
            }
            kept.AddFirst(clean);
            length += clean.Length + 1;
        }
        return Path.GetFileName(path) + " (last " + kept.Count + " lines):\n" + string.Join('\n', kept);
    }

    private static string ProductVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(LogSender).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
