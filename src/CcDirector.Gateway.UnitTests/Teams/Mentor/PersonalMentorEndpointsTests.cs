using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Teams.Mentor;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// The Mentor's page for a person's OWN account (owner, 8 Oct 2026): the same tables, keyed by the person's own tenant,
/// served only the block about the person asking - over a real migrated database and the real handler, rendered as a
/// client receives it.
/// </summary>
public sealed class PersonalMentorEndpointsTests : IDisposable
{
    private readonly MentorRig _rig = new();
    private readonly TenantId _robsOwn;
    private readonly TenantId _danasOwn;

    public PersonalMentorEndpointsTests()
    {
        _robsOwn = _rig.Tenants.LookupBySubject(MentorRig.Rob)!.Value;
        _danasOwn = _rig.Tenants.LookupBySubject(MentorRig.Dana)!.Value;
    }

    public void Dispose() => _rig.Dispose();

    private static MentorBlock BlockAbout(string subject, MentorWeek week, string workedOn) => new(
        week.ToString(), subject, MentorTones.Good, workedOn, "Every task started from an issue.", null,
        Array.Empty<MentorQuote>(), "Run two sessions in parallel on the bug fixes.", new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), "showcase");

    private IResult Read(TenantId own, string subject, string? week = "2026-W40") =>
        PersonalMentorEndpoints.Read(_rig.Store, _rig.Tenants, _ => "UTC", _rig.Now, own, subject, week);

    [Fact]
    public async Task Read_TheirOwnBlock_IsServedAsAPersonalPage_WithNoReadersAndNoTeam()
    {
        _rig.Store.SaveBlock(_robsOwn, BlockAbout(MentorRig.Rob, MentorRig.Week, "The invoice export."));

        var (status, page) = await RenderAsync(Read(_robsOwn, MentorRig.Rob));

        Assert.Equal(200, status);
        Assert.Equal("personal", page.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("teamId").ValueKind);
        Assert.Equal(0, page.GetProperty("readers").GetArrayLength());
        Assert.True(page.GetProperty("written").GetBoolean());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("emptyNote").ValueKind);
        var block = Assert.Single(page.GetProperty("blocks").EnumerateArray());
        Assert.Equal("The invoice export.", block.GetProperty("workedOn").GetString());
        Assert.Equal("rob.keller@example.com", block.GetProperty("personEmail").GetString());
        Assert.True(block.GetProperty("isYou").GetBoolean());
    }

    [Fact]
    public async Task Read_AnotherAccountsBlock_NeverReachesThisPerson()
    {
        // Dana's own page holds a block; Rob, asking for his own page, is served none of it.
        _rig.Store.SaveBlock(_danasOwn, BlockAbout(MentorRig.Dana, MentorRig.Week, "Dana's private week."));

        var (status, page) = await RenderAsync(Read(_robsOwn, MentorRig.Rob));

        Assert.Equal(200, status);
        Assert.Equal(0, page.GetProperty("blocks").GetArrayLength());
        Assert.DoesNotContain("Dana's private week.", page.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ABlockAboutSomeoneElseStoredUnderTheirTenant_IsNotServed()
    {
        // Only the block about the person asking is served, even from their own tenant.
        _rig.Store.SaveBlock(_robsOwn, BlockAbout(MentorRig.Dana, MentorRig.Week, "Not Rob's week."));

        var (_, page) = await RenderAsync(Read(_robsOwn, MentorRig.Rob));

        Assert.Equal(0, page.GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public void Read_ATenantThatIsNotTheCallersOwn_Throws()
    {
        // The route binds the tenant from the caller's own key, so this cannot happen over the wire; the handler still
        // refuses rather than serve a stranger's tenant.
        Assert.Throws<InvalidOperationException>(() => Read(_danasOwn, MentorRig.Rob));
    }

    [Fact]
    public async Task Read_NoBlockEver_SaysTheFirstPageArrivesAfterTheFirstWeeklyRun()
    {
        var (status, page) = await RenderAsync(Read(_robsOwn, MentorRig.Rob));

        Assert.Equal(200, status);
        Assert.False(page.GetProperty("written").GetBoolean());
        Assert.Equal(PersonalMentorEndpoints.FirstPageNote, page.GetProperty("emptyNote").GetString());
    }

    [Fact]
    public async Task Read_AQuietWeekAfterAnEarlierPage_SaysNothingWasWrittenThisWeek()
    {
        _rig.Store.SaveBlock(_robsOwn, BlockAbout(MentorRig.Rob, MentorRig.Week.Previous, "Last week's work."));

        var (_, page) = await RenderAsync(Read(_robsOwn, MentorRig.Rob));

        Assert.Equal(PersonalMentorEndpoints.NothingThisWeekNote, page.GetProperty("emptyNote").GetString());
    }

    [Fact]
    public async Task Read_NoWeekAsked_ServesTheLastClosedWeek()
    {
        _rig.Store.SaveBlock(_robsOwn, BlockAbout(MentorRig.Rob, MentorRig.Week, "The closed week."));

        var (_, page) = await RenderAsync(Read(_robsOwn, MentorRig.Rob, week: null));

        Assert.Equal(MentorRig.Week.ToString(), page.GetProperty("week").GetString());
        Assert.Single(page.GetProperty("blocks").EnumerateArray());
    }

    [Fact]
    public async Task Read_AWeekThatIsNotAnIsoWeek_IsRefused()
    {
        var (status, body) = await RenderAsync(Read(_robsOwn, MentorRig.Rob, week: "last week"));

        Assert.Equal(400, status);
        Assert.Equal(TeamMentorEndpoints.BadWeekRefusal, body.GetProperty("error").GetString());
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
