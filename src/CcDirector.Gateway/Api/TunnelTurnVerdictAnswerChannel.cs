using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The answer route's channel onto one located session, over the tunnel. Every leg is a path that already exists and
/// is used unchanged: the live screen-grid read, the prompt route's model-confirmed menu guard, and the prompt send -
/// which, with Enter off, the Director writes as raw bytes.
///
/// THE MENU GUARD'S MODEL CALL IS ON THE PLAN (Pricing Change mission, B1, review finding 1). The pure classifier
/// answers for every plan; when it trips, the plan is asked before the model is, and a plan that does not allow the
/// Wingman - or could not be read - leaves here as a <see cref="CcDirector.Gateway.Tenancy.TenantAiRefusedException"/>
/// for the route to answer 402 or 503. It is never answered as "a menu owns the screen".
/// </summary>
internal sealed class TunnelTurnVerdictAnswerChannel : ITurnVerdictAnswerChannel
{
    private readonly SessionVerbClient _route;
    private readonly string _sessionId;
    private readonly TenantId _tenant;
    private readonly WingmanTranslator? _translator;
    private readonly Func<CcDirector.Gateway.Tenancy.TenantAiDecision> _askPlan;
    private readonly Func<string, bool, PromptRequest> _buildRequest;

    /// <param name="askPlan">Whether the caller's plan includes the Wingman, asked only when the menu guard is
    /// about to ask the model.</param>
    /// <param name="buildRequest">Builds the prompt request for the bytes, carrying the attribution the route
    /// decided from the authenticated caller - the same markers the prompt route sets.</param>
    public TunnelTurnVerdictAnswerChannel(SessionVerbClient route, string sessionId, TenantId tenant,
        WingmanTranslator? translator, Func<CcDirector.Gateway.Tenancy.TenantAiDecision> askPlan,
        Func<string, bool, PromptRequest> buildRequest)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _sessionId = sessionId;
        _tenant = tenant;
        _translator = translator;
        _askPlan = askPlan ?? throw new ArgumentNullException(nameof(askPlan));
        _buildRequest = buildRequest ?? throw new ArgumentNullException(nameof(buildRequest));
    }

    public Task<ScreenGridResponse?> ReadScreenAsync(CancellationToken ct) => _route.GetScreenGridAsync(_sessionId, ct);

    public async Task<bool> MenuOwnsScreenAsync(CancellationToken ct)
    {
        var confirmed = await WaitingScreenReader
            .ConfirmedMenuOnThePlanAsync(_route, _sessionId, _tenant, _translator, _askPlan, ct).ConfigureAwait(false);
        if (confirmed.PlanRefusal is { } refused)
            throw new CcDirector.Gateway.Tenancy.TenantAiRefusedException(refused);
        return confirmed.IsMenu;
    }

    public async Task<TurnVerdictAnswerWrite> WriteAsync(string text, bool appendEnter, CancellationToken ct)
    {
        var sent = await _route.SendPromptAsync(_sessionId, _buildRequest(text, appendEnter), ct).ConfigureAwait(false);
        return sent.Kind switch
        {
            SessionVerbClient.PromptSendKind.Accepted when sent.Body is { Accepted: true }
                => new TurnVerdictAnswerWrite(TurnVerdictAnswerWriteKind.Accepted, ""),
            SessionVerbClient.PromptSendKind.Accepted
                => new TurnVerdictAnswerWrite(TurnVerdictAnswerWriteKind.Unanswered,
                    sent.Body?.Error ?? "the Director answered without accepting the write"),
            SessionVerbClient.PromptSendKind.NeverLeftTheGateway
                => new TurnVerdictAnswerWrite(TurnVerdictAnswerWriteKind.NeverLeftTheGateway, sent.Detail),
            // A definite Director refusal maps to Unanswered, exactly as it did before the send kinds split it out.
            SessionVerbClient.PromptSendKind.DirectorRefused
                => new TurnVerdictAnswerWrite(TurnVerdictAnswerWriteKind.Unanswered, sent.Detail),
            _ => new TurnVerdictAnswerWrite(TurnVerdictAnswerWriteKind.Unanswered, sent.Detail),
        };
    }
}
