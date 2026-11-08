using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Director's Mission button (the Missions First-Class mission, phase 2), through its presenter. Pinned: each of the
/// Gateway's three colour keys maps to its one look and each of its three icon keys to its one icon, and an unknown key of
/// either kind draws no button; the label and the tooltip are
/// the Gateway's words, verbatim; no verdict means no button; the button follows the selected session and a new verdict
/// for it; and a click opens the Gateway base plus the Gateway's address, or nothing when there is no address. The
/// verdicts below are built the way the Gateway's MissionButtonFold builds them, field for field. No screen, no
/// Gateway, no clock: the screen thread is the test's own.
/// </summary>
public sealed class MissionButtonPresenterTests
{
    private static MissionButtonDisplay WithDocument => new()
    {
        Verdict = MissionButtonDisplay.VerdictMissionWithDocument,
        Label = "Mission",
        ColorKey = MissionButtonDisplay.ColorMission,
        IconKey = MissionButtonDisplay.IconDocument,
        Tooltip = "Mission: Missions First-Class - phase 2 fixture\nGoal: three states, screenshotted\nClick to open the mission document.",
        DocumentAddress = "/r/3f2b6c1e-0000-4000-8000-000000000001",
    };

    private static MissionButtonDisplay WithoutDocument => new()
    {
        Verdict = MissionButtonDisplay.VerdictMissionWithoutDocument,
        Label = "Mission",
        ColorKey = MissionButtonDisplay.ColorMissionOutline,
        IconKey = MissionButtonDisplay.IconNone,
        Tooltip = "Mission: Repeat Events\nThis Mission has no document yet.",
        DocumentAddress = null,
    };

    private static MissionButtonDisplay NoMission => new()
    {
        Verdict = MissionButtonDisplay.VerdictNoMission,
        Label = "Mission",
        ColorKey = MissionButtonDisplay.ColorWarning,
        IconKey = MissionButtonDisplay.IconWarning,
        Tooltip = "This session has no Mission. Attach it to one with: cc-devthrottle mission attach <session> <mission>",
        DocumentAddress = null,
    };

    /// <summary>The Gateway's push as the Director receives it: a display state carrying the Mission button verdict.</summary>
    private static void Stamp(Session session, MissionButtonDisplay? verdict) =>
        session.ApplyGatewayDisplayState(null, null, null, null, null, false, missionButton: verdict);

    private static Session NewSession(MissionButtonDisplay? verdict)
    {
        var session = new Session(Guid.NewGuid(), "/test/repo", "/test/repo", null, new InertBackend(), SessionBackendType.ConPty);
        Stamp(session, verdict);
        return session;
    }

    private static (MissionButtonPresenter Presenter, List<int> Changes) NewPresenter(Session? session)
    {
        var presenter = new MissionButtonPresenter(work => work());
        var changes = new List<int>();
        presenter.Changed += () => changes.Add(changes.Count);
        presenter.Attach(session);
        return (presenter, changes);
    }

    [Theory]
    [InlineData(MissionButtonDisplay.ColorMission, MissionButtonPresenter.LookFilled)]
    [InlineData(MissionButtonDisplay.ColorMissionOutline, MissionButtonPresenter.LookOutline)]
    [InlineData(MissionButtonDisplay.ColorWarning, MissionButtonPresenter.LookWarning)]
    public void LookFor_KnownColourKey_ReturnsItsOneLook(string colorKey, string expectedLook)
    {
        // Act
        var look = MissionButtonPresenter.LookFor(colorKey);

        // Assert
        Assert.Equal(expectedLook, look);
    }

    [Theory]
    [InlineData("purple")]
    [InlineData("Mission")]
    [InlineData("")]
    [InlineData(null)]
    public void LookFor_UnknownColourKey_ReturnsNoLook(string? colorKey)
    {
        // Act
        var look = MissionButtonPresenter.LookFor(colorKey);

        // Assert: no guessed look - not even for a key that differs only in case.
        Assert.Null(look);
    }

