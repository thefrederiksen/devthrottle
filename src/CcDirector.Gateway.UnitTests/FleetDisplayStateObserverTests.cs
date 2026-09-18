using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The display-state push seam: the Gateway stamps each session's folded answer down to its owning Director,
/// and the two legs that make that STAY correct - the change gate (stamping a fold down does not make the
/// observer chase its own echo), and a dropped send being retried rather than recorded as delivered.
///
/// The fold itself is proved elsewhere (SessionOrderingTests); here the fold is a deterministic stub so the
/// tests target the observer's gate and payload, exactly as FleetRoleObserverTests target the role
/// observer's. Design: docs/new_architecture/sessions.html.
/// </summary>
public sealed class FleetDisplayStateObserverTests
{
    private static SessionDto Session(string id, string state = "WaitingForInput") => new()
    {
        SessionId = id,
        ActivityState = state,
    };

    /// <summary>A stub fold: colour/label/triage derived from the raw activity, so a test can change the
    /// answer by changing the session's ActivityState - the same shape the real fold produces.</summary>
    private static void StubFold(List<SessionDto> sessions)
    {
        foreach (var s in sessions)
        {
            var working = string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase);
            s.EffectiveColor = working ? "blue" : "red";
            s.StateLabel = working ? "Working" : "Needs you";
            s.TriageBucket = working ? "active" : "needsYou";
        }
    }

    private sealed class RecordingSender
    {
        public readonly List<(string DirectorId, string SessionId, SetDisplayStateRequest Payload)> Sent = new();
        public Func<DirectorCommandResult?> Reply = () => DirectorCommandResult.Success();

        public Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<SetDisplayStateRequest>(
                command.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            lock (Sent) Sent.Add((directorId, command.SessionId, payload!));
            return Task.FromResult(Reply());
        }
    }

    /// <summary>
    /// THE LOOP. Stamping a fold down makes the Director report it back up on its next delta
    /// (ControlEndpoints.Map echoes the cached values), which lands back in Observe. Without the change gate
    /// that is fold -> delta -> observe -> fold, forever. Sweeping an unchanged fleet must send exactly ONCE.
    /// </summary>
    [Fact]
    public void RepeatedSweeps_WithAnUnchangedFleet_SendTheFoldOnlyOnce()
    {
        var sender = new RecordingSender();
        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        for (var i = 0; i < 5; i++)
            observer.Sweep();

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("dir-A", sent.DirectorId);
        Assert.Equal("s1", sent.SessionId);
        Assert.Equal("red", sent.Payload.EffectiveColor);
        Assert.Equal("Needs you", sent.Payload.StateLabel);
        Assert.Equal("needsYou", sent.Payload.TriageBucket);
    }

    /// <summary>The gate must not be a mute button: when the fold genuinely CHANGES, the new answer goes down
    /// again. A gate that suppressed real changes would trade the echo for a stale desktop - the same defect.</summary>
    [Fact]
    public void WhenTheFoldChanges_TheNewFoldIsSentDown()
    {
        var sender = new RecordingSender();
        var s1 = Session("s1");
        var fleet = new List<(string, SessionDto)> { ("dir-A", s1) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();
        Assert.Equal("red", sender.Sent.Single().Payload.EffectiveColor);

        // The session starts working -> the fold flips to blue -> the desktop must be told.
        s1.ActivityState = "Working";
        observer.Sweep();

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("blue", sender.Sent[^1].Payload.EffectiveColor);
        Assert.Equal("active", sender.Sent[^1].Payload.TriageBucket);
    }

    /// <summary>
    /// THE ROW LINE RIDES THE SAME PUSH (Message Load mission, slice 4). The desktop cannot read the inbox, so the
    /// Gateway's line must be in the payload, and a line that changes with nothing else changing - a message
    /// arriving, a stuck age ticking over - must go down again. Clearing it must go down too.
    /// </summary>
    [Fact]
    public void TheInboxLine_IsCarriedDown_AndAChangeToItAloneIsPushed()
    {
        var sender = new RecordingSender();
        var s1 = Session("s1");
        var fleet = new List<(string, SessionDto)> { ("dir-A", s1) };
        string? line = "2 messages waiting";
        var observer = new FleetDisplayStateObserver(
            () => fleet,
            sessions => { StubFold(sessions); foreach (var s in sessions) s.InboxLine = line; },
            sender.SendAsync);

        observer.Sweep();
        observer.Sweep();
        Assert.Equal("2 messages waiting", Assert.Single(sender.Sent).Payload.InboxLine);

        line = "1 message stuck, unread for 16 minutes";
        observer.Sweep();
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("1 message stuck, unread for 16 minutes", sender.Sent[^1].Payload.InboxLine);
        Assert.Equal("red", sender.Sent[^1].Payload.EffectiveColor);

        line = null;
        observer.Sweep();
        Assert.Equal(3, sender.Sent.Count);
        Assert.Null(sender.Sent[^1].Payload.InboxLine);
    }

    /// <summary>
    /// THE STAMP STORM the per-tenant gate exists to prevent (issue #1966). On the hosted Gateway the sweep
    /// runs one pass PER TENANT (GatewayHost wraps Sweep in ITenantPass.ForEachTenant), each pass seeing only
    /// that tenant's fleet. With a SINGLE flat gate, tenant t2's pass would prune s1 (not in t2's live set)
    /// out of the gate every round, so the next round re-sends s1 - and t1's pass evicts s2 likewise - a
    /// re-send storm every 5 seconds. The gate is partitioned per tenant scope, so each session is sent EXACTLY
    /// ONCE across many rounds. Model of ForEachTenant: set the ambient scope, snapshot that tenant's slice.
    /// </summary>
    [Fact]
    public void PerTenantPasses_DoNotEvictEachOthersGate_NoStormAcrossTenants()
    {
        var sender = new RecordingSender();
        var byTenant = new Dictionary<string, List<(string, SessionDto)>>
        {
            ["t1"] = new() { ("dir-A", Session("s1")) },
            ["t2"] = new() { ("dir-B", Session("s2")) },
        };
        var current = "t1";
        var observer = new FleetDisplayStateObserver(
            () => byTenant[current], StubFold, sender.SendAsync, currentScopeKey: () => current);

        // Three full ForEachTenant rounds: each round sweeps t1 then t2.
        for (var round = 0; round < 3; round++)
        {
            current = "t1"; observer.Sweep();
            current = "t2"; observer.Sweep();
        }

        // Exactly ONE send per session across all three rounds. A shared flat gate would show ~3 sends each.
        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains(sender.Sent, x => x is { DirectorId: "dir-A", SessionId: "s1" });
        Assert.Contains(sender.Sent, x => x is { DirectorId: "dir-B", SessionId: "s2" });
    }

    /// <summary>The partition must not become a mute button either: a fold change in ONE tenant is delivered,
    /// and it does not disturb the OTHER tenant's already-settled gate.</summary>
    [Fact]
    public void AFoldChangeInOneTenant_IsDelivered_AndDoesNotResendTheOtherTenant()
    {
        var sender = new RecordingSender();
        var s1 = Session("s1");
        var byTenant = new Dictionary<string, List<(string, SessionDto)>>
        {
            ["t1"] = new() { ("dir-A", s1) },
            ["t2"] = new() { ("dir-B", Session("s2")) },
        };
        var current = "t1";
        var observer = new FleetDisplayStateObserver(
            () => byTenant[current], StubFold, sender.SendAsync, currentScopeKey: () => current);

        current = "t1"; observer.Sweep();   // s1 -> red
        current = "t2"; observer.Sweep();   // s2 -> red
        sender.Sent.Clear();

        // s1 starts working; only t1's pass should re-send, and only s1.
        s1.ActivityState = "Working";
        current = "t1"; observer.Sweep();
        current = "t2"; observer.Sweep();

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("s1", sent.SessionId);
        Assert.Equal("blue", sent.Payload.EffectiveColor);
    }

    /// <summary>A Director with no tunnel gets no stamp, and the observer must NOT record that as delivered -
    /// the fold has to be re-sent the moment it reconnects. Recording a failed send as done would leave that
    /// desktop permanently wrong, and silently.</summary>
    [Fact]
    public void WhenTheDirectorHasNoStream_TheStampIsRetriedOnTheNextSweep()
    {
        var sender = new RecordingSender { Reply = () => null };   // null = no active stream
        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();
        observer.Sweep();

        Assert.Equal(2, sender.Sent.Count);
    }

    /// <summary>
    /// THE ROLLOUT STORM. The normal deploy order ships the Gateway BEFORE the Directors, so for a while an
    /// old Director rejects this brand-new verb with BadRequest. That rejection must be recorded like a
    /// delivery - one send per fold change - not retried every single sweep, or every session that Director
    /// owns generates a rejected command every few seconds until it upgrades.
    /// </summary>
    [Fact]
    public void WhenADirectorRejectsTheVerb_ItIsNotReSentEverySweep()
    {
        var sender = new RecordingSender
        {
            Reply = () => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "unknown verb 'set-display-state'"),
        };
        var fleet = new List<(string, SessionDto)> { ("dir-old", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        for (var i = 0; i < 5; i++)
            observer.Sweep();

        // Recorded on the first rejection, so the unchanged fold is not re-sent to the old Director.
        Assert.Single(sender.Sent);
    }

    /// <summary>
    /// FINDING 3 (inspection). The desktop's raw <c>Session.OnHold</c> still drives the rail's Snooze-versus-
    /// Unsnooze menu, and it was healed only by a one-shot, unretried hold mirror. Carry the folded HoldState
    /// on THIS reliable, change-gated channel so a dropped mirror self-heals. First: it rides the down-stamp.
    /// </summary>
    [Fact]
    public void TheFoldedHoldState_RidesTheDownStamp()
    {
        var sender = new RecordingSender();
        var s1 = Session("s1");
        s1.HoldState = HoldStates.Held; // the fold stamps a real hold state onto the session
        var fleet = new List<(string, SessionDto)> { ("dir-A", s1) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();

        Assert.Equal(HoldStates.Held, sender.Sent.Single().Payload.HoldState);
    }

    /// <summary>
    /// FINDING 3, the self-heal itself: when ONLY the hold state changes (colour/label/triage unchanged), the
    /// new hold must still be stamped down - or the desktop keeps a stale raw OnHold. This is exactly the
    /// work-deletes-an-armed-snooze transition: the session was Held, then None, while its activity (and so
    /// its folded colour here) does not move.
    /// </summary>
    [Fact]
    public void WhenOnlyTheHoldStateChanges_TheNewHoldIsSentDown()
    {
        var sender = new RecordingSender();
        var s1 = Session("s1");
        s1.HoldState = HoldStates.Held;
        var fleet = new List<(string, SessionDto)> { ("dir-A", s1) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();
        Assert.Equal(HoldStates.Held, sender.Sent.Single().Payload.HoldState);

        // Work deleted the snooze: the fold now reads None. Nothing else about the session changed.
        s1.HoldState = HoldStates.None;
        observer.Sweep();

        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(HoldStates.None, sender.Sent[^1].Payload.HoldState);
    }

    /// <summary>
    /// ROUND 5 FINDING 1. The prompt push runs on the user's Snooze / Unsnooze CLICK path, so it must never
    /// hang on a connected-but-unresponsive Director. It routes through the bounded, cancellable
    /// DirectorCommandRouter chokepoint carrying the request token, so cancelling the request unblocks the
    /// wait at once. Here a sender that never answers on its own (but observes the token) stands in for that
    /// Director; if the send used CancellationToken.None - the old unbounded direct-transport path -
    /// cancelling would do nothing and this would hang past the 5-second safety wait and fail.
    /// </summary>
    [Fact]
    public async Task PushSessionAsync_IsCancellable_SoAnUnresponsiveDirectorCannotHangTheClick()
    {
        Task<DirectorCommandResult?> NeverAnswers(string _, DirectorCommand __, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<DirectorCommandResult?>();
            token.Register(() => tcs.TrySetCanceled(token)); // only the deadline/cancel token unblocks it
            return tcs.Task;
        }

        var s1 = Session("s1");
        s1.HoldState = HoldStates.Held; // a real fold, so the change gate does not short-circuit the send
        var fleet = new List<(string, SessionDto)> { ("dir-A", s1) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, NeverAnswers);

        using var cts = new CancellationTokenSource();
        var push = observer.PushSessionAsync("s1", cts.Token);
        cts.Cancel();

        // Cancelling the request unblocks the wait promptly (well within the 30s command deadline). If the
        // send ignored the token, WaitAsync would time out and throw TimeoutException instead.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => push.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A session that leaves the fleet must drop out of the change gate, so if it ever returns its
    /// fold is stamped fresh rather than suppressed by a stale gate entry.</summary>
    [Fact]
    public void ASessionThatLeavesTheFleet_IsStampedAgainIfItReturns()
    {
        var sender = new RecordingSender();
        var fleet = new List<(string, SessionDto)> { ("dir-A", Session("s1")) };
        var observer = new FleetDisplayStateObserver(() => fleet, StubFold, sender.SendAsync);

        observer.Sweep();
        Assert.Single(sender.Sent);

        fleet.Clear();
        observer.Sweep();                       // s1 is gone - the gate entry must be pruned

        fleet.Add(("dir-A", Session("s1")));
        observer.Sweep();                       // ...so its return is a fresh stamp, not a suppressed echo

        Assert.Equal(2, sender.Sent.Count);
    }
}
