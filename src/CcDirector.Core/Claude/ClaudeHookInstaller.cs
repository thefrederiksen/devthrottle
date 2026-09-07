using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Writes the Claude Code hook files the Director uses to track a session's live Claude
/// session id and transcript path across <c>/clear</c> and auto-compaction, and to inject the
/// fleet preamble into the session's context.
///
/// Claude mints a NEW session id (and a new transcript .jsonl) when the user runs
/// <c>/clear</c> or when the context auto-compacts. The Director only knows the FIRST id
/// (it preassigns it with <c>--session-id</c>), so after a clear its pointer goes stale.
/// A <c>SessionStart</c> hook (matchers startup/resume/clear/compact) fires in the
/// interactive session and hands the current <c>session_id</c> and <c>transcript_path</c>
/// to the hook command.
///
/// REMOVE-THE-NETWORK-PORT MISSION, PHASE 3: BOTH JOBS ARE NOW FILES, NOT HTTP CALLS.
/// The script used to POST the event to the Director and GET the preamble back from it, which
/// meant a session hook was one of the last things needing a listening port on this machine.
/// It now writes the event to the file named by <c>CC_SESSION_POINTER_FILE</c>, which the
/// Director watches, and prints the file named by <c>CC_SESSION_PREAMBLE_FILE</c>, which the
/// Director MAINTAINS. So the script needs no address, no credential and no port - and three
/// classes of defect go with them: an unauthenticated call answered 401 in silence, a server
/// error body printed to stdout and reaching the agent dressed as the preamble, and the two
/// platforms disagreeing about the response shape.
///
/// The hook files are STATIC and shared across all Claude sessions: the script reads its two
/// per-session paths from the environment the Director injects, so nothing per-session is baked
/// into them and it no-ops entirely in the user's own (non-Director) Claude sessions. Passing
/// this settings file via <c>--settings</c> MERGES with the user's own hooks (it never replaces
/// them - see Claude Code issue #11392), so the user's hooks keep running too.
///
/// The hook command is per-platform: PowerShell on Windows, POSIX shell on macOS/Linux (where
/// PowerShell does not exist, which used to silently disable transcript tracking and leave
/// session history permanently empty on those platforms).
/// </summary>
public static class ClaudeHookInstaller
{
    // PowerShell (Windows): drop the hook event where the Director watches for it, then print the
    // preamble file it maintains. Must never block or fail the session - it swallows all errors and
    // exits 0. Only SessionStart fires it, so the PowerShell startup cost is paid at session
    // boundaries (clear/compact/startup), not per turn.
    //
    // The event is written VERBATIM. Nothing here parses or rebuilds Claude's JSON, so this script
    // cannot get the mapping wrong and cannot drift from the POSIX one beside it - the Director parses
    // the raw snake_case shape in testable C# (ClaudeHookEventParser).
    //
    // Written to a sibling temporary file and MOVED over the destination, so the Director's watcher
    // never reads a half-written event. Move-Item -Force, not [File]::Move with an overwrite flag:
    // this runs under Windows PowerShell 5.1 on .NET Framework, where that overload does not exist.
    private const string PowerShellScriptContent =
        "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
        "try {\r\n" +
        "    $raw = [Console]::In.ReadToEnd()\r\n" +
        "    $ptr = $env:CC_SESSION_POINTER_FILE\r\n" +
        "    if ($raw -and $ptr) {\r\n" +
        "        $tmp = [System.IO.Path]::ChangeExtension($ptr, '.tmp')\r\n" +
        "        [System.IO.File]::WriteAllText($tmp, $raw)\r\n" +
        "        Move-Item -LiteralPath $tmp -Destination $ptr -Force\r\n" +
        "    }\r\n" +
        // Surface the fleet preamble into the session's context. SessionStart's additionalContext is
        // injected by Claude at startup/resume/clear/compact - exactly the moments the agent's memory
        // of the fleet is otherwise empty - so it learns its identity and the cc-* commands instantly,
        // with no skill lookup and zero turn cost. The Director keeps this file current, so a clear
        // hours after launch delivers the user's CURRENT injected text and the skills published since.
        // An empty (or absent) file means inject nothing, and is the only thing that reliably does.
        "    $pre = $env:CC_SESSION_PREAMBLE_FILE\r\n" +
        "    if ($pre -and (Test-Path -LiteralPath $pre)) {\r\n" +
        "        $out = [System.IO.File]::ReadAllText($pre)\r\n" +
        "        if ($out) { [Console]::Out.Write($out) }\r\n" +
        "    }\r\n" +
        "} catch { }\r\n" +
        "exit 0\r\n";