    [Fact]
    public void Attach_UnknownColourKey_ShowsNoButtonAndNamesTheProblem()
    {
        // Arrange
        var verdict = WithDocument;
        verdict.ColorKey = "teal";

        // Act
        var (presenter, _) = NewPresenter(NewSession(verdict));

        // Assert
        Assert.False(presenter.IsVisible);
        Assert.Null(presenter.Look);
        Assert.Equal("", presenter.Label);
        Assert.Null(presenter.Tooltip);
        Assert.False(presenter.CanOpen);
        Assert.Contains("\"teal\"", presenter.Problem);
    }

    [Fact]
    public void Note_UnknownColourKey_IsTheProblemSoTheScreenSaysWhyThereIsNoButton()
    {
        // Arrange
        var verdict = WithDocument;
        verdict.ColorKey = "teal";

        // Act
        var (presenter, _) = NewPresenter(NewSession(verdict));

        // Assert: the empty spot is explained on screen, in the presenter's own words.
        Assert.NotNull(presenter.Note);
        Assert.Equal(presenter.Problem, presenter.Note);
        Assert.Contains("\"teal\"", presenter.Note);
    }

    [Theory]
    [InlineData(MissionButtonDisplay.ColorMission)]
    [InlineData(MissionButtonDisplay.ColorMissionOutline)]
    [InlineData(MissionButtonDisplay.ColorWarning)]
    public void Note_KnownColourKey_IsNull(string colorKey)
    {
        // Arrange
        var verdict = WithDocument;
        verdict.ColorKey = colorKey;

        // Act
        var (presenter, _) = NewPresenter(NewSession(verdict));

        // Assert
        Assert.Null(presenter.Note);
    }

    [Fact]
    public void Note_GatewaySentNoVerdict_IsNull()
    {
        // Act: no verdict is not a problem - an older Gateway - so nothing is said and nothing is drawn.
        var (presenter, _) = NewPresenter(NewSession(null));

        // Assert
        Assert.Null(presenter.Note);
    }

    [Theory]
    [InlineData(MissionButtonDisplay.IconDocument, MissionButtonIcon.Document)]
    [InlineData(MissionButtonDisplay.IconNone, MissionButtonIcon.None)]
    [InlineData(MissionButtonDisplay.IconWarning, MissionButtonIcon.Warning)]
    public void IconFor_KnownIconKey_ReturnsItsOneIcon(string iconKey, MissionButtonIcon expected)
    {
        // Act
        var icon = MissionButtonPresenter.IconFor(iconKey);

        // Assert
        Assert.Equal(expected, icon);
    }

    [Theory]
    [InlineData("star")]
    [InlineData("Document")]
    [InlineData("")]
    [InlineData(null)]
    public void IconFor_UnknownIconKey_ReturnsNoIcon(string? iconKey)
    {
        // Act
        var icon = MissionButtonPresenter.IconFor(iconKey);

        // Assert: no guessed icon - not even for a key that differs only in case, and not for an absent one.
        Assert.Null(icon);
    }

    [Fact]
    public void Attach_UnknownIconKey_ShowsNoButtonAndTheNoteNamesTheKey()
    {
        // Arrange
        var verdict = WithDocument;
        verdict.IconKey = "star";

        // Act
        var (presenter, _) = NewPresenter(NewSession(verdict));

        // Assert
        Assert.False(presenter.IsVisible);
        Assert.Null(presenter.Look);
        Assert.Equal(MissionButtonIcon.None, presenter.Icon);
        Assert.Equal("", presenter.Label);
        Assert.False(presenter.CanOpen);
        Assert.Contains("icon", presenter.Note);
        Assert.Contains("\"star\"", presenter.Note);
    }

    [Fact]
    public void Attach_VerdictWithNoIconKey_IsAProblemNotAGuessedIcon()
    {
        // Arrange: a Gateway older than the icon key sends none, which reads as an empty key.
        var verdict = WithDocument;
        verdict.IconKey = "";

        // Act
        var (presenter, _) = NewPresenter(NewSession(verdict));

        // Assert
        Assert.False(presenter.IsVisible);
        Assert.Contains("icon", presenter.Problem);
    }

