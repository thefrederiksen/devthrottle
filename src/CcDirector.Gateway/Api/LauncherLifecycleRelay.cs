using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The ONE tenant-scoped path from "this tenant wants a lifecycle verb run on this machine" to the machine's
/// launcher. Both callers go through it:
///
///   * the HTTP relay routes - POST /machines/{machine}/director/start|stop|restart and
///     POST /machines/{machine}/launch - which resolve the calling tenant from the authenticated device key;
///   * <see cref="Running.RelayDirectorLauncher"/>, the in-process auto-launch the target resolver uses when
///     a tenant asks for a session on a machine whose Director is not running.
///
/// WHY THE SECOND CALLER EXISTS AT ALL, AND WHY IT NO LONGER DIALS THE GATEWAY. The auto-launcher used to
/// reach the launcher by dialing the Gateway's OWN /machines/{machine}/director/start over loopback, carrying
/// the host-wide shared token. That reused the shipped path, which was the point - but it DESTROYED THE
/// CALLER'S IDENTITY on the way: a fresh inbound request carries no device key, so on the hosted Gateway the
/// tenant resolved to nothing and the inner call refused itself. Worse, had it resolved to anything, it would
/// have resolved to a tenant unrelated to the one that asked. A loopback hop cannot carry a tenant, so the
/// shared code moved DOWN here where the tenant is an argument and cannot be lost.
///
/// THERE IS EXACTLY ONE DISPATCH ARM: the persistent stream the launcher opened to the Gateway
/// (remove-the-network-port mission, phase 6). There used to be a second - an HTTP relay that dialed the
/// launcher's loopback REST interface with a stored address, port and bearer token whenever the stream was
/// absent - and it is deliberately GONE, not switched off. The launcher no longer listens on anything, so a
/// dial would reach nothing; and a fallback that runs exactly when the primary path is already failing is
/// the second door this mission exists to remove. A launcher whose stream is down is REFUSED loudly
/// (<see cref="RelayOutcomeKind.NotConnected"/>), never reached another way.
///
/// The stream arm resolves the launcher connection as (tenant, machine) through
/// <see cref="LauncherCommandRouter"/>, and the registered-at-all check resolves the registry entry as
/// (tenant, machine) through <see cref="LauncherRegistry"/>. Neither can be reached with a machine name
/// alone, so a caller can only ever drive a launcher its OWN tenant registered.
/// </summary>
internal static class LauncherLifecycleRelay
{
    /// <summary>How the relay attempt ended. The HTTP routes map this onto a status code and body; the
    /// in-process auto-launcher only asks whether the launcher accepted.</summary>
    internal enum RelayOutcomeKind
    {
        /// <summary>The launcher answered over the stream. <see cref="LauncherRelayOutcome.RelayStatus"/>
        /// carries its verdict - which may itself be a failure the launcher reported.</summary>
        Relayed,

        /// <summary>This tenant has no launcher registered for that machine name. Another tenant's launcher of
        /// the same bare name is NOT a match and is not consulted.</summary>
        NoLauncher,

        /// <summary>
        /// The launcher is registered but its heartbeat has gone quiet, and it holds no stream. It has
        /// stopped talking to this Gateway altogether: crashed, stopped, or cut off from the network.
        /// Waiting or restarting that machine's launcher is the fix.
        /// </summary>
        NotConnected,

        /// <summary>
        /// The launcher is registered AND still heartbeating, but holds no command stream - so it is
        /// alive and reaching this Gateway while being unable to receive anything from it.
        ///
        /// THIS IS A DIFFERENT CONDITION FROM <see cref="NotConnected"/> AND MUST NOT SHARE ITS
        /// MESSAGE. A launcher built before the remove-the-network-port mission's phase 6 opens no
        /// command stream - it expected the Gateway to dial its own listener, and that relay is
        /// deleted. It heartbeats perfectly, so it looks registered and healthy, and every command
        /// silently does nothing. The hosted Gateway deploys INDEPENDENTLY of the desktop application
        /// and normally moves first, so this is the ordinary shape of an upgrade, not an edge case.
        /// One message for both conditions would send a user to check a network connection that is
        /// demonstrably working. The answer here is to update that machine's launcher.
        /// </summary>
        NotStreamCapable,
    }

