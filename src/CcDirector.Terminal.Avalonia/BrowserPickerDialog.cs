using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Terminal.Avalonia;

/// <summary>How long a <see cref="BrowserChoice"/> should be remembered.</summary>
public enum BrowserRememberScope
{
    /// <summary>Open once and remember nothing.</summary>
    None,

    /// <summary>Remember as the owning repository's default.</summary>
    Repository,

    /// <summary>Remember as the application-wide default.</summary>
    Application,
}

/// <summary>
/// What the user picked in <see cref="BrowserPickerDialog"/>: a browser+profile (or the operating
/// system default, when <see cref="Browser"/> is null) and whether to remember it.
/// </summary>
public sealed record BrowserChoice(BrowserInfo? Browser, string? ProfileFolder, BrowserRememberScope Scope);

/// <summary>
/// The "Choose Browser" dialog behind the terminal's and the History tab's link menu. It replaces
/// the four-level cascading submenu that shipped with the per-repository default (#1533): every
/// browser+profile is one flat radio row, and remembering the choice is a checkbox next to the
/// Open button instead of a third and fourth level of hover-only submenu. One pointer press per
/// decision, and nothing collapses if the pointer moves diagonally.
///
/// Browser detection reads each browser's <c>Local State</c> from disk, so it runs in the
/// background after the window is already on screen (CLAUDE.md rule 1) - the dialog paints
/// immediately with "Loading browsers..." and fills itself in when the read completes.
/// </summary>
public sealed class BrowserPickerDialog : Window
{
    private const string GroupName = "browserPickerGroup";

    private readonly string _target;
    private readonly string? _repoPath;
    private BrowserDefault? _currentDefault;

    private readonly List<(RadioButton Radio, BrowserInfo? Browser, string? ProfileFolder)> _rows = new();

    private readonly StackPanel _optionsPanel;
    private readonly TextBlock _statusText;
    private readonly CheckBox _rememberCheck;
    private readonly RadioButton _scopeRepoRadio;
    private readonly RadioButton _scopeAppRadio;
    private readonly StackPanel _scopePanel;
    private readonly Button _openButton;

    /// <summary>What the user chose, or null when the dialog was cancelled.</summary>
    public BrowserChoice? Choice { get; private set; }

    /// <param name="target">The URL or file path being opened, shown so the user knows what they are aiming at.</param>
    /// <param name="repoPath">The link's owning repository, or null when it has none.</param>
    public BrowserPickerDialog(string target, string? repoPath)
    {
        FileLog.Write($"[BrowserPickerDialog] Constructor: target={target}, repo={repoPath ?? "(none)"}");

        _target = target ?? throw new ArgumentNullException(nameof(target));
        _repoPath = repoPath;

        Title = "Choose browser";
        Width = 520;
        Height = 480;
        MinWidth = 420;
        MinHeight = 360;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#252526");

        _optionsPanel = new StackPanel { Spacing = 2 };
        _statusText = new TextBlock
        {
            Text = "Loading browsers...",
            Foreground = Brush.Parse("#888888"),
            FontSize = 12,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(6, 6, 6, 6),
        };
        _optionsPanel.Children.Add(_statusText);

        _rememberCheck = new CheckBox
        {
            Content = new TextBlock { Text = "Remember this choice", Foreground = Brush.Parse("#CCCCCC"), FontSize = 13 },
            Foreground = Brush.Parse("#CCCCCC"),
        };
        _rememberCheck.IsCheckedChanged += (_, _) => UpdateScopeEnabled();

        _scopeRepoRadio = MakeScopeRadio(
            _repoPath is null ? "For this repository" : $"For this repository ({ShortRepoName(_repoPath)})");
        _scopeAppRadio = MakeScopeRadio("For every repository");
        _scopeRepoRadio.IsChecked = true;

        _scopePanel = new StackPanel { Spacing = 2, Margin = new Thickness(26, 4, 0, 0) };
        if (_repoPath is not null)
            _scopePanel.Children.Add(_scopeRepoRadio);
        _scopePanel.Children.Add(_scopeAppRadio);

        // With no owning repository there is only one place a default can go, so the checkbox alone
        // carries the meaning and the scope radios would be a choice of one.
        if (_repoPath is null)
        {
            _scopeAppRadio.IsChecked = true;
            _scopePanel.IsVisible = false;
        }

        _openButton = MakeButton("Open", primary: true);
        _openButton.IsDefault = true;
        _openButton.Click += (_, _) => Accept();

        var cancelButton = MakeButton("Cancel", primary: false);
        cancelButton.IsCancel = true;
        cancelButton.Click += (_, _) =>
        {
            FileLog.Write("[BrowserPickerDialog] Cancelled");
            Choice = null;
            Close();
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonRow.Children.Add(_openButton);
        buttonRow.Children.Add(cancelButton);

        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
        };

        var header = new TextBlock
        {
            Text = "Open in browser",
            Foreground = Brush.Parse("#CCCCCC"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var targetText = new TextBlock
        {
            Text = _target,
            Foreground = Brush.Parse("#888888"),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 10),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
        };
        ToolTip.SetTip(targetText, _target);
        Grid.SetRow(targetText, 1);
        grid.Children.Add(targetText);

        var listBorder = new Border
        {
            Background = Brush.Parse("#1E1E1E"),
            BorderBrush = Brush.Parse("#3C3C3C"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _optionsPanel,
            },
        };
        Grid.SetRow(listBorder, 2);
        grid.Children.Add(listBorder);

        var rememberPanel = new StackPanel { Spacing = 0, Margin = new Thickness(0, 12, 0, 0) };
        rememberPanel.Children.Add(_rememberCheck);
        rememberPanel.Children.Add(_scopePanel);
        Grid.SetRow(rememberPanel, 3);
        grid.Children.Add(rememberPanel);

        Grid.SetRow(buttonRow, 4);
        grid.Children.Add(buttonRow);

        Content = grid;

        UpdateScopeEnabled();

        // Disk I/O off the UI thread; the window is already painted by the time this runs.
        Opened += async (_, _) => await LoadBrowsersAsync();
    }

    /// <summary>
    /// Opens the picker over <paramref name="owner"/> and returns the user's choice, or null when
    /// they cancelled.
    /// </summary>
    public static async Task<BrowserChoice?> ShowAsync(Window owner, string target, string? repoPath)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dialog = new BrowserPickerDialog(target, repoPath);
        await dialog.ShowDialog(owner);
        return dialog.Choice;
    }

