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
    public string PiPath { get; set; } = DefaultNpmCliPath("pi");

    /// <summary>
    /// Path to the OpenAI Codex CLI (<c>codex.cmd</c> from <c>@openai/codex</c>).
    /// Defaults to the standard npm global install location on Windows.
    /// </summary>
    public string CodexPath { get; set; } = DefaultNpmCliPath("codex");

    /// <summary>
    /// Path to the Google Gemini CLI (<c>gemini.cmd</c> from <c>@google/gemini-cli</c>).
    /// Defaults to the standard npm global install location on Windows.
    /// </summary>
    public string GeminiPath { get; set; } = DefaultNpmCliPath("gemini");

    private static string DefaultNpmCliPath(string binName)
    {
        // Windows npm global install: %APPDATA%\npm\<bin>.cmd
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var path = Path.Combine(appData, "npm", binName + ".cmd");
            FileLog.Write($"[AgentOptions] DefaultNpmCliPath({binName}): resolved from %APPDATA% to {path}");
            return path;
        }
        FileLog.Write($"[AgentOptions] DefaultNpmCliPath({binName}): %APPDATA% unavailable, falling back to bare '{binName}' (relying on PATH)");
        return binName;
    }
}
