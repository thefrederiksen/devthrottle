using CcDirector.Core.Agents;
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
/// THE SEAT IS SETTLED FROM THE CREDENTIAL, NEVER FROM THE BODY (Factory Definition mission, phase 5, design section
/// 2). These drive <see cref="SpawnFactory.TryEstablish"/> directly, as the factory's own spawn-door tests do: what
/// matters is what the settle does to the request and which spawns it refuses.
///
/// The cases that make it enforcement rather than decoration are the refusals: a person naming a seat, a session
/// naming a seat or a version that is not its own, a session with no seat claiming one, and a seat's child on an agent
/// that cannot take the seat's rules. Every inheritance test would still pass if the settle believed the body.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatAtTheSpawnDoorTests
{
    private const string Caller = "11111111-2222-3333-4444-555555555555";
    private const string Child = "66666666-7777-8888-9999-000000000000";
    private const string TheFactory = "website-factory";
    private const string TheSeat = "outreach";
    private const int TheVersion = 7;

    private static HttpContext AsDevice(string deviceType = "browser")
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
        return ctx;
    }

    private static HttpContext AsSession(string sessionId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
            new SessionCredentialIdentity(Guid.Parse(sessionId), TenantId.System, "director-1");
        return ctx;
    }

    private static NewSessionRequest Body(string agent = "ClaudeCode") => new()
    {
        RepoPath = @"D:\repos\devthrottle",
        Agent = agent,
    };

    private static Func<string, SessionFactoryLookup> Rows(params (string Id, SessionFactoryLookup Lookup)[] rows)
    {
        var map = rows.ToDictionary(r => r.Id, r => r.Lookup, StringComparer.OrdinalIgnoreCase);
        return id => map.TryGetValue(id, out var l) ? l : SessionFactoryLookup.NotKnown;
    }

    private static Func<string, SessionFactoryLookup> ASeat() => Rows((Caller, SessionFactoryLookup.In(TheFactory, TheSeat, TheVersion)));

    /// <summary>A rules reader that answers one way and counts its reads.</summary>
    private sealed class RulesReader
    {
        private readonly SeatRulesRead _answer;
        public RulesReader(SeatRulesRead answer) => _answer = answer;
        public List<(string Factory, int Version, string Seat)> Asked { get; } = new();
        public SeatRulesReader Read => (factory, version, seat) => { Asked.Add((factory, version, seat)); return _answer; };
    }

    private static string? ErrorOf(IResult? result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private static int? StatusOf(IResult? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    // ---------- a session key: the child inherits ----------

    [Fact]
    public void TryEstablish_ChildOfASeat_InheritsTheSeatAndVersionWithARulesOnlyKit()
    {
        var req = Body();

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error));

        Assert.Null(error);
        Assert.Equal(TheFactory, req.Factory);
        Assert.Equal(TheSeat, req.FactorySeat);
        Assert.Equal(TheVersion, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.RulesOnly, req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_GrandchildOfASeat_InheritsTheSameSeatFromItsParentsRow()
    {
        // The child's own row carries the inherited seat with a rules-only kit; its child reads THAT row, never the
        // grandparent's. Every level below a seat is held by the same seat's rules.
        var rows = Rows(
            (Caller, SessionFactoryLookup.In(TheFactory, TheSeat, TheVersion)),
            (Child, SessionFactoryLookup.In(TheFactory, TheSeat, TheVersion)));
        var req = Body();

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Child), "test", rows, out _));

        Assert.Equal(TheSeat, req.FactorySeat);
        Assert.Equal(TheVersion, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.RulesOnly, req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_ChildStatesItsCallersOwnSeatAndVersion_IsAccepted()
    {
        var req = Body();
        req.FactorySeat = TheSeat;
        req.FactoryDefinitionVersion = TheVersion;
        req.FactoryKit = FactoryKits.RulesOnly;

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out _));

        Assert.Equal(TheSeat, req.FactorySeat);
    }

    [Fact]
    public void TryEstablish_ChildNamesAnotherSeat_IsRefusedNotCorrected()
    {
        var req = Body();
        req.FactorySeat = "boss";

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
        Assert.Equal("a session of the seat 'outreach' (definition version 7) may not start a session as the seat 'boss'", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_ChildNamesAnotherVersion_IsRefused()
    {
        var req = Body();
        req.FactoryDefinitionVersion = TheVersion + 1;

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error));

        Assert.Equal("a session of the seat 'outreach' (definition version 7) may not start a session as definition version 8", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_ChildAsksForASeatKit_IsRefused()
    {
        // A child asking for the full seat kit would be handed the seat's brief, grants and records.
        var req = Body();
        req.FactoryKit = FactoryKits.Seat;

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error));

        Assert.Equal("a session of the seat 'outreach' (definition version 7) may not start a session as a 'seat' kit", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_ChildChoosesItsOwnFolderAndModel_ThosePassThroughUntouched()
    {
        var req = Body();
        req.FactoryWorktree = "devthrottle";
        req.FactoryHome = true;
        req.Model = "claude-sonnet-5-5";

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out _));

        Assert.Equal("devthrottle", req.FactoryWorktree);
        Assert.True(req.FactoryHome);
        Assert.Equal("claude-sonnet-5-5", req.Model);
    }

    // ---------- a session with no seat gives none ----------

    [Fact]
    public void TryEstablish_UNDEFINED_FACTORY_ChildOfASessionInAFactoryWithNoSeat_GetsNoSeat()
    {
        // A factory with no published definition: its sessions carry a factory and no seat, and their children take
        // exactly today's path - the factory, and nothing else.
        var rows = Rows((Caller, SessionFactoryLookup.In(TheFactory)));
        var req = Body();

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", rows, out _));

        Assert.Equal(TheFactory, req.Factory);
        Assert.Null(req.FactorySeat);
        Assert.Null(req.FactoryDefinitionVersion);
        Assert.Null(req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_UNDEFINED_FACTORY_SessionWithNoSeatNamesOne_IsRefused()
    {
        var rows = Rows((Caller, SessionFactoryLookup.In(TheFactory)));
        var req = Body();
        req.FactorySeat = TheSeat;

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", rows, out var error));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
        Assert.Equal("this session is no seat of 'website-factory', so it cannot start a session as the seat 'outreach'", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_SessionInNoFactoryNamesASeat_IsRefused()
    {
        var rows = Rows((Caller, SessionFactoryLookup.InNoFactory));
        var req = Body();
        req.FactorySeat = TheSeat;

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", rows, out var error));

        Assert.Equal("this session is no seat of a factory, so it cannot start a session as the seat 'outreach'", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_SessionInNoFactory_ChildGetsNoSeat()
    {
        var rows = Rows((Caller, SessionFactoryLookup.InNoFactory));
        var req = Body();

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", rows, out _));

        Assert.Null(req.FactorySeat);
        Assert.Null(req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_NoReaderAndASeatNamed_IsRefusedAsUnverifiable()
    {
        var req = Body();
        req.FactorySeat = TheSeat;

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", factoryOf: null, out var error));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(error));
        Assert.Equal(SpawnFactory.CannotVerify, ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_CallerNotYetKnownAndASeatNamed_IsRefusedAsARetry()
    {
        var req = Body();
        req.FactorySeat = TheSeat;

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", Rows(), out var error));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(error));
        Assert.Equal(SpawnFactory.NotYetKnown, ErrorOf(error));
    }

    // ---------- a person's device ----------

    [Theory]
    [InlineData("seat")]
    [InlineData("version")]
    [InlineData("kit")]
    public void TryEstablish_PersonNamesASeatVersionOrKit_IsRefusedInWords(string field)
    {
        var req = Body();
        if (field == "seat") req.FactorySeat = TheSeat;
        if (field == "version") req.FactoryDefinitionVersion = TheVersion;
        if (field == "kit") req.FactoryKit = FactoryKits.Seat;

        Assert.False(SpawnFactory.TryEstablish(req, AsDevice("phone"), "test", factoryOf: null, out var error));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(error));
        Assert.Equal("A seat is started from its factory's page or its schedule, not named on a spawn.", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_PersonWithNoSeat_ChoosesAModel_IsUnchanged()
    {
        var req = Body();
        req.Model = "claude-opus-5-5";

        Assert.True(SpawnFactory.TryEstablish(req, AsDevice(), "test", factoryOf: null, out _));

        Assert.Equal("claude-opus-5-5", req.Model);
        Assert.Null(req.FactorySeat);
    }

    [Fact]
    public void TryEstablish_TheGatewaysOwnSeatStartThroughAPersonsDoor_PassesItsSeatThrough()
    {
        // The talk: the Gateway builds the seat start from the published definition and sends it through the person's
        // own door. Only Gateway code can mark the request it built.
        var ctx = AsDevice();
        SpawnFactory.MarkGatewaySeatStart(ctx);
        var req = Body();
        req.FactorySeat = TheSeat;
        req.FactoryDefinitionVersion = TheVersion;
        req.FactoryKit = FactoryKits.Seat;

        Assert.True(SpawnFactory.TryEstablish(req, ctx, "test", factoryOf: null, out _));

        Assert.Equal(TheSeat, req.FactorySeat);
        Assert.Equal(TheVersion, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
    }

    [Fact]
    public void TryEstablish_PersonSendsTheMarkersNameAsAHeaderOrQuery_IsStillRefused()
    {
        // The mark lives in HttpContext.Items, which nothing a caller sends can write. A header, a query value or a
        // cookie with the mark's own name is just text and changes nothing.
        var ctx = AsDevice();
        ctx.Request.Headers[SpawnFactory.GatewaySeatStartItemKey] = "true";
        ctx.Request.QueryString = new QueryString($"?{SpawnFactory.GatewaySeatStartItemKey}=true");
        ctx.Request.Headers.Cookie = $"{SpawnFactory.GatewaySeatStartItemKey}=true";
        var req = Body();
        req.FactorySeat = TheSeat;

        Assert.False(SpawnFactory.TryEstablish(req, ctx, "test", factoryOf: null, out var error));

        Assert.Equal(SpawnFactory.PersonNamedASeat, ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_AMarkThatIsNotExactlyTrue_DoesNotCount()
    {
        var ctx = AsDevice();
        ctx.Items[SpawnFactory.GatewaySeatStartItemKey] = "true";
        var req = Body();
        req.FactorySeat = TheSeat;

        Assert.False(SpawnFactory.TryEstablish(req, ctx, "test", factoryOf: null, out _));
    }

    // ---------- any other credential passes through ----------

    [Fact]
    public void TryEstablish_InternalCredential_PassesEverySeatFieldThroughAsSent()
    {
        // A Director restoring a seat, or the cron starter: the seat must come back as it was.
        var req = Body("Codex");
        req.Factory = TheFactory;
        req.FactorySeat = TheSeat;
        req.FactoryDefinitionVersion = TheVersion;
        req.FactoryKit = FactoryKits.Seat;
        req.FactoryWorktree = "devthrottle";
        req.Model = "gpt-5";

        Assert.True(SpawnFactory.TryEstablish(req, new DefaultHttpContext(), "test", factoryOf: null, out _));

        Assert.Equal(TheSeat, req.FactorySeat);
        Assert.Equal(TheVersion, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        Assert.Equal("devthrottle", req.FactoryWorktree);
        Assert.Equal("gpt-5", req.Model);
    }

    // ---------- a child on an agent that cannot take the seat's rules ----------

    [Fact]
    public void TryEstablish_CodexChildOfASeatWithRules_IsRefusedWithTheTablesReason()
    {
        var rules = new RulesReader(SeatRulesRead.Carries(true));
        var req = Body("Codex");

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error, rules.Read));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(error));
        Assert.Equal(FactoryRulesSupport.For(AgentKind.Codex).Reason, ErrorOf(error));
        Assert.Equal(new[] { (TheFactory, TheVersion, TheSeat) }, rules.Asked);
    }

    [Fact]
    public void TryEstablish_CodexChildOfASeatWithNoRules_IsAccepted()
    {
        var rules = new RulesReader(SeatRulesRead.Carries(false));
        var req = Body("Codex");

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out _, rules.Read));

        Assert.Equal(FactoryKits.RulesOnly, req.FactoryKit);
        Assert.Single(rules.Asked);
    }

    [Fact]
    public void TryEstablish_ClaudeCodeChildOfASeatWithRules_IsAcceptedWithoutReadingTheDefinition()
    {
        var rules = new RulesReader(SeatRulesRead.Carries(true));
        var req = Body("ClaudeCode");

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out _, rules.Read));

        Assert.Equal(TheSeat, req.FactorySeat);
        Assert.Empty(rules.Asked);
    }

    [Fact]
    public void TryEstablish_GeminiChildOfASeatWithRules_IsRefusedAsNotShownYet()
    {
        var req = Body("Gemini");

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error,
            new RulesReader(SeatRulesRead.Carries(true)).Read));

        Assert.Equal(FactoryRulesSupport.NotShownYetReason(AgentKind.Gemini), ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_RawCliChildOfASeatWithRules_IsRefusedInWords()
    {
        var req = Body("RawCli");

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error,
            new RulesReader(SeatRulesRead.Carries(true)).Read));

        Assert.Equal("The agent 'RawCli' cannot take a seat's rules at launch, so it cannot start under the seat outreach.", ErrorOf(error));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    public void TryEstablish_ANumericAgentWordUnderASeatWithRules_IsRefusedInWordsNotThrown(string agentWord)
    {
        // Review finding 1: Enum.TryParse accepts any number, and the rules table throws for a kind it has no row for.
        var req = Body(agentWord);

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error,
            new RulesReader(SeatRulesRead.Carries(true)).Read));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(error));
        Assert.Equal($"The agent '{agentWord}' cannot take a seat's rules at launch, so it cannot start under the seat outreach.", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_CodexChildWithNoRulesReader_IsRefusedNotLetBy()
    {
        var req = Body("Codex");

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error, seatRules: null));

        Assert.Equal("The Gateway cannot read whether the seat outreach carries rules, so no Codex session was started under it.", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_CodexChildWhoseVersionCannotBeRead_IsRefusedWithTheReason()
    {
        var req = Body("Codex");

        Assert.False(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", ASeat(), out var error,
            new RulesReader(SeatRulesRead.Unread("version 7 of the definition of 'website-factory' cannot be found")).Read));

        Assert.Equal("The Gateway cannot read whether the seat outreach carries rules (version 7 of the definition of " +
                     "'website-factory' cannot be found), so no Codex session was started under it.", ErrorOf(error));
    }

    [Fact]
    public void TryEstablish_UNDEFINED_FACTORY_CodexChildOfASessionWithNoSeat_IsUnchangedAndReadsNothing()
    {
        var rules = new RulesReader(SeatRulesRead.Carries(true));
        var req = Body("Codex");

        Assert.True(SpawnFactory.TryEstablish(req, AsSession(Caller), "test", Rows((Caller, SessionFactoryLookup.In(TheFactory))),
            out _, rules.Read));

        Assert.Null(req.FactorySeat);
        Assert.Empty(rules.Asked);
    }
}
