using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// THE OWNER'S SHARE OF A FACTORY FOLLOWS THE OWNER'S PLAN (owner ruling, 9 November 2026: "Yes, Pro carries factories
/// into my teams."). The share routes (<see cref="FactoryShares"/>, piece 4.1) ask the team's factory switch themselves,
/// so a team whose Owner is on Pro and has no team bill may share, and one whose Owner is on the free plan is refused with
/// the team's own sentence. Real database, the real plan reads, the hosted switch as GatewayHost builds it with Teams
/// released; fixed clock, no port.
/// </summary>
public sealed class FactorySharesFollowTheOwnersPlanTests : IDisposable
{
    private const string Owner = "sub-share-owner";
    private const string Contributor = "sub-share-contributor";
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FarFuture = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly FactoryRegistryStore _factories;
    private readonly FactoryContributors _shares;
    private readonly FactoryShares _owner;
    private readonly string _team;

    public FactorySharesFollowTheOwnersPlanTests()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
        }
        var tenants = new TenantRegistry(_db);
        tenants.MintOrLookupBySubject(Owner, "owner@example.com");
        tenants.MintOrLookupBySubject(Contributor, "contributor@example.com");
        var entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: new TrialRegistry(_db));
        _teams = new TeamRegistry(_db, tenants);
        var plans = new TenantAiGate(entitlements, tenants, new TeamMemberEntitlement(_teams, entitlements),
            clock: new FakeTimeProvider(new DateTimeOffset(Now)));
        var factorySwitch = new FactoryAgentsSwitch(false, new TenantSettingsStore(_db), plans, log: _ => { },
            teamOwners: new TeamOwnerFactories(_teams, TeamOwnerPlan.Hosted(entitlements), () => Now, _ => { }));
        _factories = new FactoryRegistryStore(_db);
        _shares = new FactoryContributors(_db);
        _owner = new FactoryShares(_teams, _factories, _shares, factorySwitch, () => Now);

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Contributor, TeamRole.Collaborator).IsDone);
        _factories.Register(new TenantId(_team), new RegisterFactoryRequest
        {
            Factory = "website", Title = "The website", Folder = "/srv/website", Computer = "BUILDBOX", BossSeat = "boss",
            Seats = { new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" } },
        }, "the owner (test)", Now);
    }

    public void Dispose() => _harness.Dispose();

    private void OwnerOnPro()
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
            Owner, EntitlementRegistry.StatusActive, FarFuture, true, EntitlementRegistry.TierPro);
    }

    private static async Task<(int Status, JsonElement Body)> Render(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task Share_OwnerOnProWithNoTeamBill_IsAllowed()
    {
        // Arrange
        OwnerOnPro();

        // Act
        var (status, _) = await Render(_owner.Share(_team, Owner, "website", TeamMemberIds.For(_team, Contributor)));

        // Assert
        Assert.Equal(200, status);
        Assert.NotNull(_shares.Find(new TenantId(_team), "website", Contributor));
    }

    [Fact]
    public async Task Share_OwnerOnFreeWithNoTeamBill_IsRefusedWithTheTeamsSentence()
    {
        // Arrange - no plan row for the Owner: the free plan.

        // Act
        var (status, body) = await Render(_owner.Share(_team, Owner, "website", TeamMemberIds.For(_team, Contributor)));

        // Assert
        Assert.Equal(404, status);
        Assert.Equal(
            "Factories in a team come with the team Owner's Pro plan, and this team's Owner is not on Pro, so nothing was done.",
            body.GetProperty("error").GetString());
        Assert.Null(_shares.Find(new TenantId(_team), "website", Contributor));
    }
}
