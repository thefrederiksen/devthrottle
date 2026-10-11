using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// The administrator trial EXTENSION - the capability and its own gate.
///
/// This route is exempt from the host-wide token middleware (its caller is the website's admin API, a server
/// holding no device key), so everything standing between the open internet and a year of free product lives
/// in this one endpoint, and every one of those rules is asserted here:
///
///   no token / wrong token / wrong scheme -> 401
///   the service token not configured      -> 503 (refuse to serve, never serve unguarded)
///   the report token presented instead    -> 401 (a read-only credential must not hand out product)
///   a self-hosted Gateway                 -> 409, and only AFTER the token check
///   a blank subject / date / actor/reason -> 400, and NOT an outcome
///
/// And the capability's own rules, which are what an administrator's promise actually rests on:
///
///   later          -> applied, with a ledger row written in the SAME transaction
///   equal / sooner -> refused; the stored date is untouched (a trial is never shortened)
///   beyond a year  -> refused
///   no trial row   -> reported as no-trial, and no row is invented
///
/// The exemption itself is asserted too: if <c>AuthMiddleware.PublicPaths</c> stopped containing this route,
/// every extension would 401 at the host gate and the admin screen would go back to answering "could not
/// confirm" - a failure that would otherwise be discovered in production, on a customer promise.
/// </summary>
public sealed class AdminTrialExtendTests : IDisposable
{
    private const string Token = "test-admin-service-token-9c41";
    private const string WrongToken = "test-admin-service-token-9c42";
    private const string Subject = "auth0|trial-subject-1";

    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Started = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Expires = new(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"admin-trial-dev-{Guid.NewGuid():N}.json");
    private readonly string? _savedToken = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);
    private readonly string? _savedHosted = Environment.GetEnvironmentVariable(GatewayHostedMode.HostedEnvVar);

    public AdminTrialExtendTests()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, Token);
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _savedToken);
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, _savedHosted);
        _h.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private GatewayDatabase Db => _h.Open(new AsyncLocalTenantContext());

    private static readonly IServiceProvider Services =
        new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();

    private static HttpContext Request(string? bearer)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services };
        if (bearer is not null) ctx.Request.Headers.Authorization = bearer;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    /// <summary>Execute the result and read back exactly the status and JSON the website would receive.</summary>
    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    private static void SeedTrial(GatewayDatabase db, string subject, DateTime started, DateTime expires)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.AccountTrials.Add(new AccountTrialEntity
        {
            Subject = subject, StartedAtUtc = started, ExpiresAtUtc = expires,
        });
        ctx.SaveChanges();
    }

    private static AccountTrialEntity? ReadTrial(GatewayDatabase db, string subject)
    {
        using var ctx = db.CreateUnscopedContext();
        return ctx.AccountTrials.AsNoTracking().FirstOrDefault(t => t.Subject == subject);
    }

    private static AdminTrialEndpoint.ExtendRequest Body(
        DateTime? endsAt, string? subject = Subject, string? actor = "admin@devthrottle.com",
        string? reason = "four weeks promised on 3 August", string? email = "member@example.com")
        => new(subject, endsAt, actor, reason, email);

    private IResult Call(HttpContext ctx, AdminTrialEndpoint.ExtendRequest? body, GatewayDatabase db, bool hosted)
        => AdminTrialEndpoint.Handle(ctx, body, new TrialRegistry(db), Now, hosted);

    // ---- the exemption is deliberate, and it must stay -------------------------------------------------

    [Fact]
    public async Task The_route_is_exempt_from_the_host_wide_token_gate()
    {
        // Not decoration: the website's admin API holds no device key and no shared machine token, so
        // without this exemption every extension is a 401 the endpoint never sees, and the screen reports
        // "could not confirm" forever. Asserted THROUGH the middleware rather than by peeking at the set.
        var cfg = new AuthMiddleware.RequireToken
        {
            Hosted = true,
            Token = "shared-machine-token", Devices = new DeviceRegistry(_devPath),
        };

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = AdminTrialEndpoint.Path;
        ctx.Request.Method = HttpMethods.Post;
        ctx.Response.Body = new MemoryStream();

        var reached = false;
        await AuthMiddleware.Run(ctx, cfg, () => { reached = true; return Task.CompletedTask; });

        Assert.True(reached);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // ---- the gate --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]                                  // no Authorization header at all
    [InlineData("")]                                    // present but empty
    [InlineData("Bearer ")]                             // scheme with no token
    [InlineData("Bearer " + WrongToken)]                // a wrong token of the right shape
    [InlineData(Token)]                                 // the right token with NO scheme
    [InlineData("Basic " + Token)]                      // the right token under the wrong scheme
    public async Task An_unauthorized_caller_is_refused_and_nothing_is_written(string? bearer)
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);

        var ctx = Request(bearer);
        var (status, _) = await ExecuteAsync(Call(ctx, Body(Expires.AddDays(21)), db, hosted: true), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        // The rule that actually matters: a refused caller changed nothing.
        Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
    }

    [Fact]
    public async Task An_unconfigured_service_token_refuses_to_serve_rather_than_serving_unguarded()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, "   ");
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);

        var ctx = Request("Bearer " + Token);
        var (status, _) = await ExecuteAsync(Call(ctx, Body(Expires.AddDays(21)), db, hosted: true), ctx);

        // 503, not 200-with-an-allow-anything-mode. A missing deployment secret is a deployment error.
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
    }

    [Fact]
    public async Task The_read_only_report_token_cannot_hand_out_paid_product()
    {
        // The whole reason this endpoint does not share MorningReportEndpoint's variable. A credential that
        // leaked from a reporting cron must not also be able to give a year of Pro away.
        var savedReport = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);
        try
        {
            const string reportToken = "test-report-token-1a2b";
            Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, reportToken);

            var db = Db;
            SeedTrial(db, Subject, Started, Expires);

            var ctx = Request("Bearer " + reportToken);
            var (status, _) = await ExecuteAsync(Call(ctx, Body(Expires.AddDays(21)), db, hosted: true), ctx);

            Assert.Equal(StatusCodes.Status401Unauthorized, status);
            Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, savedReport);
        }
    }

    [Fact]
    public async Task Two_variable_names_are_not_two_secrets_so_an_equal_pair_refuses_to_serve()
    {
        // The separation from the report token is the REASON this endpoint has its own variable. Naming two
        // settings does not stop a deployment pasting one value into both - and if it did, the read-only
        // reporting credential would silently gain the authority to hand out a year of paid product. So the
        // Gateway checks, and refuses to serve at all rather than serving with the separation quietly gone.
        var savedReport = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);

            var db = Db;
            SeedTrial(db, Subject, Started, Expires);

            // Even the CORRECT admin token is refused while the two are equal - the misconfiguration is the
            // fault, not the caller, and serving anyone at all would leave the hole open.
            var ctx = Request("Bearer " + Token);
            var (status, _) = await ExecuteAsync(Call(ctx, Body(Expires.AddDays(21)), db, hosted: true), ctx);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
            Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, savedReport);
        }
    }

    [Fact]
    public async Task An_unset_report_token_does_not_block_the_admin_endpoint()
    {
        // The negative control for the test above. If the equality check were written so that two BLANKS
        // count as equal, a Gateway with no report token configured would refuse every extension - and the
        // guard would look correct while breaking the feature on exactly the installs that never send
        // reports.
        var savedReport = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, null);

            var db = Db;
            SeedTrial(db, Subject, Started, Expires);

            var ctx = Request("Bearer " + Token);
            var (status, json) = await ExecuteAsync(Call(ctx, Body(Expires.AddDays(21)), db, hosted: true), ctx);

            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Equal(AdminTrialEndpoint.OutcomeExtended, json.GetProperty("outcome").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, savedReport);
        }
    }

    [Fact]
    public async Task A_self_hosted_gateway_refuses_and_does_so_only_after_the_token_check()
    {
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, null);
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);

        // Authorized caller on a self-host install: a distinct refusal, and nothing written.
        var authorized = Request("Bearer " + Token);
        var (status, _) = await ExecuteAsync(Call(authorized, Body(Expires.AddDays(21)), db, hosted: false), authorized);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);

        // UNauthorized caller on the same install still gets 401 - the self-host refusal must not become an
        // oracle telling an anonymous caller which mode this Gateway runs in.
        var anonymous = Request(null);
        var (anonStatus, _) = await ExecuteAsync(Call(anonymous, Body(Expires.AddDays(21)), db, hosted: false), anonymous);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonStatus);
    }

    // ---- the gate runs before the body is read, proven through the REAL pipeline -----------------------

    /// <summary>
    /// The mapped route on a real Kestrel pipeline. Every other test here calls <c>Handle</c> directly,
    /// which is the right tool for the rules but is STRUCTURALLY BLIND to what the delegate does before it -
    /// and "is the caller authorized before we read anything they sent?" is a question only the real
    /// pipeline can answer.
    /// </summary>
    private static async Task<(WebApplication App, HttpClient Http)> StartMappedAsync(TrialRegistry trials)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        AdminTrialEndpoint.Map(app, true, trials, () => Now);
        await app.StartAsync();

        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    [Fact]
    public async Task A_tokenless_caller_is_refused_before_its_body_is_ever_parsed()
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);
        var (app, http) = await StartMappedAsync(new TrialRegistry(db));

        try
        {
            // Malformed JSON with NO credential. If the body were parsed first this answers 400 - telling an
            // anonymous caller something about its INPUT from a route whose own gate is supposed to be the
            // entire boundary between the internet and a year of free product. The only correct answer to a
            // stranger is "who are you?".
            var response = await http.PostAsync(
                AdminTrialEndpoint.Path,
                new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_authorized_caller_still_gets_a_400_for_a_body_that_is_not_json()
    {
        // The negative control for the test above: moving the gate earlier must not have turned every
        // malformed body into a 401, which would make a real client's bug undiagnosable.
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);
        var (app, http) = await StartMappedAsync(new TrialRegistry(db));

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, AdminTrialEndpoint.Path)
            {
                Content = new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Authorization", "Bearer " + Token);

            var response = await http.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_whole_route_applies_an_extension_end_to_end()
    {
        // One pass through the REAL pipeline - routing, the gate, JSON binding, the write, the response - so
        // the wire contract the website depends on is proven rather than assumed from Handle() alone. In
        // particular this is what would catch `ends_at_utc` failing to bind: the host binds property names
        // case-insensitively, which is NOT punctuation-insensitively, and a silently-null required field
        // would otherwise reach an administrator as a malformed request they did not make.
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);
        var (app, http) = await StartMappedAsync(new TrialRegistry(db));
        var later = Expires.AddDays(21);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, AdminTrialEndpoint.Path)
            {
                Content = JsonContent.Create(new
                {
                    subject = Subject,
                    ends_at_utc = later,
                    actor = "admin@devthrottle.com",
                    reason = "four weeks promised on 3 August",
                    member_email = "member@example.com",
                }),
            };
            request.Headers.Add("Authorization", "Bearer " + Token);

            var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(AdminTrialEndpoint.OutcomeExtended, doc.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(later, doc.RootElement.GetProperty("expires_at_utc").GetDateTime());
            Assert.Equal(later, ReadTrial(db, Subject)!.ExpiresAtUtc);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // ---- caller errors are 400s, and they are NOT outcomes ---------------------------------------------

    [Fact]
    public async Task A_missing_field_is_a_bad_request_and_never_an_outcome()
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);
        var later = Expires.AddDays(21);

        // A blank subject reported as "no_trial" would tell an administrator this member has no trial at
        // all. Each of these must be a 400 carrying no outcome field whatsoever.
        AdminTrialEndpoint.ExtendRequest?[] broken =
        [
            null,
            Body(later, subject: null),
            Body(later, subject: "   "),
            Body(endsAt: null),
            Body(later, actor: "  "),
            Body(later, reason: ""),
        ];

        foreach (var body in broken)
        {
            var ctx = Request("Bearer " + Token);
            var (status, json) = await ExecuteAsync(Call(ctx, body, db, hosted: true), ctx);

            Assert.Equal(StatusCodes.Status400BadRequest, status);
            Assert.False(json.TryGetProperty("outcome", out _));
        }

        Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
    }

    // ---- the capability --------------------------------------------------------------------------------

    [Fact]
    public async Task A_later_date_is_applied_and_the_ledger_records_who_and_why()
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);
        var later = Expires.AddDays(21);

        var ctx = Request("Bearer " + Token);
        var (status, json) = await ExecuteAsync(Call(ctx, Body(later), db, hosted: true), ctx);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(AdminTrialEndpoint.OutcomeExtended, json.GetProperty("outcome").GetString());
        Assert.Equal(Expires, json.GetProperty("previous_expires_at_utc").GetDateTime());
        Assert.Equal(later, json.GetProperty("expires_at_utc").GetDateTime());

        // The row actually moved - the answer is not merely well-formed.
        Assert.Equal(later, ReadTrial(db, Subject)!.ExpiresAtUtc);

        // And the ledger row exists, written in the same transaction. An extension with no record of it is
        // free product handed out with nothing saying who did it.
        using var read = db.CreateUnscopedContext();
        var ledger = Assert.Single(read.TrialExtensions.AsNoTracking().Where(e => e.Subject == Subject));
        Assert.Equal(Expires, ledger.PreviousExpiresAtUtc);
        Assert.Equal(later, ledger.NewExpiresAtUtc);
        Assert.Equal(Started, ledger.StartedAtUtc);
        Assert.Equal("admin@devthrottle.com", ledger.Actor);
        Assert.Equal("four weeks promised on 3 August", ledger.Reason);
        Assert.Equal("member@example.com", ledger.MemberEmail);
        Assert.Equal(Now, ledger.RecordedUtc);
    }

    [Theory]
    [InlineData(0)]    // exactly the current end - not an extension
    [InlineData(-1)]   // a day earlier
    [InlineData(-400)] // far earlier
    public async Task A_trial_is_never_shortened_and_the_stored_date_is_untouched(int dayOffset)
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);

        var ctx = Request("Bearer " + Token);
        var (status, json) = await ExecuteAsync(Call(ctx, Body(Expires.AddDays(dayOffset)), db, hosted: true), ctx);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(AdminTrialEndpoint.OutcomeNotLater, json.GetProperty("outcome").GetString());
        // The refusal names what is actually true, so the screen can say it.
        Assert.Equal(Expires, json.GetProperty("expires_at_utc").GetDateTime());

        Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
        using var read = db.CreateUnscopedContext();
        // A refusal writes NO ledger row - an audit naming a change that did not happen is an audit that lies.
        Assert.Empty(read.TrialExtensions.AsNoTracking().Where(e => e.Subject == Subject));
    }

    [Fact]
    public async Task A_date_beyond_the_ceiling_is_refused_so_a_mistyped_year_cannot_give_away_a_decade()
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);

        var ctx = Request("Bearer " + Token);
        var (status, json) = await ExecuteAsync(Call(ctx, Body(Now.AddYears(10)), db, hosted: true), ctx);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(AdminTrialEndpoint.OutcomeTooFar, json.GetProperty("outcome").GetString());
        Assert.Equal(Now + TrialRegistry.MaxExtensionAhead, json.GetProperty("max_expiry_utc").GetDateTime());

        Assert.Equal(Expires, ReadTrial(db, Subject)!.ExpiresAtUtc);
    }

    [Fact]
    public async Task The_ceiling_is_measured_from_now_and_the_boundary_itself_is_allowed()
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);

        // Exactly at the ceiling: allowed. One tick past it: refused. Boundaries are where a policy is
        // decided, so both sides are pinned rather than assumed.
        var atCeiling = Now + TrialRegistry.MaxExtensionAhead;

        var ctxPast = Request("Bearer " + Token);
        var (_, past) = await ExecuteAsync(Call(ctxPast, Body(atCeiling.AddTicks(1)), db, hosted: true), ctxPast);
        Assert.Equal(AdminTrialEndpoint.OutcomeTooFar, past.GetProperty("outcome").GetString());

        var ctxAt = Request("Bearer " + Token);
        var (_, at) = await ExecuteAsync(Call(ctxAt, Body(atCeiling), db, hosted: true), ctxAt);
        Assert.Equal(AdminTrialEndpoint.OutcomeExtended, at.GetProperty("outcome").GetString());
        Assert.Equal(atCeiling, ReadTrial(db, Subject)!.ExpiresAtUtc);
    }

    [Fact]
    public async Task An_account_with_no_trial_is_reported_as_having_none_and_no_trial_is_invented()
    {
        var db = Db;   // nothing seeded

        var ctx = Request("Bearer " + Token);
        var (status, json) = await ExecuteAsync(Call(ctx, Body(Now.AddDays(30)), db, hosted: true), ctx);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal(AdminTrialEndpoint.OutcomeNoTrial, json.GetProperty("outcome").GetString());

        // Granting a trial to somebody who never had one is a DIFFERENT decision and does not belong behind
        // this button - the one-trial-per-account-ever rule rests on it.
        Assert.Null(ReadTrial(db, Subject));
    }

    [Fact]
    public async Task It_reaches_only_the_account_named()
    {
        const string other = "auth0|trial-subject-2";
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);
        SeedTrial(db, other, Started, Expires);

        var ctx = Request("Bearer " + Token);
        await ExecuteAsync(Call(ctx, Body(Expires.AddDays(21)), db, hosted: true), ctx);

        Assert.Equal(Expires.AddDays(21), ReadTrial(db, Subject)!.ExpiresAtUtc);
        // The other account is untouched: the only input is one subject, matched against the primary key.
        Assert.Equal(Expires, ReadTrial(db, other)!.ExpiresAtUtc);
    }

    [Fact]
    public async Task A_second_extension_of_the_same_account_is_allowed_and_each_is_its_own_ledger_row()
    {
        var db = Db;
        SeedTrial(db, Subject, Started, Expires);

        var first = Expires.AddDays(14);
        var ctx1 = Request("Bearer " + Token);
        await ExecuteAsync(Call(ctx1, Body(first, reason: "first goodwill window"), db, hosted: true), ctx1);

        var second = Expires.AddDays(28);
        var ctx2 = Request("Bearer " + Token);
        var (_, json) = await ExecuteAsync(Call(ctx2, Body(second, reason: "extended again"), db, hosted: true), ctx2);

        Assert.Equal(AdminTrialEndpoint.OutcomeExtended, json.GetProperty("outcome").GetString());
        // The second refusal boundary moved with the row: "later" is judged against what is stored NOW.
        Assert.Equal(first, json.GetProperty("previous_expires_at_utc").GetDateTime());
        Assert.Equal(second, ReadTrial(db, Subject)!.ExpiresAtUtc);

        using var read = db.CreateUnscopedContext();
        // Two decisions, two records. The ledger is keyed on its own id, not on the subject, precisely so
        // the second decision cannot overwrite the first one's evidence.
        Assert.Equal(2, read.TrialExtensions.AsNoTracking().Count(e => e.Subject == Subject));
    }

    // ---- the never-extend rule the automatic path still holds ------------------------------------------

    [Fact]
    public void The_enrolment_path_still_never_extends_or_re_grants()
    {
        // The capability above is a HUMAN one. If adding it had loosened GrantIfFirstArrival, a member could
        // restart their free window by re-enrolling - which is the rule the whole trial design rests on.
        var db = Db;
        var expired = Now.AddDays(-1);
        SeedTrial(db, Subject, Started, expired);

        var registry = new TrialRegistry(db);
        var decision = registry.GrantIfFirstArrival(Subject, alreadyKnownToGateway: false, Now);

        Assert.Equal(TrialOutcome.None, decision.Outcome);
        Assert.Equal(expired, ReadTrial(db, Subject)!.ExpiresAtUtc);
    }

    // ---- caller errors at the capability, not just at the endpoint -------------------------------------

    [Theory]
    [InlineData("", "actor", "reason")]
    [InlineData("subject", "  ", "reason")]
    [InlineData("subject", "actor", "")]
    public void The_capability_itself_refuses_a_blank_subject_actor_or_reason(string subject, string actor, string reason)
    {
        // Enforced by the CAPABILITY, not merely by the endpoint: a rule held only at one caller is held
        // only until the next one.
        var registry = new TrialRegistry(Db);
        Assert.Throws<ArgumentException>(() =>
            registry.ExtendIfLater(subject, Now.AddDays(30), actor, reason, memberEmail: null, Now));
    }
}
