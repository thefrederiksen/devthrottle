using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// <c>GET /sessions/{sid}/wingman-now</c>, the handler's own exits, driven through
/// <see cref="GatewayEndpoints.ReadWingmanNow"/> with a REAL verdict store, a REAL conversation store, a REAL
/// director registry and a REAL pushed-session store, and no booted host (the Wingman tab, version 3, item 1).
///
/// WHY THE REAL STORES AND NOT A FAKE. <see cref="WingmanNowFoldTests"/> proves what the fold SAYS about inputs handed
/// to it; nothing there watches whether the route gathers the right inputs at all. A fold that is perfect about rows
/// nobody feeds it is the shape of proof that certifies a broken feature - so the serve below goes through the real
/// roster fold, the real stored verdict and the real stored conversation, and asserts the owner's own words come back.
///
/// THE SESSION-KEY REFUSAL IS PROVEN HERE WITH THE GUARD OUT OF THE WAY. The route is not on
/// <see cref="SessionKeyGuard"/>'s allow list (<c>SessionKeyGuardTests</c> proves that), but a refusal resting only on
/// that list would end the day somebody widened it. So the handler is called directly with a session key's identity
/// and refuses on its own - and the SAME request from a device is served, so the refusal is not a route that serves
/// nobody.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class WingmanNowRouteTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-wingman-now-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;

    private DeviceRegistry? _devices;

    public WingmanNowRouteTests()
    {
        Directory.CreateDirectory(_instancesDir);
        _registry = new DirectorRegistry(_instancesDir);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _devices?.Dispose();
        _harness.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    // TenantId.Local is what the self-host boundary binds every request to.
    private static readonly TenantId Account = TenantId.Local;
    private static readonly string Sid = Guid.NewGuid().ToString();
    private const string DirectorId = "director-now";
    private static readonly DateTime Stopped = new(2026, 9, 17, 11, 12, 0, DateTimeKind.Utc);

    // The device registry lives in this test's own directory. The parameterless registry opens the default store,
    // which every test class shares - two classes building it at once collide on its import marker.
    private CcDirector.Gateway.Tenancy.HostedTenantBoundary SelfHostBoundary()
    {
        if (_devices is null)
        {
            var path = _harness.LegacyPath("devices.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _devices = new DeviceRegistry(path);
        }
        return new(new SingleTenantContext(), _devices);
    }

    // A JSON result with no explicit status is a 200: the framework writes the default when none is set.
    private static int Status(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private static WingmanNowResponse BodyOf(IResult result)
        => Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<WingmanNowResponse>>(result).Value!;

    private static string ErrorOf(IResult result)
    {
        var body = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        return (string)body.GetType().GetProperty("error")!.GetValue(body)!;
    }

    /// <summary>What the authentication middleware leaves on a request, by the credential that authenticated it.</summary>
    public enum Caller { Device, SessionKey, SharedMachineToken, Nobody }

    private static DefaultHttpContext Request(Caller caller = Caller.Device)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        switch (caller)
        {
            case Caller.Device:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("device-1", null, "phone", "active");
                break;
            case Caller.SessionKey:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "session-key";
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse(Sid), Account, DirectorId);
                break;
            case Caller.SharedMachineToken:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-shared-machine-token";
                break;
        }
        return ctx;
    }

    /// <summary>A registered Director that has pushed one stopped session.</summary>
    /// <summary>The same real push, with this session WORKING - the only state the answer card is reached from -
    /// and every other session left waiting, so the roster has something for "next" to find.</summary>
    private PushedSessionStore PushedWorking(params string[] alsoWaiting)
    {
        _registry.RegisterFromStream(DirectorId, "SOREN_NORTH", "u", "test", 1, DateTime.UtcNow, Account);
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, DirectorId, "conn-now");

        var rows = new List<SessionDto>
        {
            new()
            {
                SessionId = Sid,
                Name = "Wingman Inspector - Manager",
                Agent = "ClaudeCode",
                ActivityState = "Working",
                LastActivityAt = DateTime.UtcNow,
            },
        };
        rows.AddRange(alsoWaiting.Select(id => new SessionDto
        {
            SessionId = id,
            Name = "Dev Reports - Architect",
            Agent = "ClaudeCode",
            ActivityState = "WaitingForInput",
            WaitingSince = Stopped,
            LastActivityAt = Stopped,
        }));

        Assert.True(pushed.ApplySnapshot(Account, DirectorId, "conn-now", 1, rows));
        return pushed;
    }

    private PushedSessionStore PushedHolding(params string[] sessionIds)
    {
        _registry.RegisterFromStream(DirectorId, "SOREN_NORTH", "u", "test", 1, DateTime.UtcNow, Account);
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, DirectorId, "conn-now");
        Assert.True(pushed.ApplySnapshot(Account, DirectorId, "conn-now", 1,
            sessionIds.Select(id => new SessionDto
            {
                SessionId = id,
                Name = "Wingman Inspector - Manager",
                Agent = "ClaudeCode",
                ActivityState = "WaitingForInput",
                WaitingSince = Stopped,
                LastActivityAt = Stopped,
            }).ToList()));
        return pushed;
    }

    private TurnVerdictStore StoreHolding(TurnVerdictDto? verdict)
    {
        var store = new TurnVerdictStore(_harness.Open());
        if (verdict is not null) store.Store(Account, Sid, verdict);
        return store;
    }

    /// <summary>The REAL row source the roster fold uses, over the same store this route reads - so the verdict on
    /// the row and the verdict in the view come from one place, exactly as they do on a running Gateway. A fake here
    /// would make the serve below prove nothing about that join.</summary>
    private static ITurnVerdictRowSource RowSourceOver(TurnVerdictStore store)
        => new TurnVerdictRowSource(_ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true },
            store, () => null);

    private static TurnVerdictDto NeedsYouVerdict() => NeedsYouVerdictAt(Stopped);

    /// <summary>The answer route's own record of the owner picking the first option of this verdict - the one
    /// record of what he answered, which is what the Now view reads back to him.</summary>
    private static TurnVerdictStoredAnswer Chose(TurnVerdictDto verdict)
        => TurnVerdictStoredAnswer.For(verdict, new[] { 0 });

    private static TurnVerdictDto NeedsYouVerdictAt(DateTime stoppedAt) => new()
    {
        VerdictId = "verdict-now-1",
        JudgedAtUtc = stoppedAt.AddSeconds(4),
        TurnEndObservedAtUtc = stoppedAt,
        Verdict = Core.Wingman.TurnVerdictVocabulary.NeededYou,
        Confidence = "high",
        Label = "Merge pull request 3002, or allow me to merge it",
        Summary = "The merge command was refused by a permission check.",
        Evidence = "Either merge 3002 yourself, or allow that command and I will do it.",
        AgentRecommends = "allow the merge",
        AnswerVia = "reply",
        Options =
        {
            new TurnVerdictOptionDto { Key = "Allow the merge", Note = "It runs the merge itself.", Send = "1", Recommended = true },
            new TurnVerdictOptionDto { Key = "I will merge it myself", Note = "It waits for you.", Send = "2" },
        },
    };

    private SessionTurnStore ConversationHolding(params (string Role, string Text)[] messages)
    {
        var store = new SessionTurnStore(_harness.Open());
        var batch = new TurnPushBatch
        {
            SessionId = Sid,
            Generation = @"C:\transcripts\gen-a.jsonl",
            GenerationStartedUtc = Stopped.AddHours(-1),
            Agent = "ClaudeCode",
            StartOrdinal = 0,
            TotalCount = messages.Length,
            Turns = messages.Select((m, i) => new PushedTurn
            {
                Ordinal = i,
                Role = m.Role,
                Parts = { new HistoryPartDto { Kind = "Text", Text = m.Text } },
                Timestamp = new DateTimeOffset(Stopped.AddSeconds(i)),
            }).ToList(),
        };
        store.Append(DirectorId, batch, Stopped);
        return store;
    }

    private IResult Read(Caller caller, string sid, TurnVerdictStore? verdicts, PushedSessionStore? pushed,
        SessionTurnStore? turns = null, Func<TenantId, TurnVerdictSettings>? settings = null,
        TimeSpan? streamStale = null, Func<TenantId, VoiceRowStamp.VoiceFacts>? voiceFacts = null,
        Func<TenantId, string, bool>? startedBySchedule = null)
        => GatewayEndpoints.ReadWingmanNow(Request(caller), sid, SelfHostBoundary(), _registry, pushed, verdicts,
            turns, null, null, verdicts is null ? null : RowSourceOver(verdicts), null, settings, streamStale,
            voiceFacts, startedBySchedule);

    /// <summary>This account's pushed roster with voice turned ON for the session - a Director-owned fact, which
    /// is why it arrives on the push and not from a lookup.</summary>
    private PushedSessionStore PushedWithVoiceOn()
    {
        _registry.RegisterFromStream(DirectorId, "SOREN_NORTH", "u", "test", 1, DateTime.UtcNow, Account);
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, DirectorId, "conn-now");
        Assert.True(pushed.ApplySnapshot(Account, DirectorId, "conn-now", 1, new List<SessionDto>
        {
            new()
            {
                SessionId = Sid,
                Name = "Wingman Inspector - Manager",
                Agent = "ClaudeCode",
                ActivityState = "WaitingForInput",
                WaitingSince = Stopped,
                LastActivityAt = Stopped,
                VoiceMode = true,
            },
        }));
        return pushed;
    }

    // ---------------------------------------------------------------- the refusals

    [Fact]
    public void A_session_key_is_refused_by_the_handler_itself_and_the_same_request_from_a_device_is_served()
    {
        var verdicts = StoreHolding(NeedsYouVerdict());
        var pushed = PushedHolding(Sid);

        var refused = Read(Caller.SessionKey, Sid, verdicts, pushed);
        Assert.Equal(StatusCodes.Status403Forbidden, Status(refused));
        Assert.Contains("never to a session key", ErrorOf(refused));

        // The positive control: without the session identity the SAME request is served, so the refusal above is
        // about the credential and not about a route that serves nobody.
        Assert.Equal(StatusCodes.Status200OK, Status(Read(Caller.Device, Sid, verdicts, pushed)));
    }

    /// <summary>A POSITIVE DEVICE IDENTITY, not merely "not a session key". On a self-hosted Gateway the shared
    /// machine token authenticates with no device at all and resolves to the Local account, so refusing only a
    /// session key would let that token read the agent's own words.</summary>
    [Theory]
    [InlineData(Caller.SharedMachineToken)]
    [InlineData(Caller.Nobody)]
    public void A_caller_with_no_device_of_its_own_is_refused(Caller caller)
    {
        var result = Read(caller, Sid, StoreHolding(NeedsYouVerdict()), PushedHolding(Sid));

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Contains("device key", ErrorOf(result));
    }

    [Fact]
    public void A_session_id_that_is_not_an_identifier_is_refused_before_any_store_is_read()
        => Assert.Equal(StatusCodes.Status400BadRequest,
            Status(Read(Caller.Device, "not-an-id", null, null)));

    [Fact]
    public void A_gateway_with_no_verdict_store_says_the_live_stop_is_not_available_here()
        => Assert.Equal(StatusCodes.Status404NotFound,
            Status(Read(Caller.Device, Sid, null, PushedHolding(Sid))));

    /// <summary>A session that is not this account's answers exactly what an unknown session answers, so one account
    /// cannot learn which ids exist in another.</summary>
    [Fact]
    public void A_session_outside_this_account_answers_what_an_unknown_session_answers()
    {
        var verdicts = StoreHolding(NeedsYouVerdict());
        var pushed = PushedHolding(Sid);
        var unknown = Guid.NewGuid().ToString();

        var foreign = Read(Caller.Device, unknown, verdicts, pushed);

        Assert.Equal(StatusCodes.Status404NotFound, Status(foreign));
        Assert.Equal(StatusCodes.Status404NotFound, Status(Read(Caller.Device, unknown, verdicts, PushedHolding(Sid))));
    }

    // ---------------------------------------------------------------- the serve, through the real stores

    [Fact]
    public void The_live_stop_is_served_from_the_stored_verdict_the_real_roster_row_and_the_stored_conversation()
    {
        var verdicts = StoreHolding(NeedsYouVerdict());
        var pushed = PushedHolding(Sid);
        var turns = ConversationHolding(
            ("User", "Get the release out."),
            ("Assistant", "I need help with three things. The merge was refused by a permission check."));

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed, turns));

        Assert.Equal(Sid, now.SessionId);
        Assert.Equal(WingmanNowStates.NeedsYou, now.State);
        Assert.Equal("Needs you", now.PillText);
        // The roster fold really ran over the pushed row: an unpushed row carries no colour at all.
        Assert.Equal("red", now.PillColour);
        Assert.False(string.IsNullOrWhiteSpace(now.PillColourHex));
        Assert.Equal("Merge pull request 3002, or allow me to merge it", now.Headline);
        Assert.Equal("The merge command was refused by a permission check.", now.Story);
        // The agent tool's display name comes from the roster fold's stamp, not from anything this route knows.
        Assert.Equal("Claude Code said", now.AgentSaid!.Who);
        Assert.Equal("I need help with three things. The merge was refused by a permission check.", now.WholeReply);
        Assert.Equal("verdict-now-1", now.VerdictId);
        Assert.True(now.CanAnswerByOption);
        Assert.Equal(2, now.Needs!.Options.Count);
        Assert.Equal(Stopped, now.When!.AtUtc);
    }

    /// <summary>The one-tap path closes the moment the store says the verdict was answered - and it is the STORE
    /// that says so, not anything this view was handed.</summary>
    [Fact]
    public void An_answered_verdict_is_served_with_its_options_readable_and_no_longer_tappable()
    {
        var verdict = NeedsYouVerdict();
        var verdicts = StoreHolding(verdict);
        var pushed = PushedHolding(Sid);

        var before = BodyOf(Read(Caller.Device, Sid, verdicts, pushed));
        Assert.True(before.CanAnswerByOption);

        Assert.True(verdicts.MarkAnswered(Account, Chose(verdict), Stopped.AddMinutes(2)));

        var after = BodyOf(Read(Caller.Device, Sid, verdicts, pushed));
        Assert.False(after.CanAnswerByOption);
        Assert.Equal(2, after.Needs!.Options.Count);
    }

    /// <summary>
    /// THE ANSWER CARD, SERVED END TO END: the store keeps what he chose, the handler supplies its own clock, and
    /// the view reads back his own words with the session working again.
    ///
    /// The moments here are the REAL clock's, deliberately. The five-minute rule is the one thing in this view that
    /// depends on when the request is answered, and a test that handed the fold a moment of its own would prove
    /// the fold and nothing about the handler - which is where the clock actually comes from.
    /// </summary>
    [Fact]
    public void A_working_session_whose_stop_he_has_just_answered_is_served_as_just_answered()
    {
        var verdict = NeedsYouVerdictAt(DateTime.UtcNow.AddMinutes(-2));
        var verdicts = StoreHolding(verdict);
        var pushed = PushedWorking();

        // CONTROL: unanswered, the same working session is plainly working and says so.
        var before = BodyOf(Read(Caller.Device, Sid, verdicts, pushed));
        Assert.Equal(WingmanNowStates.Working, before.State);
        Assert.Null(before.Answered);

        Assert.True(verdicts.MarkAnswered(Account, Chose(verdict), DateTime.UtcNow.AddSeconds(-20)));

        var after = BodyOf(Read(Caller.Device, Sid, verdicts, pushed));
        Assert.Equal(WingmanNowStates.JustAnswered, after.State);
        Assert.Equal("Working again", after.PillText);
        Assert.Equal("You answered: Allow the merge", after.Answered!.Headline);
        Assert.Equal("You answered", after.When!.Lead);
        Assert.Equal("The stop you answered", after.LastStop!.Lead);
    }

    /// <summary>
    /// AND WHERE TO GO NEXT comes from the roster the handler already read for this session's own row. One read,
    /// one fold, one order - so the tab cannot send him somewhere the Sessions list does not have at the top.
    /// </summary>
    [Fact]
    public void The_answer_card_points_at_the_next_session_of_this_account_that_needs_him()
    {
        const string other = "22222222-2222-2222-2222-222222222222";
        var verdict = NeedsYouVerdictAt(DateTime.UtcNow.AddMinutes(-2));
        var verdicts = StoreHolding(verdict);
        var pushed = PushedWorking(other);
        Assert.True(verdicts.MarkAnswered(Account, Chose(verdict), DateTime.UtcNow.AddSeconds(-20)));

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed));

        Assert.Equal("Next that needs you", now.NextNeedsYou!.Heading);
        Assert.Equal(other, now.NextNeedsYou.SessionId);
        Assert.Equal("Dev Reports - Architect", now.NextNeedsYou.Name);
    }

    /// <summary>
    /// THE VOICE VERDICT IS STAMPED ON THIS ROUTE'S ROW BY THE SAME STAMP THE ROSTER ROUTE USES.
    ///
    /// The roster FOLD does not carry it - the /sessions handler stamps it after the fold runs - so this route
    /// has to stamp it too, and the whole point of this slice is that it does so through the one shared stamp
    /// rather than a third copy of the call. The control below is what makes that visible: handed no facts, the
    /// same row and the same request offer nothing, because a route that cannot see the voice service has not
    /// earned the claim that there is no audio.
    /// </summary>
    [Fact]
    public void The_voice_control_is_served_from_the_shared_stamp_and_offers_nothing_without_it()
    {
        var verdicts = StoreHolding(NeedsYouVerdict());
        var pushed = PushedWithVoiceOn();

        // CONTROL: no facts, no claim.
        Assert.Equal(WingmanNowVoiceKinds.None,
            BodyOf(Read(Caller.Device, Sid, verdicts, pushed)).Voice.Kind);

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed,
            voiceFacts: _ => new VoiceRowStamp.VoiceFacts(AudioReady: _ => true)));

        Assert.Equal(WingmanNowVoiceKinds.Play, now.Voice.Kind);
        Assert.Equal("Play", now.Voice.Label);
    }

    /// <summary>With the clip not made yet the same route says so, from the same stamp.</summary>
    [Fact]
    public void The_voice_control_says_the_clip_is_being_prepared_when_there_is_none_yet()
    {
        var now = BodyOf(Read(Caller.Device, Sid, StoreHolding(NeedsYouVerdict()), PushedWithVoiceOn(),
            voiceFacts: _ => new VoiceRowStamp.VoiceFacts(AudioReady: _ => false)));

        Assert.Equal(WingmanNowVoiceKinds.Preparing, now.Voice.Kind);
    }

    /// <summary>
    /// THE PILL CARRIES THE ROW'S OWN COLOUR, EXACTLY, IN EVERY STOPPED STATE - three states, three different
    /// colours, each pill matching its own row.
    ///
    /// THE DEFECT IT WATCHES. The voice facts were stamped onto the row AFTER this route's roster fold ran.
    /// VoiceAudioReady and VoiceGenerating are Gateway-owned - no Director pushes them - so the fold saw false
    /// for both, SessionOrdering.IsVoicePreparing held the row yellow, and needs-you, done and report all came
    /// back wearing the same "preparing voice" yellow while the same session's dot in the Sessions list was red
    /// or cyan. Measured off the owner's screen on 2026-09-18: pill (234, 179, 8), dot (6, 182, 212).
    ///
    /// IT IS DRIVEN WITH VOICE ON AND AUDIO READY, which is the case that was wrong: nothing is being prepared,
    /// so yellow is false about every one of these rows. The control below is the same route with the audio NOT
    /// ready, where yellow is the true answer and the row and the pill agree on it.
    ///
    /// THE COMPARISON IS AGAINST THE ROW, not against a colour written out here. A test that named "red" and
    /// "cyan" would pass the day the ladder changed and the list moved without the pill.
    /// </summary>
    [Fact]
    public void The_pill_wears_the_rows_own_colour_in_every_stopped_state()
    {
        foreach (var verdict in new[] { NeedsYouVerdict(), FinishedVerdict("done"), FinishedVerdict("report") })
        {
            var verdicts = StoreHolding(verdict);
            var pushed = PushedWithVoiceOn();
            var facts = new VoiceRowStamp.VoiceFacts(AudioReady: _ => true);

            var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed, voiceFacts: _ => facts));

            // The row the Sessions list is served, folded the same way, with the same facts.
            var row = GatewayEndpoints.FoldedAccountRoster(_registry, pushed, Account, null, null,
                RowSourceOver(verdicts), null, voiceFacts: facts)
                .Single(s => s.SessionId == Sid);

            Assert.Equal(row.EffectiveColor, now.PillColour);
            Assert.Equal(row.EffectiveColorHex, now.PillColourHex);
            // The symptom, named: not one of these three is "preparing voice", and the clip is ready.
            Assert.NotEqual("yellow", now.PillColour);
        }

        // THE CONTROL. With no audio yet the row IS preparing voice, and the pill says the same thing - so the
        // assertions above are about the pill following the row, not about yellow being banned.
        var preparing = BodyOf(Read(Caller.Device, Sid, StoreHolding(NeedsYouVerdict()), PushedWithVoiceOn(),
            voiceFacts: _ => new VoiceRowStamp.VoiceFacts(AudioReady: _ => false)));
        Assert.Equal("yellow", preparing.PillColour);
    }

    /// <summary>A calm verdict of one kind or the other, so the row folds cyan rather than red.</summary>
    private static TurnVerdictDto FinishedVerdict(string finishedKind) => new()
    {
        VerdictId = "verdict-now-" + finishedKind,
        JudgedAtUtc = Stopped.AddSeconds(4),
        TurnEndObservedAtUtc = Stopped,
        Verdict = Core.Wingman.TurnVerdictVocabulary.Finished,
        FinishedKind = finishedKind,
        Confidence = "high",
        Label = "The QA report is ready",
        Summary = "It wrote the QA report and asks for nothing.",
        Evidence = "The QA report is ready.",
        AnswerVia = "reply",
    };

    /// <summary>A session this account knows about whose stop has never been judged is not an error - it is the
    /// ordinary state of a session that has not stopped, and of every session on an account with the Wingman off.</summary>
    [Fact]
    public void A_session_with_no_stored_verdict_is_served_rather_than_refused()
    {
        var now = BodyOf(Read(Caller.Device, Sid, StoreHolding(null), PushedHolding(Sid)));

        Assert.Equal(WingmanNowStates.Other, now.State);
        Assert.False(string.IsNullOrWhiteSpace(now.PillText));
    }

    /// <summary>A Gateway with no conversation store serves everything else. "No whole reply" is a missing field,
    /// never a missing view.</summary>
    [Fact]
    public void The_view_is_served_without_the_whole_reply_when_no_conversation_is_stored()
    {
        var now = BodyOf(Read(Caller.Device, Sid, StoreHolding(NeedsYouVerdict()), PushedHolding(Sid), turns: null));

        Assert.Equal(WingmanNowStates.NeedsYou, now.State);
        Assert.Null(now.WholeReply);
    }

    // ------------------------------------------- the conversation read is inside the account's scope

    /// <summary>
    /// THE CONVERSATION IS READ INSIDE THE CALLER'S ACCOUNT SCOPE, and this is the test that watches it.
    ///
    /// Every test above builds the boundary over <c>SingleTenantContext</c>, whose <c>EnterScope</c> is a no-op
    /// disposable - so not one of them can tell whether the handler enters the scope at all. Measured, not assumed:
    /// with the scope deleted the whole class stayed green. A safety line nothing can see is a safety line nobody
    /// is keeping.
    ///
    /// So this one builds the boundary the HOSTED Gateway is built with - over a real ambient context - and opens
    /// the conversation store over that SAME context. The store resolves its account from the ambient context on
    /// every read, so the read only finds the account's rows if the handler has entered its scope; outside one the
    /// ambient account is not set and the store refuses the read rather than answering from whatever account
    /// happened to be current. Delete the scope and this goes red where the whole reply should be.
    /// </summary>
    [Fact]
    public void The_stored_conversation_is_read_inside_the_callers_own_account()
    {
        // A real account, because a hosted boundary resolves neither Local nor SYSTEM - it denies both.
        var account = new TenantId("11111111-2222-3333-4444-555555555555");
        var devicesPath = _harness.LegacyPath("hosted-devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(devicesPath)!);
        using var devices = new DeviceRegistry(devicesPath);
        var ambient = new AsyncLocalTenantContext();
        var boundary = new CcDirector.Gateway.Tenancy.HostedTenantBoundary(ambient, devices);
        Assert.True(boundary.IsHosted);   // the whole point: a no-op scope would prove nothing

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
            new DeviceCredentialIdentity("device-1", account.Value, "phone", "active");

        var verdicts = new TurnVerdictStore(_harness.Open());
        verdicts.Store(account, Sid, NeedsYouVerdict());

        _registry.RegisterFromStream(DirectorId, "SOREN_NORTH", "u", "test", 1, DateTime.UtcNow, account);
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(account, DirectorId, "conn-hosted");
        Assert.True(pushed.ApplySnapshot(account, DirectorId, "conn-hosted", 1, new List<SessionDto>
        {
            new()
            {
                SessionId = Sid,
                Name = "Wingman Inspector - Manager",
                Agent = "ClaudeCode",
                ActivityState = "WaitingForInput",
                WaitingSince = Stopped,
                LastActivityAt = Stopped,
            },
        }));

        // The conversation store reads its account from the ambient context, so seeding it needs a scope too -
        // which is the test writing its own fixture, not the thing under test.
        var turns = new SessionTurnStore(_harness.Open(ambient));
        using (boundary.EnterScope(account))
        {
            turns.Append(DirectorId, new TurnPushBatch
            {
                SessionId = Sid,
                Generation = @"C:	ranscripts\gen-hosted.jsonl",
                GenerationStartedUtc = Stopped.AddHours(-1),
                Agent = "ClaudeCode",
                StartOrdinal = 0,
                TotalCount = 1,
                Turns =
                {
                    new PushedTurn
                    {
                        Ordinal = 0,
                        Role = "Assistant",
                        Parts = { new HistoryPartDto { Kind = "Text", Text = "Either merge 3002 yourself, or allow that command." } },
                        Timestamp = new DateTimeOffset(Stopped),
                    },
                },
            }, Stopped);
        }

        // And now the request, with NO account current - exactly as one arrives on the hosted Gateway. Outside a
        // scope this context REFUSES rather than naming a default account, which is the deny-by-default that makes
        // entering the scope the handler's job and not a formality.
        Assert.Throws<InvalidOperationException>(() => ambient.Current);
        var served = GatewayEndpoints.ReadWingmanNow(ctx, Sid, boundary, _registry, pushed, verdicts, turns,
            null, null, RowSourceOver(verdicts), null);

        Assert.Equal(StatusCodes.Status200OK, Status(served));
        Assert.Equal("Either merge 3002 yourself, or allow that command.", BodyOf(served).WholeReply);

        // The scope is left behind it, so the next read on this thread is no more privileged than this one was.
        Assert.Throws<InvalidOperationException>(() => ambient.Current);
    }

    // --------------------------------------------- the account's own switches reach the view

    /// <summary>
    /// A SWITCHED-OFF ACCOUNT IS SERVED THE SWITCHED-OFF WORDS, THROUGH THE REAL HANDLER.
    ///
    /// <see cref="WingmanNowFoldTests"/> proves what the fold says when it is TOLD the account's Wingman is off.
    /// Nothing there watches whether the route asks. A route that never reads the account's settings would leave
    /// every one of those fold tests green while the screen showed "Needs you" on an account that judges nothing -
    /// so this drives the handler with a real resolver and reads the answer.
    /// </summary>
    [Theory]
    [InlineData(false, true)]    // the judge is off
    [InlineData(true, false)]    // the judge runs, but no verdict may reach a screen
    [InlineData(false, false)]   // both
    public void An_account_whose_wingman_is_switched_off_is_served_the_switched_off_words(
        bool judge, bool colour)
    {
        var verdicts = StoreHolding(NeedsYouVerdict());
        var pushed = PushedHolding(Sid);
        var settings = (TenantId _) => TurnVerdictSettings.Defaults with { JudgeEnabled = judge, ColourEnabled = colour };

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed, settings: settings));

        Assert.Equal(WingmanNowStates.SwitchedOff, now.State);
        Assert.Equal("The Wingman is switched off for your account", now.SwitchedOff!.Headline);
        Assert.Equal("Switch it on in Settings", now.SwitchedOff.SettingsLinkText);
        // The link that would explain the colour is gone, because no rule produced one.
        Assert.False(now.ShowWhyColour);
    }

    /// <summary>The positive control beside it: the SAME stored verdict on an account with the Wingman fully on is
    /// served as the stop it is. Without this, the test above would pass just as well on a route that answered
    /// "switched off" to everything.</summary>
    [Fact]
    public void The_same_stop_on_an_account_with_the_wingman_on_is_served_as_the_stop_it_is()
    {
        var verdicts = StoreHolding(NeedsYouVerdict());
        var pushed = PushedHolding(Sid);
        var settings = (TenantId _) =>
            TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true };

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed, settings: settings));

        Assert.Equal(WingmanNowStates.NeedsYou, now.State);
        Assert.Null(now.SwitchedOff);
        Assert.True(now.ShowWhyColour);
    }

    // ------------------------------------------- the session's own sessions reach the clock sentence

    /// <summary>A carrying-on stop: the session said it would keep going by itself, so nothing is needed.</summary>
    private static TurnVerdictDto CarryingOnVerdict() => new()
    {
        VerdictId = "verdict-carrying-on",
        JudgedAtUtc = Stopped.AddSeconds(4),
        TurnEndObservedAtUtc = Stopped,
        Verdict = Core.Wingman.TurnVerdictVocabulary.ContinuesAlone,
        Confidence = "high",
        Label = "Waiting for its Worker to finish the slice J test run",
        Summary = "The Worker is running the full test gate on pull request 2977.",
    };

    /// <summary>This session, plus one session it OWNS, pushed by the same Director.</summary>
    private PushedSessionStore PushedWithAnOwnedSession(string ownedState, DateTime ownedLastActivity)
    {
        _registry.RegisterFromStream(DirectorId, "SOREN_NORTH", "u", "test", 1, DateTime.UtcNow, Account);
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, DirectorId, "conn-owned");
        Assert.True(pushed.ApplySnapshot(Account, DirectorId, "conn-owned", 1, new List<SessionDto>
        {
            new()
            {
                SessionId = Sid,
                Name = "Wingman Inspector - Architect",
                Agent = "ClaudeCode",
                ActivityState = "WaitingForInput",
                WaitingSince = Stopped,
                LastActivityAt = Stopped,
                CreatedAt = Stopped.AddHours(-1),
            },
            new()
            {
                SessionId = Guid.NewGuid().ToString(),
                Name = "Wingman Inspector - Worker",
                Agent = "ClaudeCode",
                ActivityState = ownedState,
                LastActivityAt = ownedLastActivity,
                CreatedAt = Stopped.AddMinutes(-30),
                IsControlled = true,
                ControllerSessionId = Sid,
            },
        }));
        return pushed;
    }

    /// <summary>
    /// THE ROUTE READS THE SESSION'S OWN SESSIONS, AND THE CLOCK SENTENCE CHANGES BECAUSE OF THEM.
    ///
    /// The fold's tests prove what the card says when it is HANDED the owned-session facts. Nothing there watches
    /// whether the route gathers them at all - and a route that never did would leave every one of them green
    /// while promising the owner a deadline the clock was not counting towards, on exactly the session that owns a
    /// running Worker. So this drives the handler with a real roster holding a real owned session.
    /// </summary>
    [Theory]
    [InlineData("Working")]            // the Worker is working
    [InlineData("WaitingForInput")]    // the Worker is alive but quiet - inside one long silent command
    public void A_session_with_a_live_session_under_it_is_served_no_deadline(string ownedState)
    {
        var verdicts = StoreHolding(CarryingOnVerdict());
        var pushed = PushedWithAnOwnedSession(ownedState, DateTime.UtcNow);

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed, streamStale: TimeSpan.FromMinutes(5)));

        Assert.Equal(WingmanNowStates.CarryingOn, now.State);
        Assert.Null(now.CarryingOnDeadline);
        Assert.Equal("It turns red if it stops working and none of the sessions it owns is still working.",
            now.CalmCard!.Body);
    }

    /// <summary>The positive control: the SAME stop on a session that owns nothing is served a real deadline, and
    /// it is the moment the clock expires on. Without this, the test above would pass on a route that answered
    /// "no deadline" to everything.</summary>
    [Fact]
    public void The_same_stop_with_nothing_under_it_is_served_the_moment_the_clock_expires_on()
    {
        var verdict = CarryingOnVerdict();
        var verdicts = StoreHolding(verdict);
        var pushed = PushedHolding(Sid);

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, pushed, streamStale: TimeSpan.FromMinutes(5)));

        Assert.Equal(WingmanNowStates.CarryingOn, now.State);
        Assert.Equal("If it has not worked again by", now.CarryingOnDeadline!.Before);
        Assert.Null(now.CalmCard!.Body);

        var at = now.CarryingOnDeadline.AtUtc;
        Assert.True(TurnVerdictWatchdog.IsExpired(verdict, at));
        Assert.False(TurnVerdictWatchdog.IsExpired(verdict, at.AddTicks(-1)));
    }

    // ------------------------------------------- a working session, served through the real handler

    /// <summary>This session, WORKING, pushed by its Director.</summary>
    private PushedSessionStore PushedWorking(DateTime? ownerTurn = null)
    {
        _registry.RegisterFromStream(DirectorId, "SOREN_NORTH", "u", "test", 1, DateTime.UtcNow, Account);
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, DirectorId, "conn-working");
        Assert.True(pushed.ApplySnapshot(Account, DirectorId, "conn-working", 1, new List<SessionDto>
        {
            new()
            {
                SessionId = Sid,
                Name = "Wingman Inspector - Manager",
                Agent = "ClaudeCode",
                ActivityState = "Working",
                LastActivityAt = Stopped.AddMinutes(2),
                LastOwnerTurnAtUtc = ownerTurn,
            },
        }));
        return pushed;
    }

    /// <summary>
    /// A WORKING SESSION IS SERVED WHAT IT IS DOING, from the real store and the real conversation.
    ///
    /// The fold's tests prove what it says when handed a superseded record and a conversation. This drives the
    /// whole path: the verdict is stored and then invalidated the way a Working transition invalidates it, so the
    /// superseding moment is the store's own rather than a number a test wrote onto a hand-built object, and the
    /// message is read out of the stored conversation.
    /// </summary>
    [Fact]
    public void A_working_session_is_served_how_long_it_has_worked_and_what_it_was_asked()
    {
        var wentBackToWork = Stopped.AddMinutes(1);
        var verdicts = StoreHolding(NeedsYouVerdict());
        Assert.Equal(1, verdicts.Invalidate(Account, Sid, wentBackToWork));

        var askedAt = Stopped.AddMinutes(2);
        var turns = ConversationHolding(("Assistant", "Either merge 3002 yourself, or allow that command."));

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, PushedWorking(askedAt.AddSeconds(2)), turns,
            streamStale: TimeSpan.FromMinutes(5)));

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Equal("Working", now.PillText);
        Assert.Equal("Working for", now.When!.Lead);
        Assert.Equal(wentBackToWork, now.When.AtUtc);
        Assert.True(now.When.ElapsedOnly);

        // The stop it came from, read back out of the store it was superseded in.
        Assert.Equal("Last stop", now.LastStop!.Lead);
        Assert.Equal(Stopped, now.LastStop.AtUtc);
        Assert.Contains("Needs you - ", now.LastStop.Text);

        // And nothing is claimed about a stop it is not in.
        Assert.Null(now.Needs);
        Assert.Null(now.CalmCard);
    }

    /// <summary>
    /// THE ROUTE ASKS WHETHER A SCHEDULE STARTED THIS SESSION, AND ASKS ABOUT THE RIGHT ONE.
    ///
    /// <see cref="WingmanNowFoldTests"/> proves what the fold says when it is TOLD a schedule started the session.
    /// Nothing there watches whether the route asks at all: a handler that never looked would leave every one of
    /// those green while the card on the screen still said a bare "at 6:00 AM". So this drives the real handler
    /// and reads both the word it served and the arguments it asked with.
    /// </summary>
    [Fact]
    public void A_working_session_a_schedule_started_is_served_who_asked_it()
    {
        var askedAt = Stopped.AddMinutes(2);
        var turns = ConversationHolding(("User", "Run the daily hygiene audit and report only on drift."));
        var asked = new List<(TenantId Tenant, string SessionId)>();

        var now = BodyOf(Read(Caller.Device, Sid, StoreHolding(null), PushedWorking(), turns,
            streamStale: TimeSpan.FromMinutes(5),
            startedBySchedule: (tenant, sid) =>
            {
                asked.Add((tenant, sid));
                return string.Equals(sid, Sid, StringComparison.Ordinal);
            }));

        Assert.Equal(WingmanNowStates.Working, now.State);
        Assert.Equal("A schedule", now.LastAsked!.By);
        Assert.Equal("A schedule, at", now.LastAsked.WhenLead);
        // The account it resolved and the session it was asked about, not some other pair.
        Assert.Equal((Account, Sid), Assert.Single(asked));
    }

    /// <summary>
    /// THE CONTROL BESIDE IT: the same session, with nothing that names a schedule, says nothing about who asked.
    /// Without this, the test above would pass just as well on a route that wrote "A schedule" onto everything.
    /// </summary>
    [Fact]
    public void A_working_session_no_schedule_names_is_served_no_asker()
    {
        var turns = ConversationHolding(("User", "Run the daily hygiene audit and report only on drift."));

        var never = BodyOf(Read(Caller.Device, Sid, StoreHolding(null), PushedWorking(), turns,
            streamStale: TimeSpan.FromMinutes(5), startedBySchedule: (_, _) => false));
        Assert.Null(never.LastAsked!.By);
        Assert.Equal("at", never.LastAsked.WhenLead);

        // And a Gateway handed no way to look claims nothing either, rather than guessing in either direction.
        var unasked = BodyOf(Read(Caller.Device, Sid, StoreHolding(null), PushedWorking(), turns,
            streamStale: TimeSpan.FromMinutes(5)));
        Assert.Null(unasked.LastAsked!.By);
    }

    /// <summary>The message the owner sent, read out of the STORED conversation, with his own name on it because
    /// the Director's record of his turn sits beside it.</summary>
    [Fact]
    public void The_message_a_working_session_was_last_sent_is_read_from_the_stored_conversation()
    {
        var verdicts = StoreHolding(NeedsYouVerdict());
        verdicts.Invalidate(Account, Sid, Stopped.AddMinutes(1));

        // ConversationHolding times message n at Stopped + n seconds, so the user message below is Stopped + 1s.
        var turns = ConversationHolding(
            ("Assistant", "Either merge 3002 yourself, or allow that command."),
            ("User", "Allow the merge, and tag straight after it."));

        var now = BodyOf(Read(Caller.Device, Sid, verdicts, PushedWorking(Stopped.AddSeconds(1)), turns,
            streamStale: TimeSpan.FromMinutes(5)));

        Assert.Equal("What it was last asked", now.LastAsked!.Heading);
        Assert.Equal("Allow the merge, and tag straight after it.", now.LastAsked.Text);
        Assert.Equal("You", now.LastAsked.By);
        Assert.Equal("You, at", now.LastAsked.WhenLead);
    }
}