    /// <summary>The result of one relay attempt.</summary>
    /// <param name="Kind">How the attempt ended.</param>
    /// <param name="RelayStatus">The launcher's own status when <see cref="RelayOutcomeKind.Relayed"/>, else 0.</param>
    /// <param name="Payload">The launcher's response body when relayed, else null.</param>
    /// <param name="LauncherVersion">The refused launcher's registered version, so the message can say
    /// WHICH build is not accepting commands rather than only that something is not.</param>
    /// <param name="QuietForSeconds">How long since that launcher last heartbeated. It is the fact that
    /// separates "too old to stream" from "stopped talking", so it travels on the answer.</param>
    internal sealed record LauncherRelayOutcome(
        RelayOutcomeKind Kind,
        int RelayStatus = 0,
        string? Payload = null,
        string? LauncherVersion = null,
        int QuietForSeconds = 0)
    {
        /// <summary>True when the launcher answered AND its answer was a success. This is the whole question
        /// the in-process auto-launcher asks.</summary>
        public bool Accepted => Kind == RelayOutcomeKind.Relayed && RelayStatus is >= 200 and < 300;
    }

    /// <summary>
    /// Run a Director lifecycle verb ("start", "stop", "restart") on the CALLING TENANT's launcher for
    /// <paramref name="machine"/>. The slot guard is NOT applied here: it reads the caller's request body and
    /// so belongs to the HTTP route, which runs it before calling this.
    /// </summary>
    /// <param name="onlyIfEmpty">
    /// Restart ONLY while the Director is holding no live sessions, and take the launcher's refusal - which
    /// names the count - when it is holding some. Meaningful to "restart" alone; the HTTP route refuses to
    /// send it with any other verb rather than letting a caller believe a stop was guarded.
    /// </param>
    public static async Task<LauncherRelayOutcome> SendDirectorVerbAsync(
        TenantId tenant, string machine, string verb, string? exePath, bool confirmProtected,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct, bool onlyIfEmpty = false)
    {
        var outcome = await SendAsync(
            tenant, machine,
            new LauncherCommand
            {
                Verb = $"director/{verb}",
                Path = exePath,
                ConfirmProtected = confirmProtected,
                OnlyIfEmpty = onlyIfEmpty,
            },
            // Only a GUARDED restart has anything of its own to say; every other lifecycle verb's news is
            // whether it worked, and its answer stays the envelope every existing caller reads.
            passLauncherPayloadThrough: onlyIfEmpty,
            launchers, sendLauncherCommand, ct);

        return onlyIfEmpty ? RequireTheGuardWasHonoured(machine, outcome) : outcome;
    }

