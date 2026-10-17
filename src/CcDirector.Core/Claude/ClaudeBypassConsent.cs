using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// CLAUDE CODE'S ONE-TIME "BYPASS PERMISSIONS" WARNING, WHICH THE DIRECTOR NEVER ANSWERS (Command Line Agent Handling
/// mission, the start-up questions).
///
/// WHY. Started with <c>--dangerously-skip-permissions</c> by a person who has never accepted it on this machine, Claude
/// Code first shows "WARNING: Claude Code running in Bypass Permissions mode ... you accept all responsibility" with
/// "No, exit" chosen. On 16 October 2026 a reviewer session opened by another session sat on it, doing nothing, until it
/// was stopped: the session that opened it cannot type into it, and nobody else was watching.
///
/// THE DIRECTOR DOES NOT ACCEPT IT. The warning asks the PERSON to accept responsibility, so writing the consent for them
/// would be the Director signing for the owner. Instead a session that nobody will answer - one opened by another session,
/// or a factory seat - is refused before it starts, with the one command that records the consent, and the session that
/// asked learns why at once instead of finding a dead session later. A session the owner opens is left as it always was:
/// the owner is there to answer.
///
/// WHERE CLAUDE CODE LOOKS, read from Claude Code 2.1.302: the key <c>skipDangerousModePermissionPrompt</c> set true in the
/// user's settings (<c>&lt;config folder&gt;/settings.json</c>, which accepting the warning writes), the folder's
/// <c>.claude/settings.local.json</c>, or the machine's managed settings; or the older
/// <c>bypassPermissionsModeAccepted</c> in its configuration file, which it migrates. A launch line that brings its own
/// <c>--settings</c> may carry the consent too, and that file is not read here, so such a line is never refused.
/// </summary>
public static class ClaudeBypassConsent
{
    /// <summary>The settings key Claude Code reads as the person's consent.</summary>
    public const string ConsentKey = "skipDangerousModePermissionPrompt";

    /// <summary>Whether <paramref name="args"/> starts Claude Code in Bypass Permissions mode.</summary>
    public static bool IsRequested(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return false;
        var tokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "--dangerously-skip-permissions") return true;
            if (tokens[i] == "--permission-mode=bypassPermissions") return true;
            if (tokens[i] == "--permission-mode" && i + 1 < tokens.Length && tokens[i + 1] == "bypassPermissions") return true;
        }
        return false;
    }

    /// <summary>
    /// The sentence that refuses the start, or null when it may go ahead: Bypass Permissions mode is not asked for, the
    /// line brings its own <c>--settings</c>, or the consent is already recorded.
    /// </summary>
    public static string? WhyItWouldStop(string? userArgs, string effectiveArgs, string workingDirectory, string machine,
        Func<string, string?>? environment = null, string? home = null, string? managedSettings = null)
    {
        if (!IsRequested(effectiveArgs)) return null;
        if (userArgs is not null && userArgs.Contains("--settings", StringComparison.Ordinal))
        {
            FileLog.Write("[ClaudeBypassConsent] WhyItWouldStop: the line brings its own --settings, which may carry the consent; not refused");
            return null;
        }
        if (IsGiven(workingDirectory, environment, home, managedSettings)) return null;
        return "No session was started: Claude Code would stop on its one-time warning about Bypass Permissions mode " +
               $"(--dangerously-skip-permissions), which nobody would be there to answer, because it has never been accepted on {machine}. " +
               "The Director does not accept it for the owner. Run 'claude --dangerously-skip-permissions' once on that machine and choose " +
               "'Yes, I accept', or start the session without that flag.";
    }

    /// <summary>Whether Claude Code holds the consent for this user, machine and folder. Reads only.
    /// <paramref name="managedSettings"/> is the machine's managed-settings file; this machine's own when null.</summary>
    public static bool IsGiven(string workingDirectory, Func<string, string?>? environment = null, string? home = null,
        string? managedSettings = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = environment("CLAUDE_CONFIG_DIR");
        var configFolder = string.IsNullOrWhiteSpace(configDir) ? Path.Combine(home, ".claude") : configDir;

        var settingsFiles = new[]
        {
            Path.Combine(configFolder, "settings.json"),
            Path.Combine(workingDirectory, ".claude", "settings.local.json"),
            managedSettings ?? ManagedSettingsPath(),
        };
        foreach (var file in settingsFiles)
        {
            if (IsTrueIn(file, ConsentKey))
            {
                FileLog.Write($"[ClaudeBypassConsent] IsGiven: true ({ConsentKey} in {file})");
                return true;
            }
        }
        var config = ClaudeFolderTrust.ConfigPathForThisUser(environment, home);
        if (IsTrueIn(config, "bypassPermissionsModeAccepted"))
        {
            FileLog.Write($"[ClaudeBypassConsent] IsGiven: true (bypassPermissionsModeAccepted in {config})");
            return true;
        }
        FileLog.Write($"[ClaudeBypassConsent] IsGiven: false (looked in {string.Join(", ", settingsFiles)} and {config})");
        return false;
    }

    private static string ManagedSettingsPath() =>
        OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode\managed-settings.json"
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode/managed-settings.json"
        : "/etc/claude-code/managed-settings.json";

    // A file that is missing holds no consent. One that cannot be read or parsed holds none either, and the refusal that
    // follows names the command that writes it properly - the start is refused in words, never started blind.
    private static bool IsTrueIn(string file, string key)
    {
        if (!File.Exists(file)) return false;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream) is JsonObject root && root[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // not-an-error: a settings file Claude Code itself cannot read holds no consent; the refusal names the command that writes it.
            FileLog.Write($"[ClaudeBypassConsent] IsTrueIn: {file} could not be read ({ex.Message}); it holds no consent");
            return false;
        }
    }
}

/// <summary>A Claude Code session was refused before it started; the message is the sentence a person acts on.</summary>
public sealed class ClaudeStartRefusedException : InvalidOperationException
{
    public ClaudeStartRefusedException(string message) : base(message) { }
}