    // POSIX shell (macOS/Linux): the same contract, and now literally the same two steps, because
    // neither side has to understand JSON any more. This is what removing the HTTP call bought: the
    // shell script used to forward the event to the Director because it could not parse JSON, and had
    // to fetch a PRE-WRAPPED preamble because it could not build JSON either. Writing bytes and
    // printing bytes needs neither. Best-effort throughout - it always exits 0.
    private const string ShellScriptContent =
        "#!/bin/sh\n" +
        "# Claude Code SessionStart hook, written by DevThrottle (ClaudeHookInstaller).\n" +
        "# Drops the current Claude session id + transcript path where the Director watches for it,\n" +
        // Deliberately does not name the JSON field: HookScriptContractTests forbids the envelope's own
        // field names anywhere in these scripts, because their presence is how you would spot a script
        // that had gone back to BUILDING the envelope instead of printing the file that already holds it.
        // A comment that mentioned them would make that guard ambiguous.
        "# and prints the maintained fleet preamble file, which is already the finished hook output.\n" +
        "# Best-effort: always exits 0.\n" +
        "raw=\"$(cat 2>/dev/null || true)\"\n" +
        "if [ -n \"$raw\" ] && [ -n \"$CC_SESSION_POINTER_FILE\" ]; then\n" +
        // Temporary file then mv, so the Director's watcher never reads half an event. The temporary
        // name ends in .tmp precisely so the watcher's *.json filter cannot see it.
        "    tmp=\"${CC_SESSION_POINTER_FILE%.json}.tmp\"\n" +
        "    if printf '%s' \"$raw\" > \"$tmp\" 2>/dev/null; then\n" +
        "        mv -f \"$tmp\" \"$CC_SESSION_POINTER_FILE\" 2>/dev/null || rm -f \"$tmp\" 2>/dev/null || true\n" +
        "    fi\n" +
        "fi\n" +
        // -s, not -e: "there is something to print". An empty preamble file means inject nothing, so
        // cat-ing it would be harmless but printing nothing at all is the honest answer.
        "if [ -n \"$CC_SESSION_PREAMBLE_FILE\" ] && [ -s \"$CC_SESSION_PREAMBLE_FILE\" ]; then\n" +
        "    cat \"$CC_SESSION_PREAMBLE_FILE\" 2>/dev/null || true\n" +
        "fi\n" +
        "exit 0\n";

    /// <summary>The hook event sources we register a SessionStart hook for. These are the
    /// moments Claude can switch to a new session id / transcript file.</summary>
    private static readonly string[] SessionStartMatchers = { "startup", "resume", "clear", "compact" };

    /// <summary>
    /// Ensure the hook script and settings file exist under the per-user Director data dir,
    /// and return the absolute settings-file path to pass to Claude via <c>--settings</c>.
    /// Returns null if the files could not be written. THE CALLER NO LONGER LAUNCHES ANYWAY: this
    /// hook is both the session-pointer tracking and the channel that surfaces the fleet preamble,
    /// so a null here means the session would not be told the rules it runs under, and
    /// <c>SessionManager.CreateSession</c> refuses to start it and says why.
    ///
    /// This comment used to end "the session still starts", stated as a reassurance. It was the
    /// defect: the third state was seen, its consequence understood well enough to write down, and
    /// shipped as a note rather than fixed.
    /// </summary>
    public static string? EnsureInstalled() => EnsureInstalled(DefaultDirectory(), OperatingSystem.IsWindows());

    /// <summary>Testable overload that writes the hook files under <paramref name="directory"/>.</summary>
    public static string? EnsureInstalled(string directory) => EnsureInstalled(directory, OperatingSystem.IsWindows());

    /// <summary>
    /// Testable overload that also pins the platform flavour. Windows gets the PowerShell
    /// script; everything else gets the POSIX shell script (issue: the PowerShell command
    /// can never run on macOS/Linux, which silently killed transcript tracking there).
    /// </summary>
    public static string? EnsureInstalled(string directory, bool forWindows)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var scriptPath = forWindows
                ? Path.Combine(directory, "report-session.ps1")
                : Path.Combine(directory, "report-session.sh");
            File.WriteAllText(scriptPath, forWindows ? PowerShellScriptContent : ShellScriptContent);

            var settingsPath = Path.Combine(directory, "hooks-settings.json");
            File.WriteAllText(settingsPath, BuildSettingsJson(scriptPath, forWindows));

            return settingsPath;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeHookInstaller] EnsureInstalled failed for '{directory}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Where the Claude hook files are written - inside the Director's OWN per-user data directory,
    /// not the user's Claude configuration. PUBLIC so a refusal can name the exact directory.
    /// </summary>
    public static string HookDirectory() => CcStorage.ClaudeHooks();

    private static string DefaultDirectory() => CcStorage.ClaudeHooks();

    private static string BuildSettingsJson(string scriptPath, bool forWindows)
    {
        // Shell form (single command string), which works whether Claude runs hooks through
        // cmd.exe or sh. The script path is quoted (per-user data dirs contain spaces, e.g.
        // "Application Support" on macOS); System.Text.Json escapes any backslashes when it
        // serializes the string.
        var command = forWindows
            ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""
            : $"/bin/sh \"{scriptPath}\"";
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
