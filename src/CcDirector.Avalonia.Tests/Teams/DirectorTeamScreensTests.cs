using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Sessions;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Avalonia.Tests.Teams;

/// <summary>
/// The three Director screens of devthrottle_internal#2311, driven headless through the REAL controls:
/// D1 (which team is this Director for), D2 (the team chip beside the Director's name) and D3 (moving the
/// Director to another team in Settings). The Gateway is faked; nothing here touches the network or the running
/// user's own storage (every test that stores a team points CC_DIRECTOR_ROOT at a temporary folder).
///
/// The pictures are WRITTEN only when TEAMS_2311_SCREENSHOT_DIR names a folder, so an ordinary run leaves
/// nothing on disk; every run still draws them and proves each is a real drawing, not a blank frame.
/// </summary>
public sealed class DirectorTeamScreensTests
{
    private const string FolderVariable = "TEAMS_2311_SCREENSHOT_DIR";

    private static readonly TeamQuestion SorenQuestion = new(
        TeamChoices.Build(new[]
        {
            new HostedTeam { TeamId = "8f1d2c34-dev", Name = "DevThrottle", Role = "owner", MemberCount = 5 },
        }),
        "SOREN_NORTH");

    // ===================== D1 =====================

    [AvaloniaFact]
    public void TeamChoiceDialog_Opens_FirstTeamSelectedAndNamePrefilled()
    {
        var dialog = new TeamChoiceDialog(SorenQuestion);

        Assert.Equal("DevThrottle", dialog.SelectedChoice.Name);
        Assert.Equal("SOREN_NORTH - DevThrottle", dialog.DirectorName);
        Assert.Equal(new[] { "DevThrottle", "You are the Owner, 5 people, you pay", "Personal", "Just you" },
            Texts(dialog).Where(t => t is "DevThrottle" or "Personal" or "Just you" || t.StartsWith("You are")).ToArray());
    }

    [AvaloniaFact]
    public void TeamChoiceDialog_SelectionChanges_NameFollowsUntilThePersonTypes()
    {
        var dialog = new TeamChoiceDialog(SorenQuestion);

        dialog.Select(SorenQuestion.Choices[1]);
        Assert.Equal("SOREN_NORTH - Personal", dialog.DirectorName);

        dialog.NameInput.Text = "Soren - home lab";
        dialog.Select(SorenQuestion.Choices[0]);
        Assert.Equal("Soren - home lab", dialog.DirectorName);
    }

    [AvaloniaFact]
    public void TryAnswer_GivesTheSelectedTeamAndTheTypedName()
    {
        var dialog = new TeamChoiceDialog(SorenQuestion);
        dialog.Select(SorenQuestion.Choices[1]);
        dialog.NameInput.Text = "Soren - home lab";

        var answer = dialog.TryAnswer();

        Assert.NotNull(answer);
        Assert.True(answer!.Choice.IsPersonal);
        Assert.Equal("Soren - home lab", answer.DirectorName);
    }

    [AvaloniaFact]
    public void TryAnswer_EmptyName_RefusedWithAReason()
    {
        var dialog = new TeamChoiceDialog(SorenQuestion);
        dialog.NameInput.Text = "  ";

        Assert.Null(dialog.TryAnswer());
        Assert.True(dialog.ErrorText.IsVisible);
        Assert.Equal("Give this Director a name.", dialog.ErrorText.Text);
    }

