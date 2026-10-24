using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Setup;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Sets up GitHub on this machine (issue #4426): shows whether git and the GitHub command line (gh) are installed and
/// whether gh is signed in, and offers the one next step - install what is missing, or sign in. Both run in a terminal
/// window the person can see. The same panel is the Settings GitHub tab and the first-run wizard's GitHub step; the
/// wizard draws it on its light page (<see cref="Light"/>), Settings on its dark one.
///
/// It reads the machine when it is shown, and after a step it re-reads every few seconds until the state moves on (or
/// ten minutes pass), so finishing the sign-in in the browser turns the panel green without a click.
/// </summary>
public sealed class GitHubSetupPanel : UserControl
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollFor = TimeSpan.FromMinutes(10);

    private readonly bool _light;
    private readonly TextBlock _gitValue = new();
    private readonly TextBlock _ghValue = new();
    private readonly TextBlock _signInValue = new();
    private readonly TextBlock _explain = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _stepButton = new() { Classes = { "dialogButton" }, Padding = new Thickness(12, 4) };
    private readonly Button _checkButton = new() { Classes = { "dialogButton" }, Content = "Check again", Padding = new Thickness(12, 4) };
    private readonly DispatcherTimer _poll = new() { Interval = PollEvery };
    private GitHubSetupStatus? _status;
    private GitHubSetupStep? _step;
    private DateTime _pollUntil;
    private bool _probing;

    /// <summary>Raised with every new reading, so a host (the wizard's receipt, Home) can show the same answer.</summary>
    public event EventHandler<GitHubSetupStatus>? StatusChanged;

    public GitHubSetupPanel() : this(light: false) { }

    /// <param name="light">Draw for a light page (the first-run wizard) instead of a dark one (Settings).</param>
    public GitHubSetupPanel(bool light) : this(light, readOnLoad: true) { }

    /// <param name="readOnLoad">Read the machine when shown. A test turns it off and hands the panel each state.</param>
    internal GitHubSetupPanel(bool light, bool readOnLoad)
    {
        _light = light;
        Content = Build();
        _stepButton.Click += (_, _) => RunStep();
        _checkButton.Click += (_, _) => _ = RefreshAsync();
        _poll.Tick += (_, _) => _ = PollTickAsync();
        if (readOnLoad) Loaded += (_, _) => _ = RefreshAsync();
        DetachedFromVisualTree += (_, _) => _poll.Stop();
    }

    /// <summary>The panel draws for a light page.</summary>
    public bool Light => _light;

    /// <summary>The last reading, or null before the first one has answered.</summary>
    public GitHubSetupStatus? Status => _status;

    private IBrush Text => Brush(_light ? "#16181D" : "#CCCCCC");
    private IBrush Quiet => Brush(_light ? "#5B616B" : "#888888");
    private static IBrush Good => Brush("#2E9E5B");
    private static IBrush Missing => Brush("#C98A1B");

    private Control Build()
    {
        var title = new TextBlock { Text = "GitHub", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Text };
        var intro = new TextBlock
        {
            Text = "Sessions use git and the GitHub command line (gh) to clone, push and open pull requests. Sign in once "
                   + "here and every session on this machine can push.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Quiet,
        };

        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        AddRow(rows, 0, "git", _gitValue);
        AddRow(rows, 1, "GitHub command line (gh)", _ghValue);
        AddRow(rows, 2, "Signed in to GitHub", _signInValue);
        foreach (var value in new[] { _gitValue, _ghValue, _signInValue }) value.Text = "Checking...";

        _explain.Foreground = Quiet;
        _explain.IsVisible = false;
        _stepButton.IsVisible = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_stepButton);
        buttons.Children.Add(_checkButton);

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(title);
        stack.Children.Add(intro);
        stack.Children.Add(rows);
        stack.Children.Add(buttons);
        stack.Children.Add(_explain);

        return new Border
        {
            Background = Brush(_light ? "#FFFFFF" : "#252526"),
            BorderBrush = Brush(_light ? "#E6E8EC" : "#3C3C3C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_light ? 10 : 4),
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private void AddRow(Grid grid, int row, string label, TextBlock value)
    {
        var name = new TextBlock { Text = label, FontSize = 13, Foreground = Quiet, Margin = new Thickness(0, 2) };
        value.FontSize = 13;
        value.TextWrapping = TextWrapping.Wrap;
        value.Margin = new Thickness(0, 2);
        Grid.SetRow(name, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
    }

    /// <summary>Read the machine again and show the answer.</summary>
    public async Task RefreshAsync()
    {
        if (_probing) return;
        _probing = true;
        _checkButton.IsEnabled = false;
        try
        {
            var status = await Task.Run(() => GitHubSetupProbe.ProbeAsync());
            Show(status);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitHubSetupPanel] RefreshAsync FAILED: {ex.Message}");
            _explain.Text = ShownError.Report("github setup", "check git and gh on this machine",
                $"Could not check GitHub on this machine: {ex.Message}", ex);
            _explain.IsVisible = true;
        }
        finally
        {
            _probing = false;
            _checkButton.IsEnabled = true;
        }
    }

    /// <summary>Paint <paramref name="status"/>. Public for the test, which hands it each state.</summary>
    public void Show(GitHubSetupStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var changed = _status?.State != status.State;
        _status = status;
        FileLog.Write($"[GitHubSetupPanel] Show: state={status.State}");

        Paint(_gitValue, status.GitVersion is { } gv ? $"installed ({gv})" : "not installed", status.GitVersion is not null);
        Paint(_ghValue, status.GhVersion is { } hv ? $"installed ({hv})" : "not installed", status.GhVersion is not null);
        Paint(_signInValue, status.State switch
        {
            GitHubSetupState.SignedIn => status.Account is { } a ? $"yes, as {a}" : "yes",
            GitHubSetupState.SignedOut => "no",
            _ => "not yet - install the missing tools first",
        }, status.State == GitHubSetupState.SignedIn);

        _step = status.State switch
        {
            GitHubSetupState.GitMissing or GitHubSetupState.GhMissing =>
                GitHubSetupCommands.Install(status.State, CurrentPlatform(), GitHubSetupCommands.FindInstaller()),
            GitHubSetupState.SignedOut when status.GhPath is { } gh => GitHubSetupCommands.SignIn(gh, CurrentPlatform()),
            _ => null,
        };
        _stepButton.Content = StepLabel(status.State);
        _stepButton.IsVisible = _step is not null;

        if (status.State == GitHubSetupState.SignedIn)
        {
            _poll.Stop();
            _explain.Text = "GitHub is set up. Sessions on this machine can clone, push and open pull requests.";
            _explain.IsVisible = true;
        }
        else if (changed && !_poll.IsEnabled)
        {
            _explain.IsVisible = false;
        }

        StatusChanged?.Invoke(this, status);
    }

    /// <summary>What the step button says for <paramref name="state"/>.</summary>
    public static string StepLabel(GitHubSetupState state) => state switch
    {
        GitHubSetupState.GitMissing => "Install git and gh",
        GitHubSetupState.GhMissing => "Install the GitHub command line",
        GitHubSetupState.SignedOut => "Sign in to GitHub",
        _ => "",
    };

    private void RunStep()
    {
        if (_step is null) return;
        FileLog.Write($"[GitHubSetupPanel] RunStep: state={_status?.State}, kind={_step.Kind}");
        try
        {
            var problem = GitHubSetupCommands.Run(_step);
            if (problem is not null)
                _explain.Text = ShownError.Report("github setup", "open a terminal for the GitHub setup", problem);
            else
                _explain.Text = _step.Explanation;
            _explain.IsVisible = true;
            if (problem is null)
            {
                _pollUntil = DateTime.UtcNow + PollFor;
                _poll.Start();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitHubSetupPanel] RunStep FAILED: {ex.Message}");
            _explain.Text = ShownError.Report("github setup", "start the GitHub setup step",
                $"That could not be started: {ex.Message}", ex);
            _explain.IsVisible = true;
        }
    }

    private async Task PollTickAsync()
    {
        if (DateTime.UtcNow > _pollUntil)
        {
            _poll.Stop();
            FileLog.Write("[GitHubSetupPanel] stopped checking after ten minutes");
            return;
        }
        await RefreshAsync();
    }

    private void Paint(TextBlock block, string text, bool good)
    {
        block.Text = text;
        block.Foreground = good ? Good : Missing;
    }

    private static System.Runtime.InteropServices.OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? System.Runtime.InteropServices.OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? System.Runtime.InteropServices.OSPlatform.OSX
        : System.Runtime.InteropServices.OSPlatform.Linux;

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>For the test: the three values and the step button as shown.</summary>
    internal (string Git, string Gh, string SignedIn, string? Step, bool StepVisible) Shown =>
        (_gitValue.Text ?? "", _ghValue.Text ?? "", _signInValue.Text ?? "", _stepButton.Content as string, _stepButton.IsVisible);
}
