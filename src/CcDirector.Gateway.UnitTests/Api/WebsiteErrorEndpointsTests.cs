using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Issue #3675: the website's errors into the one error store. The token the Gateway mints must be the only thing
/// that opens the route, must be kept as a hash, and must rotate; the route must take component "website" and
/// nothing else, scrub what it stores, and file a report under the named account only when that account exists.
/// </summary>
public sealed class WebsiteErrorEndpointsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "website-errors-" + Guid.NewGuid().ToString("N"));

    public WebsiteErrorEndpointsTests() => WebsiteErrorEndpoints.ResetForTests();

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose()
    {
        WebsiteErrorEndpoints.ResetForTests();
        _h.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ErrorReportStore NewStore() => new(Path.Combine(_root, "error-reports"), () => Now);
    private WebsiteErrorTokenStore NewTokens() => new(Path.Combine(_root, "website-error-token"), () => Now);

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static ErrorReportItem Item(string message = "Could not reach the payment server.", string component = "website") => new(
        Component: component, Source: "/account/billing", Kind: "page", Message: message, ExceptionType: "TypeError",
        Stack: null, RepeatCount: 1, FirstSeenUtc: Now, LastSeenUtc: Now, ProductVersion: "abcdef012345",
        Os: null, OsVersion: null, Arch: null, MachineId: null, UserVisible: true, Surface: "/account/billing",
        Action: "start a top-up", CorrelationId: "c0ffee00-1111-2222-3333-444455556666", HttpStatus: 502, ErrorCode: "internal_error");

    private static HttpContext WithBearer(string? token)
    {
        var ctx = new DefaultHttpContext();
        if (token is not null) ctx.Request.Headers.Authorization = "Bearer " + token;
        return ctx;
    }

    private static ErrorReportQuery Everything() => new(Now.AddDays(-1), Now.AddMinutes(1));

    [Fact]
    public void Token_IsKeptOnlyAsAHash_AndOpensTheRoute()
    {
        var tokens = NewTokens();
        var (token, _) = tokens.Mint();

        var onDisk = File.ReadAllText(Path.Combine(_root, "website-error-token", "token.json"));
        Assert.DoesNotContain(token, onDisk);
        Assert.Contains(WebsiteErrorTokenStore.HashOf(token), onDisk);
        Assert.Null(WebsiteErrorEndpoints.TokenDenial(WithBearer(token), tokens));
    }

    [Fact]
    public void Token_MintingAgainRotatesIt_TheOldOneStopsWorking()
    {
        var tokens = NewTokens();
        var (first, _) = tokens.Mint();
        var (second, _) = tokens.Mint();

        Assert.NotEqual(first, second);
        Assert.Equal(401, Status(WebsiteErrorEndpoints.TokenDenial(WithBearer(first), tokens)!));
        Assert.Null(WebsiteErrorEndpoints.TokenDenial(WithBearer(second), tokens));
    }

    [Fact]
    public void Route_RefusesNoTokenAWrongTokenAndAGatewayThatNeverMintedOne()
    {
        var tokens = NewTokens();
        Assert.Equal(401, Status(WebsiteErrorEndpoints.TokenDenial(WithBearer("anything"), tokens)!));
        tokens.Mint();
        Assert.Equal(401, Status(WebsiteErrorEndpoints.TokenDenial(WithBearer(null), tokens)!));
        Assert.Equal(401, Status(WebsiteErrorEndpoints.TokenDenial(WithBearer("dtwe_wrong"), tokens)!));
    }

    [Fact]
    public void HandlePost_StoresAWebsiteReportWithEveryFieldTheWebsiteSent()
    {
        var store = NewStore();

        var result = WebsiteErrorEndpoints.HandlePost(store, tenants: null, new(null, [Item()]), Now);

        Assert.Equal(StatusCodes.Status202Accepted, Status(result));
        var r = Assert.Single(store.Query(Everything()).Records);
        Assert.Equal("website", r.Component);
        Assert.Equal("", r.Account);
        Assert.Equal(WebsiteErrorEndpoints.WebsiteDevice, r.Device);
        Assert.True(r.UserVisible);
        Assert.Equal("/account/billing", r.Surface);
        Assert.Equal("start a top-up", r.Action);
        Assert.Equal(502, r.HttpStatus);
        Assert.Equal("internal_error", r.ErrorCode);
        Assert.Equal("c0ffee00-1111-2222-3333-444455556666", r.CorrelationId);
        Assert.False(string.IsNullOrEmpty(r.Fingerprint));
    }

    [Theory]
    [InlineData("director")]
    [InlineData("cockpit")]
    [InlineData("gateway")]
    [InlineData("install")]
    public void HandlePost_TheWebsiteTokenCanFileComponentWebsiteAndNothingElse(string component)
    {
        var store = NewStore();

        var result = WebsiteErrorEndpoints.HandlePost(store, null, new(null, [Item(component: component)]), Now);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Empty(store.Query(Everything()).Records);
    }

    [Fact]
    public void HandlePost_ScrubsAgainWhateverTheWebsiteSent()
    {
        var store = NewStore();

        WebsiteErrorEndpoints.HandlePost(store, null, new(null, [Item(message: "load failed for /Users/robert/x token=abc123def")]), Now);

        var r = Assert.Single(store.Query(Everything()).Records);
        Assert.DoesNotContain("robert", r.Message);
        Assert.DoesNotContain("abc123def", r.Message);
    }

    [Fact]
    public void HandlePost_AnEmptyOrOversizedBatchIsRefused()
    {
        var store = NewStore();
        Assert.Equal(400, Status(WebsiteErrorEndpoints.HandlePost(store, null, new(null, []), Now)));
        Assert.Equal(400, Status(WebsiteErrorEndpoints.HandlePost(store, null,
            new(null, Enumerable.Range(0, ErrorReportLimits.MaxReportsPerBatch + 1).Select(_ => Item()).ToList()), Now)));
        Assert.Equal(400, Status(WebsiteErrorEndpoints.HandlePost(store, null, new(new string('x', 121), [Item()]), Now)));
    }

    [Fact]
    public void HandlePost_OverTheHourlyLimit_IsRefusedAndCountedAsAFlood()
    {
        var store = NewStore();
        var floods = new ErrorIntakeFloods();
        var batch = Enumerable.Range(0, ErrorReportLimits.MaxReportsPerBatch).Select(_ => Item()).ToList();
        var admitted = 0;
        IResult last = Results.Ok();
        for (var i = 0; i < WebsiteErrorEndpoints.MaxReportsPerHour / batch.Count + 1; i++)
        {
            last = WebsiteErrorEndpoints.HandlePost(store, null, new(null, batch), Now, floods);
            if (Status(last) == StatusCodes.Status202Accepted) admitted++;
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(last));
        Assert.Equal(WebsiteErrorEndpoints.MaxReportsPerHour / batch.Count, admitted);
        var flood = Assert.Single(floods.TakeAll());
        Assert.Equal(ErrorIntakeFloods.WebsiteErrors, flood.Intake);
        Assert.Equal(batch.Count, flood.Dropped);
    }

    [Fact]
    public void HandlePost_AVerifiedSubjectWithAnAccount_IsFiledUnderThatAccount()
    {
        var store = NewStore();
        var tenants = new TenantRegistry(_h.Open());
        var tenant = tenants.MintOrLookupBySubject("supabase-user-1", "a@example.com");

        WebsiteErrorEndpoints.HandlePost(store, tenants, new("supabase-user-1", [Item()]), Now);

        Assert.Equal(tenant.Value, Assert.Single(store.Query(Everything()).Records).Account);
        // And the account reads it back through its own scoped read, as every other row of its own.
        Assert.Single(store.Query(Everything() with { Account = tenant.Value }).Records);
    }

    [Fact]
    public void HandlePost_ASubjectWithNoAccountYet_IsFiledUnderNoAccount_AndNoAccountIsMinted()
    {
        var store = NewStore();
        var tenants = new TenantRegistry(_h.Open());

        WebsiteErrorEndpoints.HandlePost(store, tenants, new("supabase-user-never-enrolled", [Item()]), Now);

        Assert.Equal("", Assert.Single(store.Query(Everything()).Records).Account);
        Assert.Null(tenants.LookupBySubject("supabase-user-never-enrolled"));
    }
}
