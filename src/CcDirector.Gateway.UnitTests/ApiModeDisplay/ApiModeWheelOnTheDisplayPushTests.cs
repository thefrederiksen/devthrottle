using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.ApiModeDisplay;

/// <summary>
/// The API-mode verdict rides the display push down to the Director (mission #4070, phase 2 follow-on), so the desktop's
/// wheel button moves without a reload, exactly as the browsers' does on their roster poll.
///
/// The push folds through <see cref="DisplayFold"/> with the SAME facts and the SAME stamp the roster route uses, so the
/// pushed wheel button is the roster's; the push message carries it, and a change to it - the wheel changing hands - is
/// sent down again. The payload is additive: a Director built before it still reads the request. Everything is in
/// memory: the event buffer, a fake receipt read, the push store, and a recording sender.
/// </summary>
public sealed class ApiModeWheelOnTheDisplayPushTests
{
    private static readonly TenantId Account = TenantId.Local;
    private const string Sid = "44444444-4444-4444-8444-444444444444";
    private const string DirectorId = "dir-A";

    /// <summary>One fixed instant for every row and clock, so no test reads the machine's clock.</summary>
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private sealed class NoVerdicts : ITurnVerdictRowSource
    {
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => new Dictionary<string, TurnVerdictDto>();
        public bool IsReading(TenantId tenant, string sessionId) => false;
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class RecordingSender
    {
        public readonly List<SetDisplayStateRequest> Sent = new();

        public Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
        {
            lock (Sent) Sent.Add(JsonSerializer.Deserialize<SetDisplayStateRequest>(command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success());
        }
    }

    /// <summary>The fields a Director built before <see cref="SetDisplayStateRequest.ApiMode"/> knows, and no more.</summary>
    private sealed class SetDisplayStateRequestBeforeApiMode
    {
        public string? EffectiveColor { get; set; }
        public string? StateLabel { get; set; }
        public string? TriageBucket { get; set; }
        public DateTime? NeedsYouSince { get; set; }
        public DateTime? SnoozeUntil { get; set; }
        public bool SnoozeExpired { get; set; }
        public string? HoldState { get; set; }
        public string? InboxLine { get; set; }
    }

    private static SessionDto ApiRow(string wheelHolder = WheelHolders.Protocol) => new()
    {
        SessionId = Sid,
        Name = Sid,
        ActivityState = "WaitingForInput",
        LastActivityAt = Now,
        ConnectionMode = ConnectionModes.Api,
        WheelHolder = wheelHolder,
    };

    /// <summary>The facts over an in-memory event buffer and a receipt read that answers "no receipts" for every id.</summary>
    private static ApiModeDisplayFacts Facts() => new(
        new ApiModeEventBuffer(),
        (_, ids) => ids.ToDictionary(id => id, _ => (IReadOnlyList<ApiModeVerdictReceipt>)Array.Empty<ApiModeVerdictReceipt>()));

    private static DisplayFold Fold(PushedSessionStore pushed, ApiModeDisplayFacts? facts) => new(
        voice: () => null,
        needsYou: new NeedsYouClock(),
        voiceWaiting: new VoiceWaitingClock(utcNow: () => Now),
        snoozes: null,
        handRaises: new HandRaiseRegistry(),
        snoozeExpiry: () => null,
        pushed: pushed,
        enterScope: _ => new NoScope(),
        apiModeFacts: () => facts);

    [Fact]
    public void Push_AnApiModeSession_CarriesTheWheelActionTheRosterServes()
    {
        // Arrange: one set of facts, read by both the push and the roster's own stamp.
        var facts = Facts();
        var fold = Fold(new PushedSessionStore(), facts);
        var pushRows = new List<SessionDto> { ApiRow() };
        var rosterRows = new List<SessionDto> { ApiRow() };

        // Act
        fold.Push(Account, pushRows, new NoVerdicts());
        GatewayEndpoints.StampFleetRolesAndFold(rosterRows, rosterRows, tenant: Account, handRaises: new HandRaiseRegistry(),
            turnVerdictRows: new NoVerdicts(), apiModeFacts: facts);

        // Assert
        var pushed = pushRows.Single().ApiMode?.WheelAction;
        var roster = rosterRows.Single().ApiMode?.WheelAction;
        Assert.NotNull(roster);
        Assert.NotNull(pushed);
        Assert.Equal(ApiModeWheelVerbs.TakeTheWheel, roster.Verb);
        Assert.Equal(JsonSerializer.Serialize(roster), JsonSerializer.Serialize(pushed));
        Assert.Equal(JsonSerializer.Serialize(rosterRows.Single().ApiMode), JsonSerializer.Serialize(pushRows.Single().ApiMode));
    }

    [Fact]
    public void Record_AnApiModeSession_StampsNoApiModeVerdict()
    {
        // The trace records only the colour and the label, and the verdict reads nothing they read: push only.
        var fold = Fold(new PushedSessionStore(), Facts());
        var rows = new List<SessionDto> { ApiRow() };

        fold.Record(Account, rows, new NoVerdicts());

        Assert.Null(rows.Single().ApiMode);
    }

    [Fact]
    public void Push_WithNoAccountInScope_StampsNoApiModeVerdict()
    {
        // With no account there is no account whose facts to read - never a fall back to the local one.
        var fold = Fold(new PushedSessionStore(), Facts());
        var rows = new List<SessionDto> { ApiRow() };

        fold.Push(null, rows, new NoVerdicts());

        Assert.Null(rows.Single().ApiMode);
    }

    [Fact]
    public void Sweep_TheWheelChangesHands_PushesTheNewWheelAction()
    {
        // Arrange: the real fold behind the real observer, as the Gateway wires them.
        var row = ApiRow(WheelHolders.Protocol);
        var fleet = new List<(string, SessionDto)> { (DirectorId, row) };
        var fold = Fold(new PushedSessionStore(), Facts());
        var sender = new RecordingSender();
        var observer = new FleetDisplayStateObserver(() => fleet, sessions => fold.Push(Account, sessions, new NoVerdicts()), sender.SendAsync);

        // Act: the first push, an unchanged pass, then the user takes the wheel.
        observer.Sweep();
        observer.Sweep();
        row.WheelHolder = WheelHolders.User;
        observer.Sweep();

        // Assert: one send per wheel action, each carrying the button the fold ruled.
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(ApiModeWheelVerbs.TakeTheWheel, sender.Sent[0].ApiMode?.WheelAction?.Verb);
        Assert.Equal("Take the wheel", sender.Sent[0].ApiMode?.WheelAction?.Label);
        Assert.Equal(ApiModeWheelVerbs.HandBack, sender.Sent[1].ApiMode?.WheelAction?.Verb);
        Assert.Equal("Hand back", sender.Sent[1].ApiMode?.WheelAction?.Label);
        Assert.True(sender.Sent[1].ApiMode?.WheelAction?.Enabled);
    }

    [Fact]
    public void Sweep_ATerminalModeSession_SendsNoApiModeVerdict()
    {
        var row = ApiRow();
        row.ConnectionMode = ConnectionModes.Terminal;
        var fleet = new List<(string, SessionDto)> { (DirectorId, row) };
        var fold = Fold(new PushedSessionStore(), Facts());
        var sender = new RecordingSender();
        var observer = new FleetDisplayStateObserver(() => fleet, sessions => fold.Push(Account, sessions, new NoVerdicts()), sender.SendAsync);

        observer.Sweep();

        Assert.Null(Assert.Single(sender.Sent).ApiMode);
    }

    [Fact]
    public void Deserialize_ARequestCarryingApiMode_IsReadByADirectorBuiltBeforeIt()
    {
        // Arrange: the payload exactly as the Gateway serialises it (FleetDisplayStateObserver, web defaults).
        var payload = JsonSerializer.Serialize(new SetDisplayStateRequest
        {
            EffectiveColor = "red",
            StateLabel = "Needs you",
            TriageBucket = "needsYou",
            HoldState = "None",
            InboxLine = "2 messages waiting",
            ApiMode = new ApiModeSessionDisplay
            {
                Kind = "idle",
                WheelAction = new ApiModeWheelAction { Verb = ApiModeWheelVerbs.TakeTheWheel, Label = "Take the wheel", Enabled = true },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"apiMode\"", payload);

        // Act: read through the Director's own deserialiser into the shape an older Director knows.
        var older = SessionCommandExecutor.Deserialize<SetDisplayStateRequestBeforeApiMode>(payload);

        // Assert: accepted, and every field it knows is kept.
        Assert.NotNull(older);
        Assert.Equal("red", older.EffectiveColor);
        Assert.Equal("Needs you", older.StateLabel);
        Assert.Equal("needsYou", older.TriageBucket);
        Assert.Equal("None", older.HoldState);
        Assert.Equal("2 messages waiting", older.InboxLine);
    }
}
