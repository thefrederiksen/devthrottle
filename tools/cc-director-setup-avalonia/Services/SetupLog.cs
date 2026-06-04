namespace CcDirectorSetup.Services;

public static class SetupLog
{
    private static readonly string LogDir;
    private static readonly string LogPath;
    private static readonly object Lock = new();

    /// <summary>The current setup log file (shown on-screen so a user can find/attach it).</summary>
    public static string Path => LogPath;

    /// <summary>The setup log directory.</summary>
    public static string Dir => LogDir;

    static SetupLog()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LogDir = System.IO.Path.Combine(localAppData, "cc-director", "logs", "setup");
        Directory.CreateDirectory(LogDir);
        LogPath = System.IO.Path.Combine(LogDir, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Lock)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
