using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Avalonia.Controls;

/// <summary>What the Team panel needs from the running Director. Tests supply fakes for every one.</summary>
/// <param name="Service">The hosted Gateway's list and move routes.</param>
/// <param name="RunningSessions">How many sessions this Director holds right now.</param>
/// <param name="DirectorId">This Director's id, or null while it is still starting.</param>
/// <param name="LoadTeam">Reads this Director's recorded team (null = the Gateway has no teams).</param>
/// <param name="PersistKey">Stores the new device key after a move.</param>
/// <param name="PersistTeam">Stores the new team after a move; this is what redraws the title bar.</param>
/// <param name="Reapply">Makes the running Gateway connection use the new key; null when there is none.</param>
internal sealed record DirectorTeamPanelDeps(
    IDirectorTeamService Service,
    Func<int> RunningSessions,
    Func<string?> DirectorId,
    Func<DirectorTeam?> LoadTeam,
    Action<string> PersistKey,
    Action<DirectorTeam> PersistTeam,
    Func<Task>? Reapply);

/// <summary>
/// Screen D3 (devthrottle_internal#2311): "This Director works for &lt;team&gt;", the other teams, and
/// "Move to &lt;team&gt;". While any session is running the button is disabled and says "Close the N running
/// sessions first" - and the move itself is refused by <see cref="DirectorTeamMover"/>, which counts again at
/// the moment of the click, so the rule does not rest on the button.
///
/// Listing the teams signs the person in, in the browser - a Director's key belongs to its team, so it cannot
/// say who the person is - which is why the list waits for "Choose another team..." rather than opening a
/// browser the moment Settings opens.
/// </summary>
public partial class DirectorTeamPanel : UserControl
{
    private readonly DirectorTeamPanelDeps _deps;
    private readonly DirectorTeamMover _mover;
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DirectorTeam? _current;
    private bool _busy;

    internal DirectorTeamPanel(DirectorTeamPanelDeps deps)
    {
        _deps = deps ?? throw new ArgumentNullException(nameof(deps));
        _mover = new DirectorTeamMover(deps.Service, deps.RunningSessions, deps.PersistKey, deps.PersistTeam);
        InitializeComponent();

        _lockTimer.Tick += (_, _) => RefreshLock();
        AttachedToVisualTree += (_, _) => _lockTimer.Start();
        DetachedFromVisualTree += (_, _) => _lockTimer.Stop();
        Loaded += async (_, _) => await LoadCurrentAsync();
    }

    // Parameterless constructor for the XAML loader and designer.
    public DirectorTeamPanel() : this(ForThisDirector()) { }

    /// <summary>The panel wired to this running Director: its sessions, its id, its home, the hosted Gateway.</summary>
    public static DirectorTeamPanel CreateForThisDirector() => new(ForThisDirector());

    internal static DirectorTeamPanelDeps ForThisDirector()
    {
        var app = global::Avalonia.Application.Current as App;
        return new DirectorTeamPanelDeps(
            new HostedDirectorTeamService(),
            () => app?.SessionManager?.ListSessions().Count
                  ?? throw new InvalidOperationException("The Director's sessions are not available yet."),
            () => app?.ControlApiHost?.DirectorId,
            DirectorTeamStore.Load,
            key => GatewayCredentialStore.SaveEnrolledKey(HostedGateway.ResolveUrl(), key),
            DirectorTeamStore.Save,
            app?.ControlApiHost is { } host ? host.ReapplyGatewayAsync : null);
    }

    /// <summary>The team the combo box has selected, or null.</summary>
    internal TeamChoice? SelectedTarget => TeamCombo.SelectedItem is ComboBoxItem { Tag: TeamChoice c } ? c : null;

    /// <summary>The words beside the Move button, or "" when the move is open.</summary>
    internal string LockMessage => LockText.IsVisible ? LockText.Text ?? "" : "";

    /// <summary>Whether the Move button can be pressed.</summary>
    internal bool MoveEnabled => MoveButton.IsEnabled;

    /// <summary>The words under the panel after a list or a move.</summary>
    internal string Status => StatusText.IsVisible ? StatusText.Text ?? "" : "";

