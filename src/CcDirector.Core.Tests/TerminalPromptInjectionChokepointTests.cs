using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests;

public sealed class TerminalPromptInjectionChokepointTests
{
    [Fact]
    public void Desktop_prompt_and_dictation_send_through_session_send_text_only()
    {
        var root = RepoRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Avalonia", "MainWindow.axaml.cs"));

        // DevThrottle Stats threaded an InputOrigin through the desktop choke-point calls, and source logging
        // (2026-09-05) added the door each one came through as a required argument.
        // The chokepoint (SendTextAsync only, no Enter-retry, no raw SendInput) is unchanged - the calls still
        // funnel here; they now also carry the honest origin tag and the door's own record. (The
        // VoiceModeController this audit used to read was deleted: it was orphaned code no shipping app
        // instantiated, and it pushed the transcript through a language-model summarize step the product
        // removed everywhere else.)
        //
        // The origin is a VARIABLE at both sites now, not a literal, and that is ruling R20: which modality a
        // desktop send carries is decided by the compose box's own per-character provenance, never by which
        // method was called. What that decision produces is pinned where it can actually be observed - through
        // the real window and the real send, in ComposerSendRouteTests - not by reading a literal here.
        Assert.Contains("await _activeSession.Session.SendTextAsync(text, provenance, origin: origin);", main);
        Assert.Contains("await target.SendTextAsync(text, provenance, origin: origin);", main);
        Assert.Contains("await _activeSession.Session.SendTextAsync(\"/handover\", SubmissionProvenance.FrameworkText(), SendSource.Framework);", main);

        Assert.DoesNotContain("ScheduleEnterRetry", main);
        Assert.DoesNotContain("RetryEnterAfterDelay", main);
        Assert.DoesNotContain("Enter retry", main);
        Assert.DoesNotContain(".SendEnterAsync(", main);
        Assert.DoesNotContain("SendText(\"/handover", main);
    }

