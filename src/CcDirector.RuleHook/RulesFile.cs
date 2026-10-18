using System.Text.Json.Serialization;

namespace CcDirector.RuleHook;

/// <summary>
/// The per-session rules file the Director writes and this hook reads (wire contract W2 in
/// docs/missions/session-rules-hooks-2026-10-18/phase-1-plan.md). Only the fields the hook decides on
/// are read; anything else in the file is ignored.
/// </summary>
internal sealed class RulesFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("machine")] public string? Machine { get; set; }
    [JsonPropertyName("repo")] public string? Repo { get; set; }
    [JsonPropertyName("rules")] public List<HookRule>? Rules { get; set; }
}

/// <summary>One hook rule as the Director wrote it (a W2 rules item).</summary>
internal sealed class HookRule
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("appliesTo")] public string? AppliesTo { get; set; }
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("match")] public List<string>? Match { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
}

/// <summary>The part of Claude Code's PreToolUse event the hook reads, in Claude Code's own snake_case.</summary>
internal sealed class ClaudeCodeToolEvent
{
    [JsonPropertyName("hook_event_name")] public string? HookEventName { get; set; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; set; }
    [JsonPropertyName("tool_input")] public ClaudeCodeToolInput? ToolInput { get; set; }
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }
}

/// <summary>The shell tool's input. Only the command text is read.</summary>
internal sealed class ClaudeCodeToolInput
{
    [JsonPropertyName("command")] public string? Command { get; set; }
}

/// <summary>One decision, as the hook drops it for the Director (wire contract W3).</summary>
internal sealed class DecisionRecord
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("decisionId")] public string DecisionId { get; set; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("occurredUtc")] public string OccurredUtc { get; set; } = "";
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("agentEvent")] public string AgentEvent { get; set; } = "";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("agentTool")] public string AgentTool { get; set; } = "";
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("decision")] public string Decision { get; set; } = "";
    [JsonPropertyName("decidingRuleId")] public string DecidingRuleId { get; set; } = "";
    [JsonPropertyName("overriddenRuleIds")] public List<string> OverriddenRuleIds { get; set; } = [];
    [JsonPropertyName("matchedRuleIds")] public List<string> MatchedRuleIds { get; set; } = [];
    [JsonPropertyName("evaluationMicroseconds")] public long EvaluationMicroseconds { get; set; }
}

/// <summary>
/// Source-generated serialisation for every shape the hook touches. Reflection-based JSON is switched
/// off for this project (JsonSerializerIsReflectionEnabledByDefault), so a shape missing from here
/// fails the build or the first test rather than costing start-up time on every command.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RulesFile))]
[JsonSerializable(typeof(ClaudeCodeToolEvent))]
[JsonSerializable(typeof(DecisionRecord))]
internal sealed partial class RuleHookJson : JsonSerializerContext;
