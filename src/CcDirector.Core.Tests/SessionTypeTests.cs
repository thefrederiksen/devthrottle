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

    [Fact]
    public void Playbook_IssueSubmitter_FilesOnly_StandingClerk_Issue225()
    {
        var p = SessionTypePlaybooks.For(SessionType.IssueSubmitter);
        Assert.NotNull(p);
        Assert.Contains("NEVER write code", p);
        Assert.Contains("not done after one issue", p); // standing, not one-shot like BugReport
        Assert.Contains("needs-design", p);
    }

    [Fact]
    public void Playbook_QA_VerifyNeverFix_Issue225()
    {
        var p = SessionTypePlaybooks.For(SessionType.QA);
        Assert.NotNull(p);
        Assert.Contains("NEVER fix", p);
        Assert.Contains("verify", p, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clean pass", p); // a pass is a valid QA result
    }

    // ===== Group definitions (issue #225) =====

    [Fact]
    public void ProductGroup_HasThreeMembers_InFixedOrder()
    {
        var product = SessionGroupDefinition.FindBuiltIn("Product");
        Assert.NotNull(product);
        Assert.Equal(3, product!.Members.Count);
        Assert.Equal(SessionType.IssueSubmitter, product.Members[0].Type);
        Assert.Equal(SessionType.Implement, product.Members[1].Type);
        Assert.Equal(SessionType.QA, product.Members[2].Type);
        Assert.Equal(" - submit issues", product.Members[0].NameSuffix);
        Assert.Equal(" - qa", product.Members[2].NameSuffix);
    }

    [Fact]
    public void FindBuiltIn_IsCaseInsensitive_AndNullForUnknown()
    {
        Assert.NotNull(SessionGroupDefinition.FindBuiltIn("product"));
        Assert.Null(SessionGroupDefinition.FindBuiltIn("nope"));
    }

    [Fact]
    public void PersistedSession_GroupFields_RoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var gid = Guid.NewGuid();
            var store = new SessionStateStore(tempFile);
            store.Save(new[]
            {
                new PersistedSession
                {
                    Id = Guid.NewGuid(),
                    RepoPath = @"C:\r",
                    WorkingDirectory = @"C:\r",
                    SessionType = SessionType.QA,
                    GroupId = gid,
                    GroupRole = "QA",
                    GroupName = "Product",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            });

            var s = store.Load().Sessions[0];
            Assert.Equal(gid, s.GroupId);
            Assert.Equal("QA", s.GroupRole);
            Assert.Equal("Product", s.GroupName);
            Assert.Equal(SessionType.QA, s.SessionType);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public void PreGroupJson_DeserializesWithNullGroup()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempFile, """
                [{ "id": "11111111-2222-3333-4444-555555555555",
                   "repoPath": "C:\\r", "workingDirectory": "C:\\r",
                   "activityState": "Idle", "createdAt": "2026-06-01T00:00:00+00:00" }]
                """);
            var s = new SessionStateStore(tempFile).Load().Sessions[0];
            Assert.Null(s.GroupId);
            Assert.Null(s.GroupRole);
            Assert.Null(s.GroupName);
        }
        finally { if (File.Exists(tempFile)) File.Delete(tempFile); }
    }

    [Fact]
    public void Playbook_BugReport_RetireAndSelfRename_Issue236()
    {
        // The transaction-shaped end: after filing, state the issue and rename via the API.
        var p = SessionTypePlaybooks.For(SessionType.BugReport);
        Assert.NotNull(p);
        Assert.Contains("After the issue is filed your work is COMPLETE", p);
        Assert.Contains("$CC_DIRECTOR_API/sessions/$CC_SESSION_ID", p); // self-rename path
        Assert.Contains("Bug: #", p);               // the rail-friendly name shape
        Assert.Contains("STOP", p);                 // do not drift into fixing
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
