using CcDirector.Core.Agents;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The birth facts a starting session's row carries before the session exists (factory failures of 6 November 2026,
/// "the Director answers a start at once"). They are the facts the create request settled - the same values the
/// pre-launch stamps put on the session - so the Gateway's write-once history columns, which are written on FIRST
/// SIGHT, record the same thing whether that first sight is the starting row or the launched one. The seat and the
/// workflow run are request facts too, and they matter most on the starting row: a seat in a repository is answered
/// with it, and the Gateway records the workflow participant and recognises the seat from that answer. Facts that only
/// the launch can know (the pooled slot, the process, the number) are left off; the launched row fills them in.
/// </summary>
public sealed record PendingSessionFacts(
    string? OriginKind,
    string? OriginSurface,
    Guid? ParentSessionId,
    string? Factory,
    Guid? ControllerSessionId,
    Guid? MissionId,
    string? MissionName,
    string? ExplicitRole)
{
    /// <summary>The workflow run this session is seated on, or null; with <see cref="WorkflowId"/> and
    /// <see cref="WorkflowVersion"/>, the values the pre-launch seat stamp puts on the session.</summary>
    public Guid? WorkflowRunId { get; init; }
    public string? WorkflowId { get; init; }
    public int? WorkflowVersion { get; init; }

    /// <summary>The factory seat, its definition version and its kit kind, trimmed, as the pre-launch stamp settles them.</summary>
    public string? FactorySeat { get; init; }
    public int? FactoryDefinitionVersion { get; init; }
    public string? FactoryKit { get; init; }
    public string? FactoryStartOverride { get; init; }
    public string? LaunchModel { get; init; }

    /// <summary>The connection mode's word ("terminal" or "api"), as the launched row reports it.</summary>
    public string? ConnectionMode { get; init; }
}

/// <summary>
/// A session the Director has ACCEPTED and is still STARTING: it has its id and its name, and the work that makes it a
/// running session - the seat's kit, a pooled worktree from cc-worktrees, the launch - is still in progress on its own
/// task. It is reported to the Gateway as a session row in the Starting state, so a start that takes minutes is visible
/// by its id and its name from the moment it was accepted. When the launch succeeds the launched session replaces it
/// under the same id; when it fails it becomes a failed row carrying the reason, the way a crashed session stays in the
/// roster until a person closes it.
/// </summary>
public sealed class PendingSessionStart
{
    private readonly object _gate = new();

    internal PendingSessionStart(Guid id, string? name, string repoPath, AgentKind agent, DateTime acceptedAtUtc, PendingSessionFacts facts)
    {
        Id = id;
        Name = name;
        RepoPath = repoPath;
        Agent = agent;
        AcceptedAtUtc = acceptedAtUtc;
        Facts = facts;
    }

    public Guid Id { get; }

    /// <summary>The name the session is born with; the launched session carries the same one.</summary>
    public string? Name { get; }

    /// <summary>The folder the create asked for. A pooled seat's slot is not known yet; the launched row names it.</summary>
    public string RepoPath { get; }

    public AgentKind Agent { get; }

    public DateTime AcceptedAtUtc { get; }

    public PendingSessionFacts Facts { get; }

    /// <summary>When the start failed (UTC), or null while it is still starting.</summary>
    public DateTime? FailedAtUtc
    {
        get { lock (_gate) return _failedAtUtc; }
    }

    /// <summary>Why the start failed, in the words of whatever refused it (cc-worktrees' own sentence for a pool
    /// refusal); null while it is still starting.</summary>
    public string? FailureReason
    {
        get { lock (_gate) return _failureReason; }
    }

    private DateTime? _failedAtUtc;
    private string? _failureReason;

    internal void MarkFailed(string reason, DateTime nowUtc)
    {
        lock (_gate)
        {
            _failedAtUtc = nowUtc;
            _failureReason = string.IsNullOrWhiteSpace(reason) ? "the session could not be started" : reason.Trim();
        }
    }
}

/// <summary>
/// One create, between its ACCEPT (<see cref="SessionManager.AcceptSessionStart"/>) and its START
/// (<see cref="SessionManager.StartAcceptedSessionAsync"/>). It carries every argument of the create, the id and name
/// minted at the accept, whether the start must take a slot from cc-worktrees (the step that took 106 to 430 seconds on
/// 5 and 6 November 2026), and the creation ticket that counts it against a creation hold until the start ends.
/// </summary>
public sealed class SessionStart
{
    private IDisposable? _ticket;

    internal SessionStart(Guid id, string? birthName, IDisposable ticket)
    {
        Id = id;
        BirthName = birthName;
        _ticket = ticket;
    }

    public Guid Id { get; }

    public string? BirthName { get; }

    /// <summary>True when the start must ask cc-worktrees for a slot. That call can take minutes, so a create that
    /// makes it is answered when its start reaches that call (<see cref="ReachedThePool"/>) and reports how the start
    /// ended on its session row.</summary>
    public bool TakesPooledSlot { get; internal set; }

    private readonly TaskCompletionSource _reachedThePool = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the start has passed every quick check - the seat's kit is in place, its agent and its secrets
    /// accepted - and is about to ask cc-worktrees for a slot. Everything a create refuses before this point is answered
    /// to its caller as the refusal (a seat refusal stays a Conflict); after it, the start's outcome is reported on its
    /// session row. Never completes for a start that takes no slot.
    /// </summary>
    public Task ReachedThePool => _reachedThePool.Task;

    internal void MarkReachedThePool()
    {
        if (_reachedThePool.TrySetResult())
            FileLog.Write($"[SessionStart] MarkReachedThePool: session={Id}; its outcome is reported on its row from here on");
    }

    /// <summary>The row reported while the session starts, or null for a create whose caller gets the outcome itself
    /// (the desktop's own New Session, which waits for the launch).</summary>
    public PendingSessionStart? Pending { get; internal set; }

    internal string RepoPath { get; init; } = "";
    internal IAgent Agent { get; init; } = null!;
    internal string? UserArgs { get; init; }
    internal SessionBackendType BackendType { get; init; }
    internal string? ResumeSessionId { get; init; }
    internal Guid? GroupId { get; init; }
    internal string? GroupRole { get; init; }
    internal string? GroupName { get; init; }
    internal Guid? ControllerSessionId { get; init; }
    internal Action<Session>? BeforeLaunch { get; init; }
    internal Git.PooledWorktree? ReattachPooledWorktree { get; init; }
    internal FactoryKitRequest? FactoryKit { get; init; }
    internal bool ForcePooledWorktree { get; init; }
    internal SessionConnectionMode ConnectionMode { get; init; }

    /// <summary>The pool size cc-worktrees is asked for, settled at the accept.</summary>
    internal int PoolSize { get; set; }

    /// <summary>Whether the repository's own pooled-worktree setting is on (false for a seat that takes a slot
    /// whatever the setting says), for the words of a refusal.</summary>
    internal bool PoolSettingEnabled { get; set; }

    /// <summary>Give the creation ticket back. Once only; a second call does nothing.</summary>
    internal void ReleaseTicket()
    {
        var ticket = Interlocked.Exchange(ref _ticket, null);
        if (ticket is null) return;
        ticket.Dispose();
        FileLog.Write($"[SessionStart] ReleaseTicket: session={Id}");
    }
}
