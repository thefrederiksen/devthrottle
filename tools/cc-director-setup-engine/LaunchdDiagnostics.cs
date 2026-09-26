using System.Text;

namespace CcDirector.Setup.Engine;

/// <summary>
/// What launchd and our own log files say about the launcher, gathered at the moment an install step
/// fails. It exists because "launchd did not report which process is running the launcher" told a user
/// (and us) nothing: launchd had the answer the whole time - the job's state, how it last exited, whether
/// it could be spawned at all - and the installer read it and threw it away.
///
/// Pure parsing lives here so it is testable on any operating system; the caller supplies the text.
/// </summary>
public static class LaunchdDiagnostics
{
    /// <summary>The launchctl print fields that explain a job with no process. Everything else in that
    /// output (endpoints, environment, spawn flags) is noise for this question and is left out.</summary>
    private static readonly string[] UsefulFields =
    [
        "state", "pid", "runs", "last exit code", "last exit reason", "last terminating signal",
        "job state", "spawn type", "immediate reason", "program", "path",
    ];

    /// <summary>The lines of a launchctl print block worth showing: the fields above, and any line
    /// that mentions a failure.</summary>
    public static IReadOnlyList<string> UsefulLines(string? launchctlPrintOutput)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(launchctlPrintOutput)) return lines;
        foreach (var raw in launchctlPrintOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            var key = eq > 0 ? line[..eq].Trim() : "";
            var isField = key.Length > 0 && UsefulFields.Contains(key, StringComparer.OrdinalIgnoreCase);
            var mentionsFailure = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                                  || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                                  || line.Contains("denied", StringComparison.OrdinalIgnoreCase)
                                  || line.Contains("throttl", StringComparison.OrdinalIgnoreCase);
            if (isField || mentionsFailure) lines.Add(line);
        }
        return lines;
    }

    /// <summary>The value of one "key = value" field, or null.</summary>
    public static string? Field(string? launchctlPrintOutput, string key)
    {
        if (string.IsNullOrWhiteSpace(launchctlPrintOutput)) return null;
        foreach (var raw in launchctlPrintOutput.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// One plain sentence for the person at the screen: what launchd says happened to the launcher.
    /// Null when launchd said nothing we can put into words.
    /// </summary>
    public static string? Explain(string? launchctlPrintOutput, bool jobLoaded)
    {
        if (!jobLoaded)
            return "macOS no longer has the launcher registered: it was unloaded after it was registered";

        var exitCode = Field(launchctlPrintOutput, "last exit code");
        var signal = Field(launchctlPrintOutput, "last terminating signal");
        var state = Field(launchctlPrintOutput, "state");
        var runs = Field(launchctlPrintOutput, "runs");

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(signal)) parts.Add($"it was stopped by {signal}");
        else if (!string.IsNullOrEmpty(exitCode) && exitCode != "(never exited)") parts.Add($"it exited with code {exitCode}");
        if (!string.IsNullOrEmpty(runs)) parts.Add($"macOS started it {runs} time(s)");
        if (!string.IsNullOrEmpty(state)) parts.Add($"its state is now \"{state}\"");
        return parts.Count == 0 ? null : "The launcher started but did not stay running: " + string.Join(", ", parts);
    }

    /// <summary>The last <paramref name="maxLines"/> non-empty lines of a text file, or null when the
    /// file does not exist or is empty.</summary>
    public static string? Tail(string path, int maxLines)
    {
        if (!File.Exists(path)) return null;
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return null;
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Count - maxLines)));
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of the newest <c>launcher-*.log</c> in
    /// <paramref name="launcherLogDir"/> - the launcher's own record, which says WHY it stopped when launchd
    /// only knows THAT it did (issue #3311, B4). Read with write sharing, because a launcher that is still
    /// running holds the file open. Null when there is no such log.
    /// </summary>
    public static string? LauncherLogTail(string launcherLogDir, int maxLines)
    {
        if (!Directory.Exists(launcherLogDir)) return null;
        var newest = new DirectoryInfo(launcherLogDir).GetFiles("launcher-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return null;
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                if (line.Trim().Length > 0) lines.Add(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said in the report rather than thrown: one unreadable file must not cost the whole report.
            return $"{newest.Name}: could not be read ({ex.GetType().Name}: {ex.Message})";
        }
        if (lines.Count == 0) return null;
        return newest.Name + ":\n" + string.Join('\n', lines.Skip(Math.Max(0, lines.Count - maxLines)));
    }

    /// <summary>The most lines of one binary check kept in a report. The security log can run to
    /// thousands of lines; the refusal is at the end.</summary>
    public const int MaxBinaryCheckLines = 25;

    /// <summary>
    /// The answers macOS gave about the launcher binary (quarantine flag, signature, security log), one
    /// labelled block each, with the exit code, so an empty answer and a failed command read differently.
    /// </summary>
    public static string ComposeBinaryChecks(IEnumerable<(string Name, int Exit, string Output)> checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("launcher binary:");
        foreach (var (name, exit, output) in checks)
        {
            sb.AppendLine($"  {name} -> exit {exit}:");
            var lines = (output ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0)
            {
                sb.AppendLine("    (no output)");
                continue;
            }
            if (lines.Count > MaxBinaryCheckLines)
            {
                sb.AppendLine($"    ({lines.Count - MaxBinaryCheckLines} earlier lines left out)");
                lines = lines.Skip(lines.Count - MaxBinaryCheckLines).ToList();
            }
            foreach (var line in lines) sb.AppendLine("    " + line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Everything gathered, as one block of text for the report.</summary>
    public static string Compose(string? launchctlPrintOutput, bool jobLoaded, IEnumerable<(string Name, string? Text)> logTails)
    {
        var sb = new StringBuilder();
        sb.AppendLine(jobLoaded ? "launchctl print (useful lines):" : "launchctl print: the job is NOT loaded");
        foreach (var line in UsefulLines(launchctlPrintOutput)) sb.AppendLine("  " + line);
        foreach (var (name, text) in logTails)
        {
            sb.AppendLine($"{name}:");
            sb.AppendLine(string.IsNullOrEmpty(text) ? "  (missing or empty)" : "  " + text.Replace("\n", "\n  "));
        }
        return sb.ToString().TrimEnd();
    }
}
