using System.Diagnostics;
using System.Text;
using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// Runs the Gateway-role part of an install by shelling the CLI (decision D2: the CLI is the single
/// source of truth): launches <c>cc-director-setup-cli install --role gateway --component gateway</c>,
/// which places the Gateway exe under %LOCALAPPDATA%, extracts the Cockpit, and starts the tray app.
/// Everything is per-user now (the Gateway is a tray app, not a service - docs/plans/gateway-tray-app.md),
/// so NO elevation and NO UAC prompt. The CLI tees its console to a --log-file that this tails for
/// live progress (keeps the proven tail mechanism; no pipe plumbing).
/// </summary>
public sealed class GatewayTrayLauncher
{
    public const string CliAsset = "devthrottle-setup-cli-win-x64.exe";

    private readonly ReleaseSource _source;

    public GatewayTrayLauncher(ReleaseSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public sealed record Result(bool Success, int ExitCode, string Message);

    public async Task<Result> RunAsync(ResolvedRelease release, Action<string> onLine, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(onLine);

        if (!release.DownloadUrls.ContainsKey(CliAsset))
            return new Result(false, -1, $"This release has no {CliAsset}; cannot run the Gateway install.");

        SetupLog.Write("[GatewayTrayLauncher] downloading CLI for the Gateway install");
        onLine("Downloading the installer CLI...");
        var cliPath = await _source.DownloadAssetAsync(CliAsset, release.DownloadUrls, ct);

        var logPath = Path.Combine(Path.GetTempPath(), $"cc-gateway-install-{Guid.NewGuid():N}.log");
        File.WriteAllText(logPath, "");

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = $"install --role gateway --component gateway --log-file \"{logPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        onLine("Installing the Gateway tray app...");
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");

        long pos = 0;
        while (!proc.HasExited)
        {
            pos = EmitNewLines(logPath, pos, onLine);
            try { await Task.Delay(400, ct); } catch (OperationCanceledException) { break; }
        }
        EmitNewLines(logPath, pos, onLine);

        var ok = proc.ExitCode == 0;
        SetupLog.Write($"[GatewayTrayLauncher] CLI exited {proc.ExitCode}");
        return new Result(
            ok,
            proc.ExitCode,
            ok
                ? $"Gateway tray app installed; Cockpit live at {TailnetResolver.FrontDoorUrl()}."
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
