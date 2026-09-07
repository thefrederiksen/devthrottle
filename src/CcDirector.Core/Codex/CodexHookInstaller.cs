using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Codex;

/// <summary>
/// Installs the Codex SessionStart hook that surfaces the fleet preamble into a Codex session's
/// context, the Codex analogue of <see cref="Claude.ClaudeHookInstaller"/>.
///
/// Codex fires a <c>SessionStart</c> hook with a <c>source</c> matcher (startup/resume/clear/compact)
/// whose command may print <c>hookSpecificOutput.additionalContext</c> - the same shape Claude uses.
/// Unlike Claude (which takes a private settings file via <c>--settings</c>), Codex reads hooks only
/// from fixed locations, so this MERGES our SessionStart entry into the user-layer
/// <c>~/.codex/hooks.json</c> (honoring <c>CODEX_HOME</c>) without disturbing the user's own hooks.
/// The hook script is static and shared: it reads the per-session <c>CC_SESSION_PREAMBLE_FILE</c> the
/// Director injects, so it no-ops in the user's own (non-Director) Codex sessions. The Director appends
/// <c>--dangerously-bypass-hook-trust</c> at launch so the hook runs without a per-user trust prompt
/// (verified live on codex 0.141.0).
///
/// Remove-the-network-port mission, phase 3: this used to GET the preamble from the Director's Control
/// API and wrap it as hook output itself. It now prints the file the Director MAINTAINS for the session,
/// which is already the finished envelope - so Codex and Claude receive the identical bytes from the
/// identical file, and neither the port nor a credential is involved.
/// </summary>
public static class CodexHookInstaller
{
    // PowerShell: print the preamble file the Director maintains for this session. The hook does not
    // need the event payload, so it deliberately does not read stdin; Codex 0.142+ can fail interactive
    // startup if a hook command consumes or probes the terminal stdin. Must never block or fail the
    // session - swallows all errors and exits 0. Codex re-fires SessionStart on /clear and /compact,
    // and the Director keeps the file current, so a clear hours after launch injects the user's CURRENT
    // text rather than a launch-time snapshot. An empty or absent file means inject nothing.
    private const string ScriptContent =
        "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
        "try {\r\n" +
        "    $pre = $env:CC_SESSION_PREAMBLE_FILE\r\n" +
        "    if ($pre -and (Test-Path -LiteralPath $pre)) {\r\n" +
        "        $out = [System.IO.File]::ReadAllText($pre)\r\n" +
        "        if ($out) { [Console]::Out.Write($out) }\r\n" +
        "    }\r\n" +
        "} catch { }\r\n" +
        "exit 0\r\n";

    // POSIX shell (macOS/Linux): the same two lines. Codex runs on those platforms and this installer
    // had no branch at all - it wrote a "powershell" command everywhere, which is not a runnable command
    // on macOS or Linux (PowerShell Core is "pwsh" there, and is usually absent). The Claude installer
    // next door has always made this distinction; independent inspection found that this one did not, so
    // every Codex session outside Windows silently got no preamble.
    //
    // -s, not -e: "there is something to print". An empty preamble file means inject nothing.
    private const string ShellScriptContent =
        "#!/bin/sh\n" +
        "# Codex SessionStart hook, written by DevThrottle (CodexHookInstaller).\n" +
        "# Prints the maintained fleet preamble file, which is already the finished hook output.\n" +
        "# Best-effort: always exits 0. Deliberately does not read stdin - Codex 0.142+ can fail\n" +
        "# interactive startup if a hook command consumes or probes the terminal stdin.\n" +
        "if [ -n \"$CC_SESSION_PREAMBLE_FILE\" ] && [ -s \"$CC_SESSION_PREAMBLE_FILE\" ]; then\n" +
        "    cat \"$CC_SESSION_PREAMBLE_FILE\" 2>/dev/null || true\n" +
        "fi\n" +
        "exit 0\n";

    /// <summary>The SessionStart sources Codex can switch context on - the moments we want the
    /// preamble (re-)injected. Same set Claude uses.</summary>
    private const string Matcher = "startup|resume|clear|compact";

    /// <summary>
    /// The marker that identifies an entry as OURS in a hooks file we share with the user.
    ///
    /// Idempotence used to compare the whole command string, and the script path inside it is scoped to
    /// the NAMED INSTANCE while the hooks file is global - so every instance looked like a different
    /// hook and appended another one. A machine with a default and a "work" instance ended up with two
    /// SessionStart entries reading the same variable, and a Codex session got its preamble twice.
    /// Renaming or removing an instance left its command behind for ever.
    ///
    /// Matching on this marker instead means there is exactly ONE of our entries in the file at any
    /// time, whichever instance wrote it last, and the user's own hooks are still untouched.
    ///
    /// It is carried in the SCRIPT FILE NAME, so it is present in the command by construction and
    /// cannot drift away from it.
    /// </summary>
    private const string OwnerMarker = "cc-director-preamble";

    /// <summary>
    /// What our script was called before the marker existed. Entries naming it are still OURS and are
    /// cleaned up on the next install - otherwise every machine that already has one would keep it
    /// beside the new entry and get the preamble twice, which is the exact defect being fixed.
    /// Removable once no installed machine can still be carrying one.
    /// </summary>
    private const string LegacyScriptName = "report-preamble";

