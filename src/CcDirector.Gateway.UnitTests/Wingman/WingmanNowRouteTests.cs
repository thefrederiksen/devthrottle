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

    private static TurnVerdictDto NeedsYouVerdict() => new()
    {
        VerdictId = "verdict-now-1",
        JudgedAtUtc = Stopped.AddSeconds(4),
        TurnEndObservedAtUtc = Stopped,
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
        SessionTurnStore? turns = null, Func<TenantId, TurnVerdictSettings>? settings = null)
        => GatewayEndpoints.ReadWingmanNow(Request(caller), sid, SelfHostBoundary(), _registry, pushed, verdicts,
            turns, null, null, verdicts is null ? null : RowSourceOver(verdicts), null, settings);

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
        var verdicts = StoreHolding(NeedsYouVerdict());
        var pushed = PushedHolding(Sid);

        var before = BodyOf(Read(Caller.Device, Sid, verdicts, pushed));
        Assert.True(before.CanAnswerByOption);

        Assert.True(verdicts.MarkAnswered(Account, "verdict-now-1", Stopped.AddMinutes(2)));

        var after = BodyOf(Read(Caller.Device, Sid, verdicts, pushed));
        Assert.False(after.CanAnswerByOption);
        Assert.Equal(2, after.Needs!.Options.Count);
    }

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
}
