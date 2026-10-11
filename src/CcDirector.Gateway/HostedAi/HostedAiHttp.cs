using CcDirector.Core.HostedAi;
using CcDirector.Gateway.Contracts;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.HostedAi;

/// <summary>
/// The single place that turns a shared <see cref="HostedAiState"/> into its HTTP form at the Gateway
/// boundary (issue #939, epic #937): the wire DTO stamped onto <c>SessionDto.VoiceUnavailable</c> and
/// the HTTP 402 response the paid endpoints return when a call runs dry. Both draw the copy from the
/// one source (<see cref="HostedAiMessages"/>), so every surface shows the identical message by
/// construction. The <c>error</c> field mirrors the shared text so a client that still only reads
/// <c>error</c> shows the correct copy even before it is taught the richer shape.
/// </summary>
public static class HostedAiHttp
{
    /// <summary>HTTP 402 Payment Required - the status the hosted proxy uses for out-of-credits / cap.</summary>
    public const int PaymentRequired = 402;

    /// <summary>Build the wire DTO for a state (used to stamp <c>SessionDto.VoiceUnavailable</c>).</summary>
    public static HostedAiMessageDto Dto(HostedAiState state)
    {
        var m = HostedAiMessages.For(state);
        return new HostedAiMessageDto
        {
            State = state.ToString(),
            Text = m.Text,
            CtaLabel = m.CtaLabel,
            CtaAction = m.CtaAction.ToString(),
            CtaUrl = m.CtaUrl,
        };
    }

    /// <summary>The HTTP 402 response for a state, carrying the shared message + call-to-action. The
    /// wire shape is the single-source <see cref="HostedAiPayload"/> (issue #941), plus the machine-readable
    /// <c>code</c> the hosted proxy uses for the same condition, so <see cref="HostedAiErrorMapper.ParseErrorCode"/>
    /// reads a Gateway 402 exactly as it reads the proxy's. The phone and the Cockpit read <c>state</c>,
    /// <c>text</c> and <c>ctaUrl</c> and ignore the extra field.</summary>
    public static IResult PaymentRequiredResult(HostedAiState state)
        => Results.Json(RefusalBody.For(state), statusCode: PaymentRequired);

    /// <summary>
    /// The HTTP answer for a model call <see cref="Tenancy.TenantAiGate"/> did not allow (Pricing Change mission, B1).
    /// Refused is the EXISTING 402 contract for a member without a plan that includes the feature:
    /// <c>subscription_required</c>, with the shared Pro message and the pricing page as the call to action. Unknown
    /// (the plan could not be read) is the same temporary 503 the access lease answers, with Retry-After - never a
    /// 402, because telling a paying member to subscribe on a database hiccup would be false.
    /// </summary>
    public static IResult TenantAiRefusal(Tenancy.TenantAiDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Verdict switch
        {
            Tenancy.TenantAiVerdict.Refused => PaymentRequiredResult(HostedAiState.SubscriptionRequired),
            Tenancy.TenantAiVerdict.Unknown => new EntitlementUnknownResult(),
            _ => throw new ArgumentException("An allowed call has no refusal to answer.", nameof(decision)),
        };
    }

    /// <summary>The code the hosted proxy answers for each state, or null for a state it has no code for.</summary>
    internal static string? CodeFor(HostedAiState state) => state switch
    {
        HostedAiState.NeedsCredits => HostedAiErrorMapper.InsufficientCreditsCode,
        HostedAiState.CapReached => HostedAiErrorMapper.MonthlyLimitReachedCode,
        HostedAiState.SubscriptionRequired => HostedAiErrorMapper.SubscriptionRequiredCode,
        HostedAiState.FairUseLimitReached => HostedAiErrorMapper.FairUseLimitReachedCode,
        _ => null,
    };

    /// <summary>The 402 body: the shared payload's fields, in its pinned names, and the proxy's code.</summary>
    internal sealed record RefusalBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("ctaLabel")] string CtaLabel,
        [property: JsonPropertyName("ctaAction")] string CtaAction,
        [property: JsonPropertyName("ctaUrl")] string? CtaUrl)
    {
        public static RefusalBody For(HostedAiState state)
        {
            var p = HostedAiPayload.For(state);
            return new RefusalBody(p.Error, CodeFor(state), p.State, p.Text, p.CtaLabel, p.CtaAction, p.CtaUrl);
        }
    }

    /// <summary>503 + Retry-After 10, the body the access lease writes for an unreadable entitlement.</summary>
    private sealed class EntitlementUnknownResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = "10";
            return Results.Json(new { error = "entitlement temporarily unverifiable", code = "entitlement_unknown" },
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(httpContext);
        }
    }
}
