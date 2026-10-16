using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Issue #1177 (Phase 1): the ONE place the Gateway sends a command DOWN the Director's stream (the tunnel).
/// Every command endpoint routes through <see cref="TrySendAsync"/> so the send decision - and the
/// request-DTO serialization - is uniform across verbs and cannot diverge.
///
/// The send itself lives in the injected <paramref name="sendCommand"/> delegate
/// (<c>GatewayHost.SendCommandAsync</c>). Gateway Cleanup (the cut) made the tunnel MANDATORY and deleted
/// DirectorEndpointClient, so there is NO HTTP fallback: a null return here means the Director is not
/// tunnel-connected and the command is UNROUTABLE - the endpoint surfaces that as a 502, never an HTTP dial.
/// A non-null result - success OR a typed failure - is authoritative.
///
/// Stable Release (v1.3.0), Tier 1 item 1 - a dropped command must explain itself. Because this is the one
/// chokepoint, the wait is bounded HERE and nowhere else, so it cannot diverge across verbs. Three non-success
/// outcomes are now distinguishable to the caller, each naming what actually happened in plain English:
///   1. The Director is not tunnel-connected  - null, surfaced as a 502. Unchanged.
///   2. The Director answered nothing in time - <see cref="DirectorCommandStatus.Timeout"/>.
///   3. The tunnel dropped mid-command       - <see cref="DirectorCommandStatus.TunnelDropped"/>.
/// There is no retry and no degraded path: a bounded wait that expires fails loudly and says so.
/// </summary>
internal static class DirectorCommandRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long the Gateway waits for a Director to answer a command before giving up. It bounds the hang
    /// without breaking legitimately slow verbs. A call site that genuinely needs longer passes an explicit
    /// override to <see cref="TrySendAsync"/> - do NOT raise this global default to accommodate one verb.
    /// </summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The wait for the handful of verbs that run a LANGUAGE MODEL on the Director before they can answer -
    /// recap-generate, handover-generate, and wingman-ask. Generating over a transcript digest legitimately
    /// outruns <see cref="DefaultCommandTimeout"/>, and killing a real answer at 30 seconds would be a
    /// regression dressed up as a fix. These verbs are the exception that earns an override; the default
    /// stays where it is for everything else rather than being raised to cover them.
    ///
    /// This is a BACKSTOP and must stay strictly longer than every inner bound it sits outside, so the inner
    /// one always fires first:
    ///   - recap-generate  -> RecapGenerator.ProcessTimeout  = 2 minutes
    ///   - wingman-ask     -> WingmanService.ProcessTimeout  = 60 seconds
    ///   - handover-generate -> no inner bound; this IS its only bound
    /// The ordering is the whole point. An inner timeout knows WHICH step died and says so; this one can only
    /// ever say "the Director did not answer". If they were equal - as 2 minutes would be against recap - they
    /// would race, and whenever the backstop won it would MASK the specific message with a generic one. That
    /// masking is the exact disease this release exists to kill, so when raising an inner bound, raise this too.
    /// </summary>
    public static readonly TimeSpan LanguageModelCommandTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The signature of the "send a command down a Director's stream" hook.</summary>
    public delegate Task<DirectorCommandResult?> SendDirectorCommandAsync(string directorId, DirectorCommand command, CancellationToken ct);

    /// <summary>
    /// Try to route a command down the stream, waiting at most <paramref name="timeout"/> for the Director to
    /// answer. Returns null when the command produced no result at all - either the Director is not
    /// tunnel-connected, or the Gateway itself refused the send (the hosted no-tenant-scope deny in
    /// <c>GatewayHost.SendCommandAsync</c>, which is always a Gateway bug: the caller failed to enter a tenant
    /// scope). This layer cannot tell those apart, so it must not report one as the other. Post-cut there is no
    /// HTTP fallback, so the caller surfaces null as a 502; a <see cref="DirectorCommandStatus.Timeout"/> result when the
    /// Director is connected but answers nothing in time; and a <see cref="DirectorCommandStatus.TunnelDropped"/>
    /// result when the tunnel drops mid-command. The caller's own <paramref name="ct"/> still cancels promptly -
    /// its cancellation propagates rather than being reported as a timeout.
    /// </summary>
    /// <param name="timeout">The wait to allow, or null for <see cref="DefaultCommandTimeout"/>.</param>
    /// <param name="machineName">
    /// The machine the Director runs on, used to name it in the failure message. DirectorId is a per-process
    /// identifier and means nothing to a person reading the message, so pass this wherever the caller holds a
    /// resolved Director. When it is absent the message still names what happened, just not where.
    /// </param>
    public static async Task<DirectorCommandResult?> TrySendAsync(
        SendDirectorCommandAsync? sendCommand, string directorId, string verb, string sessionId, object? payload, CancellationToken ct,
        TimeSpan? timeout = null, string? machineName = null)
    {
        if (sendCommand is null)
            return null;

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = verb,
            SessionId = sessionId,
            PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, JsonOptions),
        };

        var wait = timeout ?? DefaultCommandTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(wait);

        try
        {
            var result = await sendCommand(directorId, command, timeoutSource.Token);
            FileLog.Write($"[DirectorCommandRouter] {verb} sid={sessionId} director={directorId}: {DescribeSendOutcome(result)}");
            return result;
        }
        catch (Exception) when (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The wait expired: the Director holds the tunnel open but never answered. Only OUR linked source
            // fired - the caller's token did not - so this is a timeout, not the caller giving up.
            //
            // This filter deliberately catches EVERY exception type rather than OperationCanceledException,
            // because SignalR does not report a cancelled client-result invocation as a cancellation: it
            // completes the pending invocation with a plain exception reading "Invocation canceled by the
            // server." Filtering on the type here silently misreported every real timeout as a tunnel drop -
            // caught only by driving a live Gateway, never by a unit test with a hand-written delegate. The
            // TOKEN is the ground truth for whose deadline fired; the exception type is not.
            var error = DescribeTimeout(machineName, wait);
            FileLog.Write($"[DirectorCommandRouter] {verb} sid={sessionId} director={directorId}: TIMED OUT after {wait.TotalSeconds:0} seconds");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER gave up (the browser went away). Their cancellation stays a cancellation - reporting
            // it as "the Director did not answer" would blame the Director for something the caller did.
            throw;
        }
        catch (Exception ex)
        {
            // The send threw while our deadline had NOT expired: the tunnel dropped while the command was in
            // flight. Caught here so it cannot escape as an unhandled exception and reach the caller as a raw
            // 500 with no explanation.
            var error = DescribeTunnelDrop(machineName);
            FileLog.Write($"[DirectorCommandRouter] {verb} sid={sessionId} director={directorId}: TUNNEL DROPPED mid-command: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.TunnelDropped, error);
        }
    }

    /// <summary>
    /// The timeout message. Written for a person reading it on a phone in a moving car: it names the machine,
    /// says what was observed, and gives the wait in whole seconds. No status code, no stack trace, no jargon.
    ///
    /// Like <see cref="DescribeTunnelDrop"/>, it deliberately does not promise the command was skipped. A timeout
    /// proves only that the GATEWAY stopped waiting: the Director may have received the command, carried it out,
    /// and answered late. It used to end "The command was not carried out", which read as settled fact - so the
    /// user re-tapped and the agent got the same prompt twice. The two messages hedge the same way because they
    /// leave the caller in the same position: the outcome is genuinely unknown.
    /// </summary>
    /// <remarks>
    /// INTERNAL so its wording can be asserted directly (issue #1156). It is a pure function of the machine
    /// name and the wait, but the only way to read its output used to be to run a real timeout - so three
    /// tests that cared solely about the SENTENCE each slept through a thirty-second production timeout,
    /// costing two minutes of every suite run to check a string. The wording is now tested here, in
    /// microseconds; a separate test still runs a real (short) timeout and asserts the router returns exactly
    /// this function's output, so the join between them stays proven rather than assumed.
    /// </remarks>
    internal static string DescribeTimeout(string? machineName, TimeSpan wait) =>
        string.IsNullOrWhiteSpace(machineName)
            ? $"The Director did not answer within {wait.TotalSeconds:0} seconds. It is not known whether the command was carried out."
            : $"The Director on {machineName} did not answer within {wait.TotalSeconds:0} seconds. It is not known whether the command was carried out.";

    /// <summary>
    /// The mid-command tunnel-drop message. Same audience as <see cref="DescribeTimeout"/>. It deliberately does
    /// not promise the command was skipped: the Director may have run it before the connection died, and saying
    /// otherwise would be a guess stated as fact.
    /// </summary>
    private static string DescribeTunnelDrop(string? machineName) =>
        string.IsNullOrWhiteSpace(machineName)
            ? "The connection to the Director dropped while the command was being sent. It is not known whether the command was carried out."
            : $"The connection to the Director on {machineName} dropped while the command was being sent. It is not known whether the command was carried out.";

    /// <summary>
    /// How the send went, for the log. A null result has TWO causes and THIS LAYER CANNOT SEE WHICH: the
    /// Director is not tunnel-connected, or the Gateway refused the send before it ever left (the hosted
    /// no-tenant-scope deny in <c>GatewayHost.SendCommandAsync</c>).
    ///
    /// It used to name only the first - "director not tunnel-connected (unroutable)" - which is how the
    /// voice-mode sweep spent a day reporting a Director as unreachable while that same Director answered
    /// other verbs in the same millisecond. A Gateway bug wearing a network error's clothes reads as an
    /// infrastructure flake, so it gets waited out instead of fixed. The line must state what was OBSERVED
    /// (no result came back) and point at the line that does know the cause, rather than picking the more
    /// familiar of the two and asserting it as fact.
    ///
    /// Extracted as a pure function so the wording is a tested behaviour rather than an incidental string:
    /// this message IS the diagnostic, so it is the thing worth pinning.
    /// </summary>
    internal static string DescribeSendOutcome(DirectorCommandResult? result) =>
        result is null
            ? "no result - the Director is not tunnel-connected, OR the Gateway refused the send before it left " +
              "(the preceding [GatewayHost] line names which)"
            : $"stream status={result.Status}";

    /// <summary>Deserialize a verb response DTO carried in <see cref="DirectorCommandResult.BodyJson"/>.</summary>
    public static T? ReadBody<T>(DirectorCommandResult result) where T : class =>
        string.IsNullOrEmpty(result.BodyJson) ? null : JsonSerializer.Deserialize<T>(result.BodyJson, JsonOptions);

    /// <summary>
    /// Render a failed stream result as the "director returned N: msg" error string the HTTP client path uses.
    /// The two Gateway-synthesized outcomes are the exception: the Director returned nothing at all, so saying
    /// "director returned Timeout" would be a lie, and their message already explains itself in plain English -
    /// it is carried verbatim. Every Director-sent status keeps its existing wording byte-for-byte.
    /// </summary>
    public static string DescribeFailure(DirectorCommandResult result) =>
        result.Status is DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped
            ? result.Error ?? $"director returned {result.Status}"
            : $"director returned {result.Status}: {result.Error}";

    /// <summary>
    /// THE DIRECTOR'S OWN REFUSAL OF A SEAT START (Factory Definition mission, phase 5, QA finding 6): the sentence the
    /// Director refused a seat's create with, alone, or null when this is not one. A seat start is a create that names
    /// a seat or a kit; a refusal is the Director answering <see cref="DirectorCommandStatus.BadRequest"/> or
    /// <see cref="DirectorCommandStatus.Conflict"/> with a sentence - a secret the machine does not hold, a repository
    /// it has not registered, an agent that cannot take the seat's rules. That sentence is the answer a person acts on,
    /// so every door a seat start goes through carries it alone, as a 409 where the door answers with a status, never
    /// wrapped as "director returned ..." behind a 502.
    ///
    /// Everything else stays a fault: <see cref="DirectorCommandStatus.Error"/> (the create threw), a Director that did
    /// not answer or dropped, and every create that is not a seat start, whose answers are unchanged.
    /// </summary>
    public static string? SeatStartRefusal(NewSessionRequest? request, DirectorCommandResult? result)
    {
        if (request is null || result is null || result.Ok) return null;
        if (string.IsNullOrWhiteSpace(request.FactorySeat) && string.IsNullOrWhiteSpace(request.FactoryKit)) return null;
        if (result.Status is not (DirectorCommandStatus.BadRequest or DirectorCommandStatus.Conflict)) return null;
        return string.IsNullOrWhiteSpace(result.Error) ? null : result.Error;
    }
}
