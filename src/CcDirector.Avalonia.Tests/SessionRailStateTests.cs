using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression tests for the session rail's reading of state, after the desktop stopped folding for itself.
///
/// THE LAW (owner, 2026): every surface renders the GATEWAY'S folded answer and computes nothing. The rail
/// binds a REAL <see cref="SessionViewModel"/>, and its dot (StatusColorBrush), its row text (ActivityLabel),
/// its "N need you" verdict (NeedsYou), its waiting timer, its snooze countdown and its snooze-ended badge
/// all read the display state the Gateway STAMPS DOWN onto the Session (Session.Gateway*, applied by
/// <see cref="Session.ApplyGatewayDisplayState"/> and read back through ControlEndpoints.Map). The rail no
/// longer runs SessionOrdering over local facts - which is exactly why a snoozed session read red "Needs
/// you" here while the phone and the Cockpit read "Snoozed".
///
/// So these stamp the display state the Gateway would push, then assert the rail renders it verbatim. A
/// session with NO stamp shows a neutral placeholder - the "no Gateway, no fold" floor - not a local guess.
///
/// Design: docs/new_architecture/sessions.html
/// </summary>
public sealed class SessionRailStateTests
{
    /// <summary>A bare session with no Gateway stamp yet - the pre-first-push / no-tunnel shape.</summary>
    private static Session Bare()
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        return session;
    }

    // ===== The "no Gateway, no fold" floor: an unstamped session shows a neutral placeholder =====

    [Fact]
    public void NoGatewayStamp_RailShowsNeutralPlaceholder_NotCountedNotLabelled()
    {
        var vm = new SessionViewModel(Bare());

        Assert.Equal(Color.Parse(StatusPalette.Grey), ((ISolidColorBrush)vm.StatusColorBrush).Color);
        Assert.Equal("", vm.ActivityLabel);
        Assert.False(vm.NeedsYou);
        Assert.False(vm.HasWaitingDuration);
        Assert.False(vm.HasHoldTime);
        Assert.False(vm.IsSnoozeEnded);
    }

    // ===== The dot, the label and the count read the Gateway's stamp verbatim =====

    [Fact]
    public void NeedsYou_ReadsTheGatewayTriageStamp()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);
        Assert.True(vm.NeedsYou);

        // The Gateway folds a snooze onHold - the count must drop even though the raw session is at a turn end.
        session.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), false);
        Assert.False(vm.NeedsYou);

        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Assert.False(vm.NeedsYou);
    }

    [Fact]
    public void ActivityLabel_And_Dot_ReadTheGatewayStamp()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), false);

        Assert.Equal("Snoozed", vm.ActivityLabel);
        Assert.Equal(Color.Parse(StatusPalette.Grey), ((ISolidColorBrush)vm.StatusColorBrush).Color);
    }

    [Fact]
    public void StatusColorBrush_ReadsTheGatewayEffectiveColor()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);
        Assert.Equal(Color.Parse(StatusPalette.Red), ((ISolidColorBrush)vm.StatusColorBrush).Color);

        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Assert.Equal(Color.Parse(StatusPalette.Blue), ((ISolidColorBrush)vm.StatusColorBrush).Color);
    }

    /// <summary>An effective-color the desktop's palette does not know is a bug, not a state, and must hit
    /// the unmistakable magenta sentinel - never render as a real colour (grey would read as "parked").</summary>
    [Fact]
    public void StatusColorBrush_UnknownStampValue_FallsToMagentaSentinel()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.ApplyGatewayDisplayState("chartreuse", "???", "active", null, null, false);

        Assert.Equal(Color.Parse(StatusPalette.Broken), ((ISolidColorBrush)vm.StatusColorBrush).Color);
    }

    // ===== The waiting timer reads the Gateway's needs-you clock, so it matches every surface =====

    [Fact]
    public void WaitingDuration_ReadsTheGatewayNeedsYouSince()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow.AddMinutes(-11), null, false);

        Assert.True(vm.HasWaitingDuration);
        Assert.Equal("waiting 11m", vm.WaitingDurationLabel);
    }

    [Fact]
    public void WaitingDuration_IsHidden_WhenNotRed()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        // Snoozed carries a NeedsYouSince of null, and the colour is grey - the timer must not nag.
        session.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), false);

        Assert.False(vm.HasWaitingDuration);
        Assert.Equal("", vm.WaitingDurationLabel);
    }

    // ===== NEW: the hold time, from the Gateway's snooze clock =====

    [Fact]
    public void Snoozed_ShowsHoldTime_FromTheGatewaySnoozeClock()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        // Wakes in 3h 48m.
        session.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null,
            DateTime.UtcNow.AddHours(3).AddMinutes(48), false);

        Assert.True(vm.HasHoldTime);
        Assert.Equal("Snoozed", vm.ActivityLabel);
        Assert.StartsWith("wakes in 3h", vm.HoldTimeLabel);
    }

    [Fact]
    public void HoldTime_IsHidden_WhenThereIsNoSnoozeClock()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);

        Assert.False(vm.HasHoldTime);
        Assert.Equal("", vm.HoldTimeLabel);
    }

    // ===== NEW: the snooze-ended badge, from the Gateway's expiry overlay =====

    [Fact]
    public void SnoozeEnded_ShowsTheBadge_FromTheGatewayMarker()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        // An expired snooze folds back to red "Needs you" AND carries the just-returned marker.
        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, snoozeExpired: true);

        Assert.True(vm.IsSnoozeEnded);
        Assert.True(vm.NeedsYou);
    }

    // ===== A stamp arriving must move the WHOLE row together, not half of it =====

    [Fact]
    public void ADisplayStamp_MovesEveryRenderedFieldTogether()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changed.Add(e.PropertyName); };

        session.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", null, DateTime.UtcNow.AddHours(4), false);
        Dispatcher.UIThread.RunJobs();

        // Named individually and deliberately: asserting "something changed" would pass with the row text
        // stale, which IS the failure this whole change removes.
        Assert.Contains(nameof(SessionViewModel.StatusColorBrush), changed);
        Assert.Contains(nameof(SessionViewModel.ActivityLabel), changed);
        Assert.Contains(nameof(SessionViewModel.NeedsYou), changed);
        Assert.Contains(nameof(SessionViewModel.HasWaitingDuration), changed);
        Assert.Contains(nameof(SessionViewModel.WaitingDurationLabel), changed);
        Assert.Contains(nameof(SessionViewModel.HasHoldTime), changed);
        Assert.Contains(nameof(SessionViewModel.HoldTimeLabel), changed);
        Assert.Contains(nameof(SessionViewModel.IsSnoozeEnded), changed);

        // And the row agrees with itself afterwards.
        Assert.False(vm.NeedsYou);
        Assert.Equal("Snoozed", vm.ActivityLabel);
        Assert.True(vm.HasHoldTime);
    }

    [Fact]
    public void ClearingTheStamp_ReturnsTheRailToTheNeutralPlaceholder()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);
        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);
        Assert.True(vm.NeedsYou);   // precondition, so this cannot pass vacuously

        // The Gateway clears its stamp (a Director that lost its tunnel) by pushing a null colour.
        session.ApplyGatewayDisplayState(null, null, null, null, null, false);

        Assert.Equal(Color.Parse(StatusPalette.Grey), ((ISolidColorBrush)vm.StatusColorBrush).Color);
        Assert.Equal("", vm.ActivityLabel);
        Assert.False(vm.NeedsYou);
    }

    // ===== The role BADGE is a separate Gateway-owned fact (GatewayResolvedRole), unchanged by this work ===

    [Fact]
    public void ResolvedRole_BeforeAnyGatewayStamp_IsUnknown_NotAssertedStandalone()
    {
        var vm = new SessionViewModel(Bare());

        Assert.Null(vm.ResolvedRole);
        Assert.False(vm.HasRoleGlyph);
        Assert.Equal("", vm.RoleGlyphText);
        Assert.Equal("", vm.RoleTooltip);
    }

    [Fact]
    public void ResolvedRole_FollowsTheGatewayStamp()
    {
        var session = Bare();
        session.ControllerSessionId = Guid.NewGuid();   // a controller on another machine
        var vm = new SessionViewModel(session);

        session.SetGatewayResolvedRole(SessionRoles.Worker);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SessionRoles.Worker, vm.ResolvedRole);
        Assert.True(vm.HasRoleGlyph);
        Assert.Equal("W", vm.RoleGlyphText);
        Assert.Equal("Worker", vm.RoleTooltip);
    }

    [Fact]
    public void ResolvedRole_WhenTheGatewayClearsTheStamp_ReturnsToUnknown()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);
        session.SetGatewayResolvedRole(SessionRoles.Worker);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("W", vm.RoleGlyphText);   // precondition

        session.SetGatewayResolvedRole(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ResolvedRole);
        Assert.False(vm.HasRoleGlyph);
        Assert.Equal("", vm.RoleGlyphText);
    }

    // ===== The uncommitted-work badge ("N chg") =====
    //
    // The rail used to compute this itself, on a timer the window owned, so the number existed only here
    // and never reached the Gateway. It now reads the Session, which the Director's SessionGitStatusMonitor
    // writes and ControlEndpoints.Map puts on the wire - so the rail and the Cockpit roster show one number.

    [Fact]
    public void UncommittedBadge_UnprobedSession_ShowsNoBadge()
    {
        var vm = new SessionViewModel(Bare());

        // Null on the Session is UNKNOWN. The badge must be absent, not read "0 chg" - which would claim a
        // clean tree nobody has measured (issue 516).
        Assert.False(vm.HasUncommittedChanges);
        Assert.Equal(0, vm.UncommittedCount);
    }

    [Fact]
    public void UncommittedBadge_CleanTree_ShowsNoBadge()
    {
        var session = Bare();
        session.UncommittedCount = 0;

        var vm = new SessionViewModel(session);

        Assert.False(vm.HasUncommittedChanges);
    }

    [Fact]
    public void UncommittedBadge_DirtyTree_ShowsTheCountFromTheSession()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.UncommittedCount = 12;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasUncommittedChanges);
        Assert.Equal(12, vm.UncommittedCount);
    }

    [Fact]
    public void UncommittedBadge_CountChanges_RaisesTheRailsRepaint()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);
        var repainted = new List<string>();
        vm.PropertyChanged += (_, e) => repainted.Add(e.PropertyName ?? "");

        session.UncommittedCount = 4;
        Dispatcher.UIThread.RunJobs();

        // Without this the row keeps its old badge until some unrelated event happens to repaint it - the
        // same class of bug as a fold input with no invalidation path.
        Assert.Contains(nameof(SessionViewModel.UncommittedCount), repainted);
        Assert.Contains(nameof(SessionViewModel.HasUncommittedChanges), repainted);
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process.</summary>
    private sealed class InertBackend : ISessionBackend
    {
        public int ProcessId => 1234;
        public string Status => "Inert";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface; nothing raises them here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
