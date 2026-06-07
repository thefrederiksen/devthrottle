using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Session types (issue #211): Type is identity chosen once at creation; each type earns
/// its place with a BEHAVIORALLY different playbook; everything pre-#211 deserializes to
/// Implement so old sessions and old clients keep today's behavior exactly.
/// </summary>
public class SessionTypeTests
{
    // ===== Playbooks =====

    [Fact]
    public void Playbook_Implement_IsNull_TodaysBehavior()
        => Assert.Null(SessionTypePlaybooks.For(SessionType.Implement));

    [Fact]
    public void Playbook_Discuss_ForbidsEditsAndOffersHandoff()
    {
        var p = SessionTypePlaybooks.For(SessionType.Discuss);
        Assert.NotNull(p);
        Assert.Contains("Do NOT modify files", p);
        Assert.Contains("Implement session", p); // work lands elsewhere, not here
    }

    [Fact]
    public void Playbook_BugReport_NeverFixes_FilesCompleteIssue_NeedsDesignEscape()
    {
        var p = SessionTypePlaybooks.For(SessionType.BugReport);
        Assert.NotNull(p);
        Assert.Contains("NEVER fix the bug", p);
        Assert.Contains("file:line", p);            // evidence requirement
        Assert.Contains("needs-design", p);          // the honest escape hatch
        Assert.Contains("Implement session", p);     // fixing happens later, elsewhere
    }

    // ===== Seed composition (playbook + caller PrePrompt -> ONE dispatch) =====

    [Fact]
    public void ComposeSeed_ImplementNoPrePrompt_Null()
        => Assert.Null(SessionTypePlaybooks.ComposeSeed(SessionType.Implement, null));

    [Fact]
    public void ComposeSeed_ImplementWithPrePrompt_PassesThroughUnchanged()
        => Assert.Equal("fix the thing", SessionTypePlaybooks.ComposeSeed(SessionType.Implement, "fix the thing"));

    [Fact]
    public void ComposeSeed_TypedNoPrePrompt_PlaybookOnly()
        => Assert.Equal(SessionTypePlaybooks.For(SessionType.Discuss),
            SessionTypePlaybooks.ComposeSeed(SessionType.Discuss, "  "));

    [Fact]
    public void ComposeSeed_TypedWithPrePrompt_PlaybookFirst_ThenTask()
    {
        // The #236 flow: a BugReport session seeded with the bug description must read
        // its ground rules BEFORE the task.
        var seed = SessionTypePlaybooks.ComposeSeed(SessionType.BugReport, "The rail shows zombie rows after close.");
        Assert.NotNull(seed);
        Assert.StartsWith("This is a BUG-REPORT session.", seed);
        Assert.EndsWith("The rail shows zombie rows after close.", seed);
        Assert.Contains("\n\n", seed);
    }

    // ===== Persistence =====

    [Fact]
    public void PersistedSession_TypeRoundTrips_AsString()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);
            store.Save(new[]
            {
                new PersistedSession
                {
                    Id = Guid.NewGuid(),
                    RepoPath = @"C:\test\repo",
                    WorkingDirectory = @"C:\test\repo",
                    SessionType = SessionType.BugReport,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            });

            // Serialized as the enum NAME (readable, forward-compatible), not a number.
            Assert.Contains("BugReport", File.ReadAllText(tempFile));

            var result = store.Load();
            Assert.True(result.Success);
            Assert.Equal(SessionType.BugReport, result.Sessions[0].SessionType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PersistedSession_PreTypeJson_DeserializesToImplement()
    {
        // A sessions.json written before #211 has no sessionType field at all.
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempFile, """
                [{ "id": "11111111-2222-3333-4444-555555555555",
                   "repoPath": "C:\\test\\repo",
                   "workingDirectory": "C:\\test\\repo",
                   "activityState": "Idle",
                   "createdAt": "2026-06-01T00:00:00+00:00" }]
                """);
            var store = new SessionStateStore(tempFile);

            var result = store.Load();

            Assert.True(result.Success);
            Assert.Equal(SessionType.Implement, result.Sessions[0].SessionType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
