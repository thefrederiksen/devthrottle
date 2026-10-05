using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.Teams;

/// <summary>
/// devthrottle_internal#2311, review finding S2-F13: in a team the Gateway decides whose a session is from its
/// key row, and the first Director to register a key for a session id keeps it. So no session id may reach the
/// Gateway from a Director before that session's key has been sent - not in the reseed's roster, not in a delta,
/// not in a turn push. These tests record every hub call the stream client makes, in order, against a recording
/// hub; nothing touches the network. They live here because this is the test project the default local gate runs
/// that can see the control plane.
/// </summary>
public sealed class DirectorKeysBeforeRosterTests
{
    private const string Alpha = "11111111-1111-1111-1111-111111111111";
    private const string Bravo = "22222222-2222-2222-2222-222222222222";

    // ===================== the reseed =====================

    [Fact]
    public async Task ReseedAsync_OnANewConnection_RunsTheNewConnectionCallbackFirst_AndOnARePushNever()
    {
        // Issue #3559: a Director drops every session's Fleet Manager lessons when a NEW connection opens, before its
        // first roster goes up, so the Gateway's restamp on that roster is the only lessons block left. The timed
        // re-push reuses the connection and must leave them alone - a compaction in that gap would lose them.
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha },
            onNewConnection: () => hub.Calls.Add(new HubCall("NewConnection", Array.Empty<string>(), false)));

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration, newConnection: true);
        Assert.Equal("NewConnection", hub.Calls[0].Method);
        Assert.Single(hub.Calls, c => c.Method == "NewConnection");

        hub.Calls.Clear();
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);
        Assert.DoesNotContain(hub.Calls, c => c.Method == "NewConnection");
        Assert.Equal(2, hub.Calls.Count(c => c.Method == "PushSnapshot"));
    }

    [Fact]
    public async Task ReseedAsync_TheNewConnectionCallbackThrows_TheReseedStillCompletes()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha },
            onNewConnection: () => throw new InvalidOperationException("a session could not drop its lessons"));

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration, newConnection: true);

        Assert.True(report.Completed);
        Assert.Single(hub.Calls, c => c.Method == "PushSnapshot");
    }

    [Fact]
    public async Task ReseedAsync_SessionsWithKeys_EveryKeyIsSentBeforeTheRosterThatListsIt()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha, Bravo });

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.True(report.Completed);
        var roster = Assert.Single(hub.Calls, c => c.Method == "PushSnapshot");
        var rosterAt = hub.Calls.IndexOf(roster);
        Assert.Equal(new[] { Alpha, Bravo }, roster.SessionIds);
        foreach (var listed in roster.SessionIds)
        {
            var keyAt = hub.Calls.FindIndex(c => c.Method == "RegisterSessionKey" && c.SessionIds.Contains(listed));
            Assert.True(keyAt >= 0, $"the roster lists {listed} but its key was never sent");
            Assert.True(keyAt < rosterAt, $"the key for {listed} was sent at call {keyAt}, AFTER the roster at call {rosterAt}");
        }
        Assert.Equal("Hello", hub.Calls[0].Method);
    }

    [Fact]
    public async Task ReseedAsync_OneKeyRegistrationThrows_TheOtherKeysAndTheRosterAreStillSent()
    {
        var hub = new RecordingHub { RefuseKeyFor = Alpha };
        var client = NewClient(sessionIds: new[] { Alpha, Bravo });

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.True(report.Completed);
        Assert.Null(report.Failure);
        Assert.Contains(hub.Calls, c => c.Method == "RegisterSessionKey" && c.SessionIds.Contains(Bravo));
        var roster = Assert.Single(hub.Calls, c => c.Method == "PushSnapshot");
        Assert.Equal(new[] { Alpha, Bravo }, roster.SessionIds);
    }

    [Fact]
    public async Task ReseedAsync_TheKeyListItselfThrows_TheRosterIsStillSent()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha },
            sessionKeys: () => throw new InvalidOperationException("the key store could not be read"));

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.True(report.Completed);
        Assert.Single(hub.Calls, c => c.Method == "PushSnapshot");
    }

    [Fact]
    public async Task ReseedAsync_TheRosterPushThrows_TheFailureIsReported()
    {
        var hub = new RecordingHub { RefuseSnapshot = true };
        var client = NewClient(sessionIds: new[] { Alpha });

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.False(report.Completed);
        Assert.Equal("the Gateway refused the roster", report.Failure);
    }

    [Fact]
    public async Task ReseedAsync_HelloThrows_NoRosterIsSentAndNoSessionIdMayGoUp()
    {
        var hub = new RecordingHub { RefuseHello = true };
        var client = NewClient(sessionIds: new[] { Alpha });

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.False(report.Completed);
        Assert.DoesNotContain(hub.Calls, c => c.Method == "PushSnapshot");
        Assert.False(client.SessionIdsMayGoUp);
    }

    [Fact]
    public async Task ReseedAsync_HelloCallback_RunsOnlyAfterEveryKeyIsSent()
    {
        // The Hello's answer seeds the turn pusher, which at once starts a sweep that pushes every session's
        // turns - session ids. It must not be handed on until the keys have gone up.
        var hub = new RecordingHub { Capabilities = new GatewayCapabilities { Version = "test" } };
        var callbackAt = -1;
        var gateOpenAtCallback = false;
        GatewayStreamClient? client = null;
        client = NewClient(sessionIds: new[] { Alpha, Bravo }, onHello: _ =>
        {
            callbackAt = hub.Calls.Count;
            gateOpenAtCallback = client!.SessionIdsMayGoUp;
        });

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        var lastKeyAt = hub.Calls.FindLastIndex(c => c.Method == "RegisterSessionKey");
        Assert.True(callbackAt > lastKeyAt, $"the Hello callback ran after {callbackAt} call(s), before the last key at call {lastKeyAt}");
        Assert.True(gateOpenAtCallback);
    }

    [Fact]
    public async Task ReseedAsync_KeysAreSentWhileTheGateIsShut_AndTheRosterGoesUpWithItOpen()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha, Bravo });
        hub.Observe = () => client.SessionIdsMayGoUp;

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.All(hub.Calls.Where(c => c.Method == "RegisterSessionKey"), c => Assert.False(c.GateOpen));
        Assert.True(Assert.Single(hub.Calls, c => c.Method == "PushSnapshot").GateOpen);
    }

    [Fact]
    public async Task ReseedAsync_OnAConnectionLostWhileItRan_SendsNoRosterOnTheNewOne()
    {
        // #3558 review, K-F1. The connection drops during the key leg and comes straight back: the hub reports
        // connected again (one connection object, reconnected in place), but it is a NEW connection whose own
        // reseed has not sent its keys. The old reseed must send no roster there, and open no gate.
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha });
        var generation = client.CurrentConnectionGeneration;
        hub.OnKeyRegistered = () => client.MarkConnectionLost();

        var report = await client.ReseedAsync(hub, generation);

        Assert.True(hub.IsConnected);
        Assert.DoesNotContain(hub.Calls, c => c.Method == "PushSnapshot");
        Assert.False(report.Completed);
        Assert.False(client.SessionIdsMayGoUp);
    }

    // ===================== the delta =====================

    [Fact]
    public void NotifyDelta_BeforeThisConnectionsKeysAreSent_IsHeldBackAndNothingIsSent()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha });

        client.NotifyDelta(new SessionDto { SessionId = Alpha }, hub);

        Assert.Empty(hub.Calls);
    }

    [Fact]
    public async Task NotifyDelta_AfterTheReseedSentTheKeys_IsSent()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha });
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);
        hub.Calls.Clear();

        client.NotifyDelta(new SessionDto { SessionId = Alpha }, hub);

        var delta = Assert.Single(hub.Calls);
        Assert.Equal("PushDelta", delta.Method);
        Assert.Equal(new[] { Alpha }, delta.SessionIds);
    }

    [Fact]
    public async Task NotifyDelta_AfterTheConnectionIsLostAndBack_IsHeldBackUntilThatConnectionsReseed()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha });
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);
        client.MarkConnectionLost();
        hub.Calls.Clear();

        client.NotifyDelta(new SessionDto { SessionId = Alpha }, hub);
        Assert.Empty(hub.Calls);

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);
        hub.Calls.Clear();
        client.NotifyDelta(new SessionDto { SessionId = Alpha }, hub);
        Assert.Equal("PushDelta", Assert.Single(hub.Calls).Method);
    }

    // ===================== the turn push =====================

    [Fact]
    public async Task PushTurnsAsync_BeforeThisConnectionsKeysAreSent_IsRefusedAndNothingIsSent()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PushTurnsAsync(new TurnPushBatch { SessionId = Alpha }, hub, CancellationToken.None));

        Assert.Empty(hub.Calls);
    }

    [Fact]
    public async Task PushTurnsAsync_AfterTheReseedSentTheKeys_IsSent()
    {
        var hub = new RecordingHub();
        var client = NewClient(sessionIds: new[] { Alpha });
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);
        hub.Calls.Clear();

        await client.PushTurnsAsync(new TurnPushBatch { SessionId = Alpha }, hub, CancellationToken.None);

        var push = Assert.Single(hub.Calls);
        Assert.Equal("PushTurns", push.Method);
        Assert.Equal(new[] { Alpha }, push.SessionIds);
    }

    // ===================== helpers =====================

    private static GatewayStreamClient NewClient(
        IReadOnlyList<string> sessionIds,
        Func<List<SessionKeyRegistration>>? sessionKeys = null,
        Action<GatewayCapabilities>? onHello = null,
        Action? onNewConnection = null)
    {
        // No URL: the client is never started, so nothing dials. Every call goes to the recording hub.
        var config = new GatewayConfig();
        return new GatewayStreamClient(config, "dir-A", "test",
            snapshot: () => sessionIds.Select(id => new SessionDto { SessionId = id }).ToList(),
            sessionKeys: sessionKeys ?? (() => sessionIds
                .Select(id => new SessionKeyRegistration { SessionId = id, KeyHash = "hash-" + id, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) })
                .ToList()),
            onHello: onHello,
            onNewConnection: onNewConnection);
    }

    private sealed record HubCall(string Method, string[] SessionIds, bool GateOpen);

    /// <summary>Records each hub call in order, with the session ids it carried.</summary>
    private sealed class RecordingHub : IDirectorHubCalls
    {
        public List<HubCall> Calls { get; } = new();
        public string? RefuseKeyFor { get; init; }
        public bool RefuseHello { get; init; }
        public bool RefuseSnapshot { get; init; }
        public GatewayCapabilities? Capabilities { get; init; }
        public Func<bool>? Observe { get; set; }
        public Action? OnKeyRegistered { get; set; }

        public bool IsConnected => true;

        private void Record(string method, params string[] sessionIds)
            => Calls.Add(new HubCall(method, sessionIds, Observe?.Invoke() ?? false));

        public Task<GatewayCapabilities?> HelloAsync(DirectorStreamHello hello)
        {
            Record("Hello");
            if (RefuseHello) throw new InvalidOperationException("the Gateway refused Hello");
            return Task.FromResult(Capabilities);
        }

        public Task RegisterSessionKeyAsync(SessionKeyRegistration registration)
        {
            Record("RegisterSessionKey", registration.SessionId);
            if (registration.SessionId == RefuseKeyFor) throw new InvalidOperationException("the Gateway refused the key");
            OnKeyRegistered?.Invoke();
            return Task.CompletedTask;
        }

        public Task PushSnapshotAsync(long sequence, SessionDto[] sessions)
        {
            Record("PushSnapshot", sessions.Select(s => s.SessionId).ToArray());
            if (RefuseSnapshot) throw new InvalidOperationException("the Gateway refused the roster");
            return Task.CompletedTask;
        }

        public Task PushDeltaAsync(long sequence, SessionDto session)
        {
            Record("PushDelta", session.SessionId);
            return Task.CompletedTask;
        }

        public Task<TurnWatermark?> PushTurnsAsync(long sequence, TurnPushBatch batch, CancellationToken ct)
        {
            Record("PushTurns", batch.SessionId);
            return Task.FromResult<TurnWatermark?>(null);
        }

        public Task RevokeSessionKeyAsync(string sessionId)
        {
            Record("RevokeSessionKey", sessionId);
            return Task.CompletedTask;
        }

        public Task PushRepoSnapshotAsync(long sequence, RepoStatusDto[] repositories)
        {
            Record("PushRepoSnapshot");
            return Task.CompletedTask;
        }
    }
}
