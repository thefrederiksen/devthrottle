using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Describes one live session whose handover note the wingman should write when a
/// workspace is saved with "Include handover documents" enabled (issue #512).
/// </summary>
public sealed class WorkspaceHandoverRequest
{
    /// <summary>The session's working directory (the repository it runs in). Required; must exist.</summary>
    public string RepoPath { get; init; } = string.Empty;

    /// <summary>The agent CLI the session runs. Only kinds whose driver can read a transcript
    /// can answer an ask; others are reported as a per-session failure rather than guessed.</summary>
    public AgentKind AgentKind { get; init; } = AgentKind.ClaudeCode;

    /// <summary>Display name of the session, used in the handover title and frontmatter.</summary>
    public string SessionName { get; init; } = string.Empty;

    /// <summary>The launch arguments the session was started with, or null for the driver default.
    /// NEVER carries --print / -p (that is banned project-wide; the ask runs a real session).</summary>
    public string? AgentArgs { get; init; }
}

/// <summary>Outcome of writing one session's handover note.</summary>
public sealed class WorkspaceHandoverResult
{
    /// <summary>True when the wingman produced a note and it was persisted to disk.</summary>
    public bool Success { get; init; }

    /// <summary>The path to the written handover document, or null when <see cref="Success"/> is false.</summary>
    public string? HandoverPath { get; init; }

    /// <summary>When <see cref="Success"/> is false, why the note could not be written.</summary>
    public string? ErrorMessage { get; init; }

    public static WorkspaceHandoverResult Ok(string path) =>
        new() { Success = true, HandoverPath = path };

    public static WorkspaceHandoverResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// Writes a per-session handover note by having the wingman run as a REAL session
/// (issue #512). It drives the <see cref="SessionAskRunner"/> engine added by issue #509 -
/// open a real driver-backed session, ask it to summarize the session, read the answer
/// back from the transcript, tear the session down - then persists that answer through
/// <see cref="HandoverScanner.WriteNew"/> so the same parser the Handovers tab and the
/// /handover skill use round-trips it. There is no dependency on the /handover skill and
/// no <c>--print</c> / <c>-p</c> anywhere; the acceptance criteria require both.
///
/// The <see cref="SessionAskRunner"/> dependency is injected so the orchestration is unit
/// testable with a fake ask; the production default constructs a real runner.
///
/// HOST PROCESS WARNING (nested ConPty): like <see cref="SessionAskRunner"/>, this must be
/// hosted from a clean process (the desktop app, a service, a Task Scheduler launch), not
/// from inside a Claude Code pseudoconsole.
/// </summary>
public sealed class WorkspaceHandoverWriter
{
    /// <summary>How long to give the wingman to write one session's note before giving up on it.</summary>
    public static readonly TimeSpan DefaultAskTimeout = TimeSpan.FromSeconds(120);

    private readonly Func<AgentKind, string?, string?, string, string, TimeSpan, CancellationToken, Task<SessionAskResult>> _ask;
    private readonly Func<string, string, IReadOnlyList<string>?, string?, string> _writeHandover;
    private readonly Action<string> _log;
    private readonly TimeSpan _askTimeout;

    /// <summary>
    /// Create a writer. Production callers pass nothing and get a real
    /// <see cref="SessionAskRunner"/> + <see cref="HandoverScanner.WriteNew"/>; tests pass
    /// fakes for both seams so no live agent or filesystem is required.
    /// </summary>
    /// <param name="ask">The ask seam: (kind, exePath, agentArgs, workingDir, prompt, timeout, ct) -> answer.
    /// Defaults to a real <see cref="SessionAskRunner"/>.</param>
    /// <param name="writeHandover">The persistence seam: (title, content, repoPaths, sessionName) -> path.
    /// Defaults to <see cref="HandoverScanner.WriteNew"/>.</param>
    /// <param name="log">Diagnostic log sink. Defaults to <see cref="FileLog.Write"/>.</param>
    /// <param name="askTimeout">Per-session ask budget. Null uses <see cref="DefaultAskTimeout"/>.</param>
    public WorkspaceHandoverWriter(
        Func<AgentKind, string?, string?, string, string, TimeSpan, CancellationToken, Task<SessionAskResult>>? ask = null,
        Func<string, string, IReadOnlyList<string>?, string?, string>? writeHandover = null,
        Action<string>? log = null,
        TimeSpan? askTimeout = null)
    {
        _ask = ask ?? DefaultAsk;
        _writeHandover = writeHandover ?? HandoverScanner.WriteNew;
        _log = log ?? FileLog.Write;
        _askTimeout = askTimeout ?? DefaultAskTimeout;
    }

