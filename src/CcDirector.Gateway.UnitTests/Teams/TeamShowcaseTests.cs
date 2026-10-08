using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tests.Api;
using CcDirector.Gateway.Tests.Teams.Mentor;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The administrator showcase (owner, 8 Oct 2026): one team filled with made-up members and their pages, every row under
/// one tag, and emptied again by that tag alone. The proofs the brief asks for: the routes are administrator-only; every
/// row carries the tag; removal removes only tagged rows; a made-up member is not billed, has no account (so no email and
/// no Director), and the Mentor's writer never visits it; and the member list shows its name and email.
/// </summary>
[Collection(AdminServiceTokenCollection.Name)]
public sealed class TeamShowcaseTests : IDisposable
{
    private const string Token = "test-admin-service-token-showcase";
    private const string Tag = "showcase-test";

    private readonly MentorRig _rig = new();
    private readonly string? _priorAdmin = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);

    public TeamShowcaseTests() => Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _priorAdmin);
        _rig.Dispose();
    }

    private TeamShowcase Showcase() => new(_rig.Db, _rig.Teams, _rig.Tenants, _ => "UTC", () => _rig.Now);

    private static TeamShowcaseContent Content() => new(
        "DevThrottle",
        new[]
        {
            new TeamShowcaseMember("peter", "Peter Hansen", "peter@devthrottle.com", "Manager", 62),
            new TeamShowcaseMember("mary", "Mary Olsen", "mary@devthrottle.com", "Developer", 48),
            new TeamShowcaseMember("lisa", "Lisa Jensen", "lisa@devthrottle.com", "Collaborator", 30),
        },
        new TeamShowcaseMentorBlock("owner", "mixed", "41 sessions.", null, "Restarted the menu task.", "fix the menu", "Name the file."),
        new[] { new TeamShowcaseMentorBlock("mary", "good", "The invoice export.", "Well.", null, null, "Run two at once.") },
        new[]
        {
            new TeamShowcaseReport("mary", "Invoice export is ready to try", "Built.", 3, new[] { "owner", "peter" }, Array.Empty<string>(),
                new TeamShowcaseQuestion("cancelled-orders", "Include cancelled orders?", new[]
                {
                    new TeamShowcaseOption("paid-only", "No", true),
                    new TeamShowcaseOption("with-cancelled", "Yes", false),
                })),
        },
        new[]
        {
            new TeamShowcaseRequest("lisa", "Dark mode for the reports", 9,
                new[] { new TeamShowcaseRequestStep("declined", "owner", 8, "Not this month.") }),
        });

    private static HttpContext Authorized()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {Token}";
        return ctx;
    }

    private static int StatusOf(IResult result) =>
        result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    private AdminTeamShowcaseEndpoint.ShowcaseRequest Body(TeamShowcaseContent? content = null) =>
        new(_rig.Team.Value, Tag, "test", "a test", content ?? Content());

    // ---- administrator only ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer not-the-admin-token")]
    [InlineData("Bearer ")]
    public void BothRoutes_WithoutTheAdminToken_AreRefusedAndWriteNothing(string? authorization)
    {
        foreach (var remove in new[] { false, true })
        {
            var ctx = new DefaultHttpContext();
            if (authorization is not null) ctx.Request.Headers.Authorization = authorization;

            var result = AdminTeamShowcaseEndpoint.Handle(ctx, Body(), Showcase(), remove);

            Assert.True(StatusOf(result) is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden,
                $"expected a refusal, got {StatusOf(result)}");
        }
        using var db = _rig.Db.CreateUnscopedContext();
        Assert.False(db.TeamMembers.Any(m => m.ShowcaseTag != null));
    }

    [Fact]
    public void BothRoutes_AreExactMatchPublicOnlyBecauseTheyCarryTheAdminGate()
    {
        // Public in AuthMiddleware (no device key) - the endpoint's own gate is the whole authorization, so the gate must run
        // even when the caller sends a well-formed body.
        var ctx = new DefaultHttpContext();
        Assert.NotEqual(StatusCodes.Status200OK, StatusOf(AdminTeamShowcaseEndpoint.Handle(ctx, Body(), Showcase(), remove: false)));
    }

    // ---- every row carries the tag --------------------------------------------------------------------------------

    [Fact]
    public void Write_PutsTheTagOnEveryRowItWrites()
    {
        var before = Snapshot();

        var result = Showcase().Write(_rig.Team.Value, Tag, Content());

        Assert.Null(result.Refusal);
        var after = Snapshot();
        // Every row that is new after the write carries the tag - in every table the showcase writes.
        foreach (var (table, rows) in after)
        {
            var added = rows.Except(before[table]).ToList();
            Assert.All(added, r => Assert.Equal(Tag, r.Tag));
        }
        Assert.Equal(3, after["team_members"].Count(r => r.Tag == Tag));
        Assert.Equal(2, after["team_mentor_blocks"].Count(r => r.Tag == Tag)); // Mary's, and the Owner's own
        Assert.Equal(1, after["dev_reports"].Count(r => r.Tag == Tag));
        Assert.Equal(1, after["dev_report_versions"].Count(r => r.Tag == Tag));
        Assert.Equal(2, after["dev_report_recipients"].Count(r => r.Tag == Tag));
        Assert.Equal(1, after["team_requests"].Count(r => r.Tag == Tag));
        Assert.Equal(2, after["team_request_changes"].Count(r => r.Tag == Tag)); // sent, then declined
        using var db = _rig.Db.CreateUnscopedContext();
        Assert.Equal("DevThrottle", db.Teams.Single(t => t.Id == _rig.Team.Value).Name);
    }

    [Fact]
    public void Write_TheSameTagTwice_IsRefused()
    {
        Assert.Null(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal);
        Assert.NotNull(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal);
    }

    // ---- removal removes only tagged rows -------------------------------------------------------------------------

    [Fact]
    public void Remove_DeletesEveryTaggedRowAndNoOtherRow()
    {
        var before = Snapshot();
        // Real rows in the same tables, written the ordinary way, that must survive.
        _rig.Store.SaveBlock(_rig.Team, new MentorBlock(MentorRig.Week.ToString(), MentorRig.Rob, MentorTones.Good, "Real work.", null,
            null, Array.Empty<MentorQuote>(), "Keep going.", _rig.Now, "real"));
        var realBefore = Snapshot();
        Assert.Null(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal);
        // A second showcase under another tag must survive too.
        Assert.Null(Showcase().Write(_rig.Team.Value, "other-tag", Content() with { TeamName = null, OwnerPersonal = null }).Refusal);
        var otherTag = Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value.Where(r => r.Tag == "other-tag").ToList());

        var result = Showcase().Remove(_rig.Team.Value, Tag);

        Assert.Null(result.Refusal);
        var after = Snapshot();
        foreach (var (table, rows) in after)
        {
            Assert.DoesNotContain(rows, r => r.Tag == Tag);
            Assert.All(realBefore[table], r => Assert.Contains(r, rows)); // every untagged row is still there
            Assert.All(otherTag[table], r => Assert.Contains(r, rows));   // and every row of another tag
        }
        Assert.NotEmpty(before["team_members"]);
    }

    [Fact]
    public void Remove_WithNoTag_IsRefusedAndDeletesNothing()
    {
        Assert.Null(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal);
        var before = Snapshot();

        Assert.NotNull(Showcase().Remove(_rig.Team.Value, "").Refusal);

        var after = Snapshot();
        foreach (var (table, rows) in before) Assert.Equal(rows.Count, after[table].Count);
    }

    // ---- not billed, no account, no email, no Director, no Mentor run ----------------------------------------------

    [Fact]
    public void MadeUpMembers_HoldNoPaidSeat()
    {
        int Paid() { using var db = _rig.Db.CreateUnscopedContext(); return TeamBillStore.PaidSeats(db, _rig.Team.Value); }
        var before = Paid();

        Assert.Null(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal); // a Manager and a Developer among them

        Assert.Equal(before, Paid());
    }

    [Fact]
    public void MadeUpMembers_HaveNoAccount_SoNoSignInNoDirectorAndNoEmail()
    {
        Assert.Null(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal);

        var showcase = _rig.Teams.MembersOf(_rig.Team.Value).Where(m => m.IsShowcase).ToList();
        Assert.Equal(3, showcase.Count);
        using var db = _rig.Db.CreateUnscopedContext();
        foreach (var m in showcase)
        {
            // No tenant row: nothing can sign in as it, bind a device key to it or enrol a Director under it, and every
            // mail the Gateway sends goes to an account's recorded address - it has none.
            Assert.Null(_rig.Tenants.LookupBySubject(m.AccountSubject));
            Assert.False(db.Tenants.Any(t => t.AccountSubject == m.AccountSubject));
        }
        // And nothing that sends mail was written: no invitation for any of them.
        Assert.False(db.TeamInvitations.Any(i => i.TeamId == _rig.Team.Value));
    }

    [Fact]
    public async Task TheMentorsWriter_NeverVisitsAMadeUpMember()
    {
        Assert.Null(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal);

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        foreach (var m in _rig.Teams.MembersOf(_rig.Team.Value).Where(m => m.IsShowcase))
            Assert.Null(_rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, m.AccountSubject));
    }

    // ---- what the screens show ----------------------------------------------------------------------------------

    [Fact]
    public void TheMemberList_ShowsAMadeUpMembersNameAndEmail()
    {
        Assert.Null(Showcase().Write(_rig.Team.Value, Tag, Content()).Refusal);

        var peter = _rig.Teams.MembersOf(_rig.Team.Value).Single(m => m.Name == "Peter Hansen");

        Assert.Equal("peter@devthrottle.com", peter.Email);
        Assert.Equal(TeamRole.Manager, peter.Role);
        Assert.Equal("Peter Hansen", TeamRegistry.DisplayName(peter));
    }

    [Fact]
    public void ReportHtml_CarriesTheQuestionWithExactlyOneRecommendedOption()
    {
        var html = TeamShowcase.ReportHtml(Content().Reports[0]);

        Assert.Contains("data-dev-report-question=\"cancelled-orders\"", html);
        Assert.Equal(1, html.Split("data-recommended").Length - 1);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private sealed record Row(string Key, string? Tag);

    /// <summary>Every row of every table the showcase writes, by a key and its tag, read without a tenant filter.</summary>
    private Dictionary<string, List<Row>> Snapshot()
    {
        using var db = _rig.Db.CreateUnscopedContext();
        return new Dictionary<string, List<Row>>
        {
            ["team_members"] = db.TeamMembers.AsNoTracking().Select(m => new Row(m.TeamId + "/" + m.AccountSubject, m.ShowcaseTag)).ToList(),
            ["team_mentor_blocks"] = db.TeamMentorBlocks.IgnoreQueryFilters().AsNoTracking().Select(b => new Row(b.TenantId + "/" + b.Week + "/" + b.PersonSubject, b.ShowcaseTag)).ToList(),
            ["dev_reports"] = db.DevReports.IgnoreQueryFilters().AsNoTracking().Select(r => new Row(r.Id.ToString(), r.ShowcaseTag)).ToList(),
            ["dev_report_versions"] = db.DevReportVersions.IgnoreQueryFilters().AsNoTracking().Select(r => new Row(r.Id.ToString(), r.ShowcaseTag)).ToList(),
            ["dev_report_recipients"] = db.DevReportRecipients.IgnoreQueryFilters().AsNoTracking().Select(r => new Row(r.Id.ToString(), r.ShowcaseTag)).ToList(),
            ["team_requests"] = db.TeamRequests.IgnoreQueryFilters().AsNoTracking().Select(r => new Row(r.Id.ToString(), r.ShowcaseTag)).ToList(),
            ["team_request_changes"] = db.TeamRequestChanges.IgnoreQueryFilters().AsNoTracking().Select(r => new Row(r.Id.ToString(), r.ShowcaseTag)).ToList(),
        };
    }
}
