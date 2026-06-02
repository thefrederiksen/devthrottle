using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// Runs the privileged part of a Gateway install by shelling the elevated CLI (decision D2): the WPF
/// stays non-elevated and does the per-user work itself, while this launches
/// <c>cc-director-setup-cli install --role gateway --component gateway</c> with a single UAC prompt to
/// place the Gateway exe in %ProgramFiles%, extract the Cockpit, and register + start the service.
///
/// UAC's "runas" verb requires UseShellExecute=true, which forbids stdout pipe redirection, so the
/// elevated CLI tees its console to a --log-file that this tails for live progress.
/// </summary>
public sealed class GatewayServiceLauncher
{
    public const string CliAsset = "cc-director-setup-cli-win-x64.exe";

    private readonly ReleaseSource _source;

    public GatewayServiceLauncher(ReleaseSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public sealed record Result(bool Success, int ExitCode, string Message);

    public async Task<Result> RunAsync(ResolvedRelease release, Action<string> onLine, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(onLine);

        if (!release.DownloadUrls.ContainsKey(CliAsset))
            return new Result(false, -1, $"This release has no {CliAsset}; cannot run the elevated Gateway install.");

        SetupLog.Write("[GatewayServiceLauncher] downloading CLI for elevated handoff");
        onLine("Downloading the installer CLI...");
        var cliPath = await _source.DownloadAssetAsync(CliAsset, release.DownloadUrls, ct);

        var logPath = Path.Combine(Path.GetTempPath(), $"cc-gateway-install-{Guid.NewGuid():N}.log");
        File.WriteAllText(logPath, "");

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = $"install --role gateway --component gateway --log-file \"{logPath}\"",
            UseShellExecute = true,   // required for the runas elevation verb
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process proc;
        try
        {
            onLine("Requesting administrator approval (UAC)...");
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            SetupLog.Write("[GatewayServiceLauncher] UAC declined");
            return new Result(false, 1223, "Administrator approval was declined; the Gateway service was not installed.");
        }

        long pos = 0;
        while (!proc.HasExited)
        {
            pos = EmitNewLines(logPath, pos, onLine);
            try { await Task.Delay(400, ct); } catch (OperationCanceledException) { break; }
        }
        EmitNewLines(logPath, pos, onLine);

        var ok = proc.ExitCode == 0;
        SetupLog.Write($"[GatewayServiceLauncher] CLI exited {proc.ExitCode}");
        return new Result(
            ok,
            proc.ExitCode,
            ok
                ? $"Gateway service installed; Cockpit live at {TailnetResolver.Url(7470)}."
                : $"Gateway install failed (exit {proc.ExitCode}). See {logPath}.");
    }

    /// <summary>Emit whole new lines appended to the log since <paramref name="fromPos"/>; returns the new position (only past the last complete line).</summary>
    private static long EmitNewLines(string path, long fromPos, Action<string> onLine)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = fs.Length;
            if (len <= fromPos) return fromPos;

            fs.Seek(fromPos, SeekOrigin.Begin);
            var buf = new byte[len - fromPos];
            var read = fs.Read(buf, 0, buf.Length);
            var text = Encoding.UTF8.GetString(buf, 0, read);

            var lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return fromPos; // no complete line yet

            var complete = text[..lastNl];
            foreach (var raw in complete.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(line)) onLine(line);
            }
            return fromPos + Encoding.UTF8.GetByteCount(complete) + 1; // +1 for the consumed '\n'
        }
        catch
        {
            return fromPos;
        }
    }
}
