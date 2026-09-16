using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// <c>POST /sessions/{sid}/turn-verdict/feedback</c>, the handler's own exits (the Wingman-on-every-turn
/// mission, slice G), driven through <c>GatewayEndpoints.ReportTurnVerdictWrongAsync</c> with a real store, a
/// real pushed-session store and a real settings resolver - and no booted host.
///
/// WHAT THESE PROVE, and what they cannot. They prove the handler's own decisions: the account, the session's
/// existence inside it, the shadow rule, an unreadable body, and that the service's answer is carried through
/// unchanged. They do NOT prove that <see cref="SessionKeyGuard"/> lets a session key reach the route at all -
/// the guard is a pure function on a method and a path, decided before any handler runs, and only a booted host
/// sees the path this route is really mapped on. That is <c>TurnVerdictFeedbackRouteHostedTests</c> in the
/// Gateway.Tests assembly, and <see cref="SessionKeyGuardTests"/> proves the guard's own decision.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class TurnVerdictFeedbackRouteTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _tempDir;
    private readonly TenantSettingsStore _settingsStore;

    public TurnVerdictFeedbackRouteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-feedback-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsStore = new TenantSettingsStore(_harness.Open());
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    // TenantId.Local is what the self-host boundary binds every request to, so the store, the settings and the
    // pushed sessions all have to be written under it for the handler to find them.
    private static readonly TenantId Account = TenantId.Local;
    private static readonly string Sid = Guid.NewGuid().ToString();

    private static CcDirector.Gateway.Tenancy.HostedTenantBoundary SelfHostBoundary() =>
        new(new SingleTenantContext(), new DeviceRegistry());

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private TurnVerdictStore NewStore() => new(_harness.Open());

    private static PushedSessionStore PushedHolding(params string[] sessionIds)
    {
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, "director-feedback", "conn-feedback");
        Assert.True(pushed.ApplySnapshot(Account, "director-feedback", "conn-feedback", 1,
            sessionIds.Select(id => new SessionDto
            {
                SessionId = id,
                Name = id,
                ActivityState = "WaitingForInput",
                LastActivityAt = DateTime.UtcNow,
            }).ToList()));
        return pushed;
    }

    private static DefaultHttpContext Request(object body, bool asSessionKey = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(body)));
        if (asSessionKey)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                new SessionCredentialIdentity(Guid.Parse(Sid), Account, "director-feedback");
        return ctx;
    }

    private static DefaultHttpContext RawRequest(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx;
    }

    private static TurnVerdictDto Verdict(string verdictId) => new()
    {
        VerdictId = verdictId,
        JudgedAtUtc = new DateTime(2026, 9, 15, 21, 0, 0, DateTimeKind.Utc),
        TurnEndObservedAtUtc = new DateTime(2026, 9, 15, 20, 59, 48, DateTimeKind.Utc),
        ScreenHash = "screen-hash-feedback",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.Finished,
        FinishedKind = "report",
        Confidence = "high",
        Evidence = "I have finished the migration and pushed it.",
        Label = "Report: the migration is pushed",
        Summary = "The migration is written and pushed; nothing is waiting on you.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "The migration session has finished.",
    };

    private TenantSettingsResolver Settings(bool colourOn)
    {
        var resolver = new TenantSettingsResolver(_settingsStore);
        if (colourOn) resolver.SetTurnVerdictColourEnabled(Account, true, DateTime.UtcNow);
        return resolver;
    }

    /// <summary>The body the route answers with, read off the typed result rather than serialized through a
    /// pipeline: executing the result needs a logger factory, and a test that stood one up would be testing the
    /// framework's JSON writer rather than this route's answer.</summary>
    private static TurnVerdictFeedbackResponse BodyOf(IResult result)
        => Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<TurnVerdictFeedbackResponse>>(result).Value!;

    /// <summary>The ledger the route writes its refusals to, collected. A list rather than a seam with rules in
    /// it: the whole question these tests ask is what was written, so a double that decided anything would be
    /// the thing under test.</summary>
    private static (Action<TurnVerdictRecord> Write, List<TurnVerdictRecord> Lines) Ledger()
    {
        var lines = new List<TurnVerdictRecord>();
        return (lines.Add, lines);
    }

    [Fact]
    public async Task A_report_on_a_session_of_this_account_is_recorded()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou, note = "it asked me" }),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var body = BodyOf(result);
        Assert.True(body.Accepted);
        Assert.Equal(TurnVerdictFeedbackCodes.Recorded, body.Code);
        Assert.Equal("tv-1", body.VerdictId);
        Assert.Equal(TurnVerdictVocabulary.NeededYou, store.FeedbackFor(Account, "tv-1")!.CorrectedVerdict);
    }

    [Fact]
    public async Task A_session_this_account_has_never_pushed_answers_404_and_records_nothing()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));
        var unknown = Guid.NewGuid().ToString();

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            unknown, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Null(store.FeedbackFor(Account, "tv-1"));
    }

    [Fact]
    public async Task A_path_that_is_not_a_session_id_at_all_is_refused_before_anything_is_read()
    {
        var store = NewStore();

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            "not-a-session-id", SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true),
            PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
    }

    /// <summary>
    /// THE SHADOW RULE, the reads' rule applied to this write: while the account's colours are off its verdicts
    /// are a shadow record, and the product's own automation may not act on one. The person still may - the
    /// shadow is about a session key, never about the owner examining his own record.
    /// </summary>
    [Fact]
    public async Task With_the_colours_off_a_session_key_may_not_report_and_a_device_key_still_may()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));

        var refused = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }, asSessionKey: true),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: false), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status403Forbidden, Status(refused));
        Assert.Equal(TurnVerdictFeedbackCodes.ShadowRecord, BodyOf(refused).Code);
        Assert.Null(store.FeedbackFor(Account, "tv-1"));

        var person = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: false), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status200OK, Status(person));
        Assert.Equal(TurnVerdictVocabulary.NeededYou, store.FeedbackFor(Account, "tv-1")!.CorrectedVerdict);
    }

    [Fact]
    public async Task With_the_colours_on_a_session_key_reports_exactly_as_the_person_does()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }, asSessionKey: true),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal(TurnVerdictFeedbackCodes.Recorded, BodyOf(result).Code);
    }

    [Fact]
    public async Task A_gateway_with_no_verdict_store_says_so_rather_than_pretending_there_is_nothing_to_report()
    {
        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            Sid, SelfHostBoundary(), turnVerdictFeedback: null, Settings(colourOn: true), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Equal(TurnVerdictFeedbackCodes.Unavailable, BodyOf(result).Code);
    }

    [Fact]
    public async Task An_unreadable_body_is_refused_by_the_route_and_records_nothing()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            RawRequest("{ not json"), Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store),
            Settings(colourOn: true), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(TurnVerdictFeedbackCodes.Malformed, BodyOf(result).Code);
        Assert.Null(store.FeedbackFor(Account, "tv-1"));
    }

    /// <summary>
    /// The service's refusal reaches the caller unchanged - the route neither softens it nor swallows it, because
    /// the panel shows that sentence verbatim and a route that composed its own would put words in it.
    /// </summary>
    [Fact]
    public async Task The_services_refusal_sentence_is_carried_through_unedited()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));
        var expected = new TurnVerdictFeedbackService(store)
            .Report(Account, Sid, new TurnVerdictFeedbackRequest { VerdictId = "tv-1", CorrectVerdict = "nonsense" });

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = "nonsense" }),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true), PushedHolding(Sid));

        var body = BodyOf(result);
        Assert.Equal(expected.StatusCode, Status(result));
        Assert.Equal(expected.Code, body.Code);
        Assert.Equal(expected.Reason, body.Reason);
        Assert.Contains("not-a-turn-end", body.Reason);
    }

    // ---------- The ledger: an accepted correction is its own record, a refusal was nobody's ----------

    /// <summary>
    /// THE AUTHORISATION REFUSAL IS IN THE RECORD. A session key reporting while the account's colours are off is
    /// refused and nothing is stored anywhere - so before this line the only trace that the product's own
    /// automation tried to correct a shadow verdict was a log file, which is not a record anybody queries. The
    /// cause is the same closed word the caller was answered with.
    /// </summary>
    [Fact]
    public async Task A_session_key_refused_by_the_shadow_rule_leaves_one_ledger_line()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));
        var ledger = Ledger();

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }, asSessionKey: true),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: false),
            PushedHolding(Sid), ledger.Write);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        var line = Assert.Single(ledger.Lines);
        Assert.Equal(ActivityEventTypes.TurnVerdictFeedbackRefused, line.EventType);
        Assert.Equal(ActivityCauses.FeedbackShadowRecord, line.Cause);
        // The same word the owner was shown, so the answer and the record cannot be read as two outcomes.
        Assert.Equal(BodyOf(result).Code, line.Cause);
        Assert.Equal(Sid, line.SessionId);
        Assert.Equal(Account, line.Tenant);
    }

    /// <summary>A word the vocabulary does not hold is refused with its OWN cause, not a general one - the record
    /// has to say which rule turned the report away or it cannot be counted.</summary>
    [Fact]
    public async Task An_unknown_verdict_word_leaves_one_ledger_line_under_its_own_cause()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));
        var ledger = Ledger();

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = "nearly-finished" }),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true),
            PushedHolding(Sid), ledger.Write);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        var line = Assert.Single(ledger.Lines);
        Assert.Equal(ActivityEventTypes.TurnVerdictFeedbackRefused, line.EventType);
        Assert.Equal(ActivityCauses.FeedbackUnknownVerdict, line.Cause);
        Assert.Equal(BodyOf(result).Code, line.Cause);
        // The verdict the request named travels with the line; the note the person typed never does.
        Assert.Equal("verdict=tv-1", line.Detail);
        Assert.Null(store.FeedbackFor(Account, "tv-1"));
    }

    /// <summary>
    /// THE CONTROL, and it is the half that keeps the rule honest: an ACCEPTED correction writes no ledger line,
    /// because it writes a durable row carrying its own moment, word and note. A test that only watched refusals
    /// would pass on a route that wrote a line for everything.
    /// </summary>
    [Fact]
    public async Task An_accepted_correction_writes_the_row_and_no_ledger_line()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));
        var ledger = Ledger();

        var result = await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou, note = "it asked me" }),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true),
            PushedHolding(Sid), ledger.Write);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Empty(ledger.Lines);
        Assert.NotNull(store.FeedbackFor(Account, "tv-1"));
    }

    /// <summary>Every other refusal the handler itself decides also leaves exactly one line, each under its own
    /// cause - so "every refusal is recorded" is a sentence about all of them rather than the two above.</summary>
    [Fact]
    public async Task The_handlers_other_refusals_each_leave_one_line_under_their_own_cause()
    {
        var store = NewStore();
        store.Store(Account, Sid, Verdict("tv-1"));

        var badPath = Ledger();
        await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            "not-a-session-id", SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true),
            PushedHolding(Sid), badPath.Write);
        Assert.Equal(ActivityCauses.FeedbackInvalidSessionId, Assert.Single(badPath.Lines).Cause);

        var noService = Ledger();
        await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            Sid, SelfHostBoundary(), null, Settings(colourOn: true), PushedHolding(Sid), noService.Write);
        Assert.Equal(ActivityCauses.FeedbackUnavailable, Assert.Single(noService.Lines).Cause);

        var unknownSession = Ledger();
        await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-1", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            Guid.NewGuid().ToString(), SelfHostBoundary(), new TurnVerdictFeedbackService(store),
            Settings(colourOn: true), PushedHolding(Sid), unknownSession.Write);
        Assert.Equal(ActivityCauses.FeedbackSessionNotFound, Assert.Single(unknownSession.Lines).Cause);

        var unreadable = Ledger();
        await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            RawRequest("{ this is not json"), Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store),
            Settings(colourOn: true), PushedHolding(Sid), unreadable.Write);
        Assert.Equal(ActivityCauses.FeedbackMalformed, Assert.Single(unreadable.Lines).Cause);

        var notThisSession = Ledger();
        await GatewayEndpoints.ReportTurnVerdictWrongAsync(
            Request(new { verdictId = "tv-no-such-verdict", correctVerdict = TurnVerdictVocabulary.NeededYou }),
            Sid, SelfHostBoundary(), new TurnVerdictFeedbackService(store), Settings(colourOn: true),
            PushedHolding(Sid), notThisSession.Write);
        Assert.Equal(ActivityCauses.FeedbackVerdictNotFound, Assert.Single(notThisSession.Lines).Cause);
    }
}
