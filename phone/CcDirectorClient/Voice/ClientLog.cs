namespace CcDirectorClient.Voice;

/// <summary>
/// Minimal append-only logger for the voice client. Writes ASCII-only lines to a
/// daily file under the app data directory and mirrors to Debug output. Kept
/// dependency-free (no MAUI types) so the pure-logic classes that call it can be
/// unit tested off-device. A failure to write a log line must never break the
/// feature, so the file write is best-effort and swallowed - logging is
/// diagnostics, not control flow.
/// </summary>
public static class ClientLog
{
    private static readonly object Gate = new();

    /// <summary>Directory log files are written to. Overridable for tests.</summary>
    public static string LogDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director-client", "logs");

    /// <summary>Append one timestamped line. ASCII only by contract.</summary>
    public static void Write(string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"client-{DateTime.UtcNow:yyyy-MM-dd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics only: a logging failure must not affect the app.
        }
    }
}
