using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Rules;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Rules;

/// <summary>
/// Session Rules part 2, phase 1: the decision drop (W3) is picked up and forwarded to the Gateway as
/// firings (W5), with retry.
///
/// Built on the pointer drop's design: the directory a decision sits in is named id DOT the session's own
/// drop token, and only a directory carrying the right token is ever forwarded; a timer sweep is the
/// delivery guarantee and the file-system watcher only makes it fast; a drop is deleted only once the
/// Gateway accepted it or called it a duplicate, deleted (and logged) when refused, and LEFT when the post
/// failed, so the next sweep tries again.
///
/// These drive <see cref="HookDecisionForwarder.SweepAsync"/> directly against a fake Gateway, so no
/// assertion waits on a file-system notification or a network.
/// </summary>
public sealed class HookDecisionForwarderTests : IDisposable
{
    private readonly string _pointerDir;
    private readonly SessionManager _sessions;
    private readonly FakeSessionRuleGateway _gateway = new();
    private readonly ManualClock _clock = new();
    private int _sequence;

    public HookDecisionForwarderTests()
    {
        _pointerDir = Path.Combine(Path.GetTempPath(), "ccd-decision-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pointerDir);
        _sessions = new SessionManager(new AgentOptions());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_pointerDir, recursive: true); } catch { /* best effort */ }
    }

    private Session Adopt()
    {
        var folder = Path.Combine(_pointerDir, "repo");
        var session = new Session(
            Guid.NewGuid(),
            repoPath: folder,
            workingDirectory: folder,
            claudeArgs: null,
            backend: new StubSessionBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: "decision-drop-test",
            customColor: null);
        _sessions.AdoptSession(session);
        return session;
    }

    private HookDecisionForwarder Forwarder(Func<ISessionRuleGateway?>? gateway = null)
    {
        var forwarder = new HookDecisionForwarder(_sessions, gateway ?? (() => _gateway), _pointerDir, utcNow: () => _clock.UtcNow)
        {
            SuppressWatcherForTests = true,
        };
        return forwarder;
    }

    private string DirectoryFor(Session session)
        => SessionHookFiles.DecisionDirectoryFor(session.Id, session.PointerDropToken, _pointerDir);

    private static string DecisionBody(string decisionId, Guid sessionId, string subject = "echo RULE-PROBE") => $$"""
        {"version":1,"decisionId":"{{decisionId}}","sessionId":"{{sessionId}}","occurredUtc":"2026-10-18T12:00:00.1234567Z",
         "agent":"claude_code","event":"before_command","agentEvent":"PreToolUse","tool":"shell","agentTool":"Bash",
         "subject":"{{subject}}","machine":"devlinux","repo":"github.com/thefrederiksen/devthrottle","decision":"would_deny",
         "decidingRuleId":"3f2c","overriddenRuleIds":[],"matchedRuleIds":["3f2c"],"evaluationMicroseconds":812}
        """;

    /// <summary>Write a decision exactly as the hook does: into the directory it was handed, one file
    /// per decision, named for the decision.</summary>
    private string Drop(string directory, Guid sessionId, string? body = null)
    {
        Directory.CreateDirectory(directory);
        var decisionId = $"20261018T1200001234567Z-{++_sequence:x8}";
        var path = Path.Combine(directory, decisionId + ".json");
        File.WriteAllText(path, body ?? DecisionBody(decisionId, sessionId));
        return path;
    }

    private string Drop(Session session) => Drop(DirectoryFor(session), session.Id);

    [Fact]
    public async Task SweepAsync_DecisionInTheSessionsDirectory_IsPostedVerbatimAndDeletedWhenAccepted()
    {
        // Arrange
        var session = Adopt();
        var path = Drop(session);
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        var forwarded = await forwarder.SweepAsync();

        // Assert
        Assert.Equal(1, forwarded);
        var posted = Assert.Single(Assert.Single(_gateway.Posts));
        Assert.Equal(Path.GetFileNameWithoutExtension(path), posted.GetProperty("decisionId").GetString());
        Assert.Equal("would_deny", posted.GetProperty("decision").GetString());
        Assert.Equal(812, posted.GetProperty("evaluationMicroseconds").GetInt32());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_GatewayCallsItADuplicate_DeletesIt()
    {
        // Arrange: a retried post the Gateway already had.
        var session = Adopt();
        var path = Drop(session);
        _gateway.PostAnswer = d => HookFiringsPost.Answered(new HookFiringsResponse { Duplicates = d.Count, Refused = Array.Empty<HookFiringRefusal>() });
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_GatewayRefusesIt_DeletesIt()
    {
        // Arrange
        var session = Adopt();
        var path = Drop(session);
        var id = Path.GetFileNameWithoutExtension(path);
        _gateway.PostAnswer = _ => HookFiringsPost.Answered(new HookFiringsResponse
        {
            Refused = new[] { new HookFiringRefusal { DecisionId = id, Reason = "rule_not_found" } },
        });
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert: a refusal will never become an acceptance, so retrying it forever would be noise.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_PostFails_LeavesTheDecisionAndTheNextSweepDeliversIt()
    {
        // Arrange
        var session = Adopt();
        var path = Drop(session);
        _gateway.PostAnswer = _ => HookFiringsPost.Failed("HTTP 503");
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert: still there.
        Assert.True(File.Exists(path));

        // And once the Gateway is back and the wait is over, it goes.
        _gateway.PostAnswer = d => HookFiringsPost.Answered(new HookFiringsResponse { Accepted = d.Count, Refused = Array.Empty<HookFiringRefusal>() });
        _clock.Advance(HookDecisionForwarder.MaxBackoff);
        await forwarder.SweepAsync();
        Assert.False(File.Exists(path));
        Assert.Equal(2, _gateway.Posts.Count);
    }

    [Fact]
    public async Task SweepAsync_AfterAFailedPost_WaitsBeforeTryingAgain()
    {
        // Arrange
        var session = Adopt();
        Drop(session);
        _gateway.PostAnswer = _ => HookFiringsPost.Failed("HTTP 503");
        var forwarder = Forwarder();
        forwarder.Start();
        await forwarder.SweepAsync();

        // Act: the next sweep tick arrives at once.
        await forwarder.SweepAsync();

        // Assert: a Gateway that is down is not asked again on every tick.
        Assert.Single(_gateway.Posts);
    }

    [Fact]
    public async Task SweepAsync_RepeatedFailures_WaitLongerEachTimeUpToTheCeiling()
    {
        // Arrange
        var session = Adopt();
        Drop(session);
        _gateway.PostAnswer = _ => HookFiringsPost.Failed("HTTP 503");
        var forwarder = Forwarder();
        forwarder.Start();
        await forwarder.SweepAsync();                       // fails: wait 2 x interval
        _clock.Advance(HookDecisionForwarder.SweepInterval * 2);
        await forwarder.SweepAsync();                       // fails: wait 4 x interval
        Assert.Equal(2, _gateway.Posts.Count);

        // Act: half the new wait.
        _clock.Advance(HookDecisionForwarder.SweepInterval * 2);
        await forwarder.SweepAsync();

        // Assert
        Assert.Equal(2, _gateway.Posts.Count);
        _clock.Advance(HookDecisionForwarder.SweepInterval * 2);
        await forwarder.SweepAsync();
        Assert.Equal(3, _gateway.Posts.Count);
    }

    [Fact]
    public async Task SweepAsync_DirectoryWithTheWrongToken_IsNeverForwardedAndIsLeftInPlace()
    {
        // Arrange: a sibling that can spell the session's id but was never handed its path.
        var session = Adopt();
        var forged = SessionHookFiles.DecisionDirectoryFor(session.Id, SessionHookFiles.NewDropToken(), _pointerDir);
        var path = Drop(forged, session.Id);
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        var forwarded = await forwarder.SweepAsync();

        // Assert: left as evidence, exactly like a forged pointer drop.
        Assert.Equal(0, forwarded);
        Assert.Empty(_gateway.Posts);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_DirectoryForASessionNotOnTheRoster_IsNeverForwarded()
    {
        // Arrange
        var stranger = Guid.NewGuid();
        var path = Drop(SessionHookFiles.DecisionDirectoryFor(stranger, SessionHookFiles.NewDropToken(), _pointerDir), stranger);
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert
        Assert.Empty(_gateway.Posts);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_TemporaryFileOfAnInProgressWrite_IsNeverForwarded()
    {
        // Arrange
        var session = Adopt();
        var directory = DirectoryFor(session);
        Directory.CreateDirectory(directory);
        var tmp = Path.Combine(directory, "20261018T1200001234567Z-00000001.tmp");
        File.WriteAllText(tmp, DecisionBody("20261018T1200001234567Z-00000001", session.Id));
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert
        Assert.Empty(_gateway.Posts);
        Assert.True(File.Exists(tmp));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"version":1,"sessionId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""[1,2,3]""")]
    public async Task SweepAsync_MalformedDecision_IsDeletedAndNeverPosted(string body)
    {
        // Arrange
        var session = Adopt();
        var path = Drop(DirectoryFor(session), session.Id, body);
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert: it will never become valid, so it is not retried forever.
        Assert.Empty(_gateway.Posts);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_DecisionNamingAnotherSession_IsRefusedAndDeleted()
    {
        // Arrange: the directory is the capability; the body cannot claim a different session.
        var session = Adopt();
        var other = Adopt();
        var path = Drop(DirectoryFor(session), session.Id, DecisionBody("20261018T1200001234567Z-0000abcd", other.Id));
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert
        Assert.Empty(_gateway.Posts);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_SessionRemovedWithDecisionsWaiting_StillForwardsThemAndThenRemovesItsDirectory()
    {
        // Arrange: the session ends between its last command and the next sweep.
        var session = Adopt();
        var forwarder = Forwarder();
        forwarder.Start();
        var path = Drop(session);
        var directory = DirectoryFor(session);
        _sessions.RemoveSession(session.Id);

        // Act
        await forwarder.SweepAsync();

        // Assert: its last decisions are still on the record, and nothing is left behind.
        Assert.Single(_gateway.Posts);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task SweepAsync_SessionRemovedWhileTheGatewayIsDown_KeepsItsDecisionsForTheNextSweep()
    {
        // Arrange
        var session = Adopt();
        var forwarder = Forwarder();
        forwarder.Start();
        var path = Drop(session);
        _sessions.RemoveSession(session.Id);
        _gateway.PostAnswer = _ => HookFiringsPost.Failed("HTTP 503");

        // Act
        await forwarder.SweepAsync();

        // Assert
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_NoGatewayConfigured_LeavesTheDecisions()
    {
        // Arrange
        var session = Adopt();
        var path = Drop(session);
        var forwarder = Forwarder(() => null);
        forwarder.Start();

        // Act
        var forwarded = await forwarder.SweepAsync();

        // Assert
        Assert.Equal(0, forwarded);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SweepAsync_AnswerDoesNotAccountForEveryDecision_KeepsTheUnaccountedOnesForRetry()
    {
        // Arrange: two sent, the Gateway counts one.
        var session = Adopt();
        var first = Drop(session);
        var second = Drop(session);
        _gateway.PostAnswer = _ => HookFiringsPost.Answered(new HookFiringsResponse { Accepted = 1, Refused = Array.Empty<HookFiringRefusal>() });
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        await forwarder.SweepAsync();

        // Assert: a retry is a duplicate at worst (decisionId is unique), a lost decision is gone for good.
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task SweepAsync_MoreDecisionsThanOneBatch_PostsInBatchesAndDeliversThemAll()
    {
        // Arrange
        var session = Adopt();
        for (var i = 0; i < HookDecisionForwarder.MaxBatch + 5; i++)
            Drop(session);
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        var forwarded = await forwarder.SweepAsync();

        // Assert
        Assert.Equal(HookDecisionForwarder.MaxBatch + 5, forwarded);
        Assert.Equal(2, _gateway.Posts.Count);
        Assert.All(_gateway.Posts, batch => Assert.True(batch.Count <= HookDecisionForwarder.MaxBatch));
        Assert.Empty(Directory.EnumerateFiles(DirectoryFor(session)));
    }

    [Fact]
    public async Task SweepAsync_DecisionsFromTwoSessions_AreEachForwarded()
    {
        // Arrange
        var first = Adopt();
        var second = Adopt();
        Drop(first);
        Drop(second);
        var forwarder = Forwarder();
        forwarder.Start();

        // Act
        var forwarded = await forwarder.SweepAsync();

        // Assert
        Assert.Equal(2, forwarded);
        var sessionIds = _gateway.Posts.SelectMany(p => p).Select(d => d.GetProperty("sessionId").GetString()).ToHashSet();
        Assert.Contains(first.Id.ToString(), sessionIds);
        Assert.Contains(second.Id.ToString(), sessionIds);
    }
}
