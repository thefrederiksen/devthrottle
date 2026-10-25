using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Agents;

namespace CcDirector.Core.Sessions;

public sealed record SessionStateEvent(
    int Version,
    Guid EventId,
    Guid SessionId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Event,
    string Source,
    string Agent,
    string? Detail,
    string? CliVersion);

/// <summary>An adapter diagnostic that reports an event or notification type it could not interpret.</summary>
public sealed record SessionStateDiagnostic(
    DateTimeOffset RecordedAtUtc,
    Guid EventId,
    long Sequence,
    string Agent,
    string RawType,
    string CliVersion);

/// <summary>A lower-priority state fact that disagreed with the state selected by the resolver.</summary>
public sealed record SessionStateDisagreement(
    DateTimeOffset RecordedAtUtc,
    ActivityState ReportedState,
    string ReportedSource,
    DateTimeOffset ReportedAtUtc,
    ActivityState SelectedState,
    string SelectedSource);

/// <summary>The stable source words carried beside a session's resolved activity state.</summary>
public static class SessionStateSources
{
    public const string Initial = "initial";
    public const string Core = "core";
    public const string Agent = "agent";
    public const string TerminalOutput = "terminal-output";
    public const string Screen = "screen";
    public const string Wingman = "wingman";
    public const string SilenceTimer = "silence-timer";

    public static int Priority(string source) => source switch
    {
        Agent => 4,
        TerminalOutput => 3,
        Screen => 3,
        Wingman => 2,
        SilenceTimer => 1,
        _ => 0,
    };
}

public static class SessionStateEventContract
{
    public const int CurrentVersion = 1;
    public const int MaxDetailLength = 512;
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static bool TryParse(string json, out SessionStateEvent? evt, out string error)
    {
        evt = null;
        error = "invalid JSON";
        try { evt = JsonSerializer.Deserialize<SessionStateEvent>(json, JsonOptions); }
        catch (JsonException ex)
        {
            Utilities.FileLog.Write($"[SessionStateEventContract] TryParse rejected invalid JSON: {ex.Message}");
            return false;
        }
        if (evt is null) return false;
        if (evt.Version != CurrentVersion) error = "unsupported version";
        else if (evt.EventId == Guid.Empty || evt.SessionId == Guid.Empty) error = "eventId and sessionId must be GUIDs";
        else if (evt.Sequence <= 0) error = "sequence must be positive";
        else if (evt.OccurredAtUtc.Offset != TimeSpan.Zero) error = "occurredAtUtc must be UTC";
        else if (evt.Source != "agent") error = "source must be agent";
        else if (evt.Agent is not ("claude" or "codex" or "pi")) error = "unknown agent";
        else if (evt.Detail?.Length > MaxDetailLength) error = "detail exceeds 512 characters";
        else if (evt.CliVersion?.Length > 128) error = "cliVersion exceeds 128 characters";
        else if (evt.Event is not ("turn_started" or "turn_ended" or "permission_requested" or "menu_opened" or "menu_closed" or "unsupported")) error = "unknown event";
        else if (evt.Event == "unsupported" && string.IsNullOrWhiteSpace(evt.Detail)) error = "unsupported detail must name the raw event type";
        else if (evt.Event == "unsupported" && string.IsNullOrWhiteSpace(evt.CliVersion)) error = "unsupported cliVersion is required";
        else
        {
            error = "";
            return true;
        }
        Utilities.FileLog.Write($"[SessionStateEventContract] TryParse rejected event: {error}");
        evt = null;
        return false;
    }

    public static bool IsTimestampAccepted(DateTimeOffset occurredAtUtc, DateTimeOffset nowUtc) =>
        occurredAtUtc >= nowUtc - MaxAge && occurredAtUtc <= nowUtc + MaxFutureSkew;

    public static bool MatchesAgent(string agent, AgentKind kind) => (agent, kind) switch
    {
        ("claude", AgentKind.ClaudeCode) => true,
        ("codex", AgentKind.Codex) => true,
        ("pi", AgentKind.Pi) => true,
        _ => false,
    };

    public static ActivityState StateFor(string eventName) => eventName switch
    {
        "turn_started" => ActivityState.Working,
        "turn_ended" => ActivityState.WaitingForInput,
        "permission_requested" => ActivityState.WaitingForPerm,
        "menu_opened" => ActivityState.MenuOpen,
        "menu_closed" => ActivityState.WaitingForInput,
        _ => throw new ArgumentOutOfRangeException(nameof(eventName)),
    };
}