    private static async Task<SessionAskResult> DefaultAsk(
        AgentKind kind, string? exePath, string? agentArgs, string workingDir,
        string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var runner = new SessionAskRunner();
        return await runner.AskAsync(kind, exePath, agentArgs, workingDir, prompt, timeout, ct);
    }

    /// <summary>
    /// Have the wingman write one session's handover note and persist it. Returns the file
    /// path on success, or a failure result (logged) when the session's repo is gone, the
    /// agent cannot answer an ask, or the ask times out - the caller treats a failure as
    /// "this session simply gets no handover" so one bad session never aborts the whole save.
    /// </summary>
    public async Task<WorkspaceHandoverResult> WriteForSessionAsync(
        WorkspaceHandoverRequest request, CancellationToken ct = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        _log($"[WorkspaceHandoverWriter] WriteForSessionAsync: name={request.SessionName}, " +
             $"repo={request.RepoPath}, kind={request.AgentKind}");

        if (string.IsNullOrWhiteSpace(request.RepoPath) || !Directory.Exists(request.RepoPath))
            return Failed(request, $"repository path does not exist: {request.RepoPath}");

        SessionAskResult ask;
        try
        {
            var prompt = BuildHandoverPrompt(request.SessionName);
            ask = await _ask(
                request.AgentKind, null, request.AgentArgs, request.RepoPath, prompt, _askTimeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(request, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(ask.Answer))
            return Failed(request, "the wingman returned an empty handover note");

        var title = BuildTitle(request.SessionName);
        var path = _writeHandover(title, ask.Answer, new[] { request.RepoPath }, request.SessionName);

        _log($"[WorkspaceHandoverWriter] WriteForSessionAsync OK: name={request.SessionName}, path={path}");
        return WorkspaceHandoverResult.Ok(path);
    }

    private WorkspaceHandoverResult Failed(WorkspaceHandoverRequest request, string message)
    {
        _log($"[WorkspaceHandoverWriter] WriteForSessionAsync FAILED: name={request.SessionName}: {message}");
        return WorkspaceHandoverResult.Fail(message);
    }

    /// <summary>
    /// The handover-writing prompt the wingman is asked. It instructs the agent to summarize
    /// what the session did and what to do next - a handover, NOT a transcript dump - which is
    /// exactly the summary a reopened session can be seeded from. Public so tests assert the
    /// contract text.
    /// </summary>
    public static string BuildHandoverPrompt(string sessionName)
    {
        var who = string.IsNullOrWhiteSpace(sessionName) ? "this session" : $"the session \"{sessionName}\"";
        return
            $"Write a concise handover note for {who} so a fresh session can pick up where this one left off. " +
            "Base it on the work done in this conversation and repository. " +
            "Cover, in plain prose with short headings: what we were working on and why, " +
            "what has been done so far, the current state, and the concrete next steps. " +
            "This is a summary for a human and a future agent - NOT a transcript and NOT a replay of every message. " +
            "Use plain ASCII text only (no emoji or special symbols). Keep it focused.";
    }

    /// <summary>The handover document title for a session, used by <see cref="HandoverScanner.WriteNew"/>.</summary>
    public static string BuildTitle(string sessionName) =>
        string.IsNullOrWhiteSpace(sessionName) ? "Session handover" : $"{sessionName} handover";
}
