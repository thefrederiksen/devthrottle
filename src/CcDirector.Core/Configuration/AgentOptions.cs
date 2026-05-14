using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

public class AgentOptions
{
    public string ClaudePath { get; set; } = "claude";
    public string DefaultClaudeArgs { get; set; } = "--dangerously-skip-permissions";
    public int DefaultBufferSizeBytes { get; set; } = 2_097_152; // 2 MB
    public int GracefulShutdownTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Path to the Pi agent CLI (<c>pi.cmd</c> from <c>@earendil-works/pi-coding-agent</c>).
    /// Defaults to the standard npm global install location on Windows; users can override
    /// in config.json if pi is installed elsewhere.
    /// </summary>
    public string PiPath { get; set; } = DefaultPiPath();

    private static string DefaultPiPath()
    {
        // Windows npm global install: %APPDATA%\npm\pi.cmd
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var path = Path.Combine(appData, "npm", "pi.cmd");
            FileLog.Write($"[AgentOptions] DefaultPiPath: resolved from %APPDATA% to {path}");
            return path;
        }
        FileLog.Write("[AgentOptions] DefaultPiPath: %APPDATA% unavailable, falling back to bare 'pi' (relying on PATH)");
        return "pi";
    }
}
