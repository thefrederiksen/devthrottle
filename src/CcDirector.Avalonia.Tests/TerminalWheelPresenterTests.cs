using CcDirector.ControlApi;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The desktop Terminal view's wheel button (mission #4070, phase 4), through its presenter. Pinned: it shows exactly
/// the Gateway's stamped verdict (or nothing), goes pending before the Gateway answers, calls the Gateway's verb (never
/// the session's own switch), shows a refusal's sentence verbatim, and never writes a reply onto a session the window
/// has left. Fakes only: the Gateway seam is a fake whose answers the test releases by hand, and the screen thread is
/// the test's own.
/// </summary>
public sealed class TerminalWheelPresenterTests
{
    private static readonly GatewayWheelAction Take = new(WheelVerb.Take, "Take the wheel", true, null);
    private static readonly GatewayWheelAction HandBack = new(WheelVerb.HandBack, "Hand back", true, null);

    /// <summary>A Gateway seam whose every call waits until the test answers it.</summary>
    private sealed class FakeWheel : IGatewayWheel
    {
        public List<(string Verb, string SessionId)> Calls { get; } = new();
        public TaskCompletionSource Reply { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TakeWheelAsync(string sessionId, CancellationToken ct = default) => Record("take", sessionId);
        public Task HandBackWheelAsync(string sessionId, CancellationToken ct = default) => Record("hand-back", sessionId);

        private Task Record(string verb, string sessionId)
        {
            Calls.Add((verb, sessionId));
            return Reply.Task;
        }
    }

    private static Session NewSession(GatewayWheelAction? action)
    {
        var session = new Session(Guid.NewGuid(), "/test/repo", "/test/repo", null, new InertBackend(), SessionBackendType.ConPty);
        session.ApplyGatewayWheelAction(action);
        return session;
    }

    private static (TerminalWheelPresenter Presenter, FakeWheel Wheel) NewPresenter(Session? session)
    {
        var wheel = new FakeWheel();
        var presenter = new TerminalWheelPresenter(() => wheel, work => work());
        presenter.Attach(session);
        return (presenter, wheel);
    }

    [Fact]
    public void Attach_NoVerdict_ShowsNoButton()
    {
        // Arrange
        var (presenter, _) = NewPresenter(NewSession(null));

        // Assert
        Assert.False(presenter.IsVisible);
        Assert.Equal("", presenter.Label);
        Assert.Null(presenter.Note);
    }

    [Fact]
    public void Attach_NoSession_ShowsNoButton()
    {
        // Arrange
        var (presenter, _) = NewPresenter(null);

        // Assert
        Assert.False(presenter.IsVisible);
    }

    [Fact]
    public void Attach_TakeVerdict_ShowsTheGatewaysLabelEnabled()
    {
        // Arrange
        var (presenter, _) = NewPresenter(NewSession(Take));

        // Assert
        Assert.True(presenter.IsVisible);
        Assert.Equal("Take the wheel", presenter.Label);
        Assert.True(presenter.IsEnabled);
        Assert.Null(presenter.DisabledReason);
    }

    [Fact]
    public void Attach_NotEnabled_IsDisabledWithTheGatewaysReason()
    {
        // Arrange
        const string reason = "The terminal is in the middle of a turn. Hand back when it has finished.";
        var (presenter, _) = NewPresenter(NewSession(HandBack with { Enabled = false, DisabledReason = reason }));

        // Assert
        Assert.True(presenter.IsVisible);
        Assert.False(presenter.IsEnabled);
        Assert.Equal(reason, presenter.DisabledReason);
        Assert.Equal(reason, presenter.Note);
    }

    [Fact]
    public async Task PressAsync_NotEnabled_CallsNothing()
    {
        // Arrange
        var (presenter, wheel) = NewPresenter(NewSession(HandBack with { Enabled = false, DisabledReason = "not now" }));

        // Act
        await presenter.PressAsync();

        // Assert
        Assert.Empty(wheel.Calls);
    }

    [Fact]
    public async Task PressAsync_Take_GoesPendingBeforeTheGatewayAnswersAndCallsTake()
    {
        // Arrange
        var session = NewSession(Take);
        var (presenter, wheel) = NewPresenter(session);
        var changes = 0;
        presenter.Changed += () => changes++;

        // Act
        var press = presenter.PressAsync();

        // Assert - pending, and redrawn, while the Gateway has not answered.
        Assert.False(press.IsCompleted);
        Assert.True(presenter.IsPending);
        Assert.False(presenter.IsEnabled);
        Assert.Equal(1, changes);
        Assert.Equal(new[] { ("take", session.Id.ToString()) }, wheel.Calls);

        wheel.Reply.SetResult();
        await press;
        Assert.False(presenter.IsPending);
        Assert.Null(presenter.Refusal);
    }

    [Fact]
    public async Task PressAsync_HandBack_CallsHandBack()
    {
        // Arrange
        var session = NewSession(HandBack);
        var (presenter, wheel) = NewPresenter(session);

        // Act
        var press = presenter.PressAsync();
        wheel.Reply.SetResult();
        await press;

        // Assert
        Assert.Equal(new[] { ("hand-back", session.Id.ToString()) }, wheel.Calls);
    }

