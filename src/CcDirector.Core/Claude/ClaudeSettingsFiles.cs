using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Claude Code's own files, read the way the start-up question checks need them (<see cref="ClaudeBypassConsent"/>,
/// <see cref="ClaudeAutoModeSetupOffer"/>): read only, shared with a Claude Code process that may be rewriting them.
/// </summary>
internal static class ClaudeSettingsFiles
{
    /// <summary>The configuration folder: CLAUDE_CONFIG_DIR when set, else the home folder's <c>.claude</c>.</summary>
    public static string ConfigFolder(Func<string, string?> environment, string home)
    {
        var configDir = environment("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configDir) ? Path.Combine(home, ".claude") : configDir;
    }

    /// <summary>The machine's managed settings, which no user setting overrides.</summary>
    public static string ManagedPath() =>
        OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode\managed-settings.json"
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode/managed-settings.json"
        : "/etc/claude-code/managed-settings.json";

    // A file that is missing holds nothing. One that cannot be read or parsed holds nothing either: Claude Code cannot
    // read it, and the refusal that follows names what the owner runs to record the answer properly - the start is
    // refused in words, never started blind.
    public static JsonObject? Read(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // not-an-error: a file Claude Code itself cannot read records no answer; the refusal names the command that writes one.
            FileLog.Write($"[ClaudeSettingsFiles] Read: {file} could not be read ({ex.Message}); it records nothing");
            return null;
        }
    }

    /// <summary>Whether <paramref name="key"/> is the JSON value true at the top of <paramref name="file"/>.</summary>
    public static bool IsTrueIn(string file, string key) =>
        Read(file)?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
