using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission: the tunnel-only caller for the session verbs the voice and dictation cluster
/// needs (turns / buffer / prompt / create). Each method routes through <see cref="DirectorCommandRouter"/>:
/// a non-null <see cref="DirectorCommandResult"/> is authoritative (its Ok/typed-failure maps to a null body
/// / null return on failure), and a null result means the owning Director is not connected to the tunnel =
/// unreachable, which maps to the same not-found/failure the old HTTP path produced when the Director was down.
///
/// One instance binds a resolved owning <see cref="DirectorDto"/> - its <see cref="DirectorDto.DirectorId"/>
/// for the tunnel - to the send-command hook. This keeps the routing decision in ONE place instead of
/// scattering it across the many call sites in the cluster.
/// </summary>
internal sealed class SessionVerbClient
{
    private readonly DirectorDto _director;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync? _sendCommand;

    public SessionVerbClient(DirectorDto director,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _sendCommand = sendCommand;
    }

    /// <summary>The resolved owning Director (so a caller can log or thread its id).</summary>
    public DirectorDto Director => _director;

    /// <summary>
    /// Gateway Cleanup mission: bind a tunnel-only caller to a Director known only by its
    /// <paramref name="directorId"/> - the shape the machine spawner and the work-list drain driver hold.
    /// These are director-level callers (they create a session and read its buffer on a resolved MACHINE,
    /// not a pre-located session), so there is no owning-session resolve to do; a minimal
    /// <see cref="DirectorDto"/> carrying just the id is enough for the tunnel.
    /// </summary>
    public static SessionVerbClient ForDirector(string directorId,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        var director = new DirectorDto { DirectorId = directorId ?? "" };
        return new SessionVerbClient(director, sendCommand);
    }

    /// <summary>
    /// Resolve the Director that owns <paramref name="sid"/> (from the pushed store - the SAME resolution the
    /// session REST endpoints use via <see cref="GatewayEndpoints.LocateSessionAsync"/>) and bind it to a
    /// tunnel-only caller. Returns null when no Director owns the session.
    /// </summary>
    public static async Task<SessionVerbClient?> ResolveAsync(
        string sid, TenantId tenant, DirectorRegistry registry,
        PushedSessionStore? pushedSessions, TimeSpan streamStale, SessionOwnerCache? owners,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        // Hosted Multi-Tenancy: SessionVerbClient is the tunnel-only RELAY caller (voice/verb sends). The
        // caller resolves the tenant - the request tenant on the wingman voice surface, the owning tenant in
        // background loops - and passes it here, so a session is only ever located inside its own partition.
        var (director, session) = await GatewayEndpoints.LocateSessionAsync(
            registry, sid, pushedSessions, streamStale, tenant, owners);
        if (director is null || session is null)
            return null;
        return new SessionVerbClient(director, sendCommand);
    }

