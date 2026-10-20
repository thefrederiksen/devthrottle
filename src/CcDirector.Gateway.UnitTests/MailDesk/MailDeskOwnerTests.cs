using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// DONE, RETURN AND THE OWNER (Mail Desk mission, phase 4 contract, section 4, task C). Every test of section 4.4 that
/// the Gateway answers, over the real routes booted on an in-memory test server exactly as GatewayHost maps them, the
/// real <see cref="MailDeskRoutedStore"/>, the real <see cref="MailDeskRouter"/> (task B's rig, with the starter and
/// the session list as fakes and an injected clock), the real problems fold through <see cref="CronRunResultService"/>,
/// and the real 08:00 fold and composer. A stand-in for the authentication middleware puts on each request the
/// identity the real one would: a person's device, a session's key, a Director's device, or the machine token.
///
/// Each refusal compares every routed row and every decision before and after: NOTHING IS RECORDED ON A REFUSAL.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskOwnerTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Person = "person";
    private const string MachineToken = "machine";
    private const string Director = "director";
    private const string Mailbox = "email@devthrottle.com";
    private const string OtherSession = "75000000-0000-4000-8000-000000000009";
    private const string P = MailDeskGate.Prefix;
    private static readonly TenantId Account = MailDeskRoutingRig.Account;

    private readonly MailDeskRoutingRig _rig = new();
    private WebApplication? _app;
    private HttpClient _client = null!;
    private CronRunResultService _results = null!;
    private MailDeskChannelEntity _channel = null!;

    public async Task InitializeAsync()
    {
        _rig.AddMailDeskTrigger();
        _channel = _rig.Channel(Mailbox);
        var settings = new MailDeskSettingsStore(_rig.Db);
        var checks = new MailDeskChannelCheckStore(_rig.Db);
        MailDeskSpend SpendOf(TenantId t) => MailDeskSpend.Of(t, _rig.Decisions, settings);
        var runs = new CronRunHistoryStore(_rig.Db, Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid().ToString("N") + ".runs.json"));
        _results = new CronRunResultService(runs,
            jobById: _ => null,
            allJobs: () => Array.Empty<CronJobDto>(),
            endingsOf: _ => new Dictionary<string, SessionEndingFact>(StringComparer.Ordinal),
            zoneOf: _ => TimeZoneInfo.Utc,
            writeActivity: (tenant, run, row, correction) => runs.WriteActivityRow(tenant, run, row, correction),
            nowUtc: () => _rig.Now,
            triggers: _rig.TriggerStore,
            scheduleSessionsByName: (_, _, _) => Array.Empty<string>(),
            settleLastStatus: (_, _, _) => false,
            mailDesk: new MailDeskProblemSource(_rig.Switch.IsOn, _rig.Channels, checks, SpendOf, _rig.Routed));

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            StandInForAuthentication(ctx);
            await next();
        });
        var parts = new MailDeskEndpoints.Parts(_rig.Channels, _rig.Rules, _rig.Decisions, settings,
            new MailDeskChannelFold(_ => Array.Empty<TriggerEntity>(), _ => "UTC", _rig.Decisions, checks),
            _ => SessionFactoryLookup.InNoFactory, _ => Array.Empty<string>(), () => _rig.Now, SpendOf, _rig.Routed);
        MailDeskEndpoints.Map(MailDeskGate.Group(app, _rig.Switch, Resolve), Resolve, parts);
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        await _rig.DisposeAsync();
    }

    // ---------- who is asking ----------

    private static readonly Func<HttpContext, TenantId?> Resolve = ctx =>
        ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && !string.IsNullOrWhiteSpace(v) ? new TenantId(v.ToString()) : null;

    private static void StandInForAuthentication(HttpContext ctx)
    {
        var caller = ctx.Request.Headers[CallerHeader].ToString();
        var tenant = Resolve(ctx) ?? Account;
        if (caller == Person)
        {
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("owner-browser-1", tenant.Value, "browser", "active");
        }
        else if (caller == Director)
        {
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "director";
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("director-a", tenant.Value, "director", "active");
        }
        else if (caller == MachineToken)
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
        }
        else if (Guid.TryParse(caller, out var session))
        {
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(session, tenant, MailDeskRoutingRig.DirectorA);
        }
    }

    // ---------- calling ----------

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(string method, string path, string caller, string? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TenantHeader, Account.Value);
        request.Headers.Add(CallerHeader, caller);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Done(Guid id, string caller, string? note = null)
        => Send("POST", $"{P}/routed/{id:D}/done", caller, note is null ? null : JsonSerializer.Serialize(new { note }));

    private Task<(HttpStatusCode Status, JsonElement Body)> Return(Guid id, string caller, string? note)
        => Send("POST", $"{P}/routed/{id:D}/return", caller, JsonSerializer.Serialize(new { note }));

    private Task<(HttpStatusCode Status, JsonElement Body)> List(string query = "")
        => Send("GET", $"{P}/routed{query}", Person);

    /// <summary>
    /// EVERY column of every routed row, and every decision's outcome, as one text: a refusal leaves it as it was. Read
    /// by reflection over the entity, so a column added later is compared too (review of 4278, finding 2).
    /// </summary>
    private string Recorded()
    {
        using var ctx = _rig.Db.CreateContext(Account);
        var columns = typeof(MailDeskRoutedEntity).GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        Assert.Contains(columns, p => p.Name == nameof(MailDeskRoutedEntity.EarlierReturnNote));
        var routed = ctx.MailDeskRouted.AsNoTracking().OrderBy(r => r.Id).ToList()
            .Select(r => string.Join("|", columns.Select(p => $"{p.Name}={Text(p.GetValue(r))}")));
        var decisions = ctx.MailDeskDecisions.AsNoTracking().OrderBy(d => d.Id).ToList()
            .Select(d => $"{d.Id}|{d.Outcome}|{d.OutcomeDetail}|{Text(d.OutcomeUtc)}");
        return string.Join("\n", routed.Concat(decisions));

        static string Text(object? value) => value switch
        {
            null => "null",
            DateTime t => t.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }

    private void AssertRefused((HttpStatusCode Status, JsonElement Body) answer, HttpStatusCode status, string error, string before)
    {
        Assert.Equal(status, answer.Status);
        Assert.Equal(error, answer.Body.GetProperty("error").GetString());
        Assert.Equal(before, Recorded());
    }

    /// <summary>A live route to qa-books/clerk, routed and started: the row and the session it is with.</summary>
    private async Task<(MailDeskRoutedEntity Row, string Session)> RoutedToClerk(string key = "m1", string subject = "An invoice")
    {
        var decision = _rig.Decide(_channel, key, MailDeskWords.Decisions.Route, MailDeskRoutingRig.Books, MailDeskRoutingRig.Clerk, subject: subject);
        await _rig.RouteAsync();
        var row = _rig.Row(decision.Id);
        Assert.NotNull(row.SessionId);
        _rig.SessionIs(row.SessionId!, alive: true);
        return (row, row.SessionId!);
    }

    // ======================================================================================================
    // 4.4.1 - done by the holding session; everyone else refused
    // ======================================================================================================

    [Fact]
    public async Task Done_TheHoldingSession_AnswersTheDoneRowWithItsNoteAndLabel()
    {
        // Arrange
        var (row, session) = await RoutedToClerk();
        _rig.Now = _rig.Now.AddMinutes(5);

        // Act
        var answer = await Done(row.Id, session, "paid it");

        // Assert
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        var body = answer.Body;
        Assert.Equal(row.Id.ToString("D"), body.GetProperty("id").GetString());
        Assert.Equal(MailDeskWords.RoutedStates.Done, body.GetProperty("state").GetString());
        Assert.Equal("Done", body.GetProperty("stateLabel").GetString());
        Assert.Equal("paid it", body.GetProperty("doneNote").GetString());
        Assert.Equal(Mailbox, body.GetProperty("channel").GetString());
        Assert.Equal("An invoice", body.GetProperty("subject").GetString());
        Assert.Equal("someone@example.net", body.GetProperty("sender").GetString());
        Assert.False(body.GetProperty("queued").GetBoolean());
        Assert.Equal(MailDeskWords.RoutedStates.Done, _rig.Row(row.DecisionId).State);
    }

    [Fact]
    public async Task Done_NoBodyAtAll_IsDoneWithNoNote()
    {
        var (row, session) = await RoutedToClerk();

        var answer = await Send("POST", $"{P}/routed/{row.Id:D}/done", session);

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(JsonValueKind.Null, answer.Body.GetProperty("doneNote").ValueKind);
    }

    [Theory]
    [InlineData(OtherSession)]
    [InlineData(Person)]
    [InlineData(MachineToken)]
    [InlineData(Director)]
    public async Task Done_AnyoneButTheHoldingSession_Is403NotYourEmailNamingWhoHoldsItAndRecordsNothing(string caller)
    {
        // Arrange
        var (row, session) = await RoutedToClerk();
        var before = Recorded();

        // Act
        var answer = await Done(row.Id, caller, "not mine to say");

        // Assert
        AssertRefused(answer, HttpStatusCode.Forbidden, "not_your_email", before);
        Assert.Contains(session, answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Done_APreviousHolderAfterAReroute_Is403AndRecordsNothing()
    {
        // Arrange - the first session ended without done or return; the next run gives the email to a new session
        var (row, first) = await RoutedToClerk();
        _rig.SessionIs(first, alive: false);
        _rig.Now = _rig.Now.AddMinutes(10);
        await _rig.RouteAsync();
        var second = _rig.Row(row.DecisionId).SessionId;
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        var before = Recorded();

        // Act
        var answer = await Done(row.Id, first);

        // Assert
        AssertRefused(answer, HttpStatusCode.Forbidden, "not_your_email", before);
        Assert.Equal(HttpStatusCode.OK, (await Done(row.Id, second!)).Status);
    }

    [Fact]
    public async Task Done_ARowAlreadyDone_Is409NotOpenAndRecordsNothing()
    {
        var (row, session) = await RoutedToClerk();
        Assert.Equal(HttpStatusCode.OK, (await Done(row.Id, session)).Status);
        var before = Recorded();

        var answer = await Done(row.Id, session);

        AssertRefused(answer, HttpStatusCode.Conflict, "not_open", before);
        Assert.Equal("This email is already done.", answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Done_NoSuchRow_Is404AndRecordsNothing()
    {
        await RoutedToClerk();
        var before = Recorded();

        AssertRefused(await Done(Guid.NewGuid(), OtherSession), HttpStatusCode.NotFound, "no_such_routed_email", before);
        AssertRefused(await Send("POST", $"{P}/routed/not-an-id/done", OtherSession), HttpStatusCode.NotFound, "no_such_routed_email", before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Return_WithoutANote_Is400NoteRequiredAndRecordsNothing(string? note)
    {
        var (row, session) = await RoutedToClerk();
        var before = Recorded();

        AssertRefused(await Return(row.Id, session, note), HttpStatusCode.BadRequest, "note_required", before);
        AssertRefused(await Send("POST", $"{P}/routed/{row.Id:D}/return", session), HttpStatusCode.BadRequest, "note_required", before);
    }

    [Fact]
    public async Task Return_AnotherSession_Is403AndRecordsNothing()
    {
        var (row, _) = await RoutedToClerk();
        var before = Recorded();

        AssertRefused(await Return(row.Id, OtherSession, "it is not for books"), HttpStatusCode.Forbidden, "not_your_email", before);
    }

    // ======================================================================================================
    // 4.4.2 - return: re-queued to the Mail Desk seat, then handed to the owner
    // ======================================================================================================

    [Fact]
    public async Task Return_FirstTime_QueuesItForTheMailDeskSeatAndTheNextRunStartsTheMailDeskAgentWithTheNote()
    {
        // Arrange
        var (row, clerk) = await RoutedToClerk();
        _rig.Now = _rig.Now.AddMinutes(5);

        // Act - the clerk returns it; the next screened run routes it
        var answer = await Return(row.Id, clerk, "this is a support question, not an invoice");
        _rig.Now = _rig.Now.AddMinutes(5);
        await _rig.RouteAsync();

        // Assert - the answer: queued for the Mail Desk seat, with the note
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(MailDeskWords.RoutedStates.Open, answer.Body.GetProperty("state").GetString());
        Assert.True(answer.Body.GetProperty("queued").GetBoolean());
        Assert.Equal(MailDeskWords.DefaultFactory, answer.Body.GetProperty("factory").GetString());
        Assert.Equal(MailDeskWords.DefaultSeat, answer.Body.GetProperty("seat").GetString());
        Assert.Equal(1, answer.Body.GetProperty("returnedCount").GetInt32());
        Assert.Equal($"Queued - waiting for {MailDeskWords.DefaultFactory}/{MailDeskWords.DefaultSeat}", answer.Body.GetProperty("stateLabel").GetString());
        // ...and the Mail Desk agent was started with the note in its prompt and in check.json
        var start = _rig.Starts.Last();
        Assert.Equal(MailDeskWords.DefaultFactory, start.Request.Factory);
        Assert.Contains("Returned before: this is a support question, not an invoice", start.Request.PrePrompt);
        var email = JsonNode.Parse(start.Request.FactoryCheck!.Context!)!["emails"]![0]!;
        Assert.Equal(row.Id.ToString("D"), email["routedId"]!.GetValue<string>());
        Assert.Equal("this is a support question, not an invoice", email["returnNote"]!.GetValue<string>());
        var now = _rig.Row(row.DecisionId);
        Assert.NotNull(now.SessionId);
        Assert.NotEqual(clerk, now.SessionId);
        Assert.Equal($"{MailDeskRoutingRig.Books}/{MailDeskRoutingRig.Clerk}", now.ReturnedFrom);
    }

    [Fact]
    public async Task Return_ASecondTime_HandsItToTheOwnerReturnedTwice()
    {
        // Arrange - a first return sends the email to the Mail Desk seat, and a return from there is the Mail Desk
        // agent's. A SECOND return from a seat that is not the Mail Desk's happens when the row is somewhere else by then;
        // the row is moved by hand to stand in for that.
        var decision = _rig.Decide(_channel, "m2", MailDeskWords.Decisions.Route, MailDeskRoutingRig.Books, MailDeskRoutingRig.Clerk);
        await _rig.RouteAsync();
        var row = _rig.Row(decision.Id);
        Assert.Equal(HttpStatusCode.OK, (await Return(row.Id, row.SessionId!, "first: not mine")).Status);
        using (var ctx = _rig.Db.CreateContext(Account))
        {
            var r = ctx.MailDeskRouted.Single(x => x.Id == row.Id);
            r.Factory = MailDeskRoutingRig.Support;
            r.Seat = MailDeskRoutingRig.Desk;
            ctx.SaveChanges();
        }
        _rig.Now = _rig.Now.AddMinutes(5);
        await _rig.RouteAsync();
        var holder = _rig.Row(decision.Id).SessionId!;

        // Act
        var answer = await Return(row.Id, holder, "second: not mine either");

        // Assert
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(MailDeskWords.RoutedStates.Returned, answer.Body.GetProperty("state").GetString());
        Assert.Equal(2, answer.Body.GetProperty("returnedCount").GetInt32());
        Assert.Equal("returned twice: second: not mine either", answer.Body.GetProperty("ownerReason").GetString());
        Assert.Equal("With you - returned twice: second: not mine either", answer.Body.GetProperty("stateLabel").GetString());
        // Both returns are kept on the row, for the 08:00 email.
        var kept = _rig.Row(decision.Id);
        Assert.Equal(($"{MailDeskRoutingRig.Books}/{MailDeskRoutingRig.Clerk}", "first: not mine"), (kept.EarlierReturnedFrom, kept.EarlierReturnNote));
        Assert.Equal(($"{MailDeskRoutingRig.Support}/{MailDeskRoutingRig.Desk}", "second: not mine either"), (kept.ReturnedFrom, kept.ReturnNote));
    }

    [Fact]
    public async Task MorningSection_AnEmailReturnedTwice_ListsBothReturnsOldestFirst()
    {
        // Arrange - returned by the clerk, then by the Mail Desk agent (which hands it to the owner)
        var dayStart = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
        var (row, clerk) = await RoutedToClerk("t1", "Twice returned");
        Assert.Equal(HttpStatusCode.OK, (await Return(row.Id, clerk, "not an invoice")).Status);
        _rig.Now = _rig.Now.AddMinutes(5);
        await _rig.RouteAsync();
        Assert.Equal(HttpStatusCode.OK, (await Return(row.Id, _rig.Row(row.DecisionId).SessionId!, "no seat fits")).Status);
        var fold = new MailDeskEmailFold(_rig.Switch.IsOn, _rig.Channels, _rig.Decisions, _rig.Rules,
            t => MailDeskSpend.Of(t, _rig.Decisions, new MailDeskSettingsStore(_rig.Db)), _rig.Routed);

        // Act
        var part = fold.Section(Account, dayStart, dayStart.AddHours(24), TimeZoneInfo.Utc)!.ReturnedAndRouted;

        // Assert
        Assert.Equal(new[]
            {
                ($"{MailDeskRoutingRig.Books}/{MailDeskRoutingRig.Clerk}", "not an invoice"),
                ($"{MailDeskWords.DefaultFactory}/{MailDeskWords.DefaultSeat}", "no seat fits"),
            },
            part.Returned.Select(r => (r.ReturnedBy, r.Note)).ToArray());
        Assert.All(part.Returned, r => Assert.Equal("Twice returned", r.Subject));
        Assert.Equal("the Mail Desk agent returned it: no seat fits", Assert.Single(part.HandedToOwner).Reason);
    }

    // ---------- the counts are per seat (review of 4278, finding 1) ----------

    [Fact]
    public async Task Return_AfterTheFirstSeatFailedTwice_OneFailedMailDeskStartIsNotHandedToTheOwner()
    {
        // Arrange - the clerk's machine refuses two starts, the third works
        var decision = _rig.Decide(_channel, "c1", MailDeskWords.Decisions.Route, MailDeskRoutingRig.Books, MailDeskRoutingRig.Clerk);
        _rig.StartAnswer = _ => TriggerStartAttempt.Failed("the machine refused");
        await _rig.RouteAsync();
        await _rig.RouteAsync();
        Assert.Equal(2, _rig.Row(decision.Id).StartFailures);
        _rig.StartAnswer = _ => TriggerStartAttempt.Started(_rig.NextSessionId());
        await _rig.RouteAsync();
        var clerk = _rig.Row(decision.Id).SessionId!;
        Assert.Equal(MailDeskWords.Outcomes.SeatStarted, _rig.DecisionRow(decision.Id).Outcome);

        // Act - the clerk returns it, and the Mail Desk agent's first start fails once
        var answer = await Return(_rig.Row(decision.Id).Id, clerk, "not an invoice");
        _rig.StartAnswer = _ => TriggerStartAttempt.Failed("the machine is away");
        await _rig.RouteAsync();

        // Assert - the Mail Desk seat has had one failed start of its own, and the email waits for it
        Assert.Equal(0, answer.Body.GetProperty("startFailures").GetInt32());
        var row = _rig.Row(decision.Id);
        Assert.Equal((MailDeskWords.RoutedStates.Open, MailDeskWords.DefaultSeat, 1), (row.State, row.Seat, row.StartFailures));
        Assert.Equal(MailDeskWords.Outcomes.SeatStarted, _rig.DecisionRow(decision.Id).Outcome);
    }

    [Fact]
    public async Task Return_ThenThreeFailedMailDeskStarts_HandsItToTheOwnerButKeepsTheSeatStartedOutcome()
    {
        // Arrange - the clerk had it, and returned it
        var (row, clerk) = await RoutedToClerk("c2");
        var outcomeBefore = _rig.DecisionRow(row.DecisionId).OutcomeDetail;
        Assert.Equal(HttpStatusCode.OK, (await Return(row.Id, clerk, "not an invoice")).Status);

        // Act - the Mail Desk seat cannot be started three times
        _rig.StartAnswer = _ => TriggerStartAttempt.Failed("the machine is away");
        for (var i = 0; i < MailDeskRoutedStore.StartFailuresBeforeOwner; i++) await _rig.RouteAsync();

        // Assert - with the owner, but the record still says a seat had it
        var now = _rig.Row(row.DecisionId);
        Assert.Equal(MailDeskWords.RoutedStates.Returned, now.State);
        Assert.StartsWith("its session could not be started 3 times", now.OwnerReason);
        var decision = _rig.DecisionRow(row.DecisionId);
        Assert.Equal((MailDeskWords.Outcomes.SeatStarted, outcomeBefore), (decision.Outcome, decision.OutcomeDetail));
    }

    [Fact]
    public async Task Return_AfterTheFirstSeatWasReroutedOnce_TheMailDeskAgentGetsItsOwnReroute()
    {
        // Arrange - the clerk's first session ends without answering; the second clerk session returns it
        var (row, first) = await RoutedToClerk("c3");
        _rig.SessionIs(first, alive: false);
        _rig.Now = _rig.Now.AddMinutes(10);
        await _rig.RouteAsync();
        var second = _rig.Row(row.DecisionId).SessionId!;
        Assert.Equal(1, _rig.Row(row.DecisionId).RerouteCount);
        Assert.Equal(HttpStatusCode.OK, (await Return(row.Id, second, "not an invoice")).Status);
        _rig.Now = _rig.Now.AddMinutes(5);
        await _rig.RouteAsync();
        var agent = _rig.Row(row.DecisionId).SessionId!;

        // Act - the Mail Desk agent's session ends without answering
        _rig.SessionIs(agent, alive: false);
        _rig.Now = _rig.Now.AddMinutes(10);
        await _rig.RouteAsync();

        // Assert - re-routed once at the Mail Desk seat, not handed to the owner
        var now = _rig.Row(row.DecisionId);
        Assert.Equal((MailDeskWords.RoutedStates.Open, MailDeskWords.DefaultSeat, 1), (now.State, now.Seat, now.RerouteCount));
        Assert.NotNull(now.SessionId);
        Assert.NotEqual(agent, now.SessionId);
    }

    [Fact]
    public async Task Return_ByTheMailDeskAgent_HandsItToTheOwner()
    {
        // Arrange - an email the model was unsure of goes to the Mail Desk seat itself
        var decision = _rig.Decide(_channel, "m3", MailDeskWords.Decisions.MailDeskAgent);
        await _rig.RouteAsync();
        var row = _rig.Row(decision.Id);

        // Act
        var answer = await Return(row.Id, row.SessionId!, "I cannot tell who this is for");

        // Assert
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(MailDeskWords.RoutedStates.Returned, answer.Body.GetProperty("state").GetString());
        Assert.Equal("the Mail Desk agent returned it: I cannot tell who this is for", answer.Body.GetProperty("ownerReason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Done(row.Id, row.SessionId!)).Status);
    }

    // ======================================================================================================
    // 4.4.3 - the owner's problem: on the factory's Open problems and in the 08:00 email; resolved by hand
    // ======================================================================================================

    private async Task<MailDeskRoutedEntity> HandedToOwner()
    {
        var decision = _rig.Decide(_channel, "m4", MailDeskWords.Decisions.MailDeskAgent, subject: "Who is this for?");
        await _rig.RouteAsync();
        var row = _rig.Row(decision.Id);
        _rig.Now = _rig.Now.AddMinutes(1);
        Assert.Equal(HttpStatusCode.OK, (await Return(row.Id, row.SessionId!, "no seat fits")).Status);
        return _rig.Row(decision.Id);
    }

    [Fact]
    public async Task Problems_AnEmailHandedToTheOwner_IsOneOpenProblemOnTheMailDeskFactoryInTheContractsWords()
    {
        // Arrange
        var row = await HandedToOwner();
        _rig.Now = _rig.Now.AddHours(1);

        // Act - the Open problems card is the list narrowed to the factory
        var problem = Assert.Single(_results.Problems(Account, MailDeskWords.DefaultFactory, includeClosed: false).Problems);

        // Assert
        Assert.Equal(CronRunProblems.MailHandedToOwner, problem.Kind);
        Assert.Equal("email handed to you", problem.KindText);
        Assert.Equal(row.Id.ToString("D"), problem.RunId);
        Assert.Equal($"someone@example.net - Who is this for? on {Mailbox} is with you: the Mail Desk agent returned it: no seat fits", problem.Sentence);
        Assert.Equal(MailDeskWords.DefaultFactory, problem.Factory);
        Assert.Equal(CronRunProblemStates.Open, problem.State);
        Assert.Equal(row.OwnerUtc, problem.RaisedAtUtc);
        Assert.Equal("open 1 hour", problem.OpenForText);
    }

    [Fact]
    public async Task Problems_InTheMorningEmail_TheProblemIsListedWithNoChangeOfItsOwn()
    {
        // Arrange
        await HandedToOwner();
        _rig.Now = new DateTime(2026, 10, 20, 8, 5, 0, DateTimeKind.Utc);
        var slot = FactoryEmailSlots.DueAt(_rig.Now, TimeZoneInfo.Utc)!;

        // Act - exactly what the sweep composes from
        var (problems, days) = _results.ProblemsAndFactoryDays(Account, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, TimeZoneInfo.Utc, problems.Problems, days,
            new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase), BaseUrl: null));

        // Assert
        Assert.True(email.Send);
        Assert.Equal(1, email.ProblemCount);
        var sentence = $"someone@example.net - Who is this for? on {Mailbox} is with you: the Mail Desk agent returned it: no seat fits";
        Assert.Contains(FactoryEmailComposer.E(sentence), email.Html);
        Assert.Contains(sentence, email.Text);
    }

    [Fact]
    public async Task Resolve_TheProblemByHand_ClearsItFromTheOpenListAndKeepsWhoAndWhy()
    {
        // Arrange
        var row = await HandedToOwner();
        _rig.Now = _rig.Now.AddMinutes(30);

        // Act
        var outcome = _results.Resolve(Account, row.Id.ToString("D"), CronRunResultService.ResolvedByYou, "answered it myself");

        // Assert
        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal(CronRunProblemStates.Resolved, outcome.Answer!.State);
        Assert.Equal("answered it myself", outcome.Answer.ResolvedReason);
        Assert.Empty(_results.Problems(Account, null, includeClosed: false).Problems);
        var closed = Assert.Single(_results.Problems(Account, null, includeClosed: true).Problems);
        Assert.Equal($"resolved by {CronRunResultService.ResolvedByYou}: answered it myself", closed.StateText);
        Assert.Equal(MailDeskWords.RoutedStates.Returned, _rig.Row(row.DecisionId).State);
        // A second resolve is refused, and so is a resolve of an email that is not with the owner.
        Assert.Equal(409, _results.Resolve(Account, row.Id.ToString("D"), "you", "again").StatusCode);
    }

    [Fact]
    public async Task Resolve_AnEmailThatIsNotWithTheOwner_IsRefused409AndWritesNothing()
    {
        var (row, _) = await RoutedToClerk();
        var before = Recorded();

        var outcome = _results.Resolve(Account, row.Id.ToString("D"), "you", "not a problem");

        Assert.Equal(409, outcome.StatusCode);
        Assert.Contains("is not with you", outcome.Refusal);
        Assert.Equal(before, Recorded());
    }

    [Fact]
    public async Task Problems_SwitchOff_ListsNoOwnerProblem()
    {
        await HandedToOwner();
        _rig.Switch.Set(Account, false, "email@devthrottle.com", "switched off for the test", _rig.Now);

        Assert.Empty(_results.Problems(Account, null, includeClosed: true).Problems);
    }

    // ---------- the 08:00 section's "Returned and routed" part ----------

    [Fact]
    public async Task MorningSection_ReturnedAndRouted_ListsEveryReturnAndEveryHandAndCountsRoutedDoneAndOpen()
    {
        // Arrange - in one day: three routed; one done, one returned by the clerk (now open again at the Mail Desk
        // seat), one handed to the owner by the Mail Desk agent
        var dayStart = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
        var (done, doneSession) = await RoutedToClerk("d1", "Invoice one");
        Assert.Equal(HttpStatusCode.OK, (await Done(done.Id, doneSession, "paid")).Status);
        var returned = _rig.Decide(_channel, "d2", MailDeskWords.Decisions.Route, MailDeskRoutingRig.Support, MailDeskRoutingRig.Desk, subject: "Help please");
        await _rig.RouteAsync();
        var returnedRow = _rig.Row(returned.Id);
        Assert.Equal(HttpStatusCode.OK, (await Return(returnedRow.Id, returnedRow.SessionId!, "this is an invoice")).Status);
        await HandedToOwner();
        var fold = new MailDeskEmailFold(_rig.Switch.IsOn, _rig.Channels, _rig.Decisions, _rig.Rules,
            t => MailDeskSpend.Of(t, _rig.Decisions, new MailDeskSettingsStore(_rig.Db)), _rig.Routed);

        // Act
        var section = fold.Section(Account, dayStart, dayStart.AddHours(24), TimeZoneInfo.Utc)!;
        var part = section.ReturnedAndRouted;
        var slot = FactoryEmailSlots.DueAt(dayStart.AddHours(24).AddMinutes(5), TimeZoneInfo.Utc)!;
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, TimeZoneInfo.Utc, Array.Empty<CronRunProblemDto>(),
            new[] { new CronFactoryDayDto { Factory = "mail-desk", Done = 1 } },
            new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase), BaseUrl: null, section));

        // Assert - the fold
        Assert.Equal(3, part.Routed);
        Assert.Equal(1, part.Done);
        Assert.Equal(1, part.StillOpen);
        Assert.Equal("Routed 3 - Done 1 - Still open 1", part.CountsLine);
        Assert.Equal(2, part.Returned.Count);
        var byDesk = part.Returned[0];
        Assert.Equal((Mailbox, "someone@example.net", "Help please", $"{MailDeskRoutingRig.Support}/{MailDeskRoutingRig.Desk}", "this is an invoice"),
            (byDesk.Mailbox, byDesk.Sender, byDesk.Subject, byDesk.ReturnedBy, byDesk.Note));
        Assert.Equal($"{MailDeskWords.DefaultFactory}/{MailDeskWords.DefaultSeat}", part.Returned[1].ReturnedBy);
        var handed = Assert.Single(part.HandedToOwner);
        Assert.Equal("Who is this for?", handed.Subject);
        Assert.Equal("the Mail Desk agent returned it: no seat fits", handed.Reason);
        // ...and the email lays it out as folded
        Assert.Contains(MailDeskRoutedFold.ReturnedAndRoutedHeading, email.Html);
        Assert.Contains("Routed 3 - Done 1 - Still open 1", email.Html);
        Assert.Contains(FactoryEmailComposer.E($"{Mailbox} - returned by {MailDeskRoutingRig.Support}/{MailDeskRoutingRig.Desk}: this is an invoice"), email.Html);
        Assert.Contains("Handed to you (1)", email.Html);
        Assert.Contains($"{MailDeskRoutedFold.ReturnedAndRoutedHeading}: Routed 3 - Done 1 - Still open 1", email.Text);
        Assert.Contains($"  {Mailbox} - the Mail Desk agent returned it: no seat fits", email.Text);
    }

    [Fact]
    public void MorningSection_NothingRouted_SaysNoneAndZeros()
    {
        var dayStart = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
        var fold = new MailDeskEmailFold(_rig.Switch.IsOn, _rig.Channels, _rig.Decisions, _rig.Rules,
            t => MailDeskSpend.Of(t, _rig.Decisions, new MailDeskSettingsStore(_rig.Db)), _rig.Routed);

        var part = fold.Section(Account, dayStart, dayStart.AddHours(24), TimeZoneInfo.Utc)!.ReturnedAndRouted;

        Assert.Empty(part.Returned);
        Assert.Empty(part.HandedToOwner);
        Assert.Equal("Routed 0 - Done 0 - Still open 0", part.CountsLine);
    }

    // ======================================================================================================
    // 4.4.4 - every filter of GET /routed; the switch off answers 404
    // ======================================================================================================

    [Fact]
    public async Task List_EveryFilter_AppliesNewestFirstWithTheTotal()
    {
        // Arrange - one done (books/clerk), one open with a session (support/desk), one queued behind a busy seat
        // (support/desk), one with the owner (mail-desk/mail-desk)
        var (done, doneSession) = await RoutedToClerk("f1");
        Assert.Equal(HttpStatusCode.OK, (await Done(done.Id, doneSession)).Status);
        _rig.Now = _rig.Now.AddMinutes(1);
        var open = _rig.Decide(_channel, "f2", MailDeskWords.Decisions.Route, MailDeskRoutingRig.Support, MailDeskRoutingRig.Desk);
        await _rig.RouteAsync();
        _rig.SessionIs(_rig.Row(open.Id).SessionId!, alive: true);
        _rig.Now = _rig.Now.AddMinutes(1);
        var queued = _rig.Decide(_channel, "f3", MailDeskWords.Decisions.Route, MailDeskRoutingRig.Support, MailDeskRoutingRig.Desk);
        await _rig.RouteAsync();
        Assert.Null(_rig.Row(queued.Id).SessionId);
        _rig.Now = _rig.Now.AddMinutes(1);
        var owner = await HandedToOwner();

        // Act + Assert
        var all = await List();
        Assert.Equal(HttpStatusCode.OK, all.Status);
        Assert.Equal(4, all.Body.GetProperty("count").GetInt32());
        Assert.Equal(4, all.Body.GetProperty("total").GetInt32());
        Assert.Equal(owner.Id.ToString("D"), all.Body.GetProperty("routed")[0].GetProperty("id").GetString());

        async Task<List<string>> Ids(string query)
        {
            var answer = await List(query);
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            return answer.Body.GetProperty("routed").EnumerateArray().Select(r => r.GetProperty("decisionId").GetString()!).ToList();
        }
        Assert.Equal(new[] { done.DecisionId.ToString("D") }, await Ids("?state=done"));
        Assert.Equal(new[] { owner.DecisionId.ToString("D") }, await Ids("?state=returned"));
        Assert.Equal(new[] { queued.Id.ToString("D"), open.Id.ToString("D") }, await Ids("?state=open"));
        Assert.Equal(new[] { queued.Id.ToString("D") }, await Ids("?queued=true"));
        Assert.Equal(new[] { done.DecisionId.ToString("D") }, await Ids($"?factory={MailDeskRoutingRig.Books}"));
        Assert.Equal(new[] { queued.Id.ToString("D"), open.Id.ToString("D") }, await Ids($"?seat={MailDeskRoutingRig.Desk}"));
        Assert.Equal(new[] { queued.Id.ToString("D") }, await Ids($"?factory={MailDeskRoutingRig.Support}&seat={MailDeskRoutingRig.Desk}&queued=true"));

        var one = await List("?n=1");
        Assert.Equal(1, one.Body.GetProperty("count").GetInt32());
        Assert.Equal(4, one.Body.GetProperty("total").GetInt32());

        var none = await List("?factory=no-such-factory");
        Assert.Equal(0, none.Body.GetProperty("count").GetInt32());
        Assert.Equal(0, none.Body.GetProperty("routed").GetArrayLength());

        // The labels, folded on the Gateway
        var labels = all.Body.GetProperty("routed").EnumerateArray().ToDictionary(r => r.GetProperty("decisionId").GetString()!, r => r.GetProperty("stateLabel").GetString());
        Assert.Equal($"Open - with session {_rig.Row(open.Id).SessionId}", labels[open.Id.ToString("D")]);
        Assert.Equal($"Queued - waiting for {MailDeskRoutingRig.Support}/{MailDeskRoutingRig.Desk}", labels[queued.Id.ToString("D")]);
        Assert.Equal("Done", labels[done.DecisionId.ToString("D")]);
        Assert.Equal("With you - the Mail Desk agent returned it: no seat fits", labels[owner.DecisionId.ToString("D")]);
    }

    [Theory]
    [InlineData("?state=lost", "state")]
    [InlineData("?queued=maybe", "queued")]
    [InlineData("?n=0", "n")]
    [InlineData("?n=501", "n")]
    public async Task List_ABadFilter_Is400NamingIt(string query, string named)
    {
        var answer = await List(query);

        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
        Assert.Equal("bad_filter", answer.Body.GetProperty("error").GetString());
        Assert.Contains(named, answer.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task EveryRoutedRoute_SwitchOff_Answers404WithNoBodyAndRecordsNothing()
    {
        // Arrange
        var (row, session) = await RoutedToClerk();
        _rig.Switch.Set(Account, false, "email@devthrottle.com", "switched off for the test", _rig.Now);
        var before = Recorded();

        // Act
        var answers = new[]
        {
            await List(),
            await Done(row.Id, session, "done"),
            await Return(row.Id, session, "a note"),
        };

        // Assert
        Assert.All(answers, a =>
        {
            Assert.Equal(HttpStatusCode.NotFound, a.Status);
            Assert.Equal(JsonValueKind.Undefined, a.Body.ValueKind);
        });
        Assert.Equal(before, Recorded());
    }

    // ---------- the session key reaches the new routes ----------

    [Theory]
    [InlineData("GET", "/gateway/mail-desk/routed", true)]
    [InlineData("POST", "/gateway/mail-desk/routed/9b2c1f00-0000-4000-8000-000000000001/done", true)]
    [InlineData("POST", "/gateway/mail-desk/routed/9b2c1f00-0000-4000-8000-000000000001/return", true)]
    [InlineData("POST", "/gateway/mail-desk/routed", false)]
    [InlineData("GET", "/gateway/mail-desk/routed/9b2c1f00-0000-4000-8000-000000000001/done", false)]
    [InlineData("POST", "/gateway/mail-desk/routed/9b2c1f00-0000-4000-8000-000000000001/delete", false)]
    public void SessionKeyGuard_TheRoutedRoutes_AreAllowedAndNothingElseIs(string method, string path, bool allowed)
        => Assert.Equal(allowed, SessionKeyGuard.Check(method, path).Allowed);
}