    [Fact]
    public async Task PressAsync_SecondPressWhilePending_CallsTheVerbOnce()
    {
        // Arrange
        var (presenter, wheel) = NewPresenter(NewSession(Take));

        // Act
        var first = presenter.PressAsync();
        await presenter.PressAsync();
        wheel.Reply.SetResult();
        await first;

        // Assert
        Assert.Single(wheel.Calls);
    }

    [Fact]
    public async Task PressAsync_Refused_KeepsTheGatewaysSentenceVerbatim()
    {
        // Arrange
        const string reason = "You already have the wheel of this session: its terminal is open on the conversation. "
            + "Hand it back first if you want the session to continue in API mode.";
        var (presenter, wheel) = NewPresenter(NewSession(Take));

        // Act
        var press = presenter.PressAsync();
        wheel.Reply.SetException(new InvalidOperationException(reason));
        await press;

        // Assert
        Assert.Equal(reason, presenter.Refusal);
        Assert.Equal(reason, presenter.Note);
        Assert.False(presenter.IsPending);
        Assert.True(presenter.IsEnabled);
    }

    [Fact]
    public async Task PressAsync_NoGateway_SaysSoAndCallsNothing()
    {
        // Arrange
        var presenter = new TerminalWheelPresenter(() => null, work => work());
        presenter.Attach(NewSession(Take));

        // Act
        await presenter.PressAsync();

        // Assert
        Assert.Contains("not connected to a Gateway", presenter.Refusal);
        Assert.False(presenter.IsPending);
    }

    [Fact]
    public async Task PressAsync_ReplyAfterTheWindowMovedSession_IsNotShownOnTheNewSession()
    {
        // Arrange
        var (presenter, wheel) = NewPresenter(NewSession(Take));
        var press = presenter.PressAsync();
        var other = NewSession(Take);

        // Act
        presenter.Attach(other);
        wheel.Reply.SetException(new InvalidOperationException("refused"));
        await press;

        // Assert
        Assert.Null(presenter.Refusal);
        Assert.False(presenter.IsPending);
        Assert.True(presenter.IsEnabled);
    }

    [Fact]
    public async Task GatewayStamp_NewVerdict_ClearsTheOldRefusalAndRedraws()
    {
        // Arrange
        var session = NewSession(HandBack);
        var (presenter, wheel) = NewPresenter(session);
        var press = presenter.PressAsync();
        wheel.Reply.SetException(new InvalidOperationException("You do not have the wheel of this session."));
        await press;
        var changes = 0;
        presenter.Changed += () => changes++;

        // Act - the Gateway's next push turns the button into its other half.
        session.ApplyGatewayWheelAction(Take);

        // Assert
        Assert.Equal(1, changes);
        Assert.Null(presenter.Refusal);
        Assert.Equal("Take the wheel", presenter.Label);
    }

    [Fact]
    public void GatewayStamp_ArrivesOffTheScreenThread_IsHandledThroughThePost()
    {
        // Arrange
        var posted = new List<Action>();
        var session = NewSession(Take);
        var presenter = new TerminalWheelPresenter(() => new FakeWheel(), posted.Add);
        presenter.Attach(session);
        var changes = 0;
        presenter.Changed += () => changes++;

        // Act
        session.ApplyGatewayWheelAction(HandBack);

        // Assert - nothing redrawn until the screen thread runs the posted work.
        Assert.Equal(0, changes);
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Attach_AnotherSession_StopsFollowingTheOldOne()
    {
        // Arrange
        var first = NewSession(Take);
        var (presenter, _) = NewPresenter(first);
        presenter.Attach(NewSession(null));
        var changes = 0;
        presenter.Changed += () => changes++;

        // Act
        first.ApplyGatewayWheelAction(HandBack);

        // Assert
        Assert.Equal(0, changes);
        Assert.False(presenter.IsVisible);
    }

    [Fact]
    public void ApplyGatewayWheelAction_SameVerdictAgain_DoesNotRaise()
    {
        // Arrange
        var session = NewSession(Take);
        var raised = 0;
        session.OnGatewayDisplayStateChanged += () => raised++;

        // Act
        session.ApplyGatewayWheelAction(new GatewayWheelAction(WheelVerb.Take, "Take the wheel", true, null));

        // Assert
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ApplyGatewayWheelAction_NewVerdictThenClear_RaisesEachTime()
    {
        // Arrange
        var session = NewSession(Take);
        var raised = 0;
        session.OnGatewayDisplayStateChanged += () => raised++;

        // Act
        session.ApplyGatewayWheelAction(HandBack);
        session.ApplyGatewayWheelAction(null);

        // Assert
        Assert.Equal(2, raised);
        Assert.Null(session.GatewayWheelAction);
    }

    /// <summary>An inert backend: the Session needs one, and this test never runs a process.</summary>
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