    /// <summary>
    /// A SUCCESS TO A GUARDED RESTART IS ONLY BELIEVED WHEN THE LAUNCHER SAYS IT GUARDED IT.
    ///
    /// This is the fail-open the whole feature would otherwise have. A launcher built before
    /// <see cref="LauncherCommand.OnlyIfEmpty"/> existed deserialises the command, cannot see a field it
    /// has never heard of, restarts a Director holding live sessions, and answers a perfectly ordinary
    /// OK. Nothing about that answer is distinguishable from a launcher that read the count and found
    /// zero - unless the honouring launcher SAYS SO, which it does, in the payload it writes.
    ///
    /// So an unacknowledged success becomes a loud failure naming the real cause and the real risk. It is
    /// 502 rather than a refusal because nothing was refused: the command was carried out, by a launcher
    /// that could not honour the condition attached to it, and the caller has to know that the Director
    /// may have been restarted mid-drain. Telling them "done" would be a lie that costs sessions.
    ///
    /// THIS IS DETECTION, NOT PREVENTION, AND THE DIFFERENCE MATTERS. By the time the answer comes back
    /// the old launcher has already restarted the Director; what this saves is every attempt after the
    /// first, and the caller's belief that the first one was safe. Preventing it needs the launcher to
    /// declare what it can honour when it joins the stream, so the Gateway can refuse BEFORE dispatch -
    /// which is the capability work this epic has a separate phase for, and is deliberately not invented
    /// here in a second, competing shape.
    /// </summary>
    private static LauncherRelayOutcome RequireTheGuardWasHonoured(string machine, LauncherRelayOutcome outcome)
    {
        if (outcome.Kind != RelayOutcomeKind.Relayed || outcome.RelayStatus is < 200 or >= 300)
            return outcome; // not a success - it already says what happened

        if (AcknowledgesTheGuard(outcome.Payload))
            return outcome;

        FileLog.Write($"[LauncherLifecycleRelay] director/restart on {machine}: a restart was asked to happen "
                      + "ONLY IF the Director was empty, and the launcher answered success WITHOUT saying it "
                      + "honoured that. Reported as a failure: the launcher is older than the condition.");
        return outcome with
        {
            RelayStatus = 502,
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                error = $"the launcher on '{machine}' accepted a restart that was to happen only if the "
                      + "Director was empty, and answered success without confirming it applied that "
                      + "condition. A launcher older than the condition ignores it silently, so this "
                      + "restart may have taken live sessions with it. Update the launcher on that machine "
                      + "before relying on a guarded restart there.",
                machine,
                verb = "restart",
                reason = "launcher-did-not-honour-only-if-empty",
                via = "stream",
            }),
        };
    }

    /// <summary>
    /// Whether a launcher's answer states, in full, that it applied the only-if-empty condition: it read
    /// the count, the count was ZERO, and it restarted the Director on that basis.
    ///
    /// ALL THREE ARE REQUIRED, and an earlier version of this check asked only for the first. A payload
    /// saying <c>{"onlyIfEmpty":true,"sessions":3}</c> or <c>{"onlyIfEmpty":true,"restarted":false}</c>
    /// would have passed it - answers that contradict themselves, and exactly the shape a half-finished
    /// implementation on the other side produces. An acknowledgement that only echoes the flag back
    /// acknowledges nothing.
    ///
    /// EXTRA FIELDS ARE FINE, so a newer launcher that adds to its answer still passes. A launcher that
    /// RENAMES these fields fails closed, which is the right way round: this check exists to catch a
    /// launcher whose answer we do not understand.
    /// </summary>
    private static bool AcknowledgesTheGuard(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object
                   && StatedOnce(root, "onlyIfEmpty", out var applied)
                   && applied.ValueKind == System.Text.Json.JsonValueKind.True
                   && StatedOnce(root, "restarted", out var restarted)
                   && restarted.ValueKind == System.Text.Json.JsonValueKind.True
                   && StatedOnce(root, "sessions", out var sessions)
                   && sessions.ValueKind == System.Text.Json.JsonValueKind.Number
                   && sessions.TryGetInt32(out var count)
                   && count == 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read a field that the answer must state EXACTLY ONCE. False when it is absent, and false when it
    /// is stated more than once.
    ///
    /// A DOCUMENT MAY SAY A THING TWICE, AND THEN IT HAS NOT SAID IT. TryGetProperty quietly returns the
    /// LAST occurrence, so <c>{"onlyIfEmpty":false,"onlyIfEmpty":true,...}</c> - an answer that says both
    /// that the condition was applied and that it was not - was being read as the permissive one and
    /// accepted as proof the guard ran. That is the same shape as every other defect this change has
    /// fixed: a state nobody can make sense of resolving to the agreeable reading.
    ///
    /// The REQUEST side of this feature already refuses a doubly-spelled flag, for exactly this reason.
    /// This is that rule carried to the REPLY, which is where it was missing - fixing one side of a rule
    /// and leaving the other is how a defect moves rather than closes.
    /// </summary>
    private static bool StatedOnce(System.Text.Json.JsonElement root, string name,
        out System.Text.Json.JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found)
            {
                FileLog.Write($"[LauncherLifecycleRelay] a launcher's acknowledgement states '{name}' more "
                              + "than once, so what it claims cannot be established. Not accepted.");
                return false;
            }
            value = property.Value;
            found = true;
        }
        return found;
    }

    /// <summary>
    /// Run a generic launch on the CALLING TENANT's launcher for <paramref name="machine"/>.
    /// </summary>
    public static Task<LauncherRelayOutcome> SendLaunchAsync(
        TenantId tenant, string machine, LaunchRelayBody? body,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
        => SendAsync(
            tenant, machine,
            new LauncherCommand
            {
                Verb = "launch",
                Path = body?.Path,
                App = body?.App,
                Args = body?.Args,
                Cwd = body?.Cwd,
                Headless = body?.Headless ?? false,
            },
            passLauncherPayloadThrough: false,
            launchers, sendLauncherCommand, ct);

    /// <summary>
    /// Run a QUERY verb - "apps" or "files" - on the CALLING TENANT's launcher for <paramref name="machine"/>
    /// and return the launcher's answer.
    ///
    /// It differs from the action verbs in the only way a question differs from an instruction: a query
    /// ALWAYS has an answer to carry, and the HTTP route serves that document as the response body rather
    /// than wrapping it in a relay envelope. (An action verb's payload is carried too, whenever the
    /// launcher writes one - a guarded restart does - but it travels inside the envelope, because the news
    /// there really is whether the machine did the thing.) Everything else - tenant scoping, the failure
    /// outcomes - is the shared path, because a query that could reach a machine the action verbs could not
    /// would be a second, weaker boundary.
    /// </summary>
    public static Task<LauncherRelayOutcome> SendQueryAsync(
        TenantId tenant, string machine, string verb, string? query, int limit, int timeoutMilliseconds,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
        => SendAsync(
            tenant, machine,
            new LauncherCommand
            {
                Verb = verb,
                Query = query,
                Limit = limit,
                TimeoutMilliseconds = timeoutMilliseconds,
            },
            passLauncherPayloadThrough: true,
            launchers, sendLauncherCommand, ct);

    /// <summary>
    /// The single-arm dispatch: push the command down the calling tenant's launcher stream. A null from the
    /// router means the command could not be DELIVERED (no hook wired, or no active connection for this
    /// tenant+machine); the registry then decides which honest refusal that is - "never registered" or
    /// "registered but not connected". Nothing is dialed in either case.
    /// </summary>
    private static async Task<LauncherRelayOutcome> SendAsync(
        TenantId tenant, string machine, LauncherCommand streamCommand, bool passLauncherPayloadThrough,
        LauncherRegistry launchers, LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand,
        CancellationToken ct)
    {
        var streamResult = await LauncherCommandRouter.TrySendAsync(sendLauncherCommand, tenant, machine, streamCommand, ct);
        if (streamResult is not null)
        {
            var streamStatus = streamResult.Status switch
            {
                LauncherCommandStatus.Ok => 200,
                LauncherCommandStatus.BadRequest => 400,
                // A REFUSAL IS 409, NOT 502. The launcher understood the command, could have run it, and
                // declined because a condition the caller attached was not met - "restart only if empty"
                // against a Director holding live sessions. 502 would say the machine is broken and send
                // the reader to look for a fault that is not there; 409 says the machine is in a state
                // that conflicts with the request, which is exactly what happened, and the launcher's own
                // sentence (carrying the session count) rides back in the body.
                LauncherCommandStatus.Refused => 409,
                _ => 502,
            };
            // WHOSE ANSWER THE CALLER GETS IS DECIDED BY THE CALLER'S QUESTION, NOT BY WHAT CAME BACK.
            // A query asked for data, so the launcher's own document is passed through - synthesising
            // {ok:true} over it would hand back a success with the whole result thrown away. A guarded
            // restart asked a CONDITIONAL question, so its answer travels too: it carries the condition
            // the launcher honoured and the count it read, which is the only way to tell it from an older
            // launcher that ignored the flag. Every other verb keeps the envelope it has always had,
            // byte for byte, even if some future launcher starts writing a payload for it - a response
            // shape that changes because the other side got chattier is a change nobody asked for.
            var streamPayload = passLauncherPayloadThrough && streamResult.IsOk && streamResult.Payload is not null
                ? streamResult.Payload
                : streamResult.IsOk
                ? System.Text.Json.JsonSerializer.Serialize(new { ok = true, via = "stream" })
                : System.Text.Json.JsonSerializer.Serialize(new { error = streamResult.Error, via = "stream" });
            FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb} tenant={tenant.Value} machine={machine} via=stream -> {streamStatus}");
            return new LauncherRelayOutcome(RelayOutcomeKind.Relayed, streamStatus, streamPayload);
        }

        // Undeliverable. Decide WHICH refusal, in the CALLER'S partition - a machine name alone reaches
        // nothing here either. THREE distinct answers, because they have three different fixes and a
        // refusal that cannot say which one it is sends the reader to check the wrong thing.
        //
        // THE RULE ITSELF LIVES IN LauncherReachability AND IS NOT SPELT OUT AGAIN HERE. It used to be
        // written inline right at this spot, which was fine while a refusal was the only place anybody
        // asked - and stopped being fine the moment the capability query had to ask the SAME question
        // BEFORE sending anything. Two spellings of one rule agree until the day one of them is edited,
        // and then the query that exists to be trusted is the one that is wrong.
        var registered = launchers.Get(tenant, machine);
        var reach = LauncherReachability.Classify(registered, streamConnected: false, DateTime.UtcNow);
        var quietForSeconds = LauncherReachability.QuietForSeconds(registered, DateTime.UtcNow);

        switch (reach)
        {
            case LauncherReach.NoLauncher:
                FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb}: no launcher registered for tenant={tenant.Value}, machine={machine}");
                return new LauncherRelayOutcome(RelayOutcomeKind.NoLauncher);

            // Heartbeating and yet unreachable. The two facts together are the evidence: it can talk TO
            // this Gateway and this Gateway cannot talk to it, which is what a launcher predating the
            // stream looks like. Reported as what was observed - fresh heartbeat, no stream, this version
            // - so the reader can check the inference rather than take it.
            case LauncherReach.NotStreamCapable:
                FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb}: launcher registered and heartbeating "
                              + $"({quietForSeconds}s ago, version '{registered!.Version}') but holds NO command "
                              + $"stream for tenant={tenant.Value}, machine={machine} - refused. A launcher that reaches "
                              + "this Gateway but opens no stream is too old to accept stream commands; update it.");
                return new LauncherRelayOutcome(RelayOutcomeKind.NotStreamCapable, LauncherVersion: registered.Version,
                    QuietForSeconds: quietForSeconds);

            case LauncherReach.NotConnected:
                FileLog.Write($"[LauncherLifecycleRelay] {streamCommand.Verb}: launcher registered but silent for "
                              + $"{quietForSeconds}s and NOT stream-connected for tenant={tenant.Value}, "
                              + $"machine={machine} - refused (the stream is the only path)");
                return new LauncherRelayOutcome(RelayOutcomeKind.NotConnected, LauncherVersion: registered!.Version,
                    QuietForSeconds: quietForSeconds);

            default:
                // Unreachable by construction: streamConnected was passed false, so Classify cannot answer
                // Connected. It is a throw rather than a fall-through to one of the refusals above,
                // because picking one would invent a refusal for a state that means the opposite.
                throw new InvalidOperationException(
                    $"LauncherReachability.Classify answered {reach} for an undeliverable command on "
                    + $"machine '{machine}'; a command that could not be delivered cannot be Connected.");
        }
    }
}