    [Fact]
    public void GatewayPush_BeforeThePostedRedrawRuns_EveryGetterStillReadsTheShownVerdict()
    {
        // Arrange: a screen thread that runs posted work only when the test says so.
        var queued = new Queue<Action>();
        var session = NewSession(WithoutDocument);
        var presenter = new MissionButtonPresenter(work => queued.Enqueue(work));
        presenter.Attach(session);

        // Act: a stamp lands on another thread, mid-redraw as far as the window knows.
        Stamp(session, WithDocument);

        // Assert: until the posted redraw runs, every getter reads the one verdict being shown - never a mix.
        Assert.Equal(WithoutDocument.Tooltip, presenter.Tooltip);
        Assert.Equal(MissionButtonPresenter.LookOutline, presenter.Look);
        Assert.Equal(MissionButtonIcon.None, presenter.Icon);
        Assert.False(presenter.CanOpen);

        // Act: the screen thread runs the posted redraw.
        while (queued.Count > 0) queued.Dequeue()();

        // Assert: now every getter reads the new verdict.
        Assert.Equal(WithDocument.Tooltip, presenter.Tooltip);
        Assert.Equal(MissionButtonPresenter.LookFilled, presenter.Look);
        Assert.Equal(MissionButtonIcon.Document, presenter.Icon);
        Assert.True(presenter.CanOpen);
    }

    [Fact]
    public void Attach_MissionWithDocument_CarriesLabelAndTooltipVerbatimInTheFilledLookWithTheDocumentIcon()
    {
        // Act
        var (presenter, _) = NewPresenter(NewSession(WithDocument));

        // Assert
        Assert.True(presenter.IsVisible);
        Assert.Equal("Mission", presenter.Label);
        Assert.Equal(WithDocument.Tooltip, presenter.Tooltip);
        Assert.Equal(MissionButtonPresenter.LookFilled, presenter.Look);
        Assert.Equal(MissionButtonIcon.Document, presenter.Icon);
        Assert.True(presenter.CanOpen);
        Assert.Null(presenter.Problem);
    }

    [Fact]
    public void Attach_MissionWithoutDocument_OutlineLookNoIconAndNothingToOpen()
    {
        // Act
        var (presenter, _) = NewPresenter(NewSession(WithoutDocument));

        // Assert
        Assert.True(presenter.IsVisible);
        Assert.Equal("Mission", presenter.Label);
        Assert.Equal(WithoutDocument.Tooltip, presenter.Tooltip);
        Assert.Equal(MissionButtonPresenter.LookOutline, presenter.Look);
        Assert.Equal(MissionButtonIcon.None, presenter.Icon);
        Assert.False(presenter.CanOpen);
    }

    [Fact]
    public void Attach_NoMission_WarningLookWithTheWarningIcon()
    {
        // Act
        var (presenter, _) = NewPresenter(NewSession(NoMission));

        // Assert
        Assert.True(presenter.IsVisible);
        Assert.Equal("Mission", presenter.Label);
        Assert.Equal(NoMission.Tooltip, presenter.Tooltip);
        Assert.Equal(MissionButtonPresenter.LookWarning, presenter.Look);
        Assert.Equal(MissionButtonIcon.Warning, presenter.Icon);
        Assert.False(presenter.CanOpen);
    }

    [Fact]
    public void Attach_GatewaySentNoVerdict_ShowsNoButton()
    {
        // Act: a Gateway older than the mission sends no MissionButton at all.
        var (presenter, _) = NewPresenter(NewSession(null));

        // Assert
        Assert.False(presenter.IsVisible);
        Assert.Null(presenter.Look);
        Assert.Null(presenter.Problem);
    }

    [Fact]
    public void Attach_NoSessionSelected_ShowsNoButton()
    {
        // Act
        var (presenter, _) = NewPresenter(null);

        // Assert
        Assert.False(presenter.IsVisible);
        Assert.Equal("", presenter.Label);
    }

    [Fact]
    public void Attach_SelectionMovesToAnotherSession_FollowsTheNewSession()
    {
        // Arrange
        var first = NewSession(WithDocument);
        var second = NewSession(NoMission);
        var (presenter, changes) = NewPresenter(first);
        var before = changes.Count;

        // Act
        presenter.Attach(second);

        // Assert
        Assert.Same(second, presenter.Session);
        Assert.Equal(NoMission.Tooltip, presenter.Tooltip);
        Assert.Equal(MissionButtonPresenter.LookWarning, presenter.Look);
        Assert.True(changes.Count > before);
    }

