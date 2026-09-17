using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The administrator account lookup - the bridge between an administrator's world (emails) and the
/// Gateway's (account ids and Directors).
///
/// THE TESTS THAT MATTER MOST are the refusals. This route exists so a capability can be pointed at
/// somebody else's account, and the ways it can point at the WRONG one are the ways real harm happens:
/// naming two accounts at once, an address recorded against more than one account, and an account that
/// simply is not here. Each of those has to be a plain refusal rather than a confident answer about
/// somebody nobody named.
/// </summary>
[Collection(AdminServiceTokenCollection.Name)]
public sealed class AdminAccountLookupEndpointTests : IDisposable
{
    private const string Token = "test-admin-service-token-a4f1";

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private readonly string? _priorAdmin = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);

    public AdminAccountLookupEndpointTests()
        => Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _priorAdmin);
        _h.Dispose();
    }

    private TenantRegistry NewTenants() => new(Db);

    private static DirectorRegistry NewDirectors() => new();

    private static HttpContext Authorized()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {Token}";
        return ctx;
    }

    private static JsonElement Body(IResult result)
    {
        // Results.Json carries the value; read it back the way a caller would see it.
        var valueProp = result.GetType().GetProperty("Value");
        var value = valueProp?.GetValue(result);
        return JsonSerializer.SerializeToElement(value);
    }

    [Fact]
    public void NamingBothAnEmailAndASubject_IsRefused()
    {
        // Preferring one silently would let a caller that resolved an address to the wrong subject act on
        // an account it never named, and the reply would look perfectly correct.
        var result = AdminAccountLookupEndpoint.Handle(
            NewTenants(), NewDirectors(), "someone@example.com", "subject-1");

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void NamingNeither_IsRefused()
    {
        var result = AdminAccountLookupEndpoint.Handle(NewTenants(), NewDirectors(), "", "");
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void AnAccountThatIsNotHere_IsAPlainNegativeAndNotAnError()
    {
        // "Nobody by that name" and "the lookup broke" are different facts. An administrator told the
        // second when it was the first would go looking for a fault that does not exist.
        var result = AdminAccountLookupEndpoint.Handle(
            NewTenants(), NewDirectors(), "nobody@example.com", null);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        var body = Body(result);
        Assert.False(body.GetProperty("found").GetBoolean());
        Assert.Equal("email", body.GetProperty("searched_by").GetString());
    }

    [Fact]
    public void AnAccountFoundByEmail_AnswersItsAccountIdAndItsComputers()
    {
        var tenants = NewTenants();
        var tenant = tenants.MintOrLookupBySubject("subject-mario", "mario@example.com");

        var directors = NewDirectors();
        Register(directors, tenant, "director-1", "OFFICEPC");
        Register(directors, tenant, "director-2", "LAPTOP");

        var result = AdminAccountLookupEndpoint.Handle(tenants, directors, "mario@example.com", null);

        var body = Body(result);
        Assert.True(body.GetProperty("found").GetBoolean());
        Assert.Equal(tenant.Value, body.GetProperty("account").GetString());
        var computers = body.GetProperty("computers").EnumerateArray().ToList();
        Assert.Equal(2, computers.Count);
        // Ordered by machine name so an administrator reads a stable list.
        Assert.Equal("LAPTOP", computers[0].GetProperty("machine_name").GetString());
        Assert.Equal("OFFICEPC", computers[1].GetProperty("machine_name").GetString());
    }

    [Fact]
    public void TheEmailIsMatchedWithoutRegardToCase()
    {
        var tenants = NewTenants();
        var tenant = tenants.MintOrLookupBySubject("subject-mario", "Mario.D@Example.com");

        var result = AdminAccountLookupEndpoint.Handle(tenants, NewDirectors(), "mario.d@example.com", null);

        Assert.Equal(tenant.Value, Body(result).GetProperty("account").GetString());
    }

    [Fact]
    public void AnEmailRecordedAgainstTwoAccounts_IsRefusedRatherThanGuessed()
    {
        // The dangerous one. Picking the first would answer confidently with an account nobody named, and
        // the caller would then switch capture on for a stranger while believing it had asked about Mario.
        var tenants = NewTenants();
        tenants.MintOrLookupBySubject("subject-one", "shared@example.com");
        tenants.MintOrLookupBySubject("subject-two", "shared@example.com");

        var result = AdminAccountLookupEndpoint.Handle(tenants, NewDirectors(), "shared@example.com", null);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
    }

    [Fact]
    public void AnAccountFoundBySubject_AnswersTheSameAccount()
    {
        var tenants = NewTenants();
        var tenant = tenants.MintOrLookupBySubject("subject-mario", "mario@example.com");

        var result = AdminAccountLookupEndpoint.Handle(tenants, NewDirectors(), null, "subject-mario");

        Assert.Equal(tenant.Value, Body(result).GetProperty("account").GetString());
    }

    [Fact]
    public void ItReturnsOnlyTHATAccountsComputers()
    {
        // The whole point of this route is to name somebody else's account. If it leaked a third party's
        // machines while doing it, the bridge would be worse than the gap it closes.
        var tenants = NewTenants();
        var mario = tenants.MintOrLookupBySubject("subject-mario", "mario@example.com");
        var other = tenants.MintOrLookupBySubject("subject-other", "other@example.com");

        var directors = NewDirectors();
        Register(directors, mario, "director-mario", "OFFICEPC");
        Register(directors, other, "director-other", "SOMEONE-ELSE-PC");

        var body = Body(AdminAccountLookupEndpoint.Handle(tenants, directors, "mario@example.com", null));

        var names = body.GetProperty("computers").EnumerateArray()
            .Select(c => c.GetProperty("machine_name").GetString()).ToList();
        Assert.Equal(new[] { "OFFICEPC" }, names);
    }

    [Fact]
    public void AnAccountWithNoConnectedComputer_SaysSoRatherThanImplyingItHasNone()
    {
        // An empty list has two meanings and only one is a problem: never connected a computer, or its
        // computers are away right now. This route only sees what the Gateway holds at this instant.
        var tenants = NewTenants();
        tenants.MintOrLookupBySubject("subject-mario", "mario@example.com");

        var body = Body(AdminAccountLookupEndpoint.Handle(tenants, NewDirectors(), "mario@example.com", null));

        Assert.Empty(body.GetProperty("computers").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("computers_note").GetString()));
    }

    private static int StatusOf(IResult result)
    {
        var prop = result.GetType().GetProperty("StatusCode");
        return prop?.GetValue(result) as int? ?? StatusCodes.Status200OK;
    }

    private static void Register(DirectorRegistry registry, TenantId tenant, string directorId, string machine)
        => registry.RegisterFromStream(
            directorId, machine, user: "someone", version: "test", pid: 1,
            startedAt: DateTime.UtcNow, tenant: tenant);
}