    private static bool IsOurs(string? command) =>
        command is not null
        && (command.Contains(OwnerMarker, StringComparison.OrdinalIgnoreCase)
            || command.Contains(LegacyScriptName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The launch flag the Director appends so the hook runs without a per-user trust
    /// prompt. Exposed so SessionManager and tests share one source of truth.</summary>
    public const string BypassTrustFlag = "--dangerously-bypass-hook-trust";

    /// <summary>
    /// Ensure the hook script exists and our SessionStart entry is present in the user's Codex
    /// hooks.json. Returns true on success (the Director should then append
    /// <see cref="BypassTrustFlag"/> to the Codex command); false if anything failed. A false NO
    /// LONGER launches the session without the preamble hook: without it the fleet preamble never
    /// reaches the session, so <c>SessionManager.CreateSession</c> refuses to start it and says why.
    ///
    /// This comment used to end "the session still launches, just without the preamble hook", which
    /// named the consequence exactly and then shipped it.
    /// </summary>
    public static bool EnsureInstalled() =>
        EnsureInstalled(DefaultScriptDirectory(), DefaultCodexHooksPath(), OperatingSystem.IsWindows());

    /// <summary>Testable overload that writes under explicit paths, on this machine's platform.</summary>
    public static bool EnsureInstalled(string scriptDirectory, string hooksJsonPath) =>
        EnsureInstalled(scriptDirectory, hooksJsonPath, OperatingSystem.IsWindows());

    /// <summary>
    /// Testable overload that also pins the platform flavour, exactly as the Claude installer does.
    /// Windows gets the PowerShell script; everything else gets the POSIX shell script. The flavour is
    /// a parameter and not a platform check inside, so the macOS and Linux form can be proven from a
    /// Windows test run - which is the only way this defect would have been caught before shipping.
    /// </summary>
    public static bool EnsureInstalled(string scriptDirectory, string hooksJsonPath, bool forWindows)
    {
        try
        {
            Directory.CreateDirectory(scriptDirectory);
            var scriptPath = Path.Combine(scriptDirectory,
                forWindows ? OwnerMarker + ".ps1" : OwnerMarker + ".sh");
            File.WriteAllText(scriptPath, forWindows ? ScriptContent : ShellScriptContent);

            var command = forWindows
                ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""
                : $"/bin/sh \"{scriptPath}\"";
            MergeSessionStartHook(hooksJsonPath, command);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodexHookInstaller] EnsureInstalled failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Put our SessionStart command hook into <paramref name="hooksJsonPath"/>, preserving every hook
    /// the user has. Exactly ONE of ours exists afterwards, whatever was there before. Writes
    /// atomically (temp file then replace) so a crash mid-write cannot corrupt the user's hooks.
    /// </summary>
    private static void MergeSessionStartHook(string hooksJsonPath, string command)
    {
        var root = LoadRoot(hooksJsonPath);

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        if (hooks["SessionStart"] is not JsonArray sessionStart)
        {
            sessionStart = new JsonArray();
            hooks["SessionStart"] = sessionStart;
        }

        // REMOVE EVERY ENTRY OF OURS FIRST, then add one. Rewriting rather than skipping is what makes
        // this converge: a file that already carries a stale entry - a different instance's script path,
        // or the Windows command on a machine that has since moved to the shell one - ends up with the
        // CURRENT command and only that. Skipping when something of ours was present would leave the
        // stale entry in place for ever, which is the shape that produced duplicates.
        var ours = sessionStart
            .OfType<JsonObject>()
            .Where(entry => entry["hooks"] is JsonArray inner
                            && inner.OfType<JsonObject>().Any(h => IsOurs(h["command"]?.GetValue<string>())))
            .ToList();

        var alreadyCorrect = ours.Count == 1
            && ours[0]["hooks"] is JsonArray only
            && only.OfType<JsonObject>().Count() == 1
            && only.OfType<JsonObject>().Single()["command"]?.GetValue<string>() == command
            && ours[0]["matcher"]?.GetValue<string>() == Matcher;
        if (alreadyCorrect)
            return;

        foreach (var stale in ours)
            sessionStart.Remove(stale);

        sessionStart.Add(new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["timeout"] = 10,
            }),
        });

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(hooksJsonPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = hooksJsonPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, hooksJsonPath, overwrite: true);
    }

    private static JsonObject LoadRoot(string hooksJsonPath)
    {
        if (!File.Exists(hooksJsonPath))
            return new JsonObject();

        var existing = File.ReadAllText(hooksJsonPath);
        if (string.IsNullOrWhiteSpace(existing))
            return new JsonObject();

        // A malformed user hooks.json must not be clobbered: surface the error so EnsureInstalled
        // returns false and the session launches without the hook, rather than overwriting it.
        return JsonNode.Parse(existing) as JsonObject
            ?? throw new InvalidOperationException($"hooks.json is not a JSON object: {hooksJsonPath}");
    }

    private static string DefaultScriptDirectory() => CcStorage.CodexHooks();

    private static string DefaultCodexHooksPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Path.Combine(codexHome, "hooks.json");
    }
}
