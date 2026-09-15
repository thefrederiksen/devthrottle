using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// What the administrator read of the Wingman's corrections will and will not serve (the
/// Wingman-on-every-turn mission, slice G).
///
/// WHY THIS CLASS EXISTS AT ALL. The route is EXEMPT from the host's credential middleware - the daily
/// corpus pull is a job with no device key on this Gateway - so the only thing between it and anybody on
/// the internet is the service-token gate it calls for itself. An exempted route whose gate nothing
/// watches is a route that fails OPEN the day somebody moves a line, and every test in the repository
/// stays green while it does. So the gate is watched from two sides here: it runs before a single query
/// parameter is read, and an unset token is a 503 rather than an open door.
///
/// AND THE PARTITION. One account's rows must never include another's. That is the row that matters most
/// on this surface, because the whole read exists to serve a corpus - and a corpus quietly carrying one
/// account's corrections under another's name is a defect nobody would ever see from the outside.
///
/// Modelled on <see cref="AdminTurnLogEndpointScopeTests"/>, deliberately: same gate, same "a blank scope
/// is not a wildcard" rule, same last test.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked. They need
/// no host and take no lock.
/// </summary>
public sealed class AdminTurnVerdictFeedbackEndpointScopeTests : IDisposable
{
    private const string Token = "test-admin-service-token-feedback-9f1a";

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private readonly string? _prior = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);

    public AdminTurnVerdictFeedbackEndpointScopeTests()
        => Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _prior);
        _h.Dispose();
    }

    private static HttpContext Authorized(string query = "")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {Token}";
        ctx.Request.QueryString = query.Length == 0 ? QueryString.Empty : new QueryString("?" + query);
        return ctx;
    }

    private static int StatusOf(IResult result)
        => result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    /// <summary>The rows the result carries, read off the anonymous body the endpoint composed. Reflection
    /// rather than a typed contract because the body IS anonymous - and a test that could not see inside it
    /// could not prove the partition, which is the whole point of this class.</summary>
    private static IReadOnlyList<object> RowsOf(IResult result)
    {
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        Assert.NotNull(value);
        var rows = value!.GetType().GetProperty("rows")?.GetValue(value);
        Assert.NotNull(rows);
        return ((System.Collections.IEnumerable)rows!).Cast<object>().ToList();
    }

    private static string Field(object row, string name)
        => row.GetType().GetProperty(name)?.GetValue(row)?.ToString() ?? "";

    private static bool TruncatedOf(IResult result)
    {
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        return (bool)value!.GetType().GetProperty("truncated")!.GetValue(value)!;
    }

    private void Correct(TurnVerdictStore store, TenantId tenant, string verdictId, string word, DateTime at)
        => store.RecordFeedback(tenant, verdictId, "sid-" + verdictId, at.AddSeconds(-12), word, "a note", at);

    [Fact]
    public void An_unset_service_token_is_a_503_and_never_an_open_door()
    {
        // FAIL LOUD AND CLOSED. An unconfigured token is a deployment error, not a reason to serve to
        // anyone who asks - and this route is exempt from the host's middleware, so "serve to anyone who
        // asks" would mean exactly that.
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, null);

        var result = AdminTurnVerdictFeedbackEndpoint.Handle(
            Authorized("account=anything"), new TurnVerdictStore(Db), new TenantRegistry(Db));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(result));
    }

    [Fact]
    public void A_blank_account_is_refused_and_is_not_read_as_every_account()
    {
        var tenants = new TenantRegistry(Db);
        var tenant = tenants.MintOrLookupBySubject("subject-blank", "blank@example.com");
        var store = new TurnVerdictStore(Db);
        Correct(store, tenant, "tv-1", TurnVerdictVocabulary.NeededYou, DateTime.UtcNow);

        var missing = AdminTurnVerdictFeedbackEndpoint.Handle(Authorized(), store, tenants);
        var empty = AdminTurnVerdictFeedbackEndpoint.Handle(Authorized("account="), store, tenants);

        // A caller that forgot to send an account must never have that read as "every account on the fleet".
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(missing));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(empty));
    }

    [Fact]
    public void An_account_this_gateway_does_not_have_is_refused()
    {
        var result = AdminTurnVerdictFeedbackEndpoint.Handle(
            Authorized("account=no-such-account"), new TurnVerdictStore(Db), new TenantRegistry(Db));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    /// <summary>
    /// THE ROW THAT MATTERS MOST. Two accounts, corrections in both, and the read for one of them carries
    /// only its own - with the other's asked for separately as the positive control, so a read that served
    /// nothing at all could not pass this.
    /// </summary>
    [Fact]
    public void One_accounts_corrections_never_include_anothers()
    {
        var tenants = new TenantRegistry(Db);
        var mine = tenants.MintOrLookupBySubject("subject-mine", "mine@example.com");
        var theirs = tenants.MintOrLookupBySubject("subject-theirs", "theirs@example.com");
        var store = new TurnVerdictStore(Db);
        var at = new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc);
        Correct(store, mine, "tv-mine", TurnVerdictVocabulary.NeededYou, at);
        Correct(store, theirs, "tv-theirs", TurnVerdictVocabulary.Finished, at);

        var ours = AdminTurnVerdictFeedbackEndpoint.Handle(
            Authorized($"account={mine.Value}&since=2026-09-01T00:00:00Z"), store, tenants);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(ours));
        var rows = RowsOf(ours);
        Assert.Equal("tv-mine", Field(Assert.Single(rows), "verdict_id"));
        Assert.Equal(mine.Value, Field(rows[0], "account"));

        // POSITIVE CONTROL: the other account's row does exist and is served to ITS own read. Without this
        // the assertion above would pass on a read that returned nothing to anybody.
        var others = AdminTurnVerdictFeedbackEndpoint.Handle(
            Authorized($"account={theirs.Value}&since=2026-09-01T00:00:00Z"), store, tenants);
        Assert.Equal("tv-theirs", Field(Assert.Single(RowsOf(others)), "verdict_id"));
    }

    /// <summary>
    /// A page that silently stopped at its cap would have the daily pull record a day as complete when it
    /// is not, and a corpus that quietly acquires holes is worse than one that is plainly short - the holes
    /// land exactly on the busy days.
    /// </summary>
    [Fact]
    public void Truncation_is_reported_rather_than_silently_capped()
    {
        var tenants = new TenantRegistry(Db);
        var tenant = tenants.MintOrLookupBySubject("subject-page", "page@example.com");
        var store = new TurnVerdictStore(Db);
        var at = new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
            Correct(store, tenant, "tv-" + i, TurnVerdictVocabulary.NeededYou, at.AddMinutes(i));

        var capped = AdminTurnVerdictFeedbackEndpoint.Handle(
            Authorized($"account={tenant.Value}&since=2026-09-01T00:00:00Z&max=2"), store, tenants);
        var whole = AdminTurnVerdictFeedbackEndpoint.Handle(
            Authorized($"account={tenant.Value}&since=2026-09-01T00:00:00Z&max=50"), store, tenants);

        Assert.Equal(2, RowsOf(capped).Count);
        Assert.True(TruncatedOf(capped), "a page that stopped at its cap must say so");
        Assert.Equal(3, RowsOf(whole).Count);
        Assert.False(TruncatedOf(whole), "a page that held everything must not claim to be short");
        // Oldest first, so the pull can walk forward by the moment reported.
        Assert.Equal(new[] { "tv-0", "tv-1" }, RowsOf(capped).Select(r => Field(r, "verdict_id")).ToArray());
    }

    [Fact]
    public void TheGateStillRunsBeforeAnyOfThis()
    {
        // The query parameters must not have become a way to reach the store without the token, and the
        // answer to an unauthenticated caller must be the GATE's rather than "you forgot the account".
        //
        // THE REQUEST IS A VALID ONE, deliberately: a real account with a real correction in it, so the
        // only thing standing between this caller and somebody's rows is the token. A test that asked for
        // a nonsense account would go red on a removed gate with a 400 - the right colour for the wrong
        // reason - while this one goes red with the rows themselves in the answer.
        var tenants = new TenantRegistry(Db);
        var tenant = tenants.MintOrLookupBySubject("subject-gate", "gate@example.com");
        var store = new TurnVerdictStore(Db);
        Correct(store, tenant, "tv-gated", TurnVerdictVocabulary.NeededYou,
            new DateTime(2026, 9, 15, 20, 0, 0, DateTimeKind.Utc));

        var ctx = new DefaultHttpContext();   // no Authorization header
        ctx.Request.QueryString = new QueryString($"?account={tenant.Value}&since=2026-09-01T00:00:00Z");

        var result = AdminTurnVerdictFeedbackEndpoint.Handle(ctx, store, tenants);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        // POSITIVE CONTROL: the same request WITH the token really does serve that row, so the refusal
        // above is the gate refusing and not the read finding nothing.
        var authorized = AdminTurnVerdictFeedbackEndpoint.Handle(
            Authorized($"account={tenant.Value}&since=2026-09-01T00:00:00Z"), store, tenants);
        Assert.Equal("tv-gated", Field(Assert.Single(RowsOf(authorized)), "verdict_id"));
    }
}