    /// <summary>
    /// Reads the installed browsers, their profiles, and the default in force - all disk reads, all
    /// on one background hop - then rebuilds the option rows. Detection failures are surfaced in the
    /// list rather than swallowed: the system default row still works, so the dialog stays usable,
    /// but the user is told what went wrong.
    /// </summary>
    private async Task LoadBrowsersAsync()
    {
        FileLog.Write("[BrowserPickerDialog] LoadBrowsersAsync");

        List<(BrowserInfo Browser, IReadOnlyList<BrowserProfile> Profiles)> detected = new();
        string? error = null;

        try
        {
            var repoPath = _repoPath;
            var loaded = await Task.Run(() =>
            {
                var browsers = new List<(BrowserInfo, IReadOnlyList<BrowserProfile>)>();
                foreach (var browser in BrowserLauncher.DetectBrowsers(forceProbe: true))
                    browsers.Add((browser, BrowserLauncher.GetProfiles(browser)));
                return (Browsers: browsers, Current: BrowserDefaultStore.Resolve(repoPath));
            });

            detected = loaded.Browsers;
            _currentDefault = loaded.Current;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserPickerDialog] LoadBrowsersAsync FAILED: {ex.Message}");
            error = ex.Message;
        }

        BuildOptions(detected, error);
        FileLog.Write($"[BrowserPickerDialog] LoadBrowsersAsync: browsers={detected.Count}, rows={_rows.Count}");
    }

    /// <summary>
    /// Builds the flat option list: the system default first, then one row per browser+profile. Every
    /// row is a single radio - there is no nesting and nothing to hover through.
    /// </summary>
    private void BuildOptions(
        IReadOnlyList<(BrowserInfo Browser, IReadOnlyList<BrowserProfile> Profiles)> detected, string? error)
    {
        _optionsPanel.Children.Clear();
        _rows.Clear();

        var systemRadio = MakeOptionRadio("System default browser", "Whatever Windows opens links with today.");
        _rows.Add((systemRadio, null, null));
        _optionsPanel.Children.Add(systemRadio);

        foreach (var (browser, profiles) in detected)
        {
            if (profiles.Count == 0)
            {
                _optionsPanel.Children.Add(new TextBlock
                {
                    Text = $"{browser.DisplayName}: no profiles found",
                    Foreground = Brush.Parse("#666666"),
                    FontSize = 11,
                    Margin = new Thickness(6, 6, 6, 2),
                });
                continue;
            }

            foreach (var profile in profiles)
            {
                var subtitle = profile.Account is null ? browser.DisplayName : $"{browser.DisplayName} - {profile.Account}";
                var radio = MakeOptionRadio(profile.DisplayName, subtitle);
                _rows.Add((radio, browser, profile.FolderName));
                _optionsPanel.Children.Add(radio);
            }
        }

        if (error is not null)
        {
            _optionsPanel.Children.Add(new TextBlock
            {
                Text = $"Could not read the installed browsers: {error}",
                Foreground = Brush.Parse("#F59E0B"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 8, 6, 2),
            });
        }

        PreselectCurrentDefault();
    }

    /// <summary>Checks the row matching the default in force, so the dialog opens on today's answer.</summary>
    private void PreselectCurrentDefault()
    {
        if (_currentDefault is not null)
        {
            foreach (var (radio, browser, profileFolder) in _rows)
            {
                if (browser is null)
                    continue;

                if (string.Equals(browser.ExePath, _currentDefault.ExePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(profileFolder, _currentDefault.ProfileFolder, StringComparison.Ordinal))
                {
                    radio.IsChecked = true;
                    return;
                }
            }
        }

        // Nothing remembered, or the remembered browser is gone: the system default is the honest
        // preselection, because it is what a plain "Open in Browser" would do right now.
        if (_rows.Count > 0)
            _rows[0].Radio.IsChecked = true;
    }

    private void Accept()
    {
        var scope = BrowserRememberScope.None;
        if (_rememberCheck.IsChecked == true)
            scope = _scopeRepoRadio.IsChecked == true && _repoPath is not null
                ? BrowserRememberScope.Repository
                : BrowserRememberScope.Application;

        foreach (var (radio, browser, profileFolder) in _rows)
        {
            if (radio.IsChecked != true)
                continue;

            Choice = new BrowserChoice(browser, profileFolder, scope);
            FileLog.Write($"[BrowserPickerDialog] Accept: browser={browser?.DisplayName ?? "(system default)"}, profile={profileFolder ?? "(none)"}, scope={scope}");
            Close();
            return;
        }

        // The list always contains the system default row and one row is always checked, so reaching
        // here means the list never loaded. Keep the window open rather than closing on no answer.
        FileLog.Write("[BrowserPickerDialog] Accept: no row selected, staying open");
    }

    private void UpdateScopeEnabled()
    {
        bool remember = _rememberCheck.IsChecked == true;
        _scopeRepoRadio.IsEnabled = remember;
        _scopeAppRadio.IsEnabled = remember;
        _scopePanel.Opacity = remember ? 1.0 : 0.5;
    }

    /// <summary>One option row: a bold title over a dimmer subtitle, the whole row clickable.</summary>
    private static RadioButton MakeOptionRadio(string title, string subtitle)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush.Parse("#CCCCCC"),
            FontSize = 13,
        });
        content.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = Brush.Parse("#888888"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        return new RadioButton
        {
            GroupName = GroupName,
            Content = content,
            Foreground = Brush.Parse("#CCCCCC"),
            Padding = new Thickness(6, 6),
            Margin = new Thickness(0, 1),
        };
    }

    private static RadioButton MakeScopeRadio(string title) => new()
    {
        GroupName = "browserPickerScopeGroup",
        Content = new TextBlock { Text = title, Foreground = Brush.Parse("#CCCCCC"), FontSize = 12 },
        Foreground = Brush.Parse("#CCCCCC"),
    };

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text,
        Height = 30,
        MinWidth = primary ? 100 : 90,
        Padding = new Thickness(14, 0),
        Background = Brush.Parse(primary ? "#007ACC" : "#3C3C3C"),
        Foreground = Brush.Parse(primary ? "#FFFFFF" : "#CCCCCC"),
        BorderThickness = new Thickness(0),
        Cursor = new Cursor(StandardCursorType.Hand),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>The repository's folder name, which is what the user recognizes it by.</summary>
    private static string ShortRepoName(string repoPath)
    {
        var trimmed = repoPath.Replace('/', '\\').TrimEnd('\\');
        var lastSlash = trimmed.LastIndexOf('\\');
        return lastSlash >= 0 && lastSlash < trimmed.Length - 1 ? trimmed[(lastSlash + 1)..] : trimmed;
    }
}
