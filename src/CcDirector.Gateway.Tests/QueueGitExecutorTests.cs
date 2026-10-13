using System.Diagnostics;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (Worker W2): parity tests for the voice-queue, git-write, and
/// create-from-github verbs moved onto the shared tunnel dispatch (<see cref="SessionCommandExecutor.DispatchAsync"/>
/// routing into <see cref="QueueGitExecutor"/>). These verbs used to live only as REST lambdas; now the REST path
/// and the Gateway stream down-channel call the SAME core, so this asserts the core's guards and effect directly
/// against a real <see cref="SessionManager"/> holding an embedded buffer-only session (the same harness the W1
/// exemplar uses). Representative coverage per the mission brief: an invalid id -&gt; BadRequest, a missing session
/// -&gt; NotFound, and a real success effect (plus the git non-zero-exit and queue-send Conflict special cases).
/// </summary>
[Collection("DirectorRoot")]
public sealed class QueueGitExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (SessionManager sm, Session session, ExecuteActionTestBackend backend) NewSession(string? repoPath = null)
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(repoPath ?? Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    private static DirectorCommand Command(string verb, string sessionId, object? payload = null) => new()
    {
        CommandId = "cmd-w2",
        Verb = verb,
        SessionId = sessionId,
        PayloadJson = payload is null ? "" : JsonSerializer.Serialize(payload, Json),
    };

    // ---------- queue-add / queue-read ----------

    [Fact]
    public async Task DispatchAsync_QueueAdd_EnqueuesText_AndReturnsItems()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-add", session.Id.ToString(), new QueueItemCommand(Text: "first task")));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("cmd-w2", result.CommandId);
            Assert.Single(session.PromptQueue.Items);
            Assert.Equal("first task", session.PromptQueue.Items[0].Text);
            Assert.Contains("first task", result.BodyJson ?? "");
            Assert.Contains("items", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueAdd_BlankText_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-add", session.Id.ToString(), new QueueItemCommand(Text: "   ")));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Empty(session.PromptQueue.Items);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueAdd_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-add", "not-a-guid", new QueueItemCommand(Text: "x")));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueRead_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-read", Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueRead_ReturnsQueuedItems()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("alpha");
            session.PromptQueue.Enqueue("beta");

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-read", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Contains("alpha", result.BodyJson ?? "");
            Assert.Contains("beta", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    // ---------- queue-update ----------

    [Fact]
    public async Task DispatchAsync_QueueUpdate_EditsItemText()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("before");
            var itemId = session.PromptQueue.Items[0].Id;

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-update", session.Id.ToString(), new QueueItemCommand(ItemId: itemId.ToString(), Text: "after")));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("after", session.PromptQueue.Items[0].Text);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueUpdate_InvalidItemId_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-update", session.Id.ToString(), new QueueItemCommand(ItemId: "not-a-guid", Text: "x")));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueUpdate_BlankText_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("before");
            var itemId = session.PromptQueue.Items[0].Id;

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-update", session.Id.ToString(), new QueueItemCommand(ItemId: itemId.ToString(), Text: "   ")));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal("before", session.PromptQueue.Items[0].Text); // unchanged
        }
        finally { sm.Dispose(); }
    }

    // ---------- queue-remove / queue-clear ----------

    [Fact]
    public async Task DispatchAsync_QueueRemove_DropsItem()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("keep");
            session.PromptQueue.Enqueue("drop");
            var dropId = session.PromptQueue.Items[1].Id;

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-remove", session.Id.ToString(), new QueueItemCommand(ItemId: dropId.ToString())));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Single(session.PromptQueue.Items);
            Assert.Equal("keep", session.PromptQueue.Items[0].Text);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueClear_EmptiesQueue()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("a");
            session.PromptQueue.Enqueue("b");

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-clear", session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Empty(session.PromptQueue.Items);
        }
        finally { sm.Dispose(); }
    }

    // ---------- queue-move-up / queue-move-down ----------

    [Fact]
    public async Task DispatchAsync_QueueMoveUp_ReordersItem()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("one");
            session.PromptQueue.Enqueue("two");
            var secondId = session.PromptQueue.Items[1].Id;

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-move-up", session.Id.ToString(), new QueueItemCommand(ItemId: secondId.ToString())));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("two", session.PromptQueue.Items[0].Text); // moved to the front
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueMoveDown_ReordersItem()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("one");
            session.PromptQueue.Enqueue("two");
            var firstId = session.PromptQueue.Items[0].Id;

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-move-down", session.Id.ToString(), new QueueItemCommand(ItemId: firstId.ToString())));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("two", session.PromptQueue.Items[0].Text); // "one" moved down
        }
        finally { sm.Dispose(); }
    }

    // ---------- queue-send ----------

    [Fact]
    public async Task DispatchAsync_QueueSend_DeliversItemAndDropsIt()
    {
        // The delivery itself is a SendTextAsync through the driver's typing path (asynchronous, exactly as
        // the old lambda did); the deterministic, observable parity effect is that the item is dropped from
        // the queue and the command succeeds. (The prompt exemplar likewise only asserts synchronous bytes
        // for the raw AppendEnter=false path, not the async typing path.)
        var (sm, session, _) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("run me");
            var itemId = session.PromptQueue.Items[0].Id;

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-send", session.Id.ToString(), new QueueItemCommand(ItemId: itemId.ToString())));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Empty(session.PromptQueue.Items); // dropped after send
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueSend_MissingItem_ReturnsNotFound()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-send", session.Id.ToString(), new QueueItemCommand(ItemId: Guid.NewGuid().ToString())));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_QueueSend_ExitedSession_ReturnsConflict()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            session.PromptQueue.Enqueue("too late");
            var itemId = session.PromptQueue.Items[0].Id;
            backend.RaiseProcessExited(1); // drive Session.Status -> Exited via the real backend event
            Assert.True(session.Status is SessionStatus.Exited or SessionStatus.Failed);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("queue-send", session.Id.ToString(), new QueueItemCommand(ItemId: itemId.ToString())));

            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
            Assert.Single(session.PromptQueue.Items); // not dropped - the send was refused
        }
        finally { sm.Dispose(); }
    }

    // ---------- git writes ----------

    [Fact]
    public async Task DispatchAsync_GitDiscard_NoPaths_RidesBackAsNotAccepted()
    {
        // DiscardAsync rejects an empty path list before shelling git; the verb still executes (Ok at the
        // tunnel), with the failure shape { accepted=false, error, exitCode } the REST layer maps to 409.
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("git-discard", session.Id.ToString(), new GitPathsRequest { Paths = new() }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var env = JsonSerializer.Deserialize<GitStageProbe>(result.BodyJson ?? "", Json);
            Assert.NotNull(env);
            Assert.False(env.Accepted);
            Assert.Contains("discard requires at least one path", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_GitCommit_BlankMessage_RidesBackAsNotAccepted()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("git-commit", session.Id.ToString(), new GitCommitRequest { Message = "   " }));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var env = JsonSerializer.Deserialize<GitStageProbe>(result.BodyJson ?? "", Json);
            Assert.NotNull(env);
            Assert.False(env.Accepted);
            Assert.Contains("commit message is required", result.BodyJson ?? "");
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_GitStage_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("git-stage", "not-a-guid", new GitPathsRequest { Paths = new() }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_GitStage_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("git-stage", Guid.NewGuid().ToString(), new GitPathsRequest { Paths = new() }));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    /// <summary>Minimal probe for a git-write body's <c>accepted</c> flag (mirrors the GitWriteEnvelope the REST layer reads).</summary>
    private sealed class GitStageProbe
    {
        public bool Accepted { get; set; }
    }

    // ---------- create-from-github (guards; the success path needs a live GitHub token/network) ----------

    [Fact]
    public async Task DispatchAsync_CreateFromGitHub_MissingOwnerRepo_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("create-from-github", "", new GitHubSessionRequest { Owner = "", Repo = "", InitialPrompt = "do it" }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_CreateFromGitHub_BlankInitialPrompt_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("create-from-github", "", new GitHubSessionRequest { Owner = "o", Repo = "r", InitialPrompt = "  " }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_CreateFromGitHub_UnknownTriggerMode_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("create-from-github", "", new GitHubSessionRequest { Owner = "o", Repo = "r", InitialPrompt = "go", TriggerMode = "Nonsense" }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_CreateFromGitHub_ExistingThreadWithoutNumber_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("create-from-github", "", new GitHubSessionRequest { Owner = "o", Repo = "r", InitialPrompt = "go", TriggerMode = "ExistingThread" }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_CreateFromGitHub_WorkflowDispatchWithoutFile_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                Command("create-from-github", "", new GitHubSessionRequest { Owner = "o", Repo = "r", InitialPrompt = "go", TriggerMode = "WorkflowDispatch" }));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }
}
