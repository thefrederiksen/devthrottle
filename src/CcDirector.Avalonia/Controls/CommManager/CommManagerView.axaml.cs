using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls.CommManager;

public partial class CommManagerView : UserControl
{
    private CommManagerViewModel? _vm;
    private bool _initialized;
    private string _activeTab = "Pending";
    private string _activePlatformFilter = "All";

    // All platform filter buttons, indexed by tag name
    private readonly Dictionary<string, Button> _filterButtons = new();
    private readonly Dictionary<string, Button> _tabButtons = new();

    public CommManagerView()
    {
        InitializeComponent();
    }

    public async Task InitializeAsync()
    {
        FileLog.Write("[CommManagerView] InitializeAsync");
        if (_initialized) return;
        _initialized = true;

        _vm = new CommManagerViewModel();
        DataContext = _vm;

        // Wire up callback delegates for UI operations
        _vm.ConfirmDeleteCallback = ConfirmDeleteAsync;
        _vm.ShowErrorCallback = ShowErrorAsync;
        _vm.CopyToClipboardCallback = CopyToClipboardAsync;
        _vm.ShowScheduleDialogCallback = ShowScheduleDialogAsync;
        _vm.ShowSendProgressCallback = ShowSendProgress;

        // Cache filter and tab buttons
        _filterButtons["All"] = FilterAll;
        _filterButtons["Email"] = FilterEmail;
        _filterButtons["LinkedIn"] = FilterLinkedIn;
        _filterButtons["Facebook"] = FilterFacebook;
        _filterButtons["YouTube"] = FilterYouTube;
        _filterButtons["WhatsApp"] = FilterWhatsApp;
        _filterButtons["Reddit"] = FilterReddit;
        _filterButtons["Twitter"] = FilterTwitter;
        _filterButtons["Blog"] = FilterBlog;

        _tabButtons["Pending"] = BtnTabPending;
        _tabButtons["Approved"] = BtnTabApproved;
        _tabButtons["Rejected"] = BtnTabRejected;
        _tabButtons["Sent"] = BtnTabSent;

        // Subscribe to count changes for badge updates
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CommManagerViewModel.PendingCount)
                or nameof(CommManagerViewModel.ApprovedCount)
                or nameof(CommManagerViewModel.RejectedCount)
                or nameof(CommManagerViewModel.SentCount))
            {
                UpdateBadges();
            }
        };

        await _vm.InitializeAsync();
        UpdateBadges();
    }

    public void StartPolling() => _vm?.StartPolling();
    public void StopPolling() => _vm?.StopPolling();

    /// <summary>
    /// Returns the current pending count for the sidebar badge.
    /// </summary>
    public int PendingCount => _vm?.PendingCount ?? 0;

    /// <summary>
    /// Event raised when pending count changes, so MainWindow can update sidebar badge.
    /// </summary>
    public event Action<int>? PendingCountChanged;

    private void UpdateBadges()
    {
        if (_vm == null) return;

        UpdateBadge(BadgePending, BadgePendingText, _vm.PendingCount);
        UpdateBadge(BadgeApproved, BadgeApprovedText, _vm.ApprovedCount);
        UpdateBadge(BadgeRejected, BadgeRejectedText, _vm.RejectedCount);
        UpdateBadge(BadgeSent, BadgeSentText, _vm.SentCount);

        PendingCountChanged?.Invoke(_vm.PendingCount);
    }

    private static void UpdateBadge(Border badge, TextBlock text, int count)
    {
        badge.IsVisible = count > 0;
        text.Text = count.ToString();
    }

    private void StatusTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tab) return;
        SwitchTab(tab);
    }

    private void SwitchTab(string tab)
    {
        FileLog.Write($"[CommManagerView] SwitchTab: {tab}");
        _activeTab = tab;

        var accent = SolidColorBrush.Parse("#007ACC");
        var transparent = Brushes.Transparent;
        var white = Brushes.White;
        var inactive = SolidColorBrush.Parse("#888888");

        foreach (var (name, btn) in _tabButtons)
        {
            btn.Background = name == tab ? accent : transparent;
            btn.Foreground = name == tab ? white : inactive;
        }

        // Show/hide action button groups
        PendingActions.IsVisible = tab == "Pending";
        ApprovedActions.IsVisible = tab == "Approved";
        RejectedActions.IsVisible = tab == "Rejected";
        SentActions.IsVisible = tab == "Sent";

        _vm?.OnTabChanged(tab);
    }

    private void PlatformFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string platform) return;
        FileLog.Write($"[CommManagerView] PlatformFilter_Click: {platform}");
        _activePlatformFilter = platform;

        var active = SolidColorBrush.Parse("#094771");
        var inactive = SolidColorBrush.Parse("#333333");
        var white = Brushes.White;
        var gray = SolidColorBrush.Parse("#CCCCCC");

        foreach (var (name, filterBtn) in _filterButtons)
        {
            filterBtn.Background = name == platform ? active : inactive;
            filterBtn.Foreground = name == platform ? white : gray;
        }

        _vm?.SetPlatformFilterCommand.Execute(platform);
    }

    // === UI Callbacks ===

    private async Task<bool> ConfirmDeleteAsync(string message, string title)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = SolidColorBrush.Parse("#252526")
        };

        var result = false;
        var grid = new Grid { Margin = new global::Avalonia.Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var text = new TextBlock
        {
            Text = message,
            Foreground = SolidColorBrush.Parse("#CCCCCC"),
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            Margin = new global::Avalonia.Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(text, 0);
        grid.Children.Add(text);

        var buttons = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };
        Grid.SetRow(buttons, 1);

        var yesBtn = new Button
        {
            Content = "Yes",
            Background = SolidColorBrush.Parse("#DC3545"),
            Foreground = Brushes.White,
            Width = 70,
            Height = 28,
            BorderThickness = new global::Avalonia.Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        yesBtn.Click += (_, _) => { result = true; dialog.Close(); };

        var noBtn = new Button
        {
            Content = "No",
            Background = SolidColorBrush.Parse("#3C3C3C"),
            Foreground = SolidColorBrush.Parse("#CCCCCC"),
            Width = 70,
            Height = 28,
            BorderThickness = new global::Avalonia.Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        noBtn.Click += (_, _) => { result = false; dialog.Close(); };

        buttons.Children.Add(yesBtn);
        buttons.Children.Add(noBtn);
        grid.Children.Add(buttons);

        dialog.Content = grid;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window owner)
            await dialog.ShowDialog(owner);

        return result;
    }

    /// <summary>
    /// The error box the view model calls through <c>ShowErrorCallback</c>. Error report decision (issue #3675): this
    /// window reports nothing itself. Every call of the callback is a site the scan reads (an error-named call) and
    /// must be given text the helper already reported - which is how the scan keeps a future unreported caller out.
    /// </summary>
    private async Task ShowErrorAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Error",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = SolidColorBrush.Parse("#252526")
        };

        var grid = new Grid { Margin = new global::Avalonia.Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var text = new TextBlock
        {
            Text = message,
            Foreground = SolidColorBrush.Parse("#CCCCCC"),
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13,
            Margin = new global::Avalonia.Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(text, 0);
        grid.Children.Add(text);

        var okBtn = new Button
        {
            Content = "OK",
            Background = SolidColorBrush.Parse("#3C3C3C"),
            Foreground = SolidColorBrush.Parse("#CCCCCC"),
            Width = 70,
            Height = 28,
            BorderThickness = new global::Avalonia.Thickness(0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        okBtn.Click += (_, _) => dialog.Close();
        Grid.SetRow(okBtn, 1);
        grid.Children.Add(okBtn);

        dialog.Content = grid;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window owner)
            await dialog.ShowDialog(owner);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
            await topLevel.Clipboard.SetTextAsync(text);
    }

    private async Task<(string Timing, DateTime? ScheduledFor)?> ShowScheduleDialogAsync(
        CcDirector.Core.Communications.Models.ContentItem item)
    {
        var dialog = new ScheduleDialog(item.ScheduledFor);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window owner)
        {
            var result = await dialog.ShowDialog<bool?>(owner);
            if (result == true)
            {
                return (dialog.SelectedTiming, dialog.SelectedDateTime);
            }
        }
        return null;
    }

    private SendProgressHandle ShowSendProgress(int totalItems)
    {
        var dialog = new SendProgressDialog(totalItems);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is Window owner)
            dialog.Show(owner);
        else
            dialog.Show();

        return new SendProgressHandle(
            reportProgress: dialog.ReportProgress,
            reportComplete: dialog.ReportComplete,
            close: dialog.Close);
    }
}
