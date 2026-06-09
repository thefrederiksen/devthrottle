using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Session types (issue #211, redesigned for the CenCon four-role workflow in #254): Type is
/// identity chosen once at creation; each type earns its place with a BEHAVIORALLY different
/// playbook; everything pre-#211 deserializes to Developer so old sessions and old clients keep
/// today's behavior exactly. #254 renamed Implement-&gt;Developer and BugReport-&gt;Product
/// (integer values unchanged) and added Support; legacy names still deserialize.
/// </summary>
public class SessionTypeTests
{
    // ===== Playbooks =====

    [Fact]
    public void Playbook_Developer_IsNull_TodaysBehavior()
        => Assert.Null(SessionTypePlaybooks.For(SessionType.Developer));

    [Fact]
    public void Playbook_Discuss_ForbidsEditsAndOffersHandoff()
    {
        var p = SessionTypePlaybooks.For(SessionType.Discuss);
        Assert.NotNull(p);
        Assert.Contains("Do NOT modify files", p);
        Assert.Contains("Developer session", p); // work lands elsewhere, not here
    }

    [Fact]
    public void Playbook_Product_NeverFixes_FilesCompleteIssue_NeedsDesignEscape()
    {
        var p = SessionTypePlaybooks.For(SessionType.Product);
        Assert.NotNull(p);
        Assert.Contains("NEVER fix the bug", p);
        Assert.Contains("file:line", p);            // evidence requirement
        Assert.Contains("needs-design", p);          // the honest escape hatch
        Assert.Contains("Developer session", p);     // fixing happens later, elsewhere
    }

    [Fact]
    public void Playbook_Product_RetireAndSelfRename_Issue236()
    {
        // The transaction-shaped end (#236, preserved through the #254 rename): after filing,
        // state the issue and rename via the API. The one-click bug-session flow depends on this.
        var p = SessionTypePlaybooks.For(SessionType.Product);
        Assert.NotNull(p);
        Assert.Contains("After the issue is filed your work is COMPLETE", p);
        Assert.Contains("$CC_DIRECTOR_API/sessions/$CC_SESSION_ID", p); // self-rename path
        Assert.Contains("Bug: #", p);               // the rail-friendly name shape
        Assert.Contains("STOP", p);                 // do not drift into fixing
    }

    [Fact]
    public void Playbook_IssueSubmitter_FilesOnly_StandingClerk_Issue225()
    {
        // Legacy type kept for back-compat (#254): hidden from the picker but its playbook
        // still seeds for any session persisted under it.
        var p = SessionTypePlaybooks.For(SessionType.IssueSubmitter);
        Assert.NotNull(p);
        Assert.Contains("NEVER write code", p);
        Assert.Contains("not done after one issue", p); // standing, not one-shot like Product
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

    [Fact]
    public void Playbook_Support_TriageAnswerFile_NeverEdits_Issue254()
    {
        var p = SessionTypePlaybooks.For(SessionType.Support);
        Assert.NotNull(p);
        Assert.Contains("SUPPORT session", p);
        Assert.Contains("NEVER edit code", p);              // triage, never fix
        Assert.Contains("file a GitHub issue", p);          // real bugs become issues
        Assert.Contains("Developer session", p);            // fixing happens elsewhere
    }

    // ===== Group definitions (issue #225, grown to four members in #254) =====

    [Fact]
    public void ProductGroup_HasFourMembers_InFixedOrder()
    {
        var product = SessionGroupDefinition.FindBuiltIn("Product");
        Assert.NotNull(product);
        Assert.Equal(4, product!.Members.Count);
        Assert.Equal(SessionType.Product,   product.Members[0].Type);
        Assert.Equal(SessionType.Developer, product.Members[1].Type);
        Assert.Equal(SessionType.QA,        product.Members[2].Type);
        Assert.Equal(SessionType.Support,   product.Members[3].Type);
        Assert.Equal(" - product",   product.Members[0].NameSuffix);
        Assert.Equal(" - developer", product.Members[1].NameSuffix);
        Assert.Equal(" - qa",        product.Members[2].NameSuffix);
        Assert.Equal(" - support",   product.Members[3].NameSuffix);
    }

    [Fact]
    public void ProductGroup_HasNoIssueSubmitter_DroppedIn254()
    {
        var product = SessionGroupDefinition.FindBuiltIn("Product");
        Assert.NotNull(product);
        Assert.DoesNotContain(product!.Members, m => m.Type == SessionType.IssueSubmitter);
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

    // ===== Seed composition (playbook + caller PrePrompt -> ONE dispatch) =====

    [Fact]
    public void ComposeSeed_DeveloperNoPrePrompt_Null()
        => Assert.Null(SessionTypePlaybooks.ComposeSeed(SessionType.Developer, null));

    [Fact]
    public void ComposeSeed_DeveloperWithPrePrompt_PassesThroughUnchanged()
        => Assert.Equal("fix the thing", SessionTypePlaybooks.ComposeSeed(SessionType.Developer, "fix the thing"));

    [Fact]
    public void ComposeSeed_TypedNoPrePrompt_PlaybookOnly()
        => Assert.Equal(SessionTypePlaybooks.For(SessionType.Discuss),
            SessionTypePlaybooks.ComposeSeed(SessionType.Discuss, "  "));

    [Fact]
    public void ComposeSeed_TypedWithPrePrompt_PlaybookFirst_ThenTask()
    {
        // The #236 flow: a Product session seeded with the bug description must read
        // its ground rules BEFORE the task.
        var seed = SessionTypePlaybooks.ComposeSeed(SessionType.Product, "The rail shows zombie rows after close.");
        Assert.NotNull(seed);
        Assert.StartsWith("This is a PRODUCT session.", seed);
        Assert.EndsWith("The rail shows zombie rows after close.", seed);
        Assert.Contains("\n\n", seed);
    }

    // ===== Name parsing with legacy aliases (issue #254) =====

    [Theory]
    [InlineData("Developer", SessionType.Developer)]
    [InlineData("Product", SessionType.Product)]
    [InlineData("QA", SessionType.QA)]
    [InlineData("Support", SessionType.Support)]
    [InlineData("Discuss", SessionType.Discuss)]
    [InlineData("IssueSubmitter", SessionType.IssueSubmitter)]
    [InlineData("Implement", SessionType.Developer)]   // legacy alias
    [InlineData("BugReport", SessionType.Product)]     // legacy alias
    [InlineData("developer", SessionType.Developer)]   // case-insensitive
    [InlineData("bugreport", SessionType.Product)]
    public void SessionTypeNames_TryParse_AcceptsCanonicalAndLegacy(string input, SessionType expected)
    {
        Assert.True(SessionTypeNames.TryParse(input, out var type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Nonsense")]
    public void SessionTypeNames_TryParse_RejectsUnknown(string? input)
        => Assert.False(SessionTypeNames.TryParse(input, out _));

    // ===== Persistence =====

    [Fact]
    public void PersistedSession_TypeRoundTrips_AsCanonicalName()
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
                    SessionType = SessionType.Product,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            });

            // Serialized as the CANONICAL enum name (#254), not the legacy "BugReport".
            var json = File.ReadAllText(tempFile);
            Assert.Contains("Product", json);
            Assert.DoesNotContain("BugReport", json);

            var result = store.Load();
            Assert.True(result.Success);
            Assert.Equal(SessionType.Product, result.Sessions[0].SessionType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PersistedSession_PreTypeJson_DeserializesToDeveloper()
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
            Assert.Equal(SessionType.Developer, result.Sessions[0].SessionType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("Implement", SessionType.Developer)]   // pre-#254 default sessions
    [InlineData("BugReport", SessionType.Product)]     // pre-#254 bug sessions
    [InlineData("IssueSubmitter", SessionType.IssueSubmitter)] // legacy, still loads
    [InlineData("QA", SessionType.QA)]
    public void PersistedSession_LegacyTypeName_StillDeserializes(string storedName, SessionType expected)
    {
        // AC6: every session persisted under a prior type name still loads after the #254 rename.
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempFile, $$"""
                [{ "id": "11111111-2222-3333-4444-555555555555",
                   "repoPath": "C:\\test\\repo",
                   "workingDirectory": "C:\\test\\repo",
                   "sessionType": "{{storedName}}",
                   "activityState": "Idle",
                   "createdAt": "2026-06-01T00:00:00+00:00" }]
                """);
            var result = new SessionStateStore(tempFile).Load();

            Assert.True(result.Success);
            Assert.Equal(expected, result.Sessions[0].SessionType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
