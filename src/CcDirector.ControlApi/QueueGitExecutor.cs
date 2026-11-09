using CcDirector.Core.Backends;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (Worker W2): the QUEUE and GIT WRITE area of the tunnel command surface. It
/// owns the voice-queue group (read, add, update, remove, move-up, move-down, clear, send), the git working-tree
/// writes (stage, unstage, discard, commit), and create-from-github. Each core below reproduces its old REST
/// lambda's guards and effect verbatim, so the REST route and the tunnel verb share ONE core and cannot drift;
/// the REST route re-points to <see cref="SessionCommandExecutor.DispatchAsync"/> and maps the typed result back
/// to the same HTTP status codes the lambda returned. Adding a verb touches only this file.
/// </summary>
internal sealed class QueueGitExecutor : ISessionCommandArea
{
    /// <summary>
    /// The git write actions are stateless (each shells <c>git</c> in the given repo), so one shared instance
    /// is safe - exactly as the REST layer kept a single <c>gitWrite</c> instance for its four routes.
    /// </summary>
    private static readonly GitWriteService GitWrite = new();

    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        // Voice queue: the read plus the seven mutations.
        "queue-read", "queue-add", "queue-update", "queue-remove",
        "queue-move-up", "queue-move-down", "queue-clear", "queue-send",
        // Git working-tree writes (mirror the desktop Source Control view).
        "git-stage", "git-unstage", "git-discard", "git-commit",
        // Director-level create (no target session): a GitHub Actions remote session.
        "create-from-github",
    };

    /// <summary>
    /// The queue writes, ordered because A PERSON'S REORDERING IS A SEQUENCE (issues #4490, #4815).
    ///
    /// This list used to say something else: that <c>Session.PromptQueue</c> was a plain <c>List</c> with no
    /// lock, so two of these at once could tear it, and that the delivery loop was holding them apart until
    /// the queue itself was safe. The queue now takes a lock on every read and every write and hands out a
    /// snapshot (#4815), so tearing is no longer the reason and the loop is no longer standing in for a lock.
    ///
    /// They stay ordered on their own merits, and the merits are not the ones the first version of this
    /// comment gave. It claimed two move-ups applied at once collapse into one step; they do not - each
    /// finds its item by ID under the lock, so two of them are two steps. The review of #4815 caught that.
    ///
    /// WHAT IS ACTUALLY TRUE OF THEM. A person's reordering is a SEQUENCE: move-down A then move-up C on
    /// [A,B,C] gives [B,C,A], and the same two the other way round give [C,A,B]. Add-then-clear and
    /// clear-then-add leave different queues. A read that overtakes the write before it shows the person
    /// the queue they just changed, unchanged. The lock makes each verb safe; it cannot make a PAIR of them
    /// mean what the person meant, and only arrival order can.
    ///
    /// AND queue-send IS NOT IN THIS LIST, which took two reviews to get right. The #4815 review was correct
    /// that it TYPES - it calls <c>Session.SendTextAsync</c>, which types the queued prompt into the
    /// terminal - and at the time that meant keeping it on the delivery loop. The #4816 review then showed
    /// why a typing verb that WAITS does not belong on a session's chain: <c>SendTextAsync</c> is a guarded
    /// send, and while a guarded section is open <c>Session.SendInput</c> already holds an arriving
    /// keystroke, answers it <c>Held</c> at once and writes it after the send in the order it was pressed.
    /// The session layer orders it better than a queue can, because a queue can only be silent until its
    /// turn. Its claim on the item is atomic either way (<c>PromptQueue.TryTake</c>, #4815), so two sends
    /// cannot hand the same prompt out twice however they interleave.
    /// </summary>
    public IReadOnlyCollection<string> VerbsThatMustArriveInOrder { get; } = new[]
    {
        "queue-read", "queue-add", "queue-update", "queue-remove",
        "queue-move-up", "queue-move-down", "queue-clear",
    };

    public async Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var sessionManager = context.SessionManager;
        return command.Verb switch
        {
            "queue-read" => QueueRead(sessionManager, command),
            "queue-add" => QueueAdd(sessionManager, command),
            "queue-update" => QueueUpdate(sessionManager, command),
            "queue-remove" => QueueRemove(sessionManager, command),
            "queue-move-up" => QueueMoveUp(sessionManager, command),
            "queue-move-down" => QueueMoveDown(sessionManager, command),
            "queue-clear" => QueueClear(sessionManager, command),
            "queue-send" => await QueueSendAsync(sessionManager, command),
            "git-stage" => await GitStageAsync(sessionManager, command),
            "git-unstage" => await GitUnstageAsync(sessionManager, command),
            "git-discard" => await GitDiscardAsync(sessionManager, command),
            "git-commit" => await GitCommitAsync(sessionManager, command),
            "create-from-github" => CreateFromGitHub(sessionManager, context.DirectorId, command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the queue/git area"),
        };
    }

    // ===================== Voice queue =====================

    /// <summary>Project a session's prompt queue to the wire shape the Cockpit renders (<c>{ items = [...] }</c>),
    /// identical to the old <c>ControlEndpoints.ProjectQueue</c> so the response body is byte-for-byte the same.</summary>
    private static string SerializeQueue(Session session) =>
        SessionCommandExecutor.Serialize(new
        {
            items = session.PromptQueue.Items
                .Select(i => (object)new { id = i.Id.ToString(), text = i.Text, createdAt = i.CreatedAt })
                .ToList(),
        });

    /// <summary>
    /// The <c>queue-read</c> verb: return the session's prompt queue. Mirrors the Director's
    /// <c>GET /sessions/{sid}/queue</c> lambda - invalid id -&gt; BadRequest, missing session -&gt; NotFound.
    /// </summary>
    internal static DirectorCommandResult QueueRead(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    /// <summary>
    /// The <c>queue-add</c> verb: enqueue prompt text. Mirrors the Director's <c>POST /sessions/{sid}/queue</c>
    /// lambda - invalid id -&gt; BadRequest, blank text -&gt; BadRequest, missing session -&gt; NotFound - in
    /// the same order, and returns the resulting queue.
    /// </summary>
    internal static DirectorCommandResult QueueAdd(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<QueueItemCommand>(command.PayloadJson);
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "text is required");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.PromptQueue.Enqueue(request.Text);
        FileLog.Write($"[QueueGitExecutor] queue-add: session={guid} len={request.Text.Length}");
        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    /// <summary>
    /// The <c>queue-update</c> verb: edit the text of a queued item in place. Mirrors the Director's
    /// <c>PATCH /sessions/{sid}/queue/{itemId}</c> lambda - invalid session/item id -&gt; BadRequest, blank text
    /// -&gt; BadRequest, missing session -&gt; NotFound - and returns the resulting queue.
    /// </summary>
    internal static DirectorCommandResult QueueUpdate(SessionManager sessionManager, DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<QueueItemCommand>(command.PayloadJson);
        if (!Guid.TryParse(command.SessionId, out var guid) || !Guid.TryParse(request?.ItemId, out var itemGuid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid id format");

        if (string.IsNullOrWhiteSpace(request!.Text))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "text is required");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.PromptQueue.UpdateText(itemGuid, request.Text);
        FileLog.Write($"[QueueGitExecutor] queue-update: session={guid} item={itemGuid}");
        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    /// <summary>
    /// The <c>queue-remove</c> verb: drop a queued item. Mirrors the Director's
    /// <c>DELETE /sessions/{sid}/queue/{itemId}</c> lambda - invalid session/item id -&gt; BadRequest, missing
    /// session -&gt; NotFound - and returns the resulting queue.
    /// </summary>
    internal static DirectorCommandResult QueueRemove(SessionManager sessionManager, DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<QueueItemCommand>(command.PayloadJson);
        if (!Guid.TryParse(command.SessionId, out var guid) || !Guid.TryParse(request?.ItemId, out var itemGuid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.PromptQueue.Remove(itemGuid);
        FileLog.Write($"[QueueGitExecutor] queue-remove: session={guid} item={itemGuid}");
        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    /// <summary>
    /// The <c>queue-move-up</c> verb: move a queued item one place earlier. Mirrors the Director's
    /// <c>POST /sessions/{sid}/queue/{itemId}/move-up</c> lambda - invalid session/item id -&gt; BadRequest,
    /// missing session -&gt; NotFound - and returns the resulting queue.
    /// </summary>
    internal static DirectorCommandResult QueueMoveUp(SessionManager sessionManager, DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<QueueItemCommand>(command.PayloadJson);
        if (!Guid.TryParse(command.SessionId, out var guid) || !Guid.TryParse(request?.ItemId, out var itemGuid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.PromptQueue.MoveUp(itemGuid);
        FileLog.Write($"[QueueGitExecutor] queue-move-up: session={guid} item={itemGuid}");
        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    /// <summary>
    /// The <c>queue-move-down</c> verb: move a queued item one place later. Mirrors the Director's
    /// <c>POST /sessions/{sid}/queue/{itemId}/move-down</c> lambda - invalid session/item id -&gt; BadRequest,
    /// missing session -&gt; NotFound - and returns the resulting queue.
    /// </summary>
    internal static DirectorCommandResult QueueMoveDown(SessionManager sessionManager, DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<QueueItemCommand>(command.PayloadJson);
        if (!Guid.TryParse(command.SessionId, out var guid) || !Guid.TryParse(request?.ItemId, out var itemGuid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.PromptQueue.MoveDown(itemGuid);
        FileLog.Write($"[QueueGitExecutor] queue-move-down: session={guid} item={itemGuid}");
        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    /// <summary>
    /// The <c>queue-clear</c> verb: empty the whole queue. Mirrors the Director's
    /// <c>DELETE /sessions/{sid}/queue</c> lambda - invalid id -&gt; BadRequest, missing session -&gt; NotFound -
    /// and returns the (now empty) queue.
    /// </summary>
    internal static DirectorCommandResult QueueClear(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.PromptQueue.Clear();
        FileLog.Write($"[QueueGitExecutor] queue-clear: session={guid}");
        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    /// <summary>
    /// The <c>queue-send</c> verb: deliver one queued item to the PTY now and drop it from the queue. Mirrors
    /// the Director's <c>POST /sessions/{sid}/queue/{itemId}/send</c> lambda - invalid session/item id -&gt;
    /// BadRequest, missing session -&gt; NotFound, an Exited/Failed session -&gt; Conflict (the REST layer returns
    /// an empty 409, so no body is carried here), a missing queue item -&gt; NotFound - and returns the resulting
    /// queue. The drain is an explicit ordered mechanism (issue #1181, Task 3b lists it exempt from the dictation
    /// lock), so it sends as <see cref="SendSource.Framework"/> exactly as the REST lambda did.
    /// </summary>
    internal static async Task<DirectorCommandResult> QueueSendAsync(SessionManager sessionManager, DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<QueueItemCommand>(command.PayloadJson);
        if (!Guid.TryParse(command.SessionId, out var guid) || !Guid.TryParse(request?.ItemId, out var itemGuid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "session has exited");

        // TAKING THE ITEM IS THE CLAIM ON IT (found reviewing #4815). This used to find the item, read its
        // text, call Remove and discard the answer, then send - so two callers who both found it both acted,
        // and the prompt reached the agent twice. Only the caller TryTake answers true to sends.
        if (!session.PromptQueue.TryTake(itemGuid, out var text))
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "queue item not found");

        FileLog.Write($"[QueueGitExecutor] queue-send: session={guid} item={itemGuid}");
        var outcome = await session.SendTextAsync(text, SubmissionProvenance.FrameworkText(SubmissionRoutes.QueueDrain), SendSource.Framework);
        // A send that is still delivering (the agent took the Enter but has not recorded the prompt yet) is not a failure:
        // the item is sent, and answering a failure here would invite the drain to be retried and the words typed twice.
        FileLog.Write($"[QueueGitExecutor] queue-send done: session={guid} item={itemGuid}, " +
                      (outcome.Confirmed ? "delivered" : $"still delivering - {outcome.Reason}"));
        return DirectorCommandResult.Success(SerializeQueue(session));
    }

    // ===================== Git working-tree writes =====================

    /// <summary>
    /// Shared git-write core (past the id/session guards): resolve the session's repo, run the git operation,
    /// and return the SAME two body shapes the old <c>RunGitWrite</c> local function produced -
    /// <c>{ accepted = true, output }</c> on a zero exit, <c>{ accepted = false, error, exitCode }</c> on a
    /// non-zero exit. Both ride back as a successful command (the verb DID run); the REST layer inspects the
    /// <c>accepted</c> flag and maps a non-zero exit to HTTP 409, exactly as the lambda did. This is the same
    /// "the command ran; its outcome is in the body" shape the execute-action exemplar uses. Invalid id -&gt;
    /// BadRequest, missing session -&gt; NotFound.
    /// </summary>
    private static async Task<DirectorCommandResult> RunGitWrite(SessionManager sessionManager, string sessionId, Func<string, Task<GitWriteResult>> op)
    {
        if (!Guid.TryParse(sessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var r = await op(session.RepoPath);
        return r.Success
            ? DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { accepted = true, output = r.Output }))
            : DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { accepted = false, error = r.Error, exitCode = r.ExitCode }));
    }

    /// <summary>The <c>git-stage</c> verb: <c>git add</c> the given paths (or everything when none given). Mirrors
    /// the Director's <c>POST /sessions/{sid}/git/stage</c> lambda.</summary>
    internal static Task<DirectorCommandResult> GitStageAsync(SessionManager sessionManager, DirectorCommand command)
    {
        var req = SessionCommandExecutor.Deserialize<GitPathsRequest>(command.PayloadJson);
        return RunGitWrite(sessionManager, command.SessionId, repo => GitWrite.StageAsync(repo, req?.Paths ?? new()));
    }

    /// <summary>The <c>git-unstage</c> verb: <c>git reset HEAD</c> the given paths (or everything). Mirrors the
    /// Director's <c>POST /sessions/{sid}/git/unstage</c> lambda.</summary>
    internal static Task<DirectorCommandResult> GitUnstageAsync(SessionManager sessionManager, DirectorCommand command)
    {
        var req = SessionCommandExecutor.Deserialize<GitPathsRequest>(command.PayloadJson);
        return RunGitWrite(sessionManager, command.SessionId, repo => GitWrite.UnstageAsync(repo, req?.Paths ?? new()));
    }

    /// <summary>The <c>git-discard</c> verb: <c>git checkout --</c> the given paths (a non-zero exit, e.g. no
    /// paths, rides back as accepted=false / 409). Mirrors the Director's <c>POST /sessions/{sid}/git/discard</c>
    /// lambda.</summary>
    internal static Task<DirectorCommandResult> GitDiscardAsync(SessionManager sessionManager, DirectorCommand command)
    {
        var req = SessionCommandExecutor.Deserialize<GitPathsRequest>(command.PayloadJson);
        return RunGitWrite(sessionManager, command.SessionId, repo => GitWrite.DiscardAsync(repo, req?.Paths ?? new()));
    }

    /// <summary>The <c>git-commit</c> verb: <c>git commit -m</c> the given message (a blank message rides back as
    /// accepted=false / 409). Mirrors the Director's <c>POST /sessions/{sid}/git/commit</c> lambda.</summary>
    internal static Task<DirectorCommandResult> GitCommitAsync(SessionManager sessionManager, DirectorCommand command)
    {
        var req = SessionCommandExecutor.Deserialize<GitCommitRequest>(command.PayloadJson);
        return RunGitWrite(sessionManager, command.SessionId, repo => GitWrite.CommitAsync(repo, req?.Message ?? ""));
    }

    // ===================== Director-level create =====================

    /// <summary>
    /// The <c>create-from-github</c> verb (director-level: no target session id): create a GitHub Actions remote
    /// session. Mirrors the Director's <c>POST /sessions/github</c> lambda exactly - owner/repo required,
    /// initialPrompt required, an unknown triggerMode, a missing/non-positive threadNumber for ExistingThread,
    /// and a missing workflowFile for WorkflowDispatch all -&gt; BadRequest; a creation fault -&gt; Error (which
    /// the REST layer maps to 500). On success it returns the new session mapped through the plain
    /// <see cref="ControlEndpoints.Map"/> (the REST endpoint re-maps it with its identity-stamped mapper for the
    /// local 201 response, so that response stays byte-identical), exactly as the <c>create</c> verb does.
    /// </summary>
    internal static DirectorCommandResult CreateFromGitHub(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        var req = SessionCommandExecutor.Deserialize<GitHubSessionRequest>(command.PayloadJson);

        if (req is null || string.IsNullOrWhiteSpace(req.Owner) || string.IsNullOrWhiteSpace(req.Repo))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "owner and repo are required");
        if (string.IsNullOrWhiteSpace(req.InitialPrompt))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "initialPrompt is required");
        if (!Enum.TryParse<RemoteTriggerMode>(req.TriggerMode, ignoreCase: true, out var mode))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown triggerMode: {req.TriggerMode}. Valid: NewIssue, ExistingThread, WorkflowDispatch");
        if (mode == RemoteTriggerMode.ExistingThread && (req.ThreadNumber is null || req.ThreadNumber <= 0))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "threadNumber is required (and must be positive) for ExistingThread mode");
        if (mode == RemoteTriggerMode.WorkflowDispatch && string.IsNullOrWhiteSpace(req.WorkflowFile))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "workflowFile is required for WorkflowDispatch mode");

        var config = new RemoteSessionConfig
        {
            Owner = req.Owner.Trim(),
            Repo = req.Repo.Trim(),
            BaseBranch = string.IsNullOrWhiteSpace(req.BaseBranch) ? "main" : req.BaseBranch.Trim(),
            TriggerMode = mode,
            InitialPrompt = req.InitialPrompt.Trim(),
            ThreadNumber = req.ThreadNumber,
            IssueTitle = req.IssueTitle,
            WorkflowFile = req.WorkflowFile,
        };

        try
        {
            var session = sessionManager.CreateGitHubActionsSession(config);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(ControlEndpoints.Map(session, directorId)));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[QueueGitExecutor] create-from-github FAILED: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Error, ex.Message);
        }
    }
}
