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
        //
        // Issue 3481 took the session into a local `target` before each desktop send, so a refused send can be caught
        // at its entry point and shown rather than escape the UI thread. The receiver's name changed; the chokepoint
        // did not. The compose box's Send and the background dictation send are both still this one call.
        Assert.Equal(2, Regex.Matches(main, Regex.Escape("await target.SendTextAsync(text, provenance, origin: origin);")).Count);
        Assert.Contains("await target.SendTextAsync(\"/handover\", SubmissionProvenance.FrameworkText(), SendSource.Framework);", main);

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
        // is started, then awaited or handed on - still the one chokepoint. Since Voice Delivery phase 5 it also carries
        // the time the Gateway accepted the words, so the session can refuse a prompt that waited too long before its
        // first keystroke. The chokepoint is unchanged.
        Assert.Contains("var sending = session.SendTextAsync(request.Text, provenance, effectiveSource, origin, request.SentAtUtc);", executor);
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
        //
        // Since Voice Delivery phase 5 both shells send through sendTypedPrompt, which holds a prompt the Gateway
        // answers "still delivering" instead of reporting it sent. Both halves are pinned: the shells call it, and
        // it calls the prompt route with Enter appended - so the chokepoint is the same one, one hop further in.
        var typedDelivery = File.ReadAllText(Path.Combine(root, "packages", "client-core", "src", "dictation", "typedPromptDelivery.ts"));
        Assert.Contains("await sendTypedPrompt(sessionId, text, { spokenSpans:", cockpit);
        Assert.Contains("await sendTypedPrompt(sessionId, text, { spokenSpans:", mobileControls);
        // The dictated send carries the utterance id since ruling R10 of the "Clean up Your Throttle" mission
        // (2026-09-05), so the same words count as spoken whichever transcription path produced them. The
        // CHOKEPOINT is unchanged and is what this pins: still the prompt route, still with Enter appended,
        // never raw terminal input - on both shells.
        Assert.Contains("await sendTypedPrompt(sessionId, combined, { spokenDeliveryId: spoken, spokenSpans: sent.spans });", cockpit);
        Assert.Contains("await sendTypedPrompt(sessionId, combined, { spokenDeliveryId: spoken, spokenSpans: sent.spans });", mobileControls);
        Assert.Contains("await sendPrompt(sessionId, text, true, undefined, options.spokenDeliveryId, options.spokenSpans);", typedDelivery);
        // "Send anyway" on a held prompt is the same route with Enter appended, claiming the original delivery id.
        Assert.Contains("await sendPrompt(rec.sessionId, rec.text, true, undefined, undefined, undefined, rec.deliveryId);", typedDelivery);
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
        //
        // Since Voice Delivery phase 5 the owner's typed prompt goes through TypedPromptDelivery.SendAsync, which sends
        // through SessionVerbClient.SendPromptAsync - so both hops are pinned: the route hands req to the verb client,
        // and the verb client sends it as the prompt verb through the router. A session typing into a session it owns
        // (Parent Control, fix 1) sends the prompt verb from the route itself.
        var verbClient = File.ReadAllText(Path.Combine(root, "src", "CcDirector.Gateway", "Api", "SessionVerbClient.cs"));
        Assert.Contains("TypedPromptDelivery.SendAsync(new SessionVerbClient(director, sendCommand), sid, req)", gateway);
        Assert.Contains("DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, \"prompt\", sid, req, ct", verbClient);
        Assert.Contains("DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, \"prompt\", sid, req,", gateway);
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
