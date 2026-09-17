using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Security;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Messaging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE DOORBELL, END TO END, WITH REAL AGENTS (the Message Load mission, slice 2). Not part of any suite run:
/// it starts two real Claude Code sessions, costs model time, and takes about ten minutes. It runs only when
/// <c>CC_DOORBELL_E2E_OUT</c> names a directory, where it writes its evidence.
///
/// What is real: a Gateway host (stream mode, SQLite, loopback port the operating system picks), a Director
/// host (<see cref="ControlApiHost"/>, which runs the real terminal state detector), the Director's tunnel client,
/// the Director's command dispatcher, real agent processes in real terminals, the branch's own command line tool
/// (put first on each session's PATH through the Director's own tool directory), and session keys minted and
/// registered exactly as a Director does. What is not: the desktop window (this Mac's screen is locked, and a
/// graphical Director cannot start while it is), the hosted Gateway, and the installed Director.
///
/// Required environment: <c>CC_DOORBELL_E2E_OUT</c> (evidence directory), <c>CC_DOORBELL_E2E_TOOL</c> (the
/// branch's <c>cc-devthrottle</c> executable), <c>CC_DOORBELL_E2E_CLAUDE</c> (the agent executable).
/// </summary>
[Collection("DirectorRoot")]
public sealed class DoorbellEndToEndProof : IAsyncLifetime
{
    private sealed class ProofFactAttribute : FactAttribute
    {
        public ProofFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CC_DOORBELL_E2E_OUT")))
                Skip = "Live proof with real agents; set CC_DOORBELL_E2E_OUT, CC_DOORBELL_E2E_TOOL and CC_DOORBELL_E2E_CLAUDE to run it.";
        }
    }

    private const string Token = "doorbell-proof-token";
    private const string DirectorId = "doorbell-proof-director";

    private readonly string _out = Environment.GetEnvironmentVariable("CC_DOORBELL_E2E_OUT") ?? "";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-doorbell-proof-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly ConcurrentQueue<string> _timeline = new();
    private GatewayHost _gateway = null!;
    private SessionManager _sessions = null!;
    private ControlApiHost _director = null!;
    private GatewayStreamClient _stream = null!;
    private readonly Dictionary<Guid, string> _keys = new();
    // The product's log. A test process never starts the log writer, so the proof starts an isolated one (never
    // the owner's log directory) and mirrors every line into this list, which the proof reads while it runs.
    private FileLog.FileLogTestScope? _logScope;
    private TextWriter? _previousOut;
    private static readonly ConcurrentQueue<string> LogLines = new();

    private sealed class RecordingWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void WriteLine(string? value)
        {
            if (value is not null) LogLines.Enqueue(value);
            inner.WriteLine(value);
        }
        public override void Write(char value) => inner.Write(value);
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_out)) return;
        Directory.CreateDirectory(_out);
        _logScope = FileLog.RedirectForTests();
        FileLog.MirrorToConsole = true;
        _previousOut = Console.Out;
        Console.SetOut(new RecordingWriter(_previousOut));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        GatewayHost.FleetDoorbellHeartbeatEnabled = true;
        // THE PROOF SHORTENS THE GRACE to one minute so three rings take three minutes, not fifteen. The
        // schedule is the same code; FleetDoorbellTests proves the five-minute product numbers on a fake clock.
        GatewayHost.FleetDoorbellLimitsOverride = FleetMessageLimits.Default with { RingGrace = TimeSpan.FromMinutes(1) };

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(_root, "gw-instances"),
            workListsPath: Path.Combine(_root, "gw-instances", "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        // The Gateway switches the mirror off once it is listening (it is a startup aid in production); the proof
        // reads the product's log lines from the mirror, so it switches it back on.
        FileLog.MirrorToConsole = true;
        var url = $"http://127.0.0.1:{_gateway.Port}";
        Note($"gateway listening at {url}; ring grace {_gateway.FleetDoorbell.Limits.RingGrace}, stuck after {_gateway.FleetDoorbell.Limits.StuckAfterRings} rings");

        // The Director puts <instance home>/bin first on every session's PATH. Put the branch's tool there.
        var bin = Path.Combine(Core.Instances.InstanceContext.InstanceHome, "bin");
        Directory.CreateDirectory(bin);
        var tool = Environment.GetEnvironmentVariable("CC_DOORBELL_E2E_TOOL")!;
        var shim = Path.Combine(bin, "cc-devthrottle");
        File.WriteAllText(shim, $"#!/bin/sh\nexec \"{tool}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _sessions = new SessionManager(new AgentOptions { ClaudePath = Environment.GetEnvironmentVariable("CC_DOORBELL_E2E_CLAUDE")! });
        _director = new ControlApiHost(_sessions, "doorbell-proof", () => Task.CompletedTask,
            directorId: DirectorId, instancesDirectory: Path.Combine(_root, "dir-instances"));
        await _director.StartAsync();
        // Set AFTER the host starts: its start sets both from the Director's own Gateway configuration, which in
        // this isolated root is empty. Mint and register a key per session - the pair a Director stamps.
        _sessions.GatewayUrl = url;
        _sessions.GatewaySessionCredentialSource = id =>
        {
            var key = GatewaySessionKey.Mint();
            _gateway.SessionKeys.Register(TenantId.Local, DirectorId, id.ToString(), GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(4));
            _keys[id] = key;
            return key;
        };
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId, TailnetEndpoint = "", MachineName = "proof-mac", Pid = Environment.ProcessId,
            Version = "doorbell-proof", StartedAt = DateTime.UtcNow,
        });
        _stream = new GatewayStreamClient(new GatewayConfig { Url = url, Token = Token, StreamMode = true },
            DirectorId, "doorbell-proof",
            () => _sessions.ListSessions().Select(s => ControlEndpoints.Map(s, DirectorId)).ToList(),
            cmd => SessionCommandExecutor.DispatchAsync(_sessions, DirectorId, cmd),
            rePushInterval: TimeSpan.FromSeconds(3));
        _stream.Start();
        await WaitUntil(() => _gateway.PushedSessions.GetActiveConnectionId(TenantId.Local, DirectorId) is not null,
            TimeSpan.FromSeconds(30), "the Director's tunnel to connect");
        Note("director tunnel connected");
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_out)) return;
        try
        {
            foreach (var s in _sessions.ListSessions())
                await _sessions.KillSessionAsync(s.Id);
        }
        catch (Exception ex) { Note($"kill failed: {ex.Message}"); }
        await _stream.DisposeAsync();
        await _director.StopAsync();
        _sessions.Dispose();
        await _gateway.StopAsync();
        GatewayHost.FleetDoorbellHeartbeatEnabled = false;
        GatewayHost.FleetDoorbellLimitsOverride = null;
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        FileLog.MirrorToConsole = false;
        if (_previousOut is not null) Console.SetOut(_previousOut);
        _logScope?.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run 1: a message sent mid-turn waits for the turn to end, then exactly one doorbell, then the inbox read.
    // ---------------------------------------------------------------------------------------------------------

    [ProofFact]
    public async Task Run1_a_message_sent_mid_turn_rings_once_after_the_turn_and_is_read()
    {
        var dir = Evidence("run1");
        var (manager, worker) = await StartPairAsync(dir, workerInstructions: null);

        await Owner(worker, "Write the numbers from 1 to 250 as English words, one per line, with no other text.");
        await WaitOn(worker, () => Screen(worker).Contains(DoorbellSafetyMarker), TimeSpan.FromSeconds(60), "the worker to start its turn");
        Note($"worker turn started; screen shows '{DoorbellSafetyMarker}'");
        Capture(dir, "01-worker-mid-turn-before-send", worker);

        var text = "Slice 2 proof message, line one.\nLine two: after reading this, write the single word ACK as your answer here. Do not send any message.\nLine three ends the message.";
        var sent = await SendAsync(manager, worker, text);
        Note($"message sent mid-turn: {sent}");
        Capture(dir, "02-worker-mid-turn-just-after-send", worker);

        // Watch the worker's screen until the doorbell appears, recording whether it ever appeared during work.
        var doorbellSeenWhileWorking = false;
        var turnEndedAt = (DateTime?)null;
        var doorbellAt = (DateTime?)null;
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < deadline && doorbellAt is null)
        {
            var screen = Screen(worker);
            var working = screen.Contains(DoorbellSafetyMarker, StringComparison.OrdinalIgnoreCase);
            var rung = screen.Contains("[DevThrottle doorbell]", StringComparison.Ordinal);
            if (!working && turnEndedAt is null) { turnEndedAt = DateTime.UtcNow; Note("worker turn ended (marker gone)"); Capture(dir, "03-worker-turn-ended", worker); }
            if (rung && working && turnEndedAt is null) doorbellSeenWhileWorking = true;
            if (rung) { doorbellAt = DateTime.UtcNow; Note("doorbell line appeared on the worker's screen"); }
            await Task.Delay(250);
        }
        Capture(dir, "04-worker-doorbell", worker);
        Assert.False(doorbellSeenWhileWorking, "the doorbell was typed while the turn was still running");
        Assert.NotNull(turnEndedAt);
        Assert.NotNull(doorbellAt);
        Assert.True(doorbellAt >= turnEndedAt, "the doorbell appeared before the turn ended");

        // The agent runs the command the doorbell names; the read marks the record read.
        await WaitOn(worker, () => Row(sent).ReadAtUtc is not null, TimeSpan.FromMinutes(4), "the worker to read its inbox");
        Note($"record read at {Row(sent).ReadAtUtc:o}; rings {Row(sent).RingCount}");
        await WaitOn(worker, () => !Screen(worker).Contains(DoorbellSafetyMarker), TimeSpan.FromMinutes(3), "the worker's reply turn to end");
        await Task.Delay(3000);
        Capture(dir, "05-worker-after-reading-inbox", worker, full: true);
        Assert.Equal(1, DoorbellsTypedInto(worker));
        Assert.Equal(1, Row(sent).RingCount);
        Assert.Contains("ACK", Screen(worker));
        // What the agent's command returned, from the agent's own conversation record: the whole message, every line.
        var toolOutput = AgentToolOutput(worker, dir, "Line three ends the message");
        Assert.Contains("Slice 2 proof message, line one.", toolOutput);
        Assert.Contains("Line two: after reading this", toolOutput);
        await ReadInboxAllAsync(worker, dir);
        WriteLogs(dir);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run 2: owner text in the composer holds the doorbell; clearing it lets the ring through.
    // ---------------------------------------------------------------------------------------------------------

    [ProofFact]
    public async Task Run2_owner_text_in_the_composer_holds_the_doorbell_until_it_is_cleared()
    {
        var dir = Evidence("run2");
        var (manager, worker) = await StartPairAsync(dir, workerInstructions: null);

        const string draft = "an owner draft that must not be touched";
        worker.SendInput(Encoding.UTF8.GetBytes(draft), null,
            SubmissionProvenance.Typed(SubmissionRoutes.DesktopTerminal, SubmissionIdentityKinds.LocalUser));
        await WaitOn(worker, () => Screen(worker).Contains(draft), TimeSpan.FromSeconds(10), "the draft to show");
        Capture(dir, "01-owner-draft", worker);

        var sent = await SendAsync(manager, worker, "Proof message for the composer check.\nSecond line.");
        Note($"message sent while the owner's draft is in the composer: {sent}");

        // Three heartbeats: every one must defer, and the draft must still be there, untouched.
        await Task.Delay(TimeSpan.FromSeconds(50));
        Capture(dir, "02-owner-draft-after-50-seconds", worker);
        Assert.Contains(Screen(worker).Split('\n'), r => r.StartsWith('❯') && r.Contains(draft));
        Assert.DoesNotContain("[DevThrottle doorbell]", Screen(worker));
        Assert.Equal(0, Row(sent).RingCount);

        // The owner erases the draft but leaves three spaces behind (inspection 4, ruling 3: whitespace is text).
        // The row shows nothing after the glyph - rows are trailing-trimmed - and the doorbell must still wait.
        worker.SendInput(Enumerable.Repeat((byte)0x7f, draft.Length).ToArray(), null,
            SubmissionProvenance.Typed(SubmissionRoutes.DesktopTerminal, SubmissionIdentityKinds.LocalUser));
        await WaitOn(worker, () => !Screen(worker).Contains(draft), TimeSpan.FromSeconds(10), "the draft to be erased");
        worker.SendInput(Encoding.UTF8.GetBytes("   "), null,
            SubmissionProvenance.Typed(SubmissionRoutes.DesktopTerminal, SubmissionIdentityKinds.LocalUser));
        await Task.Delay(1000);
        Note("owner erased the draft and left three spaces");
        CaptureFrame(dir, "02b-whitespace-draft", worker);
        Capture(dir, "02b-whitespace-draft", worker);
        var deferralsBefore = ReadLog().Count(l => l.Contains($"DEFERRED (composer-holds-text): session={worker.Id}"));
        await Task.Delay(TimeSpan.FromSeconds(35));
        var deferralsAfter = ReadLog().Count(l => l.Contains($"DEFERRED (composer-holds-text): session={worker.Id}"));
        Note($"whitespace draft: {deferralsAfter - deferralsBefore} composer-holds-text deferrals in 35 seconds");
        Capture(dir, "02c-whitespace-draft-after-35-seconds", worker);
        Assert.True(deferralsAfter - deferralsBefore >= 2, "the whitespace draft should hold the doorbell on every heartbeat");
        Assert.DoesNotContain("[DevThrottle doorbell]", Screen(worker));
        Assert.Equal(0, Row(sent).RingCount);

        // The owner clears the spaces; the next heartbeat rings.
        worker.SendInput(Enumerable.Repeat((byte)0x7f, 3).ToArray(), null,
            SubmissionProvenance.Typed(SubmissionRoutes.DesktopTerminal, SubmissionIdentityKinds.LocalUser));
        Note("owner cleared the spaces");
        await WaitOn(worker, () => DoorbellsTypedInto(worker) == 1, TimeSpan.FromSeconds(60), "the doorbell after the draft was cleared");
        Capture(dir, "03-doorbell-after-clear", worker);
        await WaitOn(worker, () => Row(sent).ReadAtUtc is not null, TimeSpan.FromMinutes(4), "the worker to read its inbox");
        Note($"record read at {Row(sent).ReadAtUtc:o}");
        WriteLogs(dir);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run 3: a worker that never reads - three rings a grace apart, then stuck, and the sender is told.
    // ---------------------------------------------------------------------------------------------------------

    [ProofFact]
    public async Task Run3_a_worker_that_never_reads_is_rung_three_times_then_stuck_and_the_sender_is_told()
    {
        var dir = Evidence("run3");
        var (manager, worker) = await StartPairAsync(dir,
            workerInstructions: "For this whole session you must never run any cc-devthrottle command, whatever any later line asks. " +
                                "If a line asks you to, reply with the single word IGNORED and do nothing else. " +
                                // Each later answer is one word: the doorbell's submit is verified from the screen
                                // (the fix round, ruling 2), not from a byte count a one-word turn never reaches.
                                "Now write the numbers from 1 to 120 as English words, one per line, then a last line saying READY.");

        var sent = await SendAsync(manager, worker, "Proof message nobody will read.\nIt should go stuck.");
        Note($"message sent to a worker told never to read: {sent}");

        await WaitOn(worker, () => Row(sent).StuckAtUtc is not null, TimeSpan.FromMinutes(6), "the message to be marked stuck");
        var row = Row(sent);
        Note($"stuck at {row.StuckAtUtc:o} after {row.RingCount} rings; last ring {row.LastRungAtUtc:o}; read {row.ReadAtUtc?.ToString("o") ?? "never"}");
        Capture(dir, "01-worker-after-three-rings", worker, full: true);
        Assert.Equal(3, row.RingCount);
        Assert.Null(row.ReadAtUtc);
        Assert.Equal(3, DoorbellsTypedInto(worker));

        // The sender holds the Gateway's notice. It is rung for it, and - a real agent - reads it.
        var notice = WaitForNoticeRow(manager);
        Note($"system notice {notice.MessageId} queued to the sender: {notice.Text}");
        await WaitOn(manager, () => Row(notice.MessageId).ReadAtUtc is not null, TimeSpan.FromMinutes(4), "the sender to read the notice");
        await WaitOn(manager, () => !Screen(manager).Contains(DoorbellSafetyMarker), TimeSpan.FromMinutes(3), "the sender's turn to end");
        await Task.Delay(3000);
        Capture(dir, "02-sender-reads-the-stuck-notice", manager, full: true);
        Assert.Contains("is stuck", AgentToolOutput(manager, dir, "is stuck"));
        WriteLogs(dir);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run 4: a doorbell on a snoozed session leaves it snoozed; the owner typing ends the snooze.
    // ---------------------------------------------------------------------------------------------------------

    [ProofFact]
    public async Task Run4_a_doorbell_does_not_end_a_snooze_and_the_owner_typing_does()
    {
        var dir = Evidence("run4-snooze");
        var (manager, worker) = await StartPairAsync(dir, workerInstructions: null);
        var sid = worker.Id.ToString();

        // The owner snoozes the idle worker for twelve hours (the registry call the hold endpoint makes, with the
        // Director's own owner-turn baseline).
        _gateway.SnoozeRegistry.Snooze(sid, DateTime.UtcNow.AddHours(12), DirectorId, ownerTurnBaselineUtc: worker.LastOwnerTurnAtUtc);
        Note($"worker snoozed until {_gateway.SnoozeRegistry.SnoozeUntilFor(sid):o}");

        var sent = await SendAsync(manager, worker,
            "Snooze proof message.\nAfter reading this, write the single word SEEN as your answer here. Do not send any message.");
        await WaitOn(worker, () => DoorbellsTypedInto(worker) == 1, TimeSpan.FromSeconds(90), "the doorbell on the snoozed worker");
        Note($"doorbell rung; worker working origin now '{worker.WorkingOrigin}'");
        await WaitOn(worker, () => Row(sent).ReadAtUtc is not null, TimeSpan.FromMinutes(4), "the worker to read its inbox");
        await WaitOn(worker, () => Screen(worker).Contains("SEEN") && !Screen(worker).Contains(DoorbellSafetyMarker),
            TimeSpan.FromMinutes(3), "the doorbell turn to end");
        await WaitOn(worker, () => worker.ActivityState is not (ActivityState.Working or ActivityState.Starting),
            TimeSpan.FromMinutes(1), "the Director to settle the worker");
        await Task.Delay(TimeSpan.FromSeconds(8)); // several re-pushes of the settled state
        Capture(dir, "01-after-the-doorbell-turn", worker);
        var stillSnoozed = _gateway.SnoozeRegistry.Contains(sid);
        Note($"after the doorbell turn: snoozed={stillSnoozed}, until {_gateway.SnoozeRegistry.SnoozeUntilFor(sid)?.ToString("o") ?? "(none)"}");
        Assert.True(stillSnoozed, "the doorbell turn ended the owner's snooze");

        // The owner types a prompt: the snooze is over.
        await Owner(worker, "Write the single word BACK and nothing else.");
        Note($"owner typed; worker working origin '{worker.WorkingOrigin}', owner turn {worker.LastOwnerTurnAtUtc:o}");
        await WaitUntil(() => !_gateway.SnoozeRegistry.Contains(sid), TimeSpan.FromSeconds(30), "the owner's turn to end the snooze");
        Note("snooze ended by the owner's turn");
        await WaitOn(worker, () => Screen(worker).Contains("BACK") && !Screen(worker).Contains(DoorbellSafetyMarker),
            TimeSpan.FromMinutes(2), "the owner's turn to end");
        Capture(dir, "02-after-the-owner-typed", worker);
        var logs = ReadLog().Where(l => l.Contains("[SnoozeLandingObserver]") || l.Contains("[SnoozeRegistry]")).ToList();
        File.WriteAllLines(Path.Combine(dir, "snooze-log-lines.txt"), logs);
        WriteLogs(dir);
    }

    // ---------------------------------------------------------------------------------------------------------

    private const string DoorbellSafetyMarker = Core.Drivers.DoorbellSafety.WorkingMarker;

    private async Task<(Session Manager, Session Worker)> StartPairAsync(string dir, string? workerInstructions)
    {
        var repo = Path.Combine(_root, "repo-" + Path.GetFileName(dir));
        Directory.CreateDirectory(repo);
        System.Diagnostics.Process.Start("git", $"-C \"{repo}\" init -q")!.WaitForExit();
        // Claude Code asks whether to trust a new folder. The proof owner answers it, like the owner would.
        var manager = _sessions.CreateSession(repo, AgentKind.ClaudeCode, "--dangerously-skip-permissions", SessionBackendType.ConPty, null,
            nameFactory: _ => "Proof - Manager");
        var worker = _sessions.CreateSession(repo, AgentKind.ClaudeCode, "--dangerously-skip-permissions", SessionBackendType.ConPty, null,
            nameFactory: _ => "Proof - Worker", controllerSessionId: manager.Id);
        Note($"{dir}: manager {manager.Id}, worker {worker.Id} (controller {worker.ControllerSessionId})");
        foreach (var s in new[] { manager, worker })
        {
            s.OnActivityStateChanged += (_, _) => _stream.NotifyDelta(ControlEndpoints.Map(s, DirectorId));
            await AnswerTrustAsync(s);
        }
        await WaitUntil(() => _gateway.PushedSessions.TryLocate(TenantId.Local, worker.Id.ToString(), TimeSpan.FromSeconds(30)) is { } l
                              && l.Session.ControllerSessionId == manager.Id.ToString(),
            TimeSpan.FromSeconds(30), "the worker to reach the Gateway roster with its controller");
        if (workerInstructions is not null)
        {
            await Owner(worker, workerInstructions);
            await WaitOn(worker, () => Screen(worker).Contains("READY") && !Screen(worker).Contains(DoorbellSafetyMarker),
                TimeSpan.FromMinutes(2), "the worker to acknowledge its instructions");
        }
        Capture(dir, "00-worker-ready", worker);
        return (manager, worker);
    }

    private async Task AnswerTrustAsync(Session s)
    {
        await WaitOn(s, () => Screen(s).Contains("Enter to confirm") || HasComposer(s), TimeSpan.FromSeconds(60), "the agent to start");
        // The dialog draws before it accepts keys: move the selection until the screen shows it moved, then Enter.
        for (var attempt = 0; attempt < 20 && Screen(s).Contains("Enter to confirm"); attempt++)
        {
            await Task.Delay(1000);
            if (!Screen(s).Contains("❯ Yes, I trust this folder"))
            {
                s.SendInput(Encoding.UTF8.GetBytes("\x1b[B"), null, SubmissionProvenance.FrameworkText());
                continue;
            }
            s.SendInput(Encoding.UTF8.GetBytes("\r"), null, SubmissionProvenance.FrameworkText());
        }
        await WaitOn(s, () => HasComposer(s), TimeSpan.FromSeconds(60), "an empty composer");
        await WaitOn(s, () => s.ActivityState is not (ActivityState.Working or ActivityState.Starting), TimeSpan.FromSeconds(60), "the session to settle");
    }

    private static bool HasComposer(Session s) =>
        Core.Drivers.DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, LiveFrame(s)) == Core.Drivers.ComposerReading.Empty;

    /// <summary>The live frame, cursor included, exactly as the ringer reads it.</summary>
    private static Core.Drivers.ScreenFrame LiveFrame(Session s)
    {
        var (rows, cursorRow, cursorCol, cursorVisible, _) = s.SnapshotLiveScreen();
        return new Core.Drivers.ScreenFrame(rows, cursorRow, cursorCol, cursorVisible);
    }

    /// <summary>Save the live frame as a capture in the same shape as the Core tests' TestData/doorbell files.</summary>
    private void CaptureFrame(string dir, string name, Session s)
    {
        var f = LiveFrame(s);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedFrom = $"live Claude Code session {s.Id}, {DateTime.UtcNow:o}",
            rows = f.Rows,
            cursorRow = f.CursorRow,
            cursorCol = f.CursorCol,
            cursorVisible = f.CursorVisible,
            reading = Core.Drivers.DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, f).ToString(),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(dir, name + ".json"), json);
        Note($"captured frame {Path.GetFileName(dir)}/{name}.json: cursor ({f.CursorRow},{f.CursorCol}) visible={f.CursorVisible}");
    }

    private static Task Owner(Session s, string text) =>
        s.SendTextAsync(text, SubmissionProvenance.Typed(SubmissionRoutes.DesktopComposer, SubmissionIdentityKinds.LocalUser));

    private async Task<string> SendAsync(Session from, Session to, string text)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _keys[from.Id]);
        var r = await http.PostAsJsonAsync($"sessions/{to.Id}/message", new { text });
        var body = await r.Content.ReadFromJsonAsync<FleetMessageSendResponse>();
        Note($"POST /sessions/{to.Id}/message as {from.Id} -> {(int)r.StatusCode} {body?.Status} {body?.MessageId} {body?.Error}");
        Assert.Equal("queued", body?.Status);
        return body!.MessageId!;
    }

    private CcDirector.Gateway.Data.Entities.FleetMessageEntity Row(string id)
    {
        using var ctx = GatewayDatabaseFor().CreateContext(TenantId.Local);
        return ctx.FleetMessages.AsEnumerable().Single(m => m.MessageId == id);
    }

    private CcDirector.Gateway.Data.Entities.FleetMessageEntity WaitForNoticeRow(Session sender)
    {
        using var ctx = GatewayDatabaseFor().CreateContext(TenantId.Local);
        return ctx.FleetMessages.AsEnumerable().Single(m =>
            m.RecipientSessionId == sender.Id.ToString() && m.Kind == FleetMessageKinds.System);
    }

    private CcDirector.Gateway.Data.GatewayDatabase GatewayDatabaseFor() => _gateway.GatewayDatabaseForTests;

    private static string Screen(Session s) => string.Join("\n", s.SnapshotScreenRows());

    private static string Transcript(Session s) =>
        Core.Drivers.TerminalSubmit.StripAnsi(Encoding.UTF8.GetString(s.Buffer?.DumpAll() ?? []));

    /// <summary>How many doorbell lines the Director typed into this session, from the Director's own log.</summary>
    private static int DoorbellsTypedInto(Session s) =>
        ReadLog().Count(l => l.Contains($"[FleetDoorbellRinger] RUNG: session={s.Id}", StringComparison.Ordinal));

    private static List<string> ReadLog() => LogLines.ToList();

    /// <summary>
    /// The agent's own conversation record: every line of its JSONL transcript that contains <paramref name="needle"/>,
    /// saved to the evidence folder and returned. The screen folds command output away; the record does not.
    /// </summary>
    private string AgentToolOutput(Session s, string dir, string needle)
    {
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var file = Directory.EnumerateFiles(projects, s.ClaudeSessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault();
        Assert.True(file is not null, $"no conversation record for {s.ClaudeSessionId}");
        var lines = File.ReadAllLines(file!).Where(l => l.Contains(needle, StringComparison.Ordinal)).ToList();
        var name = $"agent-record-{s.CustomName?.Replace(' ', '-')}.jsonl";
        File.WriteAllLines(Path.Combine(dir, name), lines);
        Note($"saved {lines.Count} conversation record lines containing '{needle}' to {name}");
        // Undo the JSON string escaping so the multi-line text reads as it was sent.
        return string.Join("\n", lines).Replace("\\n", "\n").Replace("\\\"", "\"");
    }

    /// <summary>Run the branch's command line as the session itself: <c>message inbox --all</c>, output saved.</summary>
    private async Task ReadInboxAllAsync(Session s, string dir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("CC_DOORBELL_E2E_TOOL")!, "message inbox --all")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["CC_GATEWAY_URL"] = $"http://127.0.0.1:{_gateway.Port}";
        psi.Environment["CC_GATEWAY_SESSION_KEY"] = _keys[s.Id];
        psi.Environment["CC_SESSION_ID"] = s.Id.ToString();
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        File.WriteAllText(Path.Combine(dir, $"inbox-all-as-{s.CustomName?.Replace(' ', '-')}.txt"),
            $"$ cc-devthrottle message inbox --all   (exit {p.ExitCode})\n{stdout}{stderr}");
        Note($"message inbox --all as {s.CustomName}: exit {p.ExitCode}");
    }

    private string Evidence(string run)
    {
        var dir = Path.Combine(_out, run);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Capture(string dir, string name, Session s, bool full = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {name} - session {s.Id} ({s.CustomName}) at {DateTime.UtcNow:o}");
        sb.AppendLine($"# director activity state: {s.ActivityState}");
        sb.AppendLine("# visible screen rows:");
        foreach (var row in s.SnapshotScreenRows()) sb.AppendLine("|" + row);
        File.WriteAllText(Path.Combine(dir, name + ".txt"), sb.ToString());
        if (full)
            File.WriteAllText(Path.Combine(dir, name + ".scrollback.txt"), Transcript(s));
        Note($"captured {Path.GetFileName(dir)}/{name}");
    }

    private void WriteLogs(string dir)
    {
        File.WriteAllLines(Path.Combine(dir, "timeline.txt"), _timeline);
        var lines = ReadLog()
            .Where(l => l.Contains("[FleetDoorbell]") || l.Contains("[FleetDoorbellRinger]") || l.Contains("[FleetMessageService]")
                        || (l.Contains("[DirectorCommandRouter]") && l.Contains(" ring ")));
        File.WriteAllLines(Path.Combine(dir, "log-lines.txt"), lines);
    }

    private void Note(string line)
    {
        var stamped = $"{DateTime.UtcNow:HH:mm:ss.fff} {line}";
        _timeline.Enqueue(stamped);
        Console.WriteLine(stamped);
    }

    private static async Task WaitOn(Session s, Func<bool> condition, TimeSpan timeout, string what)
    {
        try
        {
            await WaitUntil(condition, timeout, what);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}. Session {s.Id} state {s.ActivityState}; screen:\n{Screen(s)}", ex);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Timed out after {timeout} waiting for {what}");
    }
}
