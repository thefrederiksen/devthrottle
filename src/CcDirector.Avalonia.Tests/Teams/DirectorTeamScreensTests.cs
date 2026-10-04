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
        public List<(string Key, string? TeamId)> Moves { get; } = new();
        public IReadOnlyList<HostedTeam> Teams { get; init; } = new[]
        {
            new HostedTeam { TeamId = "8f1d2c34-dev", Name = "DevThrottle", Role = "owner", MemberCount = 5 },
        };
        public bool Released { get; init; } = true;

        public Task<OperationResult<HostedTeamsAnswer>> ListTeamsAsync(CancellationToken ct) =>
            Task.FromResult(OperationResult<HostedTeamsAnswer>.Ok(new HostedTeamsAnswer(Released, Teams)));

        public Task<OperationResult<string>> MoveAsync(string currentDeviceKey, string? teamId, CancellationToken ct)
        {
            Moves.Add((currentDeviceKey, teamId));
            return Task.FromResult(OperationResult<string>.Ok("new-team-key"));
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
    }

    private static Rig BuildPanel(DirectorTeam? current, int running, FakeService? service = null)
    {
        service ??= new FakeService();
        Rig? rig = null;
        var keys = new List<string>();
        var teams = new List<DirectorTeam>();
        var deps = new DirectorTeamPanelDeps(
            service,
            () => rig!.Running,
            reason => new SessionCreationHold(rig!.Running, () => { }),
            () => "current-key-1",
            // What the resolver would answer: the last team the panel stored, else the starting one.
            _ => Task.FromResult(OperationResult<DirectorTeam?>.Ok(rig!.Teams.LastOrDefault() ?? current)),
            k => rig!.Keys.Add(k),
            t => rig!.Teams.Add(t),
            () => { rig!.Reapplied++; return Task.CompletedTask; });
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

        Assert.Equal(("current-key-1", (string?)"8f1d2c34-dev"), Assert.Single(rig.Service.Moves));
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
        Assert.Contains("has no teams", rig.Panel.CurrentTeamText.Text);
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