    [AvaloniaFact]
    public void TeamChoiceDialog_NoChoices_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TeamChoiceDialog(new TeamQuestion(Array.Empty<TeamChoice>(), "PC")));
    }

    // ===================== D2 =====================

    [AvaloniaFact]
    public void TeamChip_Show_UsesTheTeamsColourAndName()
    {
        var chip = new TeamChip();

        chip.Show(new DirectorTeam("8f1d2c34-dev", "DevThrottle"));

        Assert.True(chip.IsVisible);
        Assert.Equal("DevThrottle", chip.Text);
        Assert.Equal(Color.Parse(TeamColor.For("8f1d2c34-dev").Background), ((ISolidColorBrush)chip.Background!).Color);
    }

    [AvaloniaFact]
    public void TeamChip_Personal_UsesTheDefaultChip()
    {
        var chip = new TeamChip();

        chip.Show(DirectorTeam.Personal);

        Assert.Equal(Color.Parse(TeamColor.Default.Background), ((ISolidColorBrush)chip.Background!).Color);
        Assert.Equal("Personal", chip.Text);
    }

    [AvaloniaFact]
    public void TeamChip_NoTeam_IsHidden()
    {
        var chip = new TeamChip();
        chip.Show(new DirectorTeam("t", "T"));

        chip.Show(null);

        Assert.False(chip.IsVisible);
    }

    [AvaloniaFact]
    public async Task MainWindow_TeamRecorded_ShowsTheChipBesideTheName()
    {
        await WithTempRootAsync(async () =>
        {
            DirectorTeamStore.Save(new DirectorTeam("8f1d2c34-dev", "DevThrottle"));
            var window = new MainWindow();

            await window.RefreshDirectorTeamAsync();

            Assert.True(window.DirectorTeamChip.IsVisible);
            Assert.Equal("DevThrottle", window.DirectorTeamChip.Text);
            Assert.EndsWith("[DevThrottle]", window.Title);
        });
    }

    [AvaloniaFact]
    public async Task MainWindow_NoTeamRecorded_NoChip()
    {
        // The Gateway has no teams (Teams not released): nothing is recorded, so nothing is drawn.
        await WithTempRootAsync(async () =>
        {
            var window = new MainWindow();

            await window.RefreshDirectorTeamAsync();

            Assert.False(window.DirectorTeamChip.IsVisible);
            Assert.DoesNotContain("[", window.Title);
        });
    }

    [AvaloniaFact]
    public async Task MainWindow_NoFileOnAGatewayWithTeams_ShowsThePersonalChip()
    {
        // Review finding F2: a Director enrolled before Teams has no file; on a Gateway with Teams it is personal.
        await WithTempRootAsync(async () =>
        {
            var window = new MainWindow
            {
                ResolveDirectorTeam = ct => DirectorTeamView.ResolveAsync(null, "https://gw.example", new FixedSignal(true), ct),
            };

            await window.RefreshDirectorTeamAsync();

            Assert.True(window.DirectorTeamChip.IsVisible);
            Assert.Equal("Personal", window.DirectorTeamChip.Text);
        });
    }

    [AvaloniaFact]
    public async Task MainWindow_GatewayUnreachableAtStart_ChipIsReadAgainWhenTheGatewayAnswers()
    {
        // Review note R2-F3: the first read fails (offline, or a Gateway mid-deploy); the chip must not stay
        // hidden for the rest of the run once the Gateway answers again.
        await WithTempRootAsync(async () =>
        {
            var reachable = false;
            var reads = 0;
            var window = new MainWindow
            {
                ResolveDirectorTeam = ct =>
                {
                    reads++;
                    return reachable
                        ? DirectorTeamView.ResolveAsync(null, "https://gw.example", new FixedSignal(true), ct)
                        : Task.FromResult(OperationResult<DirectorTeam?>.Fail("The Gateway could not be reached."));
                },
            };
            await window.RefreshDirectorTeamAsync();
            Assert.False(window.DirectorTeamChip.IsVisible);
            var readsAfterFailure = reads;

            window.RetryDirectorTeamIfUnknown(gatewayReachable: false);
            Assert.Equal(readsAfterFailure, reads);

            reachable = true;
            window.RetryDirectorTeamIfUnknown(gatewayReachable: true);
            for (var i = 0; i < 50 && !window.DirectorTeamChip.IsVisible; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            Assert.Equal(readsAfterFailure + 1, reads);
            Assert.True(window.DirectorTeamChip.IsVisible);
            Assert.Equal("Personal", window.DirectorTeamChip.Text);

            // Known now: a later account read does not ask again.
            window.RetryDirectorTeamIfUnknown(gatewayReachable: true);
            Assert.Equal(readsAfterFailure + 1, reads);
        });
    }

    [AvaloniaFact]
    public async Task DirectorTeamStore_SaveAndClear_RaiseChangedSoTheTitleBarRedraws()
    {
        await WithTempRootAsync(() =>
        {
            var raised = 0;
            void Count() => raised++;
            DirectorTeamStore.Changed += Count;
            try
            {
                DirectorTeamStore.Save(new DirectorTeam("t", "T"));
                Assert.Equal(1, raised);
                DirectorTeamStore.Clear();
                Assert.Equal(2, raised);
            }
            finally
            {
                DirectorTeamStore.Changed -= Count;
            }
            return Task.CompletedTask;
        });
    }

    private sealed class FixedSignal(bool released) : IHostedTeamsSignal
    {
        public Task<OperationResult<bool>> TeamsReleasedAsync(string gatewayUrl, CancellationToken ct) =>
            Task.FromResult(OperationResult<bool>.Ok(released));
    }

    // ===================== D3 =====================

    private sealed class FakeService : IDirectorTeamService
    {
        public List<(string DirectorId, string? TeamId)> Moves { get; } = new();
        public IReadOnlyList<HostedTeam> Teams { get; init; } = new[]
        {
            new HostedTeam { TeamId = "8f1d2c34-dev", Name = "DevThrottle", Role = "owner", MemberCount = 5 },
        };
        public bool Released { get; init; } = true;

        public Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct) =>
            Task.FromResult(OperationResult<HostedTeamsAnswer>.Ok(new HostedTeamsAnswer(Released, Teams)));

        // What the Gateway answers to a move; a yes with a new key unless a test says otherwise.
        public OperationResult<string> MoveAnswer { get; init; } = OperationResult<string>.Ok("new-team-key");

        public Task<OperationResult<string>> MoveAsync(string directorId, string? teamId, CancellationToken ct)
        {
            Moves.Add((directorId, teamId));
            return Task.FromResult(MoveAnswer);
        }
    }

    private sealed class Rig
    {
        public required DirectorTeamPanel Panel { get; init; }
        public required FakeService Service { get; init; }
        public List<string> Keys { get; } = new();
        public List<DirectorTeam> Teams { get; } = new();
        public int Running { get; set; }
        public int Reapplied { get; set; }
        public Exception? ReapplyFails { get; set; }
        public Exception? KeySaveFails { get; set; }
    }

    private static Rig BuildPanel(DirectorTeam? current, int running, FakeService? service = null, bool connected = true)
    {
        service ??= new FakeService();
        Rig? rig = null;
        var keys = new List<string>();
        var teams = new List<DirectorTeam>();
        var deps = new DirectorTeamPanelDeps(
            service,
            () => rig!.Running,
            reason => new SessionCreationHold(rig!.Running, () => { }),
            () => "director-1",
            // What the resolver would answer: the last team the panel stored, else the starting one.
            _ => Task.FromResult(OperationResult<DirectorTeam?>.Ok(rig!.Teams.LastOrDefault() ?? current)),
            () => connected,
            k =>
            {
                if (rig!.KeySaveFails is { } e) throw e;
                rig.Keys.Add(k);
            },
            t => rig!.Teams.Add(t),
            () =>
            {
                if (rig!.ReapplyFails is { } e) throw e;
                rig.Reapplied++;
                return Task.CompletedTask;
            });
        rig = new Rig { Panel = new DirectorTeamPanel(deps), Service = service, Running = running };
        return rig;
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_SessionRunning_MoveDisabledAndSaysCloseThemFirst()
    {
        var rig = BuildPanel(DirectorTeam.Personal, running: 1);
        await rig.Panel.LoadCurrentAsync();
        await rig.Panel.ListTeamsAsync();

        Assert.Equal("DevThrottle", rig.Panel.SelectedTarget!.Name);
        Assert.False(rig.Panel.MoveEnabled);
        Assert.Equal("Close the 1 running session first.", rig.Panel.LockMessage);
        Assert.Equal("Move to DevThrottle", rig.Panel.MoveButton.Content);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_MoveWithASessionRunning_RefusedByTheCodeNotJustTheButton()
    {
        var rig = BuildPanel(DirectorTeam.Personal, running: 2);
        await rig.Panel.LoadCurrentAsync();
        await rig.Panel.ListTeamsAsync();

        await rig.Panel.MoveAsync(); // as if the disabled button were pressed anyway

        Assert.Empty(rig.Service.Moves);
        Assert.Empty(rig.Keys);
        Assert.Empty(rig.Teams);
        Assert.Equal("Close the 2 running sessions first.", rig.Panel.Status);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_NoSessionRunning_MovesAndStoresTheNewKeyAndTeam()
    {
        var rig = BuildPanel(DirectorTeam.Personal, running: 0);
        await rig.Panel.LoadCurrentAsync();
        await rig.Panel.ListTeamsAsync();
        Assert.True(rig.Panel.MoveEnabled);

        await rig.Panel.MoveAsync();

        Assert.Equal(("director-1", (string?)"8f1d2c34-dev"), Assert.Single(rig.Service.Moves));
        Assert.Equal("new-team-key", Assert.Single(rig.Keys));
        Assert.Equal(new DirectorTeam("8f1d2c34-dev", "DevThrottle"), Assert.Single(rig.Teams));
        Assert.Equal(1, rig.Reapplied);
        Assert.Equal("This Director now works for DevThrottle.", rig.Panel.Status);
        Assert.Equal("DevThrottle", rig.Panel.CurrentTeamChip.Text);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_ListsEveryChoiceButTheCurrentTeam()
    {
        var rig = BuildPanel(new DirectorTeam("8f1d2c34-dev", "DevThrottle"), running: 0);
        await rig.Panel.LoadCurrentAsync();

        await rig.Panel.ListTeamsAsync();

        var offered = rig.Panel.TeamCombo.Items.Cast<ComboBoxItem>().Select(i => ((TeamChoice)i.Tag!).Name).ToArray();
        Assert.Equal(new[] { "Personal" }, offered);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_GatewayHasNoTeams_NoChoiceOffered()
    {
        var rig = BuildPanel(current: null, running: 0);

        await rig.Panel.LoadCurrentAsync();

        Assert.False(rig.Panel.ChooseButton.IsVisible);
        Assert.Equal(DirectorTeamPanel.GatewayHasNoTeams, rig.Panel.CurrentTeamText.Text);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_NotConnectedToAnyGateway_SaysToConnectFirst()
    {
        // Review note R2-F4: no Gateway at all is not "a Gateway with no teams".
        var rig = BuildPanel(current: null, running: 0, connected: false);

        await rig.Panel.LoadCurrentAsync();

        Assert.False(rig.Panel.ChooseButton.IsVisible);
        Assert.Equal(DirectorTeamPanel.NotConnected, rig.Panel.CurrentTeamText.Text);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_ReapplyFailsAfterTheGatewaysYes_HeadingNamesTheNewTeamAndTheMoveIsNotOfferedAgain()
    {
        // Review finding R2-F1: the new team and key are stored, only the re-apply failed. The heading must name
        // the team the stored key belongs to, and "Move to DevThrottle" must not be offered a second time.
        var rig = BuildPanel(DirectorTeam.Personal, running: 0);
        rig.ReapplyFails = new IOException("the connection would not stop");
        await rig.Panel.LoadCurrentAsync();
        await rig.Panel.ListTeamsAsync();
        Assert.Equal("Personal", rig.Panel.CurrentTeamChip.Text);

        await rig.Panel.MoveAsync();

        Assert.Equal(DirectorTeamMover.MovedButNotApplied("DevThrottle", "the connection would not stop"), rig.Panel.Status);
        Assert.Equal("This Director works for", rig.Panel.CurrentTeamText.Text);
        Assert.Equal("DevThrottle", rig.Panel.CurrentTeamChip.Text);
        Assert.False(rig.Panel.MovePanel.IsVisible);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_KeyCannotBeSavedAfterTheGatewaysYes_HeadingNamesTheNewTeam()
    {
        // The team is recorded first, so when the key then cannot be saved the heading already names the new team.
        var rig = BuildPanel(DirectorTeam.Personal, running: 0);
        rig.KeySaveFails = new IOException("the disk is full");
        await rig.Panel.LoadCurrentAsync();
        await rig.Panel.ListTeamsAsync();

        await rig.Panel.MoveAsync();

        Assert.Equal(DirectorTeamMover.MovedButKeyNotSaved("DevThrottle", "the disk is full"), rig.Panel.Status);
        Assert.Equal("DevThrottle", rig.Panel.CurrentTeamChip.Text);
        Assert.False(rig.Panel.MovePanel.IsVisible);
    }

    [AvaloniaFact]
    public async Task DirectorTeamPanel_GatewayRefusesTheMove_HeadingKeepsTheOldTeamAndTheMoveStaysOffered()
    {
        const string refusal = "This Director already works for that team. Nothing was changed.";
        var rig = BuildPanel(DirectorTeam.Personal, running: 0,
            new FakeService { MoveAnswer = OperationResult<string>.Fail(refusal) });
        await rig.Panel.LoadCurrentAsync();
        await rig.Panel.ListTeamsAsync();

        await rig.Panel.MoveAsync();

        Assert.Equal(refusal, rig.Panel.Status);
        Assert.Equal("Personal", rig.Panel.CurrentTeamChip.Text);
        Assert.True(rig.Panel.MovePanel.IsVisible);
        Assert.Empty(rig.Teams);
    }

    // ===================== The pictures =====================

    [AvaloniaFact]
    public async Task Capture_D1_D2_D3_DrawRealPictures()
    {
        var folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        // D1: the setup question, as Soren sees it - one team he owns, and his personal account.
        var d1 = new TeamChoiceDialog(SorenQuestion);
        d1.Show();
        Capture(d1, folder, "d1-which-team-is-this-director-for.png");

        // D2: two Directors on one computer, each with its own team, drawn by the main window's own toolbar
        // name panel lifted off two real MainWindows.
        var d2 = new Window
        {
            Width = 720, Height = 130, Background = Brush("#252526"),
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 10,
                Children =
                {
                    ToolbarStrip("Soren - DevThrottle", new DirectorTeam("8f1d2c34-dev", "DevThrottle")),
                    ToolbarStrip("Soren - home lab", DirectorTeam.Personal),
                },
            },
        };
        d2.Show();
        Capture(d2, folder, "d2-two-directors-two-teams-one-computer.png");

        // D3: the Team tab with one session still running - the move is locked.
        var locked = BuildPanel(DirectorTeam.Personal, running: 1);
        await locked.Panel.LoadCurrentAsync();
        await locked.Panel.ListTeamsAsync();
        Capture(Host(locked.Panel), folder, "d3-move-locked-one-session-running.png");

        // D3: every session closed - the move is open.
        var open = BuildPanel(DirectorTeam.Personal, running: 0);
        await open.Panel.LoadCurrentAsync();
        await open.Panel.ListTeamsAsync();
        Capture(Host(open.Panel), folder, "d3-move-open-no-session-running.png");

        // D3: after the move - the Director works for the new team.
        var moved = BuildPanel(DirectorTeam.Personal, running: 0);
        await moved.Panel.LoadCurrentAsync();
        await moved.Panel.ListTeamsAsync();
        await moved.Panel.MoveAsync();
        Capture(Host(moved.Panel), folder, "d3-after-the-move.png");
    }

    // The real toolbar name panel - the Director's name, the team chip, the Copy button - taken off a real
    // MainWindow, as the session rail tests take the rail's template: MainWindow's own Loaded reaches for the
    // running Director, so it is never shown.
    private static Control ToolbarStrip(string directorName, DirectorTeam team)
    {
        var source = new MainWindow();
        var panel = source.DirectorInfoPanel;
        ((Panel)panel.Parent!).Children.Remove(panel);
        source.DirectorInfoText.Text = directorName;
        source.DirectorTeamChip.Show(team);
        return new Border { Background = Brush("#1E1E1E"), Padding = new Thickness(10, 8), Child = panel };
    }

    private static Window Host(Control panel)
    {
        var window = new Window
        {
            Width = 620, Height = 330, Background = Brush("#1E1E1E"),
            Content = new Border { Padding = new Thickness(16), Child = panel },
        };
        window.Show();
        return window;
    }

    private static void Capture(Window window, string? folder, string fileName)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var colours = DistinctColours(frame!);
        Assert.True(colours.Count > 100, $"{fileName}: only {colours.Count} distinct colours, so it is blank or no text was drawn");

        if (!string.IsNullOrEmpty(folder))
            frame!.Save(Path.Combine(folder, fileName));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static HashSet<uint> DistinctColours(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        var total = fb.RowBytes * fb.Size.Height;
        var buffer = new byte[total];
        Marshal.Copy(fb.Address, buffer, 0, total);
        var colours = new HashSet<uint>();
        for (var i = 0; i + 3 < total; i += 4)
            colours.Add(BitConverter.ToUInt32(buffer, i));
        return colours;
    }

    private static IEnumerable<string> Texts(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        window.Close();
        return texts;
    }

    private static async Task WithTempRootAsync(Func<Task> body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "cc-2311-team-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try { await body(); }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