    [Fact]
    public void Control_api_prompt_routes_send_submitted_text_through_session_send_text()
    {
        var root = RepoRoot();
        // Gateway Cleanup mission (the cut): the Control-API session verbs are no longer loopback REST routes -
        // they are dispatched over THE TUNNEL. The prompt verb is registered + routed in the write executor;
        // every tunnel command funnels through the ONE dispatch entry in ControlApiHost. The queue-send and
        // chat submit paths keep their own cores. The audit stays STRICT - it pins each submit chokepoint to
        // its new known home, not "any file".
        var writeExec = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "SessionWriteExecutor.cs"));
        var host = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "ControlApiHost.cs"));
        var executor = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "SessionCommandExecutor.cs"));
        var chat = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "Chat", "ChatService.cs"));
        var queueGit = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "QueueGitExecutor.cs"));

        // The prompt verb dispatches to PromptAsync (never to raw input), and every tunnel command funnels
        // through the single dispatch entry point.
        Assert.Contains("\"prompt\" => await SessionCommandExecutor.PromptAsync(sessionManager, command, context.Source),", writeExec);
        Assert.Contains("SessionCommandExecutor.DispatchAsync(_sessionManager, DirectorId, cmd,", host);
        // PromptAsync funnels submitted text through the session submit chokepoint.
        // effectiveSource, not source: a relayed fleet prompt marks itself agent-driven in the DTO, and the
        // executor resolves that before the send (issue #1636). Still the same one chokepoint.
        // The verb answers within its budget and may let the send carry on (Voice Delivery mission, phase 3), so the send
        // is started, then awaited or handed on - still the one chokepoint.
        Assert.Contains("var sending = session.SendTextAsync(request.Text, provenance, effectiveSource, origin);", executor);
        // The queue-send and chat submit paths use the SAME chokepoint. (Fleet-message delivery is now
        // Gateway-native and rides the prompt verb above, so it funnels through the same chokepoint; the
        // VoiceTurn endpoint was retired at the cut.)
        Assert.Contains("await session.SendTextAsync(text, SubmissionProvenance.FrameworkText(SubmissionRoutes.QueueDrain), SendSource.Framework);", queueGit);
        Assert.Contains("await session.SendTextAsync(req.Text, SubmissionProvenance.FrameworkText(), SendSource.Framework);", chat);

        // Raw SendInput is still allowed when the caller explicitly asked not to append Enter; that is
        // terminal typing, not prompt submission.
        Assert.Contains("if (request.AppendEnter)", executor);
        Assert.Contains("session.SendInput(Encoding.UTF8.GetBytes(request.Text), origin, provenance);", executor);
    }

    [Fact]
    public void Web_and_gateway_prompt_routes_keep_submit_separate_from_raw_terminal_input()
    {
        var root = RepoRoot();
        var client = File.ReadAllText(Path.Combine(root, "packages", "client-core", "src", "api", "client.ts"));
        var cockpit = File.ReadAllText(Path.Combine(root, "apps", "cockpit", "src", "sessions", "SessionComposer.tsx"));
        var mobileControls = File.ReadAllText(Path.Combine(root, "apps", "mobile", "src", "components", "SessionControls.tsx"));
        // Mobile Voice mode's submit was hoisted into the shared client-core hook (issue #1213), so the
        // chokepoint assertion follows it there; it still funnels through the prompt route, never raw terminal input.
        var mobileVoice = File.ReadAllText(Path.Combine(root, "packages", "client-core", "src", "voice", "useVoiceMode.ts"));
        var interactive = File.ReadAllText(Path.Combine(root, "packages", "client-core", "src", "terminal", "interactive.ts"));
        var gateway = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Gateway", "Api", "GatewayEndpoints.cs"));
        // Gateway Cleanup mission (the cut): the Gateway reaches the Director's prompt over THE TUNNEL now
        // (DirectorEndpointClient + the loopback TerminalStreamEndpoint were deleted). The browser keystroke
        // chokepoint lives in the write executor's terminal-input verb.
        var writeExec = File.ReadAllText(Path.Combine(root, "src", "CcDirector.ControlApi", "SessionWriteExecutor.cs"));

        Assert.Contains("gatewayFetch(`/sessions/${sid}/prompt`", client);
        // The typed Send on both shells: still the prompt route with Enter appended, never raw terminal
        // input. Since source logging (2026-09-05) it also carries the composer's spoken character ranges -
        // matched WITHOUT the closing parenthesis, because the CHOKEPOINT is what must not drift and the two
        // shells word that last argument differently (one keeps the projection in a local).
        Assert.Contains("await sendPrompt(sessionId, text, true, undefined, undefined,", cockpit);
        Assert.Contains("await sendPrompt(sessionId, text, true, undefined, undefined,", mobileControls);
        // The dictated send carries the utterance id as its fifth argument since ruling R10 of the "Clean up
        // Your Throttle" mission (2026-09-05), so the same words count as spoken whichever transcription
        // path produced them. The CHOKEPOINT is unchanged and is what this pins: still the prompt route,
        // still with Enter appended (the third argument), never raw terminal input - on both shells.
        Assert.Contains("await sendPrompt(sessionId, combined, true, undefined, spoken, sent.spans);", cockpit);
        Assert.Contains("await sendPrompt(sessionId, combined, true, undefined, spoken, sent.spans);", mobileControls);
        // The voice reply moved to sendVoicePrompt (issue #2193). The CHOKEPOINT is unchanged and that is
        // what this pins: it is still the prompt route with Enter appended, never raw terminal input - the
        // only difference is that the Gateway is asked to refuse the send outright when a menu owns the
        // screen. Both halves are pinned: the call site here, and (below) that the call it makes is the
        // prompt route carrying menuGuard.
        // Since ruling R10 the voice-mode reply carries the utterance id as its fourth argument, so the words
        // count as spoken only when they are exactly the transcription. The chokepoint - the prompt route,
        // menu-guarded - is unchanged and is what this pins; the id is a claim the Gateway verifies.
        Assert.Contains("await sendVoicePrompt(sid, trimmed, undefined, spokenDeliveryId);", mobileVoice);
        Assert.Contains("const body: PromptRequest & { menuGuard: boolean } = { text, appendEnter: true, menuGuard: true };", client);

        Assert.Contains("await sendPrompt(this.sessionId, chunk, false);", interactive);
        // Raw browser keystrokes go through the terminal-input verb, which calls SendInput (no submit/Enter),
        // naming its door: the Gateway's terminal relay, with the credential kind the relay verified when the
        // browser's socket opened. Taken from the wire, never invented here - a relay that sends none is
        // recorded as the unknown it is.
        Assert.Contains("session.SendInput(bytes, null, SubmissionProvenance.FromWire(request.Provenance, SubmissionRoutes.GatewayTerminal));", writeExec);

        // The Gateway prompt route submits over the tunnel prompt verb, never raw input.
        // Matched WITHOUT the closing parenthesis: the guarded invariant is "the prompt verb carries req through
        // the router", not the exact argument count. Stable Release (v1.3.0) added a trailing machineName so the
        // timeout message can name the Director, and pinning the closing parenthesis made that read as a broken
        // chokepoint. The verb, the payload and the route through the router are what must not drift.
        Assert.Contains("DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, \"prompt\", sid, req, CancellationToken.None", gateway);
    }

    [Fact]
    public void Conpty_and_builtin_driver_submit_paths_funnel_through_terminal_submit()
    {
        var root = RepoRoot();
        var conpty = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Core", "Backends", "ConPtyBackend.cs"));
        Assert.Contains("TerminalSubmit.SharedSubmitAsync(this, text, \"ConPtyBackend\")", conpty);
        Assert.DoesNotContain("Task.Delay(50)", conpty);

        var unixPty = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Core", "Backends", "UnixPtyBackend.cs"));
        Assert.Contains("TerminalSubmit.SharedSubmitAsync(this, text, \"UnixPtyBackend\")", unixPty);
        Assert.DoesNotContain("Task.Delay(50)", unixPty);

        var driversDir = Path.Combine(root, "src", "CcDirector.Core", "Drivers");
        foreach (var file in Directory.GetFiles(driversDir, "*Driver.cs")
                     .Where(f => !Path.GetFileName(f).StartsWith("I", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            var submitMatch = Regex.Match(
                text,
                @"public\s+Task\s+SubmitAsync\s*\([^)]*\)\s*=>\s*(?<body>[^;]+);",
                RegexOptions.Singleline);

            Assert.True(submitMatch.Success, $"{name} should expose an expression-bodied SubmitAsync so this chokepoint audit can read it.");
            var body = submitMatch.Groups["body"].Value;
            Assert.Contains("TerminalSubmit.", body);
            Assert.DoesNotContain("backend.SendTextAsync", body);
            Assert.DoesNotContain("backend.Write", body);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }
}
