using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// CLAUDE CODE'S "TEACH AUTO MODE ABOUT YOUR ENVIRONMENT?", WHICH THE DIRECTOR NEVER ANSWERS (Command Line Agent Handling
/// mission, the start-up questions).
///
/// WHY. A session in auto mode - the Director's own preset for Claude Code - is offered "Auto mode works better when it
/// knows your environment. Takes about a minute." with Yes, Not now and Don't show again, when its first turn ends. The
/// menu sits under the composer and takes the keys: the words of the next prompt vanish, and the Enter after them would
/// answer "Yes" for the owner, which starts a setup that reads the owner's shell history and other repositories. On 17
/// October 2026 it stopped a gate seat on SOREN_NORTH. The delivery rig's scenario auto-mode-question shows it: 0 of 2 on
/// main, the next prompt refused and the seat stuck.
///
/// THE DIRECTOR DOES NOT ANSWER IT. Like the Bypass Permissions warning (<see cref="ClaudeBypassConsent"/>), the question
/// is the owner's: a session nobody will answer that would be asked it is refused before it starts, with what the owner
/// does to settle it. A session the owner opens is left as it always was: the owner is there to answer.
///
/// WHEN CLAUDE CODE ASKS, read from Claude Code 2.1.302. Whether a session is offered it is decided once, at start, and all
/// of these must hold: the session starts in auto mode; no environment is described (<c>autoMode.environment</c> in the
/// user's, a <c>--settings</c> file's or the machine's managed settings); Claude Code has been started at least five times;
/// auto mode has turned down at least five actions (<c>autoModeEnvSetup.denials</c> in its configuration file, which stops
/// counting at five); the auto mode warning is not still to be shown; the owner has not answered "Don't show again"
/// (<c>autoModeEnvSetup.dismissed</c>) or "Not now" in the last seven days (<c>autoModeEnvSetup.dismissedAt</c>); the
/// setup is not turned off (<c>skillOverrides["auto-mode-setup"] = "off"</c>); and Claude Code's own server setting offers
/// it - <c>tengu_auto_mode_config.envOnboarding</c>, as last fetched into its configuration file, is exactly true. When
/// nothing was fetched (telemetry off, a third-party endpoint) Claude Code's built-in setting has no such key, and it
/// never asks (review of pull request 4180, finding 1). Everything except that server setting is the owner's own record,
/// and any one of them settles it.
/// </summary>
public static class ClaudeAutoModeSetupOffer
{
    /// <summary>The question, word for word as Claude Code shows it.</summary>
    public const string Question = "Teach auto mode about your environment?";