    /// <summary>Read the session's turn widgets. Tunnel-only ("turns" verb -> <see cref="TurnsResponse"/>);
    /// a failed or absent tunnel result maps to null (owning Director not connected = unreachable).</summary>
    public async Task<TurnsResponse?> GetTurnsAsync(string sid, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "turns", sid, null, ct,
            machineName: _director.MachineName);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<TurnsResponse>(result) : null;
    }

    /// <summary>Read the session terminal buffer. Tunnel-only ("buffer" verb, the query arguments in a
    /// <see cref="BufferRequest"/> payload -> <see cref="BufferResponse"/>).</summary>
    public async Task<BufferResponse?> GetBufferAsync(string sid, int? lines, bool raw, long? since, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "buffer", sid,
            new BufferRequest { Lines = lines, Raw = raw, Since = since }, ct,
            machineName: _director.MachineName);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<BufferResponse>(result) : null;
    }

    /// <summary>Read the session's RESOLVED live screen grid (issue #1777). Tunnel-only ("screen-grid" verb
    /// -> <see cref="ScreenGridResponse"/>); a failed or absent tunnel result maps to null (owning Director not
    /// connected = unreachable, which the caller treats as an unreadable screen and fails closed). This is the
    /// alternate-screen-correct read the menu detector uses - it sees a full-screen picker the scrollback-based
    /// <see cref="GetBufferAsync"/> cannot.</summary>
    public async Task<ScreenGridResponse?> GetScreenGridAsync(string sid, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "screen-grid", sid, null, ct,
            machineName: _director.MachineName);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<ScreenGridResponse>(result) : null;
    }

    /// <summary>What became of a prompt send. THREE answers, because the boolean below has only two and the
    /// missing one is the one that matters: a command that never left this Gateway is not the same event as
    /// one that went out and came back a failure.</summary>
    internal enum PromptSendKind
    {
        /// <summary>The Director answered Ok. The prompt was accepted.</summary>
        Accepted,

        /// <summary>The command produced no result at all - the owning Director is not on the tunnel, or
        /// this Gateway refused the send before it left. NOTHING was sent.</summary>
        NeverLeftTheGateway,

        /// <summary>The command went out and what became of it is not known: a timeout, a dropped tunnel, or
        /// a Director that answered a failure other than the two definite refusals below. It must not be
        /// reported as accepted OR as never sent.</summary>
        Unanswered,

        /// <summary>The Director answered, and its answer was a DEFINITE REFUSAL that typed nothing: the prompt
        /// verb's <see cref="DirectorCommandStatus.Conflict"/> ("session has exited") or
        /// <see cref="DirectorCommandStatus.NotFound"/> ("session not found"). Both are returned by
        /// <c>SessionCommandExecutor.PromptAsync</c> BEFORE it touches the session, so the text provably did not
        /// land. Kept apart from <see cref="Unanswered"/> because a caller that records delivery must not tell
        /// the owner his words "may have reached" a session that refused them (dev reports review, High 4).</summary>
        DirectorRefused,
    }

    /// <summary>What became of a prompt send, and the route's own words for it.</summary>
    internal sealed record PromptSendOutcome(PromptSendKind Kind, PromptResponse? Body, string Detail);

    /// <summary>
    /// Send a prompt into the session, KEEPING THE THREE OUTCOMES APART. Tunnel-only ("prompt" verb, the
    /// <see cref="PromptRequest"/> as payload -> <see cref="PromptResponse"/>).
    ///
    /// <see cref="PostPromptAsync"/> collapses the last two into one false, deliberately, because its
    /// callers only ever needed "did it work". A caller that writes a DURABLE RECORD of what it did to
    /// somebody's session needs more than that: reporting an absent tunnel result and a remote refusal the
    /// same way is how a firing record came to say that text had been typed into a session that never
    /// received it. So the distinction is preserved here rather than reconstructed by parsing an error
    /// string, which would be a second answer to a question this layer already knows.
    /// </summary>
    public async Task<PromptSendOutcome> SendPromptAsync(
        string sid, PromptRequest req, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "prompt", sid, req, ct,
            machineName: _director.MachineName);

        if (result is null)
            return new PromptSendOutcome(
                PromptSendKind.NeverLeftTheGateway, null,
                "the owning Director is not connected to the tunnel, so the prompt never left this Gateway.");

        if (result.Ok)
            return new PromptSendOutcome(PromptSendKind.Accepted, DirectorCommandRouter.ReadBody<PromptResponse>(result), "");

        return result.Status is DirectorCommandStatus.Conflict or DirectorCommandStatus.NotFound
            ? new PromptSendOutcome(PromptSendKind.DirectorRefused, null, DirectorCommandRouter.DescribeFailure(result))
            : new PromptSendOutcome(PromptSendKind.Unanswered, null, DirectorCommandRouter.DescribeFailure(result));
    }

    /// <summary>Send a prompt into the session. Tunnel-only ("prompt" verb, the <see cref="PromptRequest"/>
    /// as payload -> <see cref="PromptResponse"/>); a failed or absent tunnel result maps to the same
    /// (false, null, error) tuple. This is the UserInput send the voice cluster needs. A caller that RECORDS
    /// what it did should use <see cref="SendPromptAsync"/> instead - see its remarks.</summary>
    public async Task<(bool ok, PromptResponse? body, string? error)> PostPromptAsync(
        string sid, PromptRequest req, CancellationToken ct = default)
    {
        var sent = await SendPromptAsync(sid, req, ct).ConfigureAwait(false);
        return sent.Kind == PromptSendKind.Accepted
            ? (true, sent.Body, null)
            : (false, null, sent.Kind == PromptSendKind.NeverLeftTheGateway
                ? "owning Director is not connected to the tunnel"
                : sent.Detail);
    }

    /// <summary>
    /// Create a new session on this Director. Tunnel-only ("create" verb - director-level, so the command
    /// carries an EMPTY session id exactly like the /directors surface reads do; the
    /// <see cref="NewSessionRequest"/> is the payload -&gt; <see cref="SessionDto"/>). A failed or absent
    /// tunnel result maps to the same (false, null, error) tuple.
    /// </summary>
    public async Task<(bool ok, SessionDto? body, string? error)> CreateSessionAsync(
        NewSessionRequest req, CancellationToken ct = default)
    {
        var (ok, body, error, _) = await CreateSessionWithOutcomeAsync(req, ct);
        return (ok, body, error);
    }

    /// <summary>
    /// <see cref="CreateSessionAsync"/>, also saying whether a failure left the outcome UNKNOWN: the Gateway stopped
    /// waiting (<see cref="DirectorCommandStatus.Timeout"/>) or the tunnel dropped mid-command
    /// (<see cref="DirectorCommandStatus.TunnelDropped"/>). Either way the Director may have created the session, so
    /// a caller that must never start twice has to treat it as possibly started. Read from the status, never the text.
    /// </summary>
    public async Task<(bool ok, SessionDto? body, string? error, bool outcomeUnknown)> CreateSessionWithOutcomeAsync(
        NewSessionRequest req, CancellationToken ct = default)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, _director.DirectorId, "create", "", req, ct,
            machineName: _director.MachineName);
        if (result is null)
            return (false, null, "owning Director is not connected to the tunnel", false);
        return result.Ok
            ? (true, DirectorCommandRouter.ReadBody<SessionDto>(result), null, false)
            : (false, null, DirectorCommandRouter.DescribeFailure(result),
                result.Status is DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped);
    }
}
