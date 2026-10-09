using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// Who may read which block of the Mentor's page (devthrottle_internal#2305): the issue's tests 1, 3 and 5, over a real
/// migrated database, a week written by the real writer with a FAKE model, the real team gate and the real handler,
/// rendered exactly as a client receives it.
/// </summary>
public sealed class TeamMentorEndpointsTests : IDisposable
{
    private const string Route = "/teams/{teamId}/mentor";

    private readonly MentorRig _rig = new();
    private readonly TeamAccess _access;
    private readonly TeamEndpointGate _gate;
    private readonly string _teamId;
    private PromptOfWeek _robQuoted = null!;
    private PromptOfWeek _robNotQuoted = null!;
    private PromptOfWeek _danaPrompt = null!;

    private sealed record PromptOfWeek(string Id, string Text);

    public TeamMentorEndpointsTests()
    {
        _access = new TeamAccess(_rig.Teams);
        _gate = new TeamEndpointGate(_access, _rig.Teams, _rig.Tenants,
            new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry()));
        _teamId = _rig.Team.Value;
    }

    public void Dispose() => _rig.Dispose();

    /// <summary>Rob had a hard week and one of his two prompts is quoted; Dana had a good week and nothing is quoted.</summary>
    private async Task WriteTheWeekAsync()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(2), "s-dana");
        var q = _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 9), "fix the signup thing so it doesnt break on mobile");
        var n = _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 11), "rob's prompt nobody quotes");
        var d = _rig.PromptOf(MentorRig.Dana, MentorRig.InWeek(2), "dana's own prompt");
        _robQuoted = new PromptOfWeek(q.PromptId!, q.Text);
        _robNotQuoted = new PromptOfWeek(n.PromptId!, n.Text);
        _danaPrompt = new PromptOfWeek(d.PromptId!, d.Text);
        _rig.Brain.Answer = prompt => prompt.Contains("rob's prompt nobody quotes", StringComparison.Ordinal)
            ? FakeBrain.HardWeek("P1")
            : FakeBrain.GoodWeek();
        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);
    }

    private IResult Read(string subject, string? week = "2026-W40", bool mentorRunning = true) =>
        TeamMentorEndpoints.Read(_rig.Teams, _access, _rig.Store, _ => "UTC", _rig.Now, subject, _teamId, week, mentorRunning);

    private TeamGateVerdict GateForPage(string subject) =>
        _gate.Check("GET", Route, name => name == "teamId" ? _teamId : null, null, () => subject, _ => TeamOwnership.Unknown);

    // ---- devthrottle_internal#2305, test 1 ---------------------------------------------------------------------------

    [Fact]
    public async Task Issue2305Test1_ADevelopersPage_HoldsOnlyTheirOwnBlock_WordForWordTheManagersCopy()
    {
        await WriteTheWeekAsync();

        Assert.Equal(TeamGateOutcome.Allowed, GateForPage(MentorRig.Rob).Outcome);
        var (robStatus, robPage) = await RenderAsync(Read(MentorRig.Rob));
        var (managerStatus, managerPage) = await RenderAsync(Read(MentorRig.Manager));

        Assert.Equal(200, robStatus);
        Assert.Equal(200, managerStatus);
        Assert.Equal("own", robPage.GetProperty("scope").GetString());
        var robsBlock = Assert.Single(robPage.GetProperty("blocks").EnumerateArray());
        Assert.Equal("rob.keller@example.com", robsBlock.GetProperty("personEmail").GetString());
        Assert.True(robsBlock.GetProperty("isYou").GetBoolean());
        Assert.DoesNotContain("dana", robPage.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var managersCopyOfRob = managerPage.GetProperty("blocks").EnumerateArray()
            .Single(b => b.GetProperty("personEmail").GetString() == "rob.keller@example.com");
        Assert.False(managersCopyOfRob.GetProperty("isYou").GetBoolean());
        // Byte for byte the same, but for the one field that says whose page it is.
        Assert.Equal(WithoutIsYou(managersCopyOfRob), WithoutIsYou(robsBlock));
    }

    [Fact]
    public async Task Read_AnOwnerOrManager_GetsEveryBlockOfTheWeek_InEmailOrder()
    {
        await WriteTheWeekAsync();

        foreach (var reader in new[] { MentorRig.Owner, MentorRig.Manager })
        {
            Assert.Equal(TeamGateOutcome.Allowed, GateForPage(reader).Outcome);
            var (_, page) = await RenderAsync(Read(reader));
            Assert.Equal("everyone", page.GetProperty("scope").GetString());
            Assert.Equal(new[] { "dana@example.com", "rob.keller@example.com" },
                page.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("personEmail").GetString()));
        }
    }

    [Fact]
    public async Task Read_TheAnswer_CarriesTheContractsFields_AsTheGatewayDecidedThem()
    {
        await WriteTheWeekAsync();

        var (_, page) = await RenderAsync(Read(MentorRig.Manager));

        Assert.Equal("2026-W40", page.GetProperty("week").GetString());
        Assert.Equal("2026-09-28", page.GetProperty("weekStart").GetString());
        Assert.Equal("2026-10-04", page.GetProperty("weekEnd").GetString());
        Assert.Equal("UTC", page.GetProperty("timeZone").GetString());
        Assert.True(page.GetProperty("written").GetBoolean());
        Assert.Equal(new[] { "Owner:olivia.owner@example.com", "Manager:priya.nair@example.com" },
            page.GetProperty("readers").EnumerateArray().Select(r => $"{r.GetProperty("role").GetString()}:{r.GetProperty("email").GetString()}"));
        var rob = page.GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("personEmail").GetString() == "rob.keller@example.com");
        Assert.Equal("Developer", rob.GetProperty("role").GetString());
        Assert.False(rob.GetProperty("isYou").GetBoolean());
        // The person's account subject is not given out (review G6), and no block names a person.
        Assert.False(rob.TryGetProperty("personSubject", out _));
        Assert.DoesNotContain("sub-", page.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("hard", rob.GetProperty("tone").GetString());
        Assert.Equal("a hard week", rob.GetProperty("toneLabel").GetString());
        Assert.Equal(JsonValueKind.Null, rob.GetProperty("howItWent").ValueKind);
        var quote = Assert.Single(rob.GetProperty("quotes").EnumerateArray());
        Assert.Equal(_robQuoted.Id, quote.GetProperty("promptId").GetString());
        Assert.Equal(_robQuoted.Text, quote.GetProperty("text").GetString());
        // A real member's block carries no name: the Gateway holds none for an account. Only a made-up showcase member,
        // which has a name on its own row, is given one.
        Assert.Equal(JsonValueKind.Null, rob.GetProperty("personName").ValueKind);
    }

    // ---- devthrottle_internal#2305, test 3 ---------------------------------------------------------------------------

    [Fact]
    public async Task Issue2305Test3_AManagerReadsOnlyTheQuotedPrompts_AndEveryRouteToAPersonsPromptsIsRefused()
    {
        await WriteTheWeekAsync();

        // The page carries the quoted prompt and no other.
        var (_, page) = await RenderAsync(Read(MentorRig.Manager));
        var raw = page.GetRawText();
        Assert.Contains(_robQuoted.Text, raw);
        Assert.DoesNotContain(_robNotQuoted.Text, raw);
        Assert.DoesNotContain(_robNotQuoted.Id, raw);
        Assert.DoesNotContain(_danaPrompt.Text, raw);

        // The prompt log, inside the team's tenant, for any date range, by any method: refused - whether the gate can
        // tell the prompts are someone else's or cannot tell whose they are.
        foreach (var (method, pattern) in new[] { ("GET", "/prompts"), ("GET", "/prompts/export"), ("DELETE", "/prompts") })
        {
            foreach (var whose in new[] { TeamOwnership.SomeoneElses, TeamOwnership.Unknown })
            {
                var verdict = _gate.Check(method, pattern, _ => null, _rig.Team, () => MentorRig.Manager, _ => whose);
                Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
            }
        }

        // And no route anywhere is declared to grant reading another person's prompts: no role holds that cell.
        Assert.All(Enum.GetValues<TeamRole>(), role =>
            Assert.Equal(TeamGrant.No, TeamPermissions.Grant(role, TeamAction.ReadAnotherPersonsPrompts)));
        Assert.DoesNotContain(TeamEndpointRules.All, r => r.Action == TeamAction.ReadPromptsQuotedOnMentorPage);
    }

    // ---- devthrottle_internal#2305, test 5 ---------------------------------------------------------------------------

    [Fact]
    public async Task Issue2305Test5_ACollaboratorCannotReachThePage_TheServerRefusesIt()
    {
        await WriteTheWeekAsync();

        var verdict = GateForPage(MentorRig.Collaborator);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamAction.ReadOwnMentorPage, verdict.Action);

        // The handler refuses too, rather than trusting the gate ran.
        var (status, body) = await RenderAsync(Read(MentorRig.Collaborator));
        Assert.Equal(403, status);
        Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("blocks", out _));
    }

    [Fact]
    public async Task Read_SomeoneNotInTheTeam_IsTold_ThereIsNoSuchTeam()
    {
        await WriteTheWeekAsync();
        _rig.Tenants.MintOrLookupBySubject("sub-stranger", "stranger@example.com");

        Assert.Equal(TeamGateOutcome.NoSuchTeam, GateForPage("sub-stranger").Outcome);
        var (status, body) = await RenderAsync(Read("sub-stranger"));
        Assert.Equal(404, status);
        Assert.Equal(TeamEndpoints.NoSuchTeamRefusal, body.GetProperty("error").GetString());
    }

    // ---- the week -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Read_NoWeekGiven_IsTheMostRecentClosedWeek()
    {
        await WriteTheWeekAsync();

        var (_, page) = await RenderAsync(Read(MentorRig.Manager, week: null));

        Assert.Equal("2026-W40", page.GetProperty("week").GetString());
        Assert.Equal(2, page.GetProperty("blocks").GetArrayLength());
    }

    [Theory]
    [InlineData("2026-40")]
    [InlineData("last week")]
    [InlineData("2027-W53")]
    public async Task Read_AWeekThatIsNotAnIsoWeek_IsABadRequest(string week)
    {
        var (status, body) = await RenderAsync(Read(MentorRig.Manager, week));

        Assert.Equal(400, status);
        Assert.Equal(TeamMentorEndpoints.BadWeekRefusal, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Read_AWeekNotWrittenYet_SaysSo_WithNoBlocks()
    {
        var (status, page) = await RenderAsync(Read(MentorRig.Manager));

        Assert.Equal(200, status);
        Assert.False(page.GetProperty("written").GetBoolean());
        Assert.Equal(0, page.GetProperty("blocks").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("writingNote").ValueKind);
    }

    [Fact]
    public async Task Read_AWeekTheWriterLeftUnfinished_ServesTheBlocksSoFar_WithTheStillWritingLine()
    {
        // Review H1: the model answers for Rob and cannot be reached for Dana, so the writer saves Rob's block and
        // leaves the week unmarked for a later tick.
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(1), "s-dana");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "rob's prompt");
        _rig.PromptOf(MentorRig.Dana, MentorRig.InWeek(1), "dana's prompt");
        _rig.Brain.Answer = prompt => prompt.Contains("dana's prompt", StringComparison.Ordinal)
            ? throw new HttpRequestException("down")
            : FakeBrain.GoodWeek();
        var run = await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);
        Assert.Equal(1, run.Unfinished);

        var (status, page) = await RenderAsync(Read(MentorRig.Manager));

        Assert.Equal(200, status);
        Assert.False(page.GetProperty("written").GetBoolean());
        Assert.Equal(new[] { "rob.keller@example.com" },
            page.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("personEmail").GetString()));
        Assert.Equal(TeamMentorEndpoints.StillWritingNote, page.GetProperty("writingNote").GetString());
    }

    /// <summary>Rob's block is saved; the model cannot be reached for Dana, so the week is left unmarked.</summary>
    private async Task LeaveTheWeekUnfinishedAsync()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(1), "s-dana");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "rob's prompt");
        _rig.PromptOf(MentorRig.Dana, MentorRig.InWeek(1), "dana's prompt");
        _rig.Brain.Answer = prompt => prompt.Contains("dana's prompt", StringComparison.Ordinal)
            ? throw new HttpRequestException("down")
            : FakeBrain.GoodWeek();
        Assert.Equal(1, (await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone)).Unfinished);
    }

    [Fact]
    public async Task Read_AWeekLeftUnfinished_ReadByTheDeveloperWithABlock_GetsTheBlockAndNoStillWritingLine()
    {
        // Review J1: on a person's own page more blocks never follow, and their block is final.
        await LeaveTheWeekUnfinishedAsync();

        var (_, page) = await RenderAsync(Read(MentorRig.Rob));

        Assert.Equal("own", page.GetProperty("scope").GetString());
        Assert.Single(page.GetProperty("blocks").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("writingNote").ValueKind);
    }

    [Fact]
    public async Task Read_AWeekLeftUnfinished_ReadByTheDeveloperStillWaiting_IsNotWrittenYet_WithNoLine()
    {
        await LeaveTheWeekUnfinishedAsync();

        var (_, page) = await RenderAsync(Read(MentorRig.Dana));

        Assert.False(page.GetProperty("written").GetBoolean());
        Assert.Equal(0, page.GetProperty("blocks").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("writingNote").ValueKind);
    }

    [Fact]
    public async Task Read_AWeekLeftUnfinished_OutsideTheSweepsReach_IsServedAsFinished_WithNoLine()
    {
        // Review J2: three weeks on, the sweep no longer goes back to it; nothing will ever finish it.
        await LeaveTheWeekUnfinishedAsync();
        _rig.Now = _rig.Now.AddDays(21);

        var (_, page) = await RenderAsync(Read(MentorRig.Manager));

        Assert.True(page.GetProperty("written").GetBoolean());
        Assert.Single(page.GetProperty("blocks").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("writingNote").ValueKind);
    }

    [Fact]
    public async Task Read_AWeekLeftUnfinished_OnAGatewayWithTheMentorOff_IsServedAsFinished_WithNoLine()
    {
        // Review J2: the owner switched the Mentor off while a week was unfinished.
        await LeaveTheWeekUnfinishedAsync();

        var (_, page) = await RenderAsync(Read(MentorRig.Manager, mentorRunning: false));

        Assert.True(page.GetProperty("written").GetBoolean());
        Assert.Single(page.GetProperty("blocks").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("writingNote").ValueKind);
    }

    [Fact]
    public async Task Read_AWrittenWeek_CarriesNoStillWritingLine()
    {
        await WriteTheWeekAsync();

        var (_, page) = await RenderAsync(Read(MentorRig.Manager));

        Assert.True(page.GetProperty("written").GetBoolean());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("writingNote").ValueKind);
    }

    [Fact]
    public async Task Read_ADeveloperWithNoBlockThatWeek_GetsNone_NotSomeoneElses()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "rob's prompt");
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();
        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        var (_, page) = await RenderAsync(Read(MentorRig.Dana));

        Assert.True(page.GetProperty("written").GetBoolean());
        Assert.Equal(0, page.GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public async Task Read_TheBlockOfSomeoneWhoHasLeftTheTeam_IsNotShown()
    {
        await WriteTheWeekAsync();
        _rig.Teams.RemoveMember(_teamId, MentorRig.Dana);

        var (_, page) = await RenderAsync(Read(MentorRig.Manager));

        Assert.Equal(new[] { "rob.keller@example.com" },
            page.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("personEmail").GetString()));
    }

    [Fact]
    public async Task Read_AManagerReadingTheirOwnBlock_SeesIsYouOnThatBlockOnly()
    {
        _rig.SessionOf(MentorRig.Manager, MentorRig.InWeek(1), "s-manager");
        _rig.PromptOf(MentorRig.Manager, MentorRig.InWeek(1), "manager's prompt");
        await WriteTheWeekAsync();

        var (_, page) = await RenderAsync(Read(MentorRig.Manager));

        var mine = page.GetProperty("blocks").EnumerateArray().Where(b => b.GetProperty("isYou").GetBoolean()).ToList();
        Assert.Equal("priya.nair@example.com", Assert.Single(mine).GetProperty("personEmail").GetString());
    }

    private static string WithoutIsYou(JsonElement block)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(block.GetRawText())!.AsObject();
        Assert.True(node.Remove("isYou"));
        return node.ToJsonString();
    }

    private static async Task<(int Status, JsonElement Body)> RenderAsync(IResult result)
    {
        var provider = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = provider };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }
}