    /// <summary>Read and draw this Director's current team.</summary>
    internal async Task LoadCurrentAsync()
    {
        FileLog.Write("[DirectorTeamPanel] LoadCurrentAsync");
        try
        {
            _current = await Task.Run(_deps.LoadTeam);
            DrawCurrent();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorTeamPanel] LoadCurrentAsync FAILED: {ex.Message}");
            CurrentTeamText.Text = $"Could not read which team this Director works for: {ex.Message}";
            ChooseButton.IsVisible = false;
        }
    }

    private void DrawCurrent()
    {
        if (_current is null)
        {
            CurrentTeamText.Text = "This Director's Gateway has no teams, so there is no team to choose.";
            CurrentTeamChip.Show(null);
            ChooseButton.IsVisible = false;
            MovePanel.IsVisible = false;
            return;
        }
        CurrentTeamText.Text = "This Director works for";
        CurrentTeamChip.Show(_current);
        ChooseButton.IsVisible = true;
    }

    /// <summary>Sign in and list the teams this Director can move to: every choice but the current one.</summary>
    internal async Task ListTeamsAsync()
    {
        FileLog.Write("[DirectorTeamPanel] ListTeamsAsync");
        ChooseButton.IsEnabled = false;
        ShowStatus("Signing in to DevThrottle in your browser to list your teams...", "#888888");
        try
        {
            var listed = await _deps.Service.ListTeamsAsync(CancellationToken.None);
            if (!listed.Success)
            {
                ShowStatus(listed.ErrorMessage ?? "Your teams could not be listed.", "#F14C4C");
                return;
            }
            if (!listed.Value!.TeamsReleased)
            {
                ShowStatus("This Director's Gateway has no teams.", "#888888");
                return;
            }

            var others = TeamChoices.Build(listed.Value.Teams).Where(c => c.TeamId != _current?.TeamId).ToList();
            if (others.Count == 0)
            {
                ShowStatus("There is no other team you can run sessions in.", "#888888");
                return;
            }

            TeamCombo.ItemsSource = others.Select(c => new ComboBoxItem { Content = $"{c.Name}  ({c.Detail})", Tag = c }).ToList();
            TeamCombo.SelectedIndex = 0;
            MovePanel.IsVisible = true;
            StatusText.IsVisible = false;
            RefreshLock();
            FileLog.Write($"[DirectorTeamPanel] ListTeamsAsync: {others.Count} team(s) to move to");
        }
        finally
        {
            ChooseButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Draw the Move button for the selected team: enabled with none running, otherwise disabled with
    /// "Close the N running sessions first."
    /// </summary>
    internal void RefreshLock()
    {
        if (!MovePanel.IsVisible) return;
        var target = SelectedTarget;
        MoveButton.Content = target is null ? "Move" : $"Move to {target.Name}";

        var refusal = DirectorTeamMover.RefusalFor(_deps.RunningSessions());
        LockText.Text = refusal ?? "";
        LockText.IsVisible = refusal is not null;
        MoveButton.IsEnabled = refusal is null && target is not null && !_busy;
    }

    /// <summary>Move to the selected team. Refused by the mover itself while any session is running.</summary>
    internal async Task MoveAsync()
    {
        var target = SelectedTarget ?? throw new InvalidOperationException("No team is selected.");
        var directorId = _deps.DirectorId();
        if (directorId is null)
        {
            ShowStatus("This Director is still starting, so it cannot move yet. Try again in a moment.", "#F14C4C");
            return;
        }

        FileLog.Write($"[DirectorTeamPanel] MoveAsync: to {(target.IsPersonal ? "the personal account" : "team " + target.TeamId)}");
        _busy = true;
        RefreshLock();
        ShowStatus($"Moving this Director to {target.Name}...", "#888888");
        try
        {
            var moved = await _mover.MoveAsync(directorId, target, CancellationToken.None);
            if (!moved.Success)
            {
                ShowStatus(moved.ErrorMessage ?? "The move did not happen.", "#F14C4C");
                return;
            }

            _current = moved.Value;
            if (_deps.Reapply is not null)
                await _deps.Reapply();
            DrawCurrent();
            MovePanel.IsVisible = false;
            ShowStatus($"This Director now works for {moved.Value!.Name}.", "#22C55E");
        }
        finally
        {
            _busy = false;
            RefreshLock();
        }
    }

    private void ShowStatus(string text, string colour)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(colour));
        StatusText.IsVisible = true;
    }

    private async void BtnChoose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[DirectorTeamPanel] BtnChoose_Click");
        try
        {
            await ListTeamsAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorTeamPanel] BtnChoose_Click FAILED: {ex.Message}");
            ShowStatus($"Your teams could not be listed: {ex.Message}", "#F14C4C");
        }
    }

    private async void BtnMove_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[DirectorTeamPanel] BtnMove_Click");
        try
        {
            await MoveAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorTeamPanel] BtnMove_Click FAILED: {ex.Message}");
            ShowStatus($"The move failed: {ex.Message}", "#F14C4C");
        }
    }

    private void TeamCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshLock();
}
