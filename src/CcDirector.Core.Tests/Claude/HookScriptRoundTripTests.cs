using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Claude;
using CcDirector.Core.Codex;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Remove-the-network-port mission, phase 3: END-TO-END proof of the session-hook channel, with the REAL
/// hook script executed by the REAL interpreter, exactly as the agent's own runtime executes it.
///
/// The ACTUAL report-session.ps1 (or report-session.sh) that <see cref="ClaudeHookInstaller"/> writes is
/// run with the two environment variables the Director stamps and fed Claude's raw hook event on stdin -
/// what Claude Code does at SessionStart on startup, resume, clear and compact. Then both halves are
/// asserted: the preamble came back on stdout as ready-made hook output, and the Director's live watcher
/// picked the dropped pointer up and moved the session onto the rotated transcript.
///
/// This is the test that would catch a script that is syntactically fine and does nothing. Every one of
/// these scripts swallows all errors and exits 0 - it must, because a hook may never take a session down -
/// so a broken one looks exactly like a working one from the outside. The only way to know is to run it
/// and look at what came out.
///
/// It lives in the serialised half of the Core suite because it spawns a process and uses a real
/// FileSystemWatcher. It used to need a running Control API host and therefore sat in the Gateway suite
/// behind that suite's machine-wide lock; the whole point of this phase is that it does not need one now.
/// </summary>
[Collection("ProcessWideState")]
public sealed class HookScriptRoundTripTests : IDisposable
{
    private readonly string _dir;
    private readonly string _preambleDir;
    private readonly string _pointerDir;
    private readonly string _hookDir;
    private readonly SessionManager _sessions;
    private readonly SessionPointerWatcher _watcher;

    public HookScriptRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-hook-roundtrip-" + Guid.NewGuid().ToString("N"));
        _preambleDir = Path.Combine(_dir, "session-preambles");
        _pointerDir = Path.Combine(_dir, "session-pointers");
        _hookDir = Path.Combine(_dir, "hooks");
        Directory.CreateDirectory(_preambleDir);
        Directory.CreateDirectory(_pointerDir);
        Directory.CreateDirectory(_hookDir);

        _sessions = new SessionManager(new AgentOptions());
        _watcher = new SessionPointerWatcher(_sessions, _pointerDir);
        _watcher.Start();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _sessions.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Session Adopt()
    {
        var session = new Session(
            Guid.NewGuid(),
            repoPath: Path.GetTempPath(),
            workingDirectory: Path.GetTempPath(),
            claudeArgs: null,
            backend: new StubSessionBackend(),
            claudeSessionId: "the-id-from-launch",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "hook-roundtrip-test",
            customColor: null);
        _sessions.AdoptSession(session);
        return session;
    }

    private string WritePreamble(Session session, SignedInUser? user = null)
        => SessionPreambleFile.WriteFor(
            session, "TEST-MACHINE", user, _preambleDir, InjectedTextStore.AlwaysOurs(_dir));

    /// <summary>Install the real Claude hook files and return the script the current platform runs.</summary>
    private (string interpreter, string arguments) ClaudeHook()
    {
        var forWindows = OperatingSystem.IsWindows();
        Assert.NotNull(ClaudeHookInstaller.EnsureInstalled(_hookDir, forWindows));
        var script = Path.Combine(_hookDir, forWindows ? "report-session.ps1" : "report-session.sh");
        Assert.True(File.Exists(script));
        return forWindows
            ? ("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            : ("/bin/sh", $"\"{script}\"");
    }

    /// <summary>
    /// Run a hook script the way its agent runs it: the two per-session paths in the environment, the
    /// event on stdin, and NOTHING else - no address, no credential.
    /// </summary>
    private static (int exitCode, string stdout, string stderr) RunHook(
        string interpreter, string arguments, string? stdin,
        string? preambleFile = null, string? pointerFile = null)
    {
        var psi = new ProcessStartInfo(interpreter, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // REMOVE BOTH FIRST, ALWAYS. psi.Environment starts as a copy of THIS process's environment, and
        // these tests used only to ADD to it - so a case that passes null for either path was not testing
        // "the variable is not set", it was inheriting whatever the caller had. Run from inside a live
        // DevThrottle session (which is how the release gate runs), both variables ARE set, and the
        // hook then did exactly its job against the REAL session.
        //
        // That was not merely a false red. `With_no_variables_set_the_hook_is_inert` feeds the hook
        // {"session_id":"x"}, so the drop landed in the live session's own pointer file and overwrote its
        // Claude transcript id with the literal "x" - permanently, and persisted. That session could never
        // resolve its transcript again, so it never narrated another turn: no error, no retry, just a rail
        // stuck on "Preparing voice" forever. It hit three sessions, one of them while it was running the
        // v1.9.11 release gate (#2456).
        //
        // So the premise is now ESTABLISHED rather than assumed. A test that says "neither variable is
        // set" must make that true; inheriting the answer is how it came to be false in exactly the
        // environment that mattered most.
        psi.Environment.Remove(SessionHookFiles.PreambleFileEnvVar);
        psi.Environment.Remove(SessionHookFiles.PointerFileEnvVar);
        if (preambleFile is not null) psi.Environment[SessionHookFiles.PreambleFileEnvVar] = preambleFile;
        if (pointerFile is not null) psi.Environment[SessionHookFiles.PointerFileEnvVar] = pointerFile;

        using var proc = Process.Start(psi)!;
        if (!string.IsNullOrEmpty(stdin))
            proc.StandardInput.Write(stdin);
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(60_000), "the hook script did not finish within a minute");
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Wait, briefly and with a real deadline, for the live watcher to apply a drop.
    ///
    /// The failure message carries the drop box's actual contents, because "timed out" alone cannot
    /// distinguish the two things that would cause it: the hook never wrote the drop, or it wrote it and
    /// the watcher never saw it. Those need opposite fixes, and one intermittent red with no evidence
    /// costs a whole investigation.
    /// </summary>
    private void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(50);
        }

