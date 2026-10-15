using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A HANDOVER CARRIES THE SEAT EXACTLY WHEN IT CARRIES THE FACTORY (Factory Definition mission, phase 5, design section
/// 2, the rule added after review). <see cref="SpawnFactory.ForHandover"/> decides the factory from who asked - a person
/// or the source session itself carries it, anyone else does not - and <see cref="SpawnFactory.SettleHandoverSeat"/>
/// carries the source's seat, definition version, kit kind and model on that same answer. Both legs then send what was
/// settled: the Gateway's cross-Director create (<see cref="GatewayEndpoints.HandoverTargetCreate"/>) and the Director's
/// own handover (<see cref="SessionWriteExecutor.StampHandoverSeat"/>). And a handover that starts a seat is refused on a
/// Director that cannot build its kit.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatThroughAHandoverTests
{
    private const string Source = "11111111-2222-3333-4444-555555555555";
    private const string Stranger = "99999999-8888-7777-6666-555555555555";
    private const string TheFactory = "website-factory";

    private static SessionDto ASeatSource(string? factory = TheFactory) => new()
    {
        SessionId = Source,
        Name = "Outreach",
        Agent = "ClaudeCode",
        RepoPath = @"D:\repos\devthrottle",
        OriginKind = "schedule",
        Factory = factory,
        FactorySeat = factory is null ? null : "outreach",
        FactoryDefinitionVersion = factory is null ? null : 4,
        FactoryKit = factory is null ? null : FactoryKits.Seat,
        LaunchModel = "claude-sonnet-5-5",
    };

    private static HandoverRequest AHandover() => new()
    {
        FromSessionId = Source,
        ToRepoPath = @"D:\repos\devthrottle",
        ToAgent = "ClaudeCode",
    };

    private static HttpContext AsSession(string sessionId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
            new SessionCredentialIdentity(Guid.Parse(sessionId), TenantId.Local, "director-1");
        return ctx;
    }

    private static HttpContext AsPerson()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return ctx;
    }

    /// <summary>What the Gateway's handover route does to the request, in its order.</summary>
    private static HandoverRequest Settled(HttpContext ctx, SessionDto source, HandoverRequest? req = null)
    {
        req ??= AHandover();
        req.Factory = SpawnFactory.ForHandover(ctx, req.FromSessionId, source.Factory);
        SpawnFactory.SettleHandoverSeat(req, source);
        return req;
    }

    // ---------- the settle ----------

    [Fact]
    public void SettleHandoverSeat_TheSourceHandsItselfOver_TheSeatMovesWithTheFactory()
    {
        var req = Settled(AsSession(Source), ASeatSource());

        Assert.Equal(TheFactory, req.Factory);
        Assert.Equal("outreach", req.FactorySeat);
        Assert.Equal(4, req.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, req.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", req.Model);
    }

    [Fact]
    public void SettleHandoverSeat_APersonMovesTheWork_TheSeatMovesWithTheFactory()
    {
        var req = Settled(AsPerson(), ASeatSource());

        Assert.Equal(TheFactory, req.Factory);
        Assert.Equal("outreach", req.FactorySeat);
        Assert.Equal("claude-sonnet-5-5", req.Model);
    }

    [Fact]
    public void SettleHandoverSeat_AnotherSessionMovesTheWork_NoFactoryAndNoSeatMove()
    {
        var req = Settled(AsSession(Stranger), ASeatSource());

        Assert.Null(req.Factory);
        Assert.Null(req.FactorySeat);
        Assert.Null(req.FactoryDefinitionVersion);
        Assert.Null(req.FactoryKit);
        Assert.Null(req.Model);
    }

    [Fact]
    public void SettleHandoverSeat_WhatTheBodySaid_IsOverwrittenBothWays()
    {
        // A stranger's body naming a seat gets none; the source's own body naming another seat gets its own.
        var forged = AHandover();
        forged.FactorySeat = "boss";
        forged.FactoryDefinitionVersion = 9;
        forged.FactoryKit = FactoryKits.Seat;
        forged.Model = "claude-opus-5-5";
        var byStranger = Settled(AsSession(Stranger), ASeatSource(), forged);
        Assert.Null(byStranger.FactorySeat);
        Assert.Null(byStranger.Model);

        var own = AHandover();
        own.FactorySeat = "boss";
        own.Model = "claude-opus-5-5";
        var bySource = Settled(AsSession(Source), ASeatSource(), own);
        Assert.Equal("outreach", bySource.FactorySeat);
        Assert.Equal("claude-sonnet-5-5", bySource.Model);
    }

    [Fact]
    public void SettleHandoverSeat_UNDEFINED_FACTORY_ASourceInAFactoryWithNoSeat_MovesTheFactoryAndNoSeat()
    {
        var source = ASeatSource();
        source.FactorySeat = null;
        source.FactoryDefinitionVersion = null;
        source.FactoryKit = null;
        source.LaunchModel = null;

        var req = Settled(AsSession(Source), source);

        Assert.Equal(TheFactory, req.Factory);
        Assert.Null(req.FactorySeat);
        Assert.Null(req.Model);
        Assert.False(SpawnFactory.HandoverCreate(req)!.CarriesSeatStartFields());
    }

    [Fact]
    public void SettleHandoverSeat_ASourceInNoFactory_MovesNoSeatEvenFromItself()
    {
        var req = Settled(AsSession(Source), ASeatSource(factory: null));

        Assert.Null(req.Factory);
        Assert.Null(req.FactorySeat);
        Assert.Null(req.Model);
    }

    // ---------- the Gateway's cross-Director leg ----------

    [Fact]
    public void HandoverTargetCreate_CarriesTheSettledFactoryAndSeat()
    {
        var create = GatewayEndpoints.HandoverTargetCreate(Settled(AsSession(Source), ASeatSource()), ASeatSource(), "context");

        Assert.Equal(TheFactory, create.Factory);
        Assert.Equal("outreach", create.FactorySeat);
        Assert.Equal(4, create.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, create.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", create.Model);
        Assert.Equal("context", create.PrePrompt);
    }

    [Fact]
    public void HandoverTargetCreate_AHandoverThatMovesNoFactory_SendsNoSeatField()
    {
        var create = GatewayEndpoints.HandoverTargetCreate(Settled(AsSession(Stranger), ASeatSource()), ASeatSource(), "context");

        Assert.Null(create.Factory);
        Assert.False(create.CarriesSeatStartFields());
    }

    // ---------- the older-Director refusal ----------

    private static readonly DirectorDto Older = new() { DirectorId = "d-old", MachineName = "SOREN_SOUTH", Version = "2.21.0" };

    [Fact]
    public void HandoverCreate_ASeatHandedToAnOlderDirector_IsRefused()
    {
        var create = SpawnFactory.HandoverCreate(Settled(AsSession(Source), ASeatSource()));

        Assert.StartsWith("The Director on SOREN_SOUTH is version 2.21.0, which cannot start a seat",
            SpawnFactory.DirectorCannotBuildKit(create!, Older, buildsFactoryKit: false));
    }

    [Fact]
    public void HandoverCreate_UNDEFINED_FACTORY_AHandoverMovingNoSeat_GoesToAnOlderDirector()
    {
        var create = SpawnFactory.HandoverCreate(Settled(AsSession(Stranger), ASeatSource()));

        Assert.Null(SpawnFactory.DirectorCannotBuildKit(create!, Older, buildsFactoryKit: false));
    }

    [Fact]
    public void HandoverCreate_AHandoverIntoAnExistingSession_StartsNothingAndIsNotAsked()
    {
        var req = AHandover();
        req.ToRepoPath = null;
        req.ToSessionId = Stranger;

        Assert.Null(SpawnFactory.HandoverCreate(Settled(AsSession(Source), ASeatSource(), req)));
    }

    // ---------- the Director's own handover ----------

    private sealed class NullBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Session ANewTarget() => new SessionManager(new AgentOptions()).RestoreEmbeddedSession(new PersistedSession
    {
        Id = Guid.NewGuid(),
        RepoPath = Path.GetTempPath(),
        WorkingDirectory = Path.GetTempPath(),
        CreatedAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
    }, new NullBackend());

    [Fact]
    public void StampHandoverSeat_TheDirectorsLeg_StampsTheSettledSeatAndPushesIt()
    {
        var target = ANewTarget();

        SessionWriteExecutor.StampHandoverSeat(target, Settled(AsSession(Source), ASeatSource()));
        var pushed = ControlEndpoints.Map(target, "dir-1");

        Assert.Equal("outreach", pushed.FactorySeat);
        Assert.Equal(4, pushed.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, pushed.FactoryKit);
        Assert.Equal("claude-sonnet-5-5", pushed.LaunchModel);
    }

    [Fact]
    public void StampHandoverSeat_AHandoverThatMovesNoFactory_StampsNoSeat()
    {
        var target = ANewTarget();

        SessionWriteExecutor.StampHandoverSeat(target, Settled(AsSession(Stranger), ASeatSource()));
        var pushed = ControlEndpoints.Map(target, "dir-1");

        Assert.Null(pushed.FactorySeat);
        Assert.Null(pushed.FactoryKit);
        Assert.Null(pushed.LaunchModel);
    }
}
