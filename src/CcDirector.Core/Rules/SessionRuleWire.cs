using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Core.Rules;

/// <summary>
/// Session Rules part 2, phase 1: one hook rule as the Gateway answers it (wire contract W1) and as the
/// Director writes it into a session's rules file (W2). The two are the same shape on purpose - the
/// Director copies the Gateway's answer through, it does not decide anything about a rule. The decision
/// is made in one place only, the hook's evaluator (ruling P11).
/// </summary>
public sealed record HookRuleEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("rank")] public int Rank { get; init; }
    [JsonPropertyName("appliesTo")] public string AppliesTo { get; init; } = "";
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("match")] public IReadOnlyList<string> Match { get; init; } = Array.Empty<string>();
    [JsonPropertyName("result")] public string Result { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "";
}

/// <summary>The body of <c>GET /gateway/rules/effective</c> (W1).</summary>
public sealed record EffectiveHookRulesResponse
{
    [JsonPropertyName("machine")] public string? Machine { get; init; }
    [JsonPropertyName("repo")] public string? Repo { get; init; }
    [JsonPropertyName("rules")] public IReadOnlyList<HookRuleEntry>? Rules { get; init; }
}

/// <summary>The body of <c>POST /gateway/rules/hook-firings</c> (W5): the hook's decisions, each
/// forwarded exactly as the hook wrote it.</summary>
public sealed record HookFiringsRequest
{
    [JsonPropertyName("decisions")] public IReadOnlyList<JsonElement> Decisions { get; init; } = Array.Empty<JsonElement>();
}

/// <summary>One decision the Gateway refused, with its reason (W5).</summary>
public sealed record HookFiringRefusal
{
    [JsonPropertyName("decisionId")] public string DecisionId { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>The answer to <c>POST /gateway/rules/hook-firings</c> (W5).</summary>
public sealed record HookFiringsResponse
{
    [JsonPropertyName("accepted")] public int Accepted { get; init; }
    [JsonPropertyName("duplicates")] public int Duplicates { get; init; }
    [JsonPropertyName("refused")] public IReadOnlyList<HookFiringRefusal>? Refused { get; init; }
}

/// <summary>
/// What a W1 fetch produced: the rules, or why there are none to use. A failure is NEVER an empty list -
/// "the Gateway could not be asked" and "the Gateway says no rules reach this session" are different
/// facts, and writing the second when the first is true would tell the hook a lie.
/// </summary>
public sealed record EffectiveHookRulesFetch(IReadOnlyList<HookRuleEntry>? Rules, string? Failure)
{
    public static EffectiveHookRulesFetch Answered(IReadOnlyList<HookRuleEntry> rules) => new(rules, null);
    public static EffectiveHookRulesFetch Failed(string reason) => new(null, reason);
}

/// <summary>What a W5 post produced: the Gateway's answer, or why there is none (the drops stay for the
/// next attempt).</summary>
public sealed record HookFiringsPost(HookFiringsResponse? Answer, string? Failure)
{
    public static HookFiringsPost Answered(HookFiringsResponse answer) => new(answer, null);
    public static HookFiringsPost Failed(string reason) => new(null, reason);
}

/// <summary>
/// The two Gateway calls the Director makes for session rules, implemented by the Director's existing
/// Gateway client (so they ride its address, its Director credential and its handler) and faked in tests.
/// Neither ever throws for a network or Gateway failure: each says what went wrong in its result.
/// </summary>
public interface ISessionRuleGateway
{
    /// <summary>W1: the hook rules whose scope reaches this machine and repo.</summary>
    Task<EffectiveHookRulesFetch> FetchEffectiveHookRulesAsync(string machine, string repo, CancellationToken ct);

    /// <summary>W5: forward the hook's decisions as firings.</summary>
    Task<HookFiringsPost> PostHookFiringsAsync(IReadOnlyList<JsonElement> decisions, CancellationToken ct);
}
