using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The hook files are STATIC and shared across all Claude sessions: the scripts read their
/// per-session paths and capability token from the environment the Director injects, so nothing
/// per-session is baked into them and they no-op entirely in the user's own (non-Director) Claude
/// sessions. The state script turns supported prompt, stop, permission, notification and tool
/// lifecycle hooks into versioned atomic records in the session's state-event directory. Passing
/// this settings file via <c>--settings</c> MERGES with the user's own hooks (it never replaces
/// them - see Claude Code issue #11392), so the user's hooks keep running too.
///
/// The hook command is per-platform: PowerShell on Windows, POSIX shell on macOS/Linux (where
/// PowerShell does not exist, which used to silently disable transcript tracking and leave
/// session history permanently empty on those platforms).
///
/// SESSION RULES (part 2, phase 1, rulings P9, P12 and P16): the same settings also register a
/// PreToolUse hook for Claude Code's shell tools (<c>Bash</c>, and <c>PowerShell</c> on Windows) that
/// runs <c>cc-rule-hook</c>, the native rule evaluator. The Director CARRIES that executable inside
/// itself (<see cref="RuleHookBinary"/>), and this installer writes it into the same folder as the hook
/// scripts - so whichever Director build is running brings the hook that matches it, through install,
/// update, roll back and every slot, with nothing else to ship. It is registered for EVERY session,
/// because Claude Code reads its hooks at launch and rules arrive mid-session - but only when this build
/// carries one, because a hook pointed at a missing file errors on every shell command. It is a native
/// executable, not a script, because it runs before every shell command and PowerShell's start-up cost
/// (above) cannot be paid that often.
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

    // One state fact per invocation. Claude runs command hooks synchronously unless a hook explicitly
    // asks to be asynchronous, so the sequence file is a per-session monotonic counter without a race
    // between two invocations. A gap after a process failure is valid; reusing a number is not.
    private const string PowerShellStateScriptContent =
        "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
        "try {\r\n" +
        "    $eventName = $args[0]\r\n" +
        "    $detail = $null\r\n" +
        "    if ($eventName -in @('tool_started','tool_finished','notification')) {\r\n" +
        "        try { $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json }\r\n" +
        "        catch { $eventName = 'unsupported'; $detail = $args[0] }\r\n" +
        "        if ($eventName -eq 'notification') {\r\n" +
        "            if ($payload.notification_type -eq 'permission_prompt') { $eventName = 'permission_requested' }\r\n" +
        "            elseif ($payload.notification_type -eq 'idle_prompt') { $eventName = 'turn_ended' }\r\n" +
        "            else { $eventName = 'unsupported'; $detail = [string]$payload.notification_type; if (-not $detail) { $detail = 'Notification' } }\r\n" +
        "        }\r\n" +
        "        elseif ($eventName -eq 'tool_started') { if ($payload.tool_name -eq 'AskUserQuestion') { $eventName = 'menu_opened' } else { $eventName = 'turn_started' } }\r\n" +
        "        elseif ($eventName -eq 'tool_finished') { $eventName = 'turn_started' }\r\n" +
        "    }\r\n" +
        "    $dir = $env:CC_STATE_EVENT_DIR\r\n" +
        "    $token = $env:CC_STATE_EVENT_TOKEN\r\n" +
        "    $sessionText = $env:CC_SESSION_ID\r\n" +
        "    $session = [Guid]::Empty\r\n" +
        "    if (-not $dir -or -not $token -or $token -notmatch '^[0-9a-f]{32}$' -or\r\n" +
        "        -not [Guid]::TryParse($sessionText, [ref]$session) -or\r\n" +
        "        ([System.IO.Path]::GetFileName($dir) -ne ($sessionText + '.' + $token))) { exit 0 }\r\n" +
        "    if ($eventName -notin @('turn_started','turn_ended','permission_requested','menu_opened','menu_closed','unsupported')) { exit 0 }\r\n" +
        "    [System.IO.Directory]::CreateDirectory($dir) | Out-Null\r\n" +
        "    $versionPath = [System.IO.Path]::Combine($dir, '.claude-version')\r\n" +
        "    if (-not [System.IO.File]::Exists($versionPath)) {\r\n" +
        "        $version = ((& claude --version 2>$null | Select-Object -First 1) -replace '[\\x00-\\x1f\\\"]','').Trim()\r\n" +
        "        if (-not $version) { $version = 'unavailable' }\r\n" +
        "        if ($version.Length -gt 200) { $version = $version.Substring(0, 200) }\r\n" +
        "        [System.IO.File]::WriteAllText($versionPath, $version)\r\n" +
        "    }\r\n" +
        "    $version = [System.IO.File]::ReadAllText($versionPath)\r\n" +
        "    $lockPath = [System.IO.Path]::Combine($dir, '.claude-sequence.lock')\r\n" +
        "    $lock = $null\r\n" +
        "    for ($attempt = 0; $attempt -lt 500 -and -not $lock; $attempt++) { try { $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None) } catch { Start-Sleep -Milliseconds 10 } }\r\n" +
        "    if (-not $lock) { exit 0 }\r\n" +
        "    try {\r\n" +
        "        $counter = [System.IO.Path]::Combine($dir, '.claude-sequence')\r\n" +
        "        [long]$sequence = 0\r\n" +
        "        if ([System.IO.File]::Exists($counter) -and (-not [long]::TryParse([System.IO.File]::ReadAllText($counter), [ref]$sequence) -or $sequence -lt 0)) { exit 0 }\r\n" +
        "        $sequence++\r\n" +
        "        $counterTmp = $counter + '.' + $PID + '.tmp'\r\n" +
        "        [System.IO.File]::WriteAllText($counterTmp, $sequence.ToString([Globalization.CultureInfo]::InvariantCulture))\r\n" +
        "        Move-Item -LiteralPath $counterTmp -Destination $counter -Force\r\n" +
        "        $record = [ordered]@{ version=1; eventId=[Guid]::NewGuid().ToString(); sessionId=$session.ToString(); sequence=$sequence; occurredAtUtc=[DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture); event=$eventName; source='agent'; agent='claude'; detail=$detail; cliVersion=$version }\r\n" +
        "        $path = [System.IO.Path]::Combine($dir, $sequence.ToString([Globalization.CultureInfo]::InvariantCulture) + '.state.json')\r\n" +
        "        $tmp = $path + '.' + $PID + '.tmp'\r\n" +
        "        [System.IO.File]::WriteAllText($tmp, ($record | ConvertTo-Json -Compress))\r\n" +
        "        Move-Item -LiteralPath $tmp -Destination $path -Force\r\n" +
        "    } finally { $lock.Dispose() }\r\n" +
        "} catch { }\r\n" +
        "exit 0\r\n";

    private const string ShellStateScriptContent =
        "#!/bin/sh\n" +
        "# Claude Code state hook, written by DevThrottle. Best-effort and local-file only.\n" +
        "event_name=$1\n" +
        "detail=\n" +
        "case \"$event_name\" in\n" +
        "  tool_started|tool_finished|notification) raw=$(cat 2>/dev/null || true); compact=$(printf '%s' \"$raw\" | tr -d '\\r\\n'); case \"$compact\" in \\{*\\}) ;; *) case \"$event_name\" in tool_started) detail=PreToolUse ;; tool_finished) detail=PostToolUse ;; *) detail=Notification ;; esac; event_name=unsupported ;; esac; if [ \"$event_name\" = notification ]; then notification_type=$(printf '%s' \"$raw\" | sed -n 's/.*\"notification_type\"[[:space:]]*:[[:space:]]*\"\\([A-Za-z0-9_.:-]*\\)\".*/\\1/p' | head -n 1); case \"$notification_type\" in permission_prompt) event_name=permission_requested ;; idle_prompt) event_name=turn_ended ;; *) event_name=unsupported; detail=${notification_type:-Notification} ;; esac; elif [ \"$event_name\" = tool_started ]; then if printf '%s' \"$raw\" | grep -Eq '\"tool_name\"[[:space:]]*:[[:space:]]*\"AskUserQuestion\"'; then event_name=menu_opened; else event_name=turn_started; fi; elif [ \"$event_name\" = tool_finished ]; then event_name=turn_started; fi ;;\n" +
        "  turn_started|turn_ended|permission_requested|menu_opened|menu_closed) ;;\n" +
        "  *) exit 0 ;;\n" +
        "esac\n" +
        "case \"$CC_STATE_EVENT_TOKEN\" in *[!0-9a-f]*|'') exit 0 ;; esac\n" +
        "[ ${#CC_STATE_EVENT_TOKEN} -eq 32 ] || exit 0\n" +
        "[ -n \"$CC_SESSION_ID\" ] && [ -n \"$CC_STATE_EVENT_DIR\" ] || exit 0\n" +
        "case \"$CC_SESSION_ID\" in *[!0-9a-fA-F-]*) exit 0 ;; esac\n" +
        "[ ${#CC_SESSION_ID} -eq 36 ] || exit 0\n" +
        "[ \"$(basename \"$CC_STATE_EVENT_DIR\")\" = \"$CC_SESSION_ID.$CC_STATE_EVENT_TOKEN\" ] || exit 0\n" +
        "mkdir -p \"$CC_STATE_EVENT_DIR\" 2>/dev/null || exit 0\n" +
        "version_path=\"$CC_STATE_EVENT_DIR/.claude-version\"\n" +
        "if [ ! -f \"$version_path\" ]; then version=$(claude --version 2>/dev/null | head -n 1 | tr -cd '[:alnum:].,_+() /:-' | cut -c 1-200); [ -n \"$version\" ] || version=unavailable; printf '%s' \"$version\" > \"$version_path\" 2>/dev/null || exit 0; fi\n" +
        "version=$(cat \"$version_path\" 2>/dev/null || printf unavailable)\n" +
        "lock=\"$CC_STATE_EVENT_DIR/.claude-sequence.lock\"\n" +
        "while ! mkdir \"$lock\" 2>/dev/null; do holder=$(cat \"$lock/pid\" 2>/dev/null || true); case \"$holder\" in *[!0-9]*|'') ;; *) if ! kill -0 \"$holder\" 2>/dev/null; then rm -f \"$lock/pid\" 2>/dev/null && rmdir \"$lock\" 2>/dev/null || true; fi ;; esac; sleep 0.01; done\n" +
        "printf '%s' \"$$\" > \"$lock/pid\" 2>/dev/null || { rmdir \"$lock\" 2>/dev/null || true; exit 0; }\n" +
        "cleanup() { rm -f \"$lock/pid\" 2>/dev/null || true; rmdir \"$lock\" 2>/dev/null || true; }\n" +
        "trap 'cleanup' EXIT\n" +
        "trap 'cleanup; exit 0' HUP INT TERM\n" +
        "counter=\"$CC_STATE_EVENT_DIR/.claude-sequence\"\n" +
        "sequence=0\n" +
        "if [ -f \"$counter\" ]; then sequence=$(cat \"$counter\" 2>/dev/null) || exit 0; fi\n" +
        "case \"$sequence\" in *[!0-9]*|'') exit 0 ;; esac\n" +
        "sequence=$((sequence + 1))\n" +
        "counter_tmp=\"$counter.$$.tmp\"\n" +
        "printf '%s' \"$sequence\" > \"$counter_tmp\" 2>/dev/null || exit 0\n" +
        "mv -f \"$counter_tmp\" \"$counter\" 2>/dev/null || { rm -f \"$counter_tmp\"; exit 0; }\n" +
        "if [ -r /proc/sys/kernel/random/uuid ]; then event_id=$(cat /proc/sys/kernel/random/uuid); elif command -v uuidgen >/dev/null 2>&1; then event_id=$(uuidgen | tr 'A-F' 'a-f'); else exit 0; fi\n" +
        "occurred=$(date -u '+%Y-%m-%dT%H:%M:%SZ')\n" +
        "path=\"$CC_STATE_EVENT_DIR/$sequence.state.json\"\n" +
        "tmp=\"$path.$$.tmp\"\n" +
        "if [ -n \"$detail\" ]; then detail_json=\"\\\"$detail\\\"\"; else detail_json=null; fi\n" +
        "printf '{\"version\":1,\"eventId\":\"%s\",\"sessionId\":\"%s\",\"sequence\":%s,\"occurredAtUtc\":\"%s\",\"event\":\"%s\",\"source\":\"agent\",\"agent\":\"claude\",\"detail\":%s,\"cliVersion\":\"%s\"}' \"$event_id\" \"$CC_SESSION_ID\" \"$sequence\" \"$occurred\" \"$event_name\" \"$detail_json\" \"$version\" > \"$tmp\" 2>/dev/null || exit 0\n" +
        "mv -f \"$tmp\" \"$path\" 2>/dev/null || rm -f \"$tmp\" 2>/dev/null || true\n" +
        "exit 0\n";

    /// <summary>Claude Code's shell tools, as its PreToolUse matcher names them. Claude Code 2.1.302 lists
    /// exactly these two as shell tools; PowerShell exists on Windows only, so naming it costs nothing
    /// elsewhere. Anchored, so no other tool whose name merely CONTAINS one of the two ever runs the
    /// evaluator: a live run on 2.1.302 showed this anchored form fires on Bash, and that the matcher is
    /// read as a regular expression (<c>^(Bas)$</c> did not fire).</summary>
    public const string ShellToolMatcher = "^(Bash|PowerShell)$";

    /// <summary>The rule evaluator's file name on each platform.</summary>
    public static string RuleHookExecutableName(bool forWindows) => forWindows ? "cc-rule-hook.exe" : "cc-rule-hook";

    /// <summary>Where the installer writes the rule evaluator: beside the hook scripts in <paramref name="directory"/>.</summary>
    public static string RuleHookPathIn(string directory, bool forWindows)
        => Path.Combine(directory, RuleHookExecutableName(forWindows));

    /// <summary>The hook event sources we register a SessionStart hook for. These are the
    /// moments Claude can switch to a new session id / transcript file.</summary>
    private static readonly string[] SessionStartMatchers = { "startup", "resume", "clear", "compact" };

    /// <summary>
    /// Ensure the hook script and settings file exist under the per-user Director data dir,
    /// and return the absolute settings-file path to pass to Claude via <c>--settings</c>.
    /// Returns null if the files could not be written, in which case the caller launches the
    /// session without hook-based pointer tracking (the session still starts).
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
        => EnsureInstalled(directory, forWindows, RuleHookBinary.Carried());

    /// <summary>
    /// Testable overload that also takes the rule evaluator's bytes - what this Director build carries, or
    /// null when it carries none. The PreToolUse hook is registered only when the evaluator is on disk
    /// afterwards.
    /// </summary>
    public static string? EnsureInstalled(string directory, bool forWindows, byte[]? ruleHook)
        => EnsureInstalled(directory, forWindows, ruleHook, ReplaceFile);

    /// <summary>Testable overload whose final replace of the evaluator can be made to fail, the way Windows
    /// refuses to replace an executable that is running.</summary>
    internal static string? EnsureInstalled(string directory, bool forWindows, byte[]? ruleHook, Action<string, string> replace)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var scriptPath = ScriptPathIn(directory, forWindows);
            File.WriteAllText(scriptPath, forWindows ? PowerShellScriptContent : ShellScriptContent);
            var stateScriptPath = StateScriptPathIn(directory, forWindows);
            File.WriteAllText(stateScriptPath, forWindows ? PowerShellStateScriptContent : ShellStateScriptContent);

            var settingsPath = Path.Combine(directory, SharedSettingsFileName);
            var ruleHookPath = InstallRuleHook(directory, forWindows, ruleHook, replace);
            File.WriteAllText(settingsPath, BuildSettingsJson(scriptPath, stateScriptPath, forWindows, ruleHookPath));

            return settingsPath;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeHookInstaller] EnsureInstalled failed for '{directory}' FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>The one settings file every Claude Code session on the machine shares.</summary>
    public const string SharedSettingsFileName = "hooks-settings.json";

    /// <summary>The name of a seat's OWN settings file, in its kit folder (Factory Definition mission, phase 5).</summary>
    public const string SessionSettingsFileName = "settings.json";

    /// <summary>
    /// A SEAT'S OWN SETTINGS FILE (Factory Definition mission, phase 5, design section 5; phase 0 proof (a), finding 3).
    /// Writes <c>&lt;kitDirectory&gt;/settings.json</c>: exactly the hooks content <see cref="EnsureInstalled(string, bool)"/>
    /// writes into the shared file for <paramref name="hooksDirectory"/> - the same builder, the same script path - plus
    /// <paramref name="permissions"/>, the seat's rules. Returns the file's path, which the session is given as its one
    /// <c>--settings</c> in place of the shared file.
    ///
    /// The rules NEVER go into the shared file: every Claude Code session on the machine is launched with it, so a merge
    /// there would hand this seat's limits to every other session, and the next launch rewrites it whole, erasing them
    /// under a seat still running. A copy in the kit is seen by this session alone and goes with the kit.
    ///
    /// Unlike <see cref="EnsureInstalled(string, bool)"/>, a failure is THROWN: a seat with rules is never started
    /// without them.
    /// </summary>
    public static string WriteSessionSettings(string kitDirectory, string hooksDirectory, bool forWindows, JsonObject permissions)
        => WriteSessionSettings(kitDirectory, hooksDirectory, forWindows, permissions, RuleHookBinary.Carried());

    /// <summary>Testable overload of <see cref="WriteSessionSettings(string, string, bool, JsonObject)"/> that says
    /// whether this Director build carries the rule evaluator. The seat's file registers it exactly when the shared
    /// file does: the build carries one AND <see cref="EnsureInstalled(string, bool)"/> left it on disk.</summary>
    public static string WriteSessionSettings(string kitDirectory, string hooksDirectory, bool forWindows, JsonObject permissions, byte[]? ruleHook)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kitDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hooksDirectory);
        ArgumentNullException.ThrowIfNull(permissions);

        // The seat's file is written BEFORE the shared install in the Director's launch order
        // (PrepareFactoryKitLaunch runs first), so on a fresh instance nothing has written the evaluator yet.
        // It installs it itself - the same compare-before-write, so the shared install after it is a no-op.
        var ruleHookPath = InstallRuleHook(hooksDirectory, forWindows, ruleHook, ReplaceFile);

        var settings = JsonSerializer.SerializeToNode(BuildSettings(
            ScriptPathIn(hooksDirectory, forWindows), StateScriptPathIn(hooksDirectory, forWindows), forWindows, ruleHookPath))!.AsObject();
        settings["permissions"] = permissions.DeepClone();

        var path = Path.Combine(kitDirectory, SessionSettingsFileName);
        File.WriteAllText(path, settings.ToJsonString(SettingsJsonOptions));
        FileLog.Write($"[ClaudeHookInstaller] WriteSessionSettings: wrote {path} (hooks from {hooksDirectory})");
        return path;
    }

    /// <summary>The directory the shared hook files go in on this machine.</summary>
    public static string DefaultDirectory() => CcStorage.ClaudeHooks();

    private static string ScriptPathIn(string directory, bool forWindows) => forWindows
        ? Path.Combine(directory, "report-session.ps1")
        : Path.Combine(directory, "report-session.sh");

    private static string StateScriptPathIn(string directory, bool forWindows) => forWindows
        ? Path.Combine(directory, "report-state.ps1")
        : Path.Combine(directory, "report-state.sh");

    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };

    private static string BuildSettingsJson(string scriptPath, string stateScriptPath, bool forWindows, string? ruleHookPath)
        => JsonSerializer.Serialize(BuildSettings(scriptPath, stateScriptPath, forWindows, ruleHookPath), SettingsJsonOptions);

    // THE ONE BUILDER of the hooks content: the shared file is it serialized, and a seat's own file is it plus the
    // seat's permissions (WriteSessionSettings). Two builders would drift, and a seat would lose the pointer tracking
    // and the preamble every other session has.
    private static object BuildSettings(string scriptPath, string stateScriptPath, bool forWindows, string? ruleHookPath)
    {
        // Shell form (single command string, no args). Claude Code hands it to sh on Linux and macOS;
        // on Windows to Git Bash when it is installed (what the Session Rules phase 1 Windows proof
        // measured) and to PowerShell when it is not (the vendor's hooks documentation). Both forms
        // below are valid command lines in all three shells: they start with a bare program name, and
        // inside double quotes a backslash before a letter stays literal. The script path is quoted
        // (per-user data dirs contain spaces, e.g. "Application Support" on macOS); System.Text.Json
        // escapes any backslashes when it serializes the string.
        var command = forWindows
            ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""
            : $"/bin/sh \"{scriptPath}\"";
        var hook = new { type = "command", command, timeout = 10 };
        object StateHook(string fact)
        {
            var stateCommand = forWindows
                ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{stateScriptPath}\" {fact}"
                : $"/bin/sh \"{stateScriptPath}\" {fact}";
            return new { type = "command", command = stateCommand, timeout = 10 };
        }

        var sessionStart = new object[SessionStartMatchers.Length];
        for (var i = 0; i < SessionStartMatchers.Length; i++)
            sessionStart[i] = new { matcher = SessionStartMatchers[i], hooks = new[] { hook } };

        var hooks = new Dictionary<string, object[]>
        {
            ["SessionStart"] = sessionStart,
            ["UserPromptSubmit"] = [new { hooks = new[] { StateHook("turn_started") } }],
            ["Stop"] = [new { hooks = new[] { StateHook("turn_ended") } }],
            ["PreToolUse"] = [new { hooks = new[] { StateHook("tool_started") } }],
            ["PostToolUse"] = [new { hooks = new[] { StateHook("tool_finished") } }],
            ["PostToolUseFailure"] = [new { hooks = new[] { StateHook("tool_finished") } }],
            ["PermissionRequest"] = [new { hooks = new[] { StateHook("permission_requested") } }],
            ["Notification"] = [new { hooks = new[] { StateHook("notification") } }],
        };

        // The rule evaluator is registered in EXEC form: args present (empty), so Claude Code spawns the
        // command as the program itself, with no shell on any platform and no quoting to survive one.
        // Shell form would reach PowerShell on a Windows machine without Git Bash, which reads a quoted
        // path as a string rather than a call, and where a failing shell-form PreToolUse hook can block
        // the tool call (whole-phase review, finding 1). Null means there is no evaluator on disk to run
        // (InstallRuleHook has already said why, loudly).
        if (ruleHookPath is not null)
        {
            var ruleHook = new { type = "command", command = ruleHookPath, args = Array.Empty<string>(), timeout = 10 };
            hooks["PreToolUse"] =
            [
                new { hooks = new[] { StateHook("tool_started") } },
                new { matcher = ShellToolMatcher, hooks = new object[] { ruleHook } },
            ];
        }

        return new { hooks };
    }

    private static void ReplaceFile(string source, string destination) => File.Move(source, destination, overwrite: true);

    /// <summary>
    /// Write the evaluator this build carries into <paramref name="directory"/> and return the path to
    /// register, or null when there is nothing on disk to run.
    ///
    /// Compare before write, so a launch with the same build touches nothing. Otherwise written to a sibling
    /// temporary file, made executable on Linux and macOS, and moved over the old one in one step, so a hook
    /// starting at that moment runs either the old evaluator or the new one, never half of one. Windows
    /// refuses to replace an executable that is running - another session's hook may be - and that is
    /// never fatal (ruling P16): it is logged, the replace is tried again at the next session launch, and
    /// meanwhile the evaluator already there, from the previous build, is a working hook and stays
    /// registered.
    ///
    /// NOTHING here may throw. The evaluator is optional and the SessionStart hooks written beside it are
    /// not: an exception escaping to <see cref="EnsureInstalled(string, bool)"/>'s catch would launch the
    /// session with no --settings at all, losing the session pointer and the preamble that worked before
    /// the rule hook existed. Every failure resolves to "register the evaluator already on disk" or
    /// "register none", and says which in the log.
    /// </summary>
    private static string? InstallRuleHook(string directory, bool forWindows, byte[]? ruleHook, Action<string, string> replace)
    {
        if (ruleHook is null)
        {
            FileLog.Write("[ClaudeHookInstaller] InstallRuleHook: RULE HOOK MISSING - this Director build carries no cc-rule-hook " +
                "(it was built without the native compiler, or for a platform that does not get one yet), so NO session rule is " +
                "evaluated before shell commands in sessions started now.");
            return null;
        }

        var path = RuleHookPathIn(directory, forWindows);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(ruleHook))
                return path;

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(tmp, ruleHook);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            replace(tmp, path);
            FileLog.Write($"[ClaudeHookInstaller] InstallRuleHook: wrote {path} ({ruleHook.Length} bytes)");
            return path;
        }
        catch (Exception ex)
        {
            RemoveTemporary(tmp);
            var kept = File.Exists(path);
            FileLog.Write($"[ClaudeHookInstaller] InstallRuleHook FAILED: could not check or replace {path} ({ex.GetType().Name}: {ex.Message}); it is tried again at the next " +
                (kept
                    ? "session launch, and the evaluator already there, from an earlier build, stays registered meanwhile."
                    : "session launch. There is NO evaluator on disk, so NO session rule is evaluated in sessions started now."));
            return kept ? path : null;
        }
    }

    // A failed delete is logged and left: a stray temporary file is harmless, and it must not turn a
    // handled failure back into one that costs the session its hooks.
    private static void RemoveTemporary(string tmp)
    {
        try
        {
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            // not-an-error: a stray temporary file beside the evaluator is harmless, and the failure that led here is already reported
            FileLog.Write($"[ClaudeHookInstaller] RemoveTemporary: could not delete {tmp}: {ex.Message}");
        }
    }
}