    [Fact]
    public void Attach_SelectionMovedAway_OldSessionsNewVerdictNoLongerRedraws()
    {
        // Arrange
        var first = NewSession(WithDocument);
        var second = NewSession(NoMission);
        var (presenter, changes) = NewPresenter(first);
        presenter.Attach(second);
        var before = changes.Count;

        // Act: the session the window left gets a new verdict.
        Stamp(first, WithoutDocument);

        // Assert
        Assert.Equal(before, changes.Count);
        Assert.Equal(NoMission.Tooltip, presenter.Tooltip);
    }

    [Fact]
    public void GatewayPush_NewVerdictForTheSelectedSession_RedrawsWithTheNewWords()
    {
        // Arrange
        var session = NewSession(WithoutDocument);
        var (presenter, changes) = NewPresenter(session);
        var before = changes.Count;

        // Act: the Mission's document is published, and the Gateway pushes the new verdict.
        Stamp(session, WithDocument);

        // Assert
        Assert.True(changes.Count > before);
        Assert.Equal(WithDocument.Tooltip, presenter.Tooltip);
        Assert.Equal(MissionButtonPresenter.LookFilled, presenter.Look);
        Assert.Equal(MissionButtonIcon.Document, presenter.Icon);
        Assert.True(presenter.CanOpen);
    }

    [Fact]
    public void GatewayPush_VerdictWithdrawn_HidesTheButton()
    {
        // Arrange
        var session = NewSession(NoMission);
        var (presenter, _) = NewPresenter(session);

        // Act
        Stamp(session, null);

        // Assert
        Assert.False(presenter.IsVisible);
    }

    [Fact]
    public void ApplyGatewayDisplayState_StoresACopy_ALaterChangeToTheCallersObjectIsNotSeen()
    {
        // Arrange
        var verdict = WithDocument;
        var session = NewSession(verdict);

        // Act
        verdict.Tooltip = "changed after the push";

        // Assert
        Assert.Equal(WithDocument.Tooltip, session.GatewayMissionButton!.Tooltip);
    }

    [Theory]
    [InlineData("https://gateway.example.com", "https://gateway.example.com/r/abc")]
    [InlineData("https://gateway.example.com/", "https://gateway.example.com/r/abc")]
    [InlineData("https://gateway.example.com//", "https://gateway.example.com/r/abc")]
    public void BuildOpenUrl_WithAddress_JoinsTheGatewayBaseAndTheAddress(string gatewayBase, string expected)
    {
        // Act
        var url = MissionButtonPresenter.BuildOpenUrl(gatewayBase, "/r/abc");

        // Assert
        Assert.Equal(expected, url);
    }

    [Fact]
    public void BuildOpenUrl_NoAddress_OpensNothing()
    {
        // Act
        var url = MissionButtonPresenter.BuildOpenUrl("https://gateway.example.com", null);

        // Assert
        Assert.Null(url);
    }

    [Theory]
    [InlineData("r/abc")]
    [InlineData("//evil.example.com/r/abc")]
    [InlineData("https://elsewhere.example.com/r/abc")]
    public void BuildOpenUrl_AddressNotAPathOnTheGateway_Throws(string address)
    {
        // Act and Assert
        Assert.Throws<InvalidOperationException>(() => MissionButtonPresenter.BuildOpenUrl("https://gateway.example.com", address));
    }

    [Fact]
    public void BuildOpenUrl_NoGatewayBase_Throws()
    {
        // Act and Assert
        Assert.Throws<ArgumentException>(() => MissionButtonPresenter.BuildOpenUrl(" ", "/r/abc"));
    }

    [Fact]
    public void BuildOpenUrl_SelectedSessionsVerdict_OpensThatSessionsDocument()
    {
        // Arrange
        var (presenter, _) = NewPresenter(NewSession(WithDocument));

        // Act
        var url = MissionButtonPresenter.BuildOpenUrl("https://gateway.example.com", presenter.Verdict?.DocumentAddress);

        // Assert
        Assert.Equal("https://gateway.example.com/r/3f2b6c1e-0000-4000-8000-000000000001", url);
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
