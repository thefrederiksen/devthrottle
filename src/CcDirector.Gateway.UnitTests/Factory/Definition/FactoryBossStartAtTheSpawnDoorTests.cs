using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A FACTORY'S BOSS, STARTED FOR THE OWNER (issue #4351). A session in no factory - the owner's own agent - names a
/// factory and its Boss seat, and the new session is born in that factory as its Boss: for a defined factory, the Boss
/// seat of the published version with a rules-only kit, exactly what its night start's children carry.
///
/// Both ways, because a rule that only ever says yes is not a rule: every other seat is refused, a session already in a
/// factory cannot use this to reach another one, a person still cannot name a seat, and a Gateway that cannot read the
/// Boss refuses rather than guessing.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryBossStartAtTheSpawnDoorTests
{
    private const string Caller = "11111111-2222-3333-4444-555555555555";
    private const string TheFactory = "dev-sales";
    private const string TheBoss = "boss";
    private const int ThePublished = 5;

    private static HttpContext AsSession(string sessionId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
            new SessionCredentialIdentity(Guid.Parse(sessionId), TenantId.System, "director-1");
        return ctx;
    }

    private static HttpContext AsDevice()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return ctx;
    }

    private static NewSessionRequest Body(string? factory = TheFactory, string? seat = TheBoss) => new()
    {
        RepoPath = @"D:\repos\dev-sales-factory",
        Agent = "ClaudeCode",
        Factory = factory,
        FactorySeat = seat,
    };

    private static Func<string, SessionFactoryLookup> Caller_(SessionFactoryLookup lookup)
        => id => string.Equals(id, Caller, StringComparison.OrdinalIgnoreCase) ? lookup : SessionFactoryLookup.NotKnown;

    private static readonly Func<string, SessionFactoryLookup> InNoFactory = Caller_(SessionFactoryLookup.InNoFactory);

    private static BossStartReader Boss(string? bossSeat = TheBoss, int? published = ThePublished, bool archived = false)
        => factory => string.Equals(factory, TheFactory, StringComparison.Ordinal)
            ? new FactoryBossStart(TheFactory, bossSeat, published, archived)
            : null;

    private static readonly SeatRulesReader NoRules = (_, _, _) => SeatRulesRead.Carries(false);

    private static string? ErrorOf(IResult? result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private static int? StatusOf(IResult? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    // ---------- allowed ----------

    [Fact]
    public void TryEstablish_SessionInNoFactoryNamesTheBoss_IsSeatedAsTheBossOfThePublishedVersion()
    {
        var req = Body();

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, Boss()));

        Assert.Null(error);
        Assert.Equal(TheFactory, req.Factory);
        Assert.Equal(TheBoss, req.FactorySeat);
        Assert.Equal(ThePublished, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.RulesOnly, req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_TheBossNamedInAnotherCase_IsRecordedInTheRegistrationsSpelling()
    {
        var req = Body(factory: "Dev-Sales", seat: "BOSS");

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out _, NoRules, Boss()));

        Assert.Equal(TheFactory, req.Factory);
        Assert.Equal(TheBoss, req.FactorySeat);
    }

    [Fact]
    public void TryEstablish_TheBossOfAFactoryWithNoPublishedDefinition_IsInTheFactoryWithNoSeatRecorded()
    {
        // No published definition, no kit to build: the Boss is in its factory as every session of such a factory is.
        var req = Body();

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out _, NoRules, Boss(published: null)));

        Assert.Equal(TheFactory, req.Factory);
        Assert.Null(req.FactorySeat);
        Assert.Null(req.FactoryDefinitionVersion);
        Assert.Null(req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_TheBoss_IsToldTheOwnersWordIsFinal_WithOrWithoutAPublishedDefinition()
    {
        // The Boss started by the owner's own session has no brief (a rules-only kit, or none): it is told on its prompt.
        foreach (var published in new int?[] { ThePublished, null })
        {
            var req = Body();
            req.PrePrompt = "Remove the cc-gmail rule.";

            Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out _, NoRules, Boss(published: published)));

            Assert.Equal("Remove the cc-gmail rule.\n\n" + CcDirector.Gateway.Factory.Talk.FactorySeatOpening.OwnerWordIsFinal, req.PrePrompt);
        }
    }

    [Fact]
    public void TryEstablish_ARefusedBossStart_LeavesThePromptAsItWas()
    {
        var req = Body(seat: "finder");
        req.PrePrompt = "Go.";

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out _, NoRules, Boss()));

        Assert.Equal("Go.", req.PrePrompt);
    }

    // ---------- refused ----------

    [Fact]
    public void TryEstablish_SessionInNoFactoryNamesASeatThatIsNotTheBoss_IsRefused()
    {
        var req = Body(seat: "finder");

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, Boss()));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
        Assert.Equal("a session in no factory may start only the Boss of 'dev-sales' (the seat 'boss'), not the seat 'finder'", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_SessionInNoFactoryNamesAFactoryWithNoSeat_IsStillRefused()
    {
        // Joining a factory without being its Boss stays what it was: refused.
        var req = Body(seat: null);

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, Boss()));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
        Assert.Equal("this session is in no factory, so it cannot start a session in 'dev-sales'", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_SessionOfAnotherFactoryNamesThisFactorysBoss_IsRefused()
    {
        // Only the owner's agent - a session in no factory - may start a Boss. A factory's own sessions cannot use it
        // to reach into another factory.
        var req = Body();
        var ofAnother = Caller_(SessionFactoryLookup.In("mindzie", "outreach", 3));

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ofAnother, out var error, NoRules, Boss()));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
        Assert.Equal("a session of factory 'mindzie' may not start a session in 'dev-sales'", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_SeatOfTheSameFactoryNamesTheBoss_IsRefused()
    {
        // A Finder cannot start a Boss: its children are held by its own seat.
        var req = Body();
        var aFinder = Caller_(SessionFactoryLookup.In(TheFactory, "finder", ThePublished));

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", aFinder, out var error, NoRules, Boss()));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
    }

    [Fact]
    public void TryEstablish_APersonNamesTheBoss_IsRefused()
    {
        // A person starts the Boss with the Talk button, never by naming a seat on a spawn.
        var req = Body();

        Assert.False(SpawnFactory.TryEstablish(req, AsDevice(), "test", InNoFactory, out var error, NoRules, Boss()));

        Assert.Equal(SpawnFactory.PersonNamedASeat, ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_NoBossReader_IsRefusedNotGuessed()
    {
        var req = Body();

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, bossStart: null));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(error));
        Assert.Null(req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_AFactoryTheAccountDoesNotRegister_IsRefused()
    {
        var req = Body(factory: "no-such-factory");

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, Boss()));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(error));
    }

    [Fact]
    public void TryEstablish_AFactoryThatNamesNoBoss_IsRefused()
    {
        var req = Body();

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, Boss(bossSeat: null)));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(error));
    }

    [Fact]
    public void TryEstablish_AnArchivedFactory_IsRefused()
    {
        var req = Body();

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, Boss(archived: true)));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(error));
    }

    [Theory]
    [InlineData(FactoryKits.Seat, null)]
    [InlineData(null, ThePublished - 1)]
    public void TryEstablish_TheBossNamedWithAnotherKitOrVersion_IsRefused(string? kit, int? version)
    {
        // The Boss is held by its published version's rules; the caller does not get to choose the kit or the version.
        var req = Body();
        req.FactoryKit = kit;
        req.FactoryDefinitionVersion = version;

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, NoRules, Boss()));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
    }

    [Fact]
    public void TryEstablish_TheBossOnAnAgentThatCannotTakeItsRules_IsRefused()
    {
        // The same limit a Boss's child is held to: a Boss whose seat carries rules does not start free of them.
        var req = Body();
        req.Agent = "Codex";
        SeatRulesReader carries = (_, _, _) => SeatRulesRead.Carries(true);

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", InNoFactory, out var error, carries, Boss()));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(error));
    }
}
