using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Writes the Claude Code hook files the Director uses to track a session's live Claude
/// session id and transcript path across <c>/clear</c> and auto-compaction.
///
/// Claude mints a NEW session id (and a new transcript .jsonl) when the user runs
/// <c>/clear</c> or when the context auto-compacts. The Director only knows the FIRST id
/// (it preassigns it with <c>--session-id</c>), so after a clear its pointer goes stale.
/// A <c>SessionStart</c> hook (matchers startup/resume/clear/compact) fires in the
/// interactive session and hands the current <c>session_id</c> and <c>transcript_path</c>
/// to the hook command, which POSTs them back to the owning Director.
///
/// The hook files are STATIC and shared across all Claude sessions: the script reads the
/// per-session <c>CC_SESSION_ID</c> and <c>CC_DIRECTOR_API</c> from the environment the
/// Director already injects, so nothing per-session is baked into them. Passing this
/// settings file via <c>--settings</c> MERGES with the user's own hooks (it never replaces
/// them - see Claude Code issue #11392), so the user's hooks keep running too.
/// </summary>
public static class ClaudeHookInstaller
{
    // PowerShell: read the hook event JSON on stdin, report the current session id +
    // transcript path to the owning Director. Must never block or fail the session - it
    // swallows all errors and exits 0. Only SessionStart fires it, so the PowerShell
    // startup cost is paid at session boundaries (clear/compact/startup), not per turn.
    private const string ScriptContent =
        "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
        "try {\r\n" +
        "    $raw = [Console]::In.ReadToEnd()\r\n" +
        "    $api = $env:CC_DIRECTOR_API\r\n" +
        "    $sid = $env:CC_SESSION_ID\r\n" +
        "    if ($raw) {\r\n" +
        "        $evt = $raw | ConvertFrom-Json\r\n" +
        "        if ($api -and $sid -and $evt.session_id) {\r\n" +
        "            $body = @{\r\n" +
        "                claudeSessionId = $evt.session_id\r\n" +
        "                transcriptPath  = $evt.transcript_path\r\n" +
        "                hookEvent       = $evt.hook_event_name\r\n" +
        "                source          = $evt.source\r\n" +
        "            } | ConvertTo-Json -Compress\r\n" +
        "            Invoke-RestMethod -Uri \"$api/sessions/$sid/claude-hook\" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 3 | Out-Null\r\n" +
        "        }\r\n" +
        "    }\r\n" +
        // Surface the launch-time fleet preamble into the session's context. SessionStart's
        // additionalContext is injected by Claude at startup/resume/clear/compact - exactly the
        // moments the agent's memory of the fleet is otherwise empty - so it learns its identity
        // and the cc-* commands instantly, with no skill lookup and zero turn cost. The text is
        // owned by the Director (GET /fleet-preamble); the script just relays it.
        "    if ($api -and $sid) {\r\n" +
        "        $preamble = Invoke-RestMethod -Uri \"$api/sessions/$sid/fleet-preamble\" -TimeoutSec 3\r\n" +
        "        if ($preamble) {\r\n" +
        "            $out = @{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = [string]$preamble } } | ConvertTo-Json -Compress\r\n" +
        "            [Console]::Out.Write($out)\r\n" +
        "        }\r\n" +
        "    }\r\n" +
        "} catch { }\r\n" +
        "exit 0\r\n";

    /// <summary>The hook event sources we register a SessionStart hook for. These are the
    /// moments Claude can switch to a new session id / transcript file.</summary>
    private static readonly string[] SessionStartMatchers = { "startup", "resume", "clear", "compact" };

    /// <summary>
    /// Ensure the hook script and settings file exist under the per-user Director data dir,
    /// and return the absolute settings-file path to pass to Claude via <c>--settings</c>.
    /// Returns null if the files could not be written, in which case the caller launches the
    /// session without hook-based pointer tracking (the session still starts).
    /// </summary>
    public static string? EnsureInstalled() => EnsureInstalled(DefaultDirectory());

    /// <summary>Testable overload that writes the hook files under <paramref name="directory"/>.</summary>
    public static string? EnsureInstalled(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var scriptPath = Path.Combine(directory, "report-session.ps1");
            File.WriteAllText(scriptPath, ScriptContent);

            var settingsPath = Path.Combine(directory, "hooks-settings.json");
            File.WriteAllText(settingsPath, BuildSettingsJson(scriptPath));

            return settingsPath;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeHookInstaller] EnsureInstalled failed for '{directory}': {ex.Message}");
            return null;
        }
    }

    private static string DefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "claude-hooks");

    private static string BuildSettingsJson(string scriptPath)
    {
        // Shell form (single command string), which works whether Claude runs hooks through
        // cmd.exe or sh on Windows. The script path is quoted; System.Text.Json escapes the
        // backslashes when it serializes the string.
        var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
        var hook = new { type = "command", command, timeout = 10 };

        var sessionStart = new object[SessionStartMatchers.Length];
        for (var i = 0; i < SessionStartMatchers.Length; i++)
            sessionStart[i] = new { matcher = SessionStartMatchers[i], hooks = new[] { hook } };

        var settings = new
        {
            hooks = new Dictionary<string, object[]>
            {
                ["SessionStart"] = sessionStart,
            },
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }
}