    private const int StartsBeforeOffer = 5;
    private const int DenialsBeforeOffer = 5;
    private static readonly TimeSpan NotNowLasts = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether <paramref name="args"/> starts Claude Code in auto mode: <c>--permission-mode auto</c> on the line, or,
    /// with no mode on the line, <c>permissions.defaultMode</c> "auto" in the settings Claude Code reads for the folder.
    /// </summary>
    public static bool StartsInAutoMode(string? args, IReadOnlyList<JsonObject?> settingsByPrecedence)
    {
        var tokens = (args ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "--dangerously-skip-permissions") return false;
            if (tokens[i].StartsWith("--permission-mode=", StringComparison.Ordinal)) return tokens[i]["--permission-mode=".Length..] == "auto";
            if (tokens[i] == "--permission-mode") return i + 1 < tokens.Length && tokens[i + 1] == "auto";
        }
        foreach (var settings in settingsByPrecedence)
        {
            if (settings?["permissions"]?["defaultMode"] is JsonValue mode && mode.TryGetValue<string>(out var name))
                return name == "auto";
        }
        return false;
    }

    /// <summary>
    /// The sentence that refuses the start, or null when it may go ahead: the session does not start in auto mode, the
    /// line brings its own <c>--settings</c> (which may describe the environment, and is not read here), or a record
    /// settles the question.
    /// </summary>
    public static string? WhyItWouldStop(string? userArgs, string effectiveArgs, string workingDirectory, string machine,
        Func<string, string?>? environment = null, string? home = null, string? managedSettings = null, TimeProvider? clock = null)
    {
        FileLog.Write($"[ClaudeAutoModeSetupOffer] WhyItWouldStop: folder={workingDirectory}, machine={machine}");
        environment ??= Environment.GetEnvironmentVariable;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configFolder = ClaudeSettingsFiles.ConfigFolder(environment, home);
        var managed = ClaudeSettingsFiles.Read(managedSettings ?? ClaudeSettingsFiles.ManagedPath());
        var user = ClaudeSettingsFiles.Read(Path.Combine(configFolder, "settings.json"));
        var local = ClaudeSettingsFiles.Read(Path.Combine(workingDirectory, ".claude", "settings.local.json"));
        var project = ClaudeSettingsFiles.Read(Path.Combine(workingDirectory, ".claude", "settings.json"));

        if (!StartsInAutoMode(effectiveArgs, [managed, local, project, user]))
        {
            FileLog.Write("[ClaudeAutoModeSetupOffer] WhyItWouldStop: not auto mode; not asked");
            return null;
        }
        if (userArgs is not null && userArgs.Contains("--settings", StringComparison.Ordinal))
        {
            FileLog.Write("[ClaudeAutoModeSetupOffer] WhyItWouldStop: the line brings its own --settings, which may describe the environment; not refused");
            return null;
        }
        var config = ClaudeSettingsFiles.Read(ClaudeFolderTrust.ConfigPathForThisUser(environment, home));
        if (WhatSettlesIt(config, [managed, user], [managed, local, project, user], (clock ?? TimeProvider.System).GetUtcNow()) is { } settled)
        {
            FileLog.Write($"[ClaudeAutoModeSetupOffer] WhyItWouldStop: not asked ({settled})");
            return null;
        }
        return $"No session was started: Claude Code would stop after its first reply on its question '{Question}', which nobody " +
               $"would be there to answer, because it is not settled on {machine}. The Director does not answer it for the owner. " +
               "Open 'claude --permission-mode auto' once on that machine, send it one message, and answer the question when it appears: " +
               "'Yes' runs a setup that reads this machine's shell history and other repositories and settles it for good, " +
               "'Don't show again' settles it for good, and 'Not now' only for a week. Or start the session without --permission-mode auto.";
    }

    /// <summary>The record that means Claude Code will not ask, in words for the log, or null when it will.
    /// <paramref name="environmentSources"/> are the managed and user settings, the only ones Claude Code takes the
    /// environment and the warning's skip from; <paramref name="settingsByPrecedence"/> are every settings file it reads.</summary>
    internal static string? WhatSettlesIt(JsonObject? config, IReadOnlyList<JsonObject?> environmentSources,
        IReadOnlyList<JsonObject?> settingsByPrecedence, DateTimeOffset now)
    {
        if (environmentSources.Any(s => s?["autoMode"]?["environment"] is JsonArray { Count: > 0 }))
            return "the environment is described in autoMode.environment";
        if (settingsByPrecedence.Any(s => s?["skillOverrides"]?["auto-mode-setup"] is JsonValue v && v.TryGetValue<string>(out var o) && o == "off"))
            return "the auto mode setup is turned off";
        // No configuration file: Claude Code has never started here (its count would be 1), and it is not asked.
        if (config is null)
            return "Claude Code has no configuration file here";
        switch (config["cachedGrowthBookFeatures"]?["tengu_auto_mode_config"]?["envOnboarding"])
        {
            case null:
                return "Claude Code's own server setting is absent, and it offers the question only when that is true";
            case JsonValue offer when offer.TryGetValue<bool>(out var offered) && offered:
                break;
            default:
                // Claude Code tests it with === true, so false, a string, a number, an object or a list all mean not asked.
                return "Claude Code's own server setting does not offer it";
        }
        // Claude Code counts this start before it decides, so the count on disk is one short of the one it compares.
        if (Number(config["numStartups"]) + 1 < StartsBeforeOffer)
            return "Claude Code has not been started often enough";
        var setup = config["autoModeEnvSetup"];
        if (Number(setup?["denials"]) < DenialsBeforeOffer)
            return "auto mode has not turned down enough actions";
        if (setup?["dismissed"] is JsonValue d && d.TryGetValue<bool>(out var dismissed) && dismissed)
            return "the owner answered 'Don't show again'";
        if (Number(setup?["dismissedAt"]) is var at and > 0 && now - DateTimeOffset.FromUnixTimeMilliseconds(at) < NotNowLasts)
            return "the owner answered 'Not now' in the last seven days";
        if (!(config["hasSeenAutoModeEntryWarning"] is JsonValue w && w.TryGetValue<bool>(out var seen) && seen)
            && !environmentSources.Any(s => s?["skipAutoPermissionPrompt"] is JsonValue k && k.TryGetValue<bool>(out var skip) && skip))
            return "the auto mode warning is still to be shown, and Claude Code offers this only after it";
        return null;
    }

    private static long Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var n) ? n
        : node is JsonValue d && d.TryGetValue<double>(out var x) ? (long)x
        : 0;
}
