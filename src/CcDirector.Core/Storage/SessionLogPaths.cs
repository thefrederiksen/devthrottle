namespace CcDirector.Core.Storage;

/// <summary>
/// Phase 5: file-system layout for the per-session persistent log. One directory per
/// session under <c>%LOCALAPPDATA%\cc-director\session-logs\&lt;sid&gt;\</c>, holding
/// append-only JSONL streams the wingman and any future tooling can replay.
/// </summary>
public static class SessionLogPaths
{
    /// <summary>Root: <c>{CcStorage.Root()}\session-logs</c>.</summary>
    public static string Root => Path.Combine(CcStorage.Root(), "session-logs");

    public static string SessionDir(Guid sessionId)
        => Path.Combine(Root, sessionId.ToString("D"));

    public static string MetaJson(Guid sessionId)        => Path.Combine(SessionDir(sessionId), "meta.json");
    public static string RawJsonl(Guid sessionId)        => Path.Combine(SessionDir(sessionId), "raw.jsonl");
    public static string TurnsJsonl(Guid sessionId)      => Path.Combine(SessionDir(sessionId), "turns.jsonl");
    public static string AgentViewJsonl(Guid sessionId)  => Path.Combine(SessionDir(sessionId), "agent-view.jsonl");
    public static string WingmanEventsJsonl(Guid sessionId) => Path.Combine(SessionDir(sessionId), "wingman-events.jsonl");
}
