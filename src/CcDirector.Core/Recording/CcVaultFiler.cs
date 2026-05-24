using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Recording;

/// <summary>
/// Files transcripts into the vault through the official <c>cc-vault</c> CLI
/// (the only sanctioned way to write the vault). The transcript markdown and
/// its source audio are written by <see cref="RecordingIngestService"/> into a
/// dedicated transcripts collection folder; this filer registers that folder
/// as a vault library once and imports each transcript as a typed
/// (<c>--type transcript</c>) document so the recordings form their own
/// collection rather than mixing into the general docs library.
/// </summary>
public sealed class CcVaultFiler : IVaultFiler
{
    private const string LibraryLabel = "Transcripts";
    private const string RegisteredMarker = ".library-registered";

    private readonly string _collectionDir;
    private readonly string? _explicitCcVaultPath;
    private string? _resolvedCcVaultPath;

    /// <param name="collectionDir">Folder that holds transcript markdown + audio. Registered as a vault library.</param>
    /// <param name="ccVaultPath">Full path to cc-vault. Resolved from PATH on first use if null.</param>
    public CcVaultFiler(string collectionDir, string? ccVaultPath = null)
    {
        _collectionDir = collectionDir;
        _explicitCcVaultPath = ccVaultPath;
    }

    // Resolve cc-vault lazily so merely constructing the filer (e.g. when the
    // Gateway maps its routes) never fails on a machine without cc-vault on
    // PATH. The failure surfaces only if a transcript is actually filed.
    private string CcVaultPath => _resolvedCcVaultPath ??= (_explicitCcVaultPath ?? ResolveCcVault());

    public async Task<string> FileTranscriptAsync(VaultFilingRequest request, CancellationToken ct = default)
    {
        FileLog.Write($"[CcVaultFiler] FileTranscriptAsync: title={request.Title}, md={request.TranscriptMarkdownPath}");

        await EnsureLibraryRegisteredAsync(ct);

        var (exit, stdout, stderr) = await RunAsync(new[]
        {
            "docs", "add", request.TranscriptMarkdownPath,
            "--type", "transcript",
            "--title", request.Title,
            "--tags", "transcript,recording",
        }, ct);

        if (exit != 0)
        {
            // Idempotent: a transcript that was already filed on a prior attempt
            // (e.g. the phone lost the success ack and retried) is the desired
            // end state, not a failure. Treat "already exists" as success.
            var combined = stderr + stdout;
            if (combined.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[CcVaultFiler] transcript already filed, treating as success: {request.Title}");
                return request.TranscriptMarkdownPath;
            }
            throw new InvalidOperationException(
                $"cc-vault docs add failed (exit {exit}): {Trim(combined)}");
        }

        var id = ExtractId(stdout);
        FileLog.Write($"[CcVaultFiler] filed transcript: id={id}");
        return string.IsNullOrWhiteSpace(id) ? request.TranscriptMarkdownPath : id;
    }

    private async Task EnsureLibraryRegisteredAsync(CancellationToken ct)
    {
        var marker = Path.Combine(_collectionDir, RegisteredMarker);
        if (File.Exists(marker)) return;

        Directory.CreateDirectory(_collectionDir);
        var (exit, stdout, stderr) = await RunAsync(new[]
        {
            "library", "add", _collectionDir,
            "--label", LibraryLabel,
            "--category", "personal",
        }, ct);

        // A non-zero exit here usually means the label/path is already
        // registered from a prior run. That is the desired end state, so we
        // record the marker either way and only log the detail.
        FileLog.Write($"[CcVaultFiler] library add exit={exit}: {Trim(stderr + stdout)}");
        await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("o"), ct);
    }

    private async Task<(int exit, string stdout, string stderr)> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CcVaultPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) errSb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    private static string ResolveCcVault()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = new[] { ".exe", ".cmd", ".bat", "" };
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim(), "cc-vault" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new InvalidOperationException(
            "cc-vault was not found on PATH. The vault filing step requires the cc-vault CLI. "
            + "Install it and ensure its directory is on PATH.");
    }

    private static string ExtractId(string stdout)
    {
        // cc-vault prints a confirmation line; capture anything that looks like
        // an id token without depending on an exact format.
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.Contains("id", StringComparison.OrdinalIgnoreCase) && t.Contains(':'))
                return t[(t.IndexOf(':') + 1)..].Trim();
        }
        return "";
    }

    private static string Trim(string s)
        => string.IsNullOrEmpty(s) || s.Length <= 400 ? s.Trim() : s[..400].Trim() + "...";
}
