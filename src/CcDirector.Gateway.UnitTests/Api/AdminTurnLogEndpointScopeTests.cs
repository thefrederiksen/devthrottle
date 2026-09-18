using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.TurnLog;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// What the capture switch will and will not accept as a scope.
///
/// THE ASYMMETRY IS THE POINT, and both halves are here. Switching capture ON must name something real,
/// because a mistyped scope used to sit in the table looking like a recorded decision while naming nothing
/// - and under a wider ON, somebody who believed they had switched a machine off would have been captured
/// anyway. Switching capture OFF is never blocked, whatever it names, because refusing a withdrawal is the
/// same failure arriving from the other side.
/// </summary>
[Collection(AdminServiceTokenCollection.Name)]
public sealed class AdminTurnLogEndpointScopeTests : IDisposable
{
    private const string Token = "test-admin-service-token-77c2";

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private readonly string? _prior = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);
    private readonly List<TurnLogSwitchStore> _stores = new();

    public AdminTurnLogEndpointScopeTests()
        => Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _prior);
        foreach (var s in _stores) s.Dispose();
        _h.Dispose();
    }

    private TurnLogSwitchStore NewStore()
    {
        var store = new TurnLogSwitchStore(Db);
        store.Start();
        _stores.Add(store);
        return store;
    }

    private static HttpContext Authorized()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {Token}";
        return ctx;
    }

    private static int StatusOf(IResult result)
        => result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    private static void Register(DirectorRegistry registry, TenantId tenant, string directorId, string machine)
        => registry.RegisterFromStream(directorId, machine, "someone", "test", 1, DateTime.UtcNow, tenant);

    private AdminTurnLogEndpoint.SetRequest Req(string account, string machine, bool enabled)
        => new(account, machine, enabled, "soren@example.com", "a reason that is written down");

    [Fact]
    public void SwitchingON_AMachineTheGatewayDoesNotKnow_IsRefused()
    {
        var tenants = new TenantRegistry(Db);
        var tenant = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var directors = new DirectorRegistry();

        var result = AdminTurnLogEndpoint.Handle(
            Authorized(), Req(tenant.Value, "a-typo-nobody-has", enabled: true), NewStore(), tenants, directors);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void SwitchingON_AnAccountTheGatewayDoesNotKnow_IsRefused()
    {
        var tenants = new TenantRegistry(Db);
        var result = AdminTurnLogEndpoint.Handle(
            Authorized(), Req("no-such-account", TurnLogSwitchEntity.Any, enabled: true),
            NewStore(), tenants, new DirectorRegistry());

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void SwitchingON_AMachineBelongingToADIFFERENTAccount_IsRefused()
    {
        var tenants = new TenantRegistry(Db);
        var mine = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var theirs = tenants.MintOrLookupBySubject("subject-b", "b@example.com");
        var directors = new DirectorRegistry();
        Register(directors, theirs, "director-theirs", "THEIR-PC");

        var result = AdminTurnLogEndpoint.Handle(
            Authorized(), Req(mine.Value, "director-theirs", enabled: true), NewStore(), tenants, directors);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void SwitchingON_ARealAccountAndItsOwnMachine_IsAccepted()
    {
        var tenants = new TenantRegistry(Db);
        var tenant = tenants.MintOrLookupBySubject("subject-a", "a@example.com");
        var directors = new DirectorRegistry();
        Register(directors, tenant, "director-1", "THE-PC");
        var store = NewStore();

        var result = AdminTurnLogEndpoint.Handle(
            Authorized(), Req(tenant.Value, "director-1", enabled: true), store, tenants, directors);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.True(store.IsEnabled(tenant.Value, "director-1"));
    }

    [Fact]
    public void SwitchingOFF_AMachineTheGatewayDoesNotKnow_IsACCEPTED()
    {
        // THE OTHER HALF OF THE ASYMMETRY. A withdrawal must never be refused - not for a machine that is
        // away, not for one whose identifier no longer resolves. Blocking an OFF is the same harm this
        // validation exists to prevent, arriving from the other side.
        var tenants = new TenantRegistry(Db);
        var store = NewStore();

        var result = AdminTurnLogEndpoint.Handle(
            Authorized(), Req("some-account-long-gone", "some-machine-long-gone", enabled: false),
            store, tenants, new DirectorRegistry());

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
    }

    [Fact]
    public void TheGateStillRunsBeforeAnyOfThis()
    {
        // Validation must not have become a way to reach the store without the token.
        var ctx = new DefaultHttpContext();   // no Authorization header
        var result = AdminTurnLogEndpoint.Handle(
            ctx, Req("*", "*", enabled: true), NewStore(), new TenantRegistry(Db), new DirectorRegistry());

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
    }
}