        var box = Directory.Exists(_pointerDir)
            ? string.Join("; ", Directory.GetFiles(_pointerDir)
                .Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} bytes) = {File.ReadAllText(f)}"))
            : "(the drop box does not exist)";
        var pointers = string.Join("; ", _sessions.ListSessions()
            .Select(s => $"{s.Id} -> {s.ClaudeSessionId} @ {s.ClaudeTranscriptPath}"));
        Assert.Fail($"timed out waiting for {what}.{Environment.NewLine}" +
                    $"  drop box: {box}{Environment.NewLine}" +
                    $"  sessions: {pointers}");
    }

    // ---------- The whole channel, both halves, one run of the real script ----------

    [Fact]
    public void The_real_claude_hook_prints_the_preamble_and_the_Director_picks_up_the_rotated_pointer()
    {
        var session = Adopt();
        var preambleFile = WritePreamble(session, new SignedInUser("star@example.com", "Starlord"));
        var pointerFile = SessionHookFiles.PointerPathFor(session.Id, session.PointerDropToken, _pointerDir);
        var (interpreter, arguments) = ClaudeHook();

        var rotatedId = Guid.NewGuid().ToString();
        var rotatedTranscript = "/tmp/" + rotatedId + ".jsonl";
        var rawEvent = $$"""
            {"session_id":"{{rotatedId}}","transcript_path":"{{rotatedTranscript}}","hook_event_name":"SessionStart","source":"clear","cwd":"/tmp"}
            """;

        var (exitCode, stdout, stderr) = RunHook(
            interpreter, arguments, rawEvent, preambleFile, pointerFile);

        // A hook must never fail the session, and must never write to stderr - the agent shows that.
        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"the hook wrote to stderr: {stderr}");

        // Half one: the preamble reached the agent as ready-made SessionStart hook output, carrying the
        // identity line the two deleted routes disagreed about for as long as they existed.
        using var doc = JsonDocument.Parse(stdout);
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hook.GetProperty("hookEventName").GetString());
        var context = hook.GetProperty("additionalContext").GetString()!;
        Assert.Contains("cc-devthrottle", context);
        Assert.Contains("The user of this session is Starlord (star@example.com).", context);
        Assert.Contains(session.Id.ToString(), context);

        // Half two: the Director's LIVE watcher saw the drop and moved the pointer. No sweep, no polling
        // of its own - the file-system notification is the delivery.
        WaitFor(() => session.ClaudeSessionId == rotatedId, "the watcher to apply the dropped pointer");
        Assert.Equal(rotatedTranscript, session.ClaudeTranscriptPath);
        Assert.Equal(session.Id, _sessions.GetSessionByClaudeId(rotatedId)?.Id);

        // And the drop was written atomically: nothing half-finished is left beside it.
        Assert.Empty(Directory.GetFiles(_pointerDir, "*.tmp"));
    }

    /// <summary>
    /// A CLEAR and then a COMPACT, on one already-running session. This is the case the hook exists for:
    /// Claude mints a new id and transcript at each, hours apart, and the Director has to follow both.
    /// </summary>
    [Fact]
    public void A_clear_then_a_compact_each_move_the_pointer_again()
    {
        var session = Adopt();
        var preambleFile = WritePreamble(session);
        var pointerFile = SessionHookFiles.PointerPathFor(session.Id, session.PointerDropToken, _pointerDir);
        var (interpreter, arguments) = ClaudeHook();

        foreach (var source in new[] { "clear", "compact" })
        {
            // A PLAIN GUID, because that is what Claude actually mints. This used to be
            // $"{source}-{Guid.NewGuid()}" - readable in a failure message, and a shape the product never
            // produces. Since #2456 the pointer setter refuses an id that is not a GUID, so an
            // unrealistic id here would fail against a guard that is behaving correctly. The source label
            // still distinguishes the two iterations; it just lives in the transcript name now.
            var id = Guid.NewGuid().ToString();
            var transcript = $"/tmp/{source}-{id}.jsonl";
            var (exitCode, stdout, _) = RunHook(interpreter, arguments,
                $$"""{"session_id":"{{id}}","transcript_path":"{{transcript}}","hook_event_name":"SessionStart","source":"{{source}}"}""",
                preambleFile, pointerFile);

            Assert.Equal(0, exitCode);
            // Every fire re-injects the preamble - that is what makes the agent still know the fleet
            // after its context was cleared.
            Assert.Contains("hookSpecificOutput", stdout);

            WaitFor(() => session.ClaudeSessionId == id, $"the pointer to follow the {source}");
            Assert.Equal(transcript, session.ClaudeTranscriptPath);
        }
    }

    /// <summary>
    /// THE PROOF THAT A LAUNCH-TIME SNAPSHOT WOULD HAVE FAILED, taken through the real script.
    ///
    /// The session is already running. Its preamble file is rewritten - which is what the Director's
    /// refresh does when the user edits their injected text or a skill is published - and the SAME hook
    /// fires again, as a clear or a compact does hours after launch. What the agent receives must be the
    /// NEW text. Under a file written once at launch, the script would print the old one and nothing
    /// anywhere would look wrong.
    /// </summary>
    [Fact]
    public void A_rewritten_preamble_reaches_the_next_fire_of_the_same_running_session()
    {
        var session = Adopt();
        var preambleFile = SessionHookFiles.PreamblePathFor(session.Id, _preambleDir);
        var pointerFile = SessionHookFiles.PointerPathFor(session.Id, session.PointerDropToken, _pointerDir);
        var (interpreter, arguments) = ClaudeHook();

        var store = new InjectedTextStore(Path.Combine(_dir, "injected-text-cache.json"));
        var maintainer = new SessionPreambleMaintainer(
            _sessions, () => null, _preambleDir, machine: "TEST-MACHINE", store: store);

        store.WriteCache(new InjectedTextCacheEntry(true, "THE TEXT THEY HAD AT LAUNCH", DateTime.UtcNow));
        maintainer.Start();

        var first = RunHook(interpreter, arguments, null, preambleFile, pointerFile).stdout;
        Assert.Contains("THE TEXT THEY HAD AT LAUNCH", first);

        // The user edits their text mid-session; the Director's refresh downloads it and rewrites.
        store.WriteCache(new InjectedTextCacheEntry(true, "THE TEXT THEY EDITED MID SESSION", DateTime.UtcNow));
        maintainer.RewriteAll();

        var second = RunHook(interpreter, arguments, null, preambleFile, pointerFile).stdout;
        Assert.Contains("THE TEXT THEY EDITED MID SESSION", second);
        Assert.DoesNotContain("THE TEXT THEY HAD AT LAUNCH", second);

        maintainer.Dispose();
    }

    /// <summary>
    /// An empty preamble file means INJECT NOTHING, and the script has to print nothing at all - not an
    /// empty envelope, which would be a message that says nothing rather than no message.
    /// </summary>
    [Fact]
    public void An_empty_preamble_file_makes_the_hook_print_nothing()
    {
        var session = Adopt();
        var preambleFile = SessionHookFiles.PreamblePathFor(session.Id, _preambleDir);
        File.WriteAllText(preambleFile, "");
        var (interpreter, arguments) = ClaudeHook();

        var (exitCode, stdout, _) = RunHook(interpreter, arguments, null, preambleFile,
            SessionHookFiles.PointerPathFor(session.Id, session.PointerDropToken, _pointerDir));

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(stdout), $"an empty preamble file produced output: {stdout}");
    }

    /// <summary>
    /// The user's OWN Claude or Codex session, started outside DevThrottle. Neither variable is set, so
    /// the hook must do nothing and say nothing rather than erroring into the user's context.
    /// </summary>
    [Fact]
    public void With_no_variables_set_the_hook_is_inert()
    {
        var (interpreter, arguments) = ClaudeHook();

        var (exitCode, stdout, stderr) = RunHook(interpreter, arguments, """{"session_id":"x"}""");

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(stdout), $"a non-Director session got hook output: {stdout}");
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"a non-Director session got hook stderr: {stderr}");
        Assert.Empty(Directory.GetFiles(_pointerDir));
    }

    /// <summary>
    /// A missing preamble file - the Director never wrote one, or it was removed - must also be silent.
    /// The hook prints straight into the agent's context, so "file not found" reaching stdout would arrive
    /// as instructions.
    /// </summary>
    [Fact]
    public void A_missing_preamble_file_makes_the_hook_print_nothing()
    {
        var (interpreter, arguments) = ClaudeHook();

        var (exitCode, stdout, stderr) = RunHook(interpreter, arguments, null,
            Path.Combine(_preambleDir, Guid.NewGuid() + ".json"));

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrEmpty(stdout), $"a missing preamble file produced output: {stdout}");
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"a missing preamble file produced stderr: {stderr}");
    }

    /// <summary>
    /// THE SWEEP ALONE DELIVERS - proven with the file-system watcher switched off.
    ///
    /// This has to be provable on its own, and it took a defect to learn why. The watcher was the sole
    /// delivery path first, and it silently missed a notification about one run in ten: the drop was
    /// present, complete and valid, the pointer had not moved, and no Error event had been raised. So the
    /// sweep is now the guarantee and the watcher is the thing that makes it fast.
    ///
    /// With both running, the watcher wins the race nearly every time - which would mask a sweep that did
    /// not work at all. Suppressing notifications is the only way to put the guarantee itself on trial.
    /// </summary>
    [Fact]
    public void With_the_watcher_suppressed_the_sweep_still_delivers_a_real_hook_drop()
    {
        var session = Adopt();
        var pointerDir = Path.Combine(_dir, "sweep-only-pointers");
        Directory.CreateDirectory(pointerDir);
        using var sweepOnly = new SessionPointerWatcher(_sessions, pointerDir) { SuppressWatcherForTests = true };
        sweepOnly.Start();

        var pointerFile = SessionHookFiles.PointerPathFor(session.Id, session.PointerDropToken, pointerDir);
        var (interpreter, arguments) = ClaudeHook();
        var rotatedId = Guid.NewGuid().ToString();

        var (exitCode, _, _) = RunHook(interpreter, arguments,
            $$"""{"session_id":"{{rotatedId}}","transcript_path":"/tmp/{{rotatedId}}.jsonl","hook_event_name":"SessionStart","source":"compact"}""",
            preambleFile: null, pointerFile: pointerFile);
        Assert.Equal(0, exitCode);

        // Well inside the deadline at a two-second interval, and nothing but the timer can deliver it.
        //
        // BOTH conditions are waited for, not just the pointer. Apply moves the pointer and THEN deletes
        // the drop, so waiting on the pointer alone and asserting on the file immediately afterwards
        // races those two statements - measured at roughly one run in ten, on this change and on its
        // parent alike. The wait is the fix; the assertions below still state what must be true.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline
               && (session.ClaudeSessionId != rotatedId || File.Exists(pointerFile)))
            Thread.Sleep(50);

        Assert.Equal(rotatedId, session.ClaudeSessionId);
        Assert.Equal($"/tmp/{rotatedId}.jsonl", session.ClaudeTranscriptPath);
        Assert.False(File.Exists(pointerFile), "the sweep applied the drop but left it in the box");
    }

    // ---------- Codex, the other hook family, from the same file ----------

    /// <summary>
    /// Codex and Claude now receive the IDENTICAL bytes from the IDENTICAL file. Before this phase the
    /// Codex script fetched plain text and wrapped it, the Windows Claude script did the same separately,
    /// and the POSIX one fetched a pre-wrapped envelope from a second route - three code paths for one
    /// answer, which is how the platforms came to disagree.
    /// </summary>
    [Fact]
    public void The_real_codex_hook_prints_the_same_file_byte_for_byte()
    {
        // Runs on EVERY platform now. It used to return early off Windows with the note that the Codex
        // hook is a PowerShell script and there is no POSIX flavour to run - which was true, and was the
        // defect: the installer had no branch, so macOS and Linux got a command that cannot run there.
        var session = Adopt();
        var preambleFile = WritePreamble(session, new SignedInUser("star@example.com", "Starlord"));
        var codexDir = Path.Combine(_dir, "codex-hooks");
        Assert.True(CodexHookInstaller.EnsureInstalled(codexDir, Path.Combine(codexDir, "hooks.json")));

        var windows = OperatingSystem.IsWindows();
        var script = Path.Combine(codexDir, windows ? "cc-director-preamble.ps1" : "cc-director-preamble.sh");
        var (interpreterName, interpreterArgs) = windows
            ? ("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            : ("/bin/sh", $"\"{script}\"");

        var (exitCode, stdout, stderr) = RunHook(interpreterName, interpreterArgs, null, preambleFile);

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"the Codex hook wrote to stderr: {stderr}");
        Assert.Equal(File.ReadAllText(preambleFile), stdout);

        // And the same run of the Claude hook produces the same bytes, which is the property that matters.
        var (interpreter, arguments) = ClaudeHook();
        var claudeStdout = RunHook(interpreter, arguments, null, preambleFile).stdout;
        Assert.Equal(stdout, claudeStdout);
    }
}
