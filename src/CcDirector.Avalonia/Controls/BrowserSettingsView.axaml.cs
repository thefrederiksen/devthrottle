using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Settings > Browsers: create, sign-in-once, rename, stop/start, and remove drivable automation
/// browsers. Every action calls the same <see cref="AutomationBrowserService"/> engine the Control API
/// exposes to agents, and every verdict shown is the Core fold rendered verbatim - the tab is a layout
/// over the one source of truth, never a second implementation of it.
/// </summary>
public partial class BrowserSettingsView : UserControl
{
    /// <summary>Raised after any successful change (create/rename/remove/start/stop/sign-in) so the
    /// host can refresh other Browsers surfaces (the rail group).</summary>
    public event EventHandler? Changed;

    private IReadOnlyList<AutomationBrowserView> _views = Array.Empty<AutomationBrowserView>();
    private ObservableCollection<BrowserCardViewModel> _cards = new();
    private readonly HashSet<string> _renamingIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshing;

    /// <summary>Bumped by every refresh. A status probe carries the generation it started under and
    /// drops its result if a newer refresh has replaced the list underneath it.</summary>
    private int _refreshGeneration;

    public BrowserSettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Open the inline create panel (used by the Browsers menu's "New Browser...").</summary>
    public void OpenCreatePanel()
    {
        CreatePanel.IsVisible = true;
        // The empty state and the form both want the window. While the form is open the form wins -
        // the teaching has already been read by anyone who got this far, and leaving it underneath
        // pushes the fields the user is filling in off the top of a small window.
        EmptyStatePanel.IsVisible = false;
        CreateNameBox.Focus();
    }

    /// <summary>
    /// Re-read the registry and repaint the tab, in TWO passes.
    ///
    /// The list itself is local file data and costs microseconds, so it paints immediately with every
    /// browser's real name, browser, port and account. Whether each one is RUNNING is the only slow
    /// fact - it needs a probe of that browser's debug port, and on a machine whose network stack does
    /// not refuse a dead port promptly that probe takes seconds. So each browser shows "Checking..."
    /// and its own row is swapped in the moment its own probe answers. Nothing waits for anything else.
    ///
    /// This replaces a single pass that probed every browser before painting anything, which left the
    /// whole tab blank behind "Checking browsers..." for as long as the slowest probe took.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        var generation = ++_refreshGeneration;
        try
        {
            var previous = _views;
            var harnessInstalled = false;
            IReadOnlyList<AutomationBrowserView> views = Array.Empty<AutomationBrowserView>();
            IReadOnlyList<BrowserInfo> installed = Array.Empty<BrowserInfo>();
            await Task.Run(() =>
            {
                harnessInstalled = AutomationBrowserViewFold.IsHarnessInstalled();
                installed = BrowserLauncher.DetectBrowsers(forceProbe: true);
                views = AutomationBrowserViewFold.ListPending(previous);
            });

            _views = views;
            HarnessBanner.IsVisible = !harnessInstalled;

            // With no browsers, the empty state IS the tab: it has the whole window to explain what a
            // browser here is, why it is not the one you already use, and that it starts signed in to
            // nothing. The header repeats the first line of that, so it stays out of the way until
            // there is a list to head. The create panel replaces the empty state while it is open,
            // rather than pushing a wall of teaching below the form the user is already filling in.
            var isEmpty = views.Count == 0;
            EmptyStatePanel.IsVisible = isEmpty && !CreatePanel.IsVisible;
            HeaderPanel.IsVisible = !isEmpty;
            StatusText.Text = "";

            // The create panel offers only the browsers actually installed on this machine.
            var kinds = installed.Select(b => b.Kind.ToString()).ToList();
            var selected = CreateKindCombo.SelectedItem as string;
            CreateKindCombo.ItemsSource = kinds;
            CreateKindCombo.SelectedItem = kinds.Contains(selected ?? "") ? selected : kinds.FirstOrDefault();
            NewBrowserButton.IsEnabled = kinds.Count > 0;
            if (kinds.Count == 0)
                // Named from the enum rather than spelled out, so this sentence cannot go stale the
                // next time a browser is added - a list that says "Chrome or Edge" on a build that also
                // supports Brave tells someone to install a browser they may already have.
                StatusText.Text =
                    $"None of the browsers DevThrottle can drive ({string.Join(", ", Enum.GetNames<BrowserKind>())}) "
                    + "was found on this machine, so no browser can be created.";

            _cards = new ObservableCollection<BrowserCardViewModel>(views.Select(v => new BrowserCardViewModel(v)
            {
                IsRenaming = _renamingIds.Contains(v.Id),
            }));
            BrowserCards.ItemsSource = _cards;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] RefreshAsync FAILED: {ex.Message}");
            StatusText.Text = $"Could not read the browsers list: {ex.Message}";
            return;
        }
        finally
        {
            _refreshing = false;
        }

        // The list is on screen; now find out which of them are running. Deliberately NOT awaited into
        // the paint above - that is the whole point.
        await ProbeStatusesAsync(generation);
    }

    /// <summary>
    /// Probe every listed browser CONCURRENTLY and swap each row in as its own answer arrives. A
    /// browser that answers in milliseconds is not made to wait behind one that takes seconds.
    ///
    /// <paramref name="generation"/> is the refresh this probe run belongs to. A result is dropped when
    /// a newer refresh has started, because by then the rows it would update belong to a list that no
    /// longer exists - writing into it would resurrect a browser the user just removed, or paint a
    /// status onto the wrong row.
    /// </summary>
    private async Task ProbeStatusesAsync(int generation)
    {
        var browsers = _views;
        if (browsers.Count == 0) return;

        await Task.WhenAll(browsers.Select(async pending =>
        {
            AutomationBrowserView probed;
            try
            {
                probed = await Task.Run(() => AutomationBrowserViewFold.FoldAsync(AutomationBrowserRegistry.Get(pending.Id)));
            }
            catch (Exception ex)
            {
                // One browser that cannot be probed (removed mid-refresh, unreadable entry) leaves its
                // own row saying "Checking..." and takes nothing else down with it.
                FileLog.Write($"[BrowserSettingsView] ProbeStatusesAsync: id={pending.Id} failed (non-fatal): {ex.Message}");
                return;
            }

            if (generation != _refreshGeneration) return;
            ApplyProbedView(probed);
        }));
    }

    /// <summary>Swap one browser's finished view into the list, keeping its position and any in-progress
    /// rename. Called on the UI thread - Avalonia marshals the await continuation back to it.</summary>
    private void ApplyProbedView(AutomationBrowserView probed)
    {
        _views = _views.Select(v => v.Id == probed.Id ? probed : v).ToList();

        var index = IndexOfCard(probed.Id);
        if (index < 0) return;

        _cards[index] = new BrowserCardViewModel(probed)
        {
            IsRenaming = _renamingIds.Contains(probed.Id),
        };
    }

    private int IndexOfCard(string id)
    {
        for (var i = 0; i < _cards.Count; i++)
            if (_cards[i].Id == id)
                return i;
        return -1;
    }

    private async Task RefreshAndNotifyAsync()
    {
        await RefreshAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Window OwnerWindow()
        => TopLevel.GetTopLevel(this) as Window
           ?? throw new InvalidOperationException("BrowserSettingsView has no owner window.");

    private BrowserCardViewModel? CardFor(object? sender)
    {
        var id = (sender as Button)?.Tag as string;
        return _cards.FirstOrDefault(c => c.Id == id);
    }

    // ---- header actions ----

    private void BtnNewBrowser_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowserSettingsView] BtnNewBrowser_Click");
        CreateError.IsVisible = false;
        OpenCreatePanel();
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowserSettingsView] BtnRefresh_Click");
        await RefreshAsync();
    }

    /// <summary>
    /// Install browser-harness on this machine (issue #1012). This button used to open a GitHub install
    /// page and leave the user to it - which also made the wizard's "set browsers up later, in the
    /// Browsers group" a promise that led to a documentation page rather than to a working browser. It
    /// now runs the same installer the wizard runs.
    /// </summary>
    private async void BtnInstallHarness_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[BrowserSettingsView] BtnInstallHarness_Click: installing");
        HarnessInstallButton.IsEnabled = false;
        HarnessManualLink.IsVisible = false;
        try
        {
            var progress = new Progress<string>(line => StatusText.Text = line);
            var result = await Task.Run(() => BrowserHarnessInstaller.InstallAsync(progress));

            StatusText.Text = result.Message;
            if (!result.Success)
            {
                // Say what went wrong and offer the manual page. Never re-check and never continue as
                // though it had worked (CLAUDE.md rule 3).
                FileLog.Write($"[BrowserSettingsView] BtnInstallHarness_Click FAILED: {result.Message}");
                HarnessManualLink.IsVisible = true;
                return;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnInstallHarness_Click FAILED: {ex.Message}");
            StatusText.Text = $"Could not install Browser Harness: {ex.Message}";
            HarnessManualLink.IsVisible = true;
        }
        finally
        {
            HarnessInstallButton.IsEnabled = true;
        }
    }

    /// <summary>The manual install page, offered only after our own install failed.</summary>
    private void BtnHarnessInstallPage_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnHarnessInstallPage_Click -> {AutomationBrowserViewFold.HarnessInstallUrl}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AutomationBrowserViewFold.HarnessInstallUrl)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnHarnessInstallPage_Click FAILED: {ex.Message}");
            StatusText.Text = $"Could not open the install guide: {ex.Message}";
        }
    }

    // ---- create ----

    private async void BtnCreate_Click(object? sender, RoutedEventArgs e)
    {
        var name = CreateNameBox.Text?.Trim() ?? "";
        var kindText = CreateKindCombo.SelectedItem as string;
        FileLog.Write($"[BrowserSettingsView] BtnCreate_Click: name={name}, kind={kindText}");

        try
        {
            if (name.Length == 0)
                throw new ArgumentException("Give the browser a name first.");
            if (!Enum.TryParse<BrowserKind>(kindText, ignoreCase: true, out var kind))
                throw new ArgumentException("Pick which browser to use.");

            CreateError.IsVisible = false;
            var created = await Task.Run(() => AutomationBrowserService.Create(name, kind));

            CreateNameBox.Text = "";
            CreatePanel.IsVisible = false;
            await RefreshAndNotifyAsync();

            // Go straight into the sign-in, which is what the button says it will do. A profile
            // created and left unsigned is worth nothing to an agent, and the old flow - create, then
            // find and click "Sign in once" on the new card - made the one step that MATTERS the one
            // step the user had to know to take. Backing out is still allowed: the profile exists and
            // its card offers the sign-in whenever they come back to it.
            //
            // Reported through StatusText and NOT through CreateError, in its own try: by this point
            // the profile has been created and the create panel is closed, so a failure written to
            // CreateError would land in a hidden control and the user would see nothing at all. What
            // can fail here is the launch - the browser not coming up on its debug port - and that
            // must be visible, with the profile still listed so they can retry from its card.
            try
            {
                if (await BrowserSignInFlow.RunAsync(OwnerWindow(), AutomationBrowserViewFold.FoldPending(created)))
                    StatusText.Text = $"\"{name}\" is signed in and ready to drive.";
                else
                    StatusText.Text = $"Created \"{name}\". It holds no login yet - use Sign in once when you are ready.";
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BrowserSettingsView] BtnCreate_Click: sign-in after create FAILED: {ex.Message}");
                StatusText.Text = $"Created \"{name}\", but it could not be opened for sign-in: {ex.Message}";
            }

            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnCreate_Click FAILED: {ex.Message}");
            CreateError.Text = ex.Message;
            CreateError.IsVisible = true;
        }
    }

    private void BtnCreateCancel_Click(object? sender, RoutedEventArgs e)
    {
        CreatePanel.IsVisible = false;
        CreateError.IsVisible = false;
        // Backing out of the form on a machine with no browsers must land back on the explanation,
        // not on a blank tab.
        EmptyStatePanel.IsVisible = _views.Count == 0;
    }

    /// <summary>
    /// Fill the name box from one of the offered role names. These are a starting point for the
    /// hardest part of the form - people stall on naming, then type "test" and regret it - so the
    /// text stays editable rather than being a fixed choice.
    /// </summary>
    private void BtnNameSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string suggestion) return;
        CreateNameBox.Text = suggestion;
        CreateNameBox.Focus();
        CreateNameBox.CaretIndex = suggestion.Length;
    }

    // ---- per-card actions ----

    private async void BtnStart_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnStart_Click: id={card.Id}");
            card.IsBusy = true;
            StatusText.Text = $"Starting \"{card.Name}\"...";
            await Task.Run(() => AutomationBrowserService.LaunchAsync(card.Id));
            StatusText.Text = $"\"{card.Name}\" is up.";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnStart_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    private async void BtnStop_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnStop_Click: id={card.Id}");
            card.IsBusy = true;
            StatusText.Text = $"Stopping \"{card.Name}\"...";
            await Task.Run(() => AutomationBrowserService.StopAsync(card.Id));
            StatusText.Text = $"Stopped \"{card.Name}\". Its login is kept - start it again any time.";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnStop_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    private async void BtnSignIn_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnSignIn_Click: id={card.Id}");
            var view = _views.First(v => v.Id == card.Id);
            card.IsBusy = true;
            if (await BrowserSignInFlow.RunAsync(OwnerWindow(), view))
                StatusText.Text = $"\"{card.Name}\" is signed in and ready to drive.";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnSignIn_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    private async void BtnAttach_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnAttach_Click: id={card.Id}");
            var view = _views.First(v => v.Id == card.Id);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException("The clipboard is not available.");
            await clipboard.SetTextAsync(view.AttachCommand);
            StatusText.Text = $"Copied: {view.AttachCommand}";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnAttach_Click FAILED: id={card.Id}, {ex.Message}");
            StatusText.Text = ex.Message;
        }
    }

    // ---- rename ----

    private void BtnRenameStart_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;
        FileLog.Write($"[BrowserSettingsView] BtnRenameStart_Click: id={card.Id}");
        _renamingIds.Add(card.Id);
        card.EditName = card.Name;
        card.IsRenaming = true;
    }

    private void BtnRenameCancel_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;
        _renamingIds.Remove(card.Id);
        card.IsRenaming = false;
    }

    private async void BtnRenameSave_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnRenameSave_Click: id={card.Id}, newName={card.EditName}");
            await Task.Run(() => AutomationBrowserService.Rename(card.Id, card.EditName));
            _renamingIds.Remove(card.Id);
            StatusText.Text = $"Renamed to \"{card.EditName.Trim()}\".";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnRenameSave_Click FAILED: id={card.Id}, {ex.Message}");
            StatusText.Text = ex.Message;
        }
    }

    // ---- remove ----

    private async void BtnRemove_Click(object? sender, RoutedEventArgs e)
    {
        var card = CardFor(sender);
        if (card is null) return;

        try
        {
            FileLog.Write($"[BrowserSettingsView] BtnRemove_Click: id={card.Id}");
            var view = _views.First(v => v.Id == card.Id);

            var signedInClause = view.LastSignedInUtc is null
                ? "It has never been signed in."
                : $"It was signed in on {view.LastSignedInUtc.Value.ToLocalTime():yyyy-MM-dd}, and that login is deleted with it.";
            var dialog = new ConfirmDialog(
                "Remove browser",
                $"This closes \"{view.Name}\" and permanently deletes its profile folder. {signedInClause} " +
                "To use this identity again you would have to create a new browser and sign in again.",
                confirmLabel: "Delete browser",
                cancelLabel: "Cancel");
            if (await dialog.ShowDialog<bool?>(OwnerWindow()) != true) return;

            card.IsBusy = true;
            StatusText.Text = $"Removing \"{view.Name}\"...";
            await Task.Run(() => AutomationBrowserService.RemoveAsync(view.Id));
            StatusText.Text = $"Removed \"{view.Name}\".";
            await RefreshAndNotifyAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserSettingsView] BtnRemove_Click FAILED: id={card.Id}, {ex.Message}");
            card.IsBusy = false;
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>
    /// One rendered browser card: the fold's strings verbatim, plus the brushes this surface maps the
    /// fold's names to, plus the two bits of transient UI state (renaming, busy).
    /// </summary>
    public sealed class BrowserCardViewModel : INotifyPropertyChanged
    {
        private static readonly IBrush ChromeIcon = Brush.Parse("#0F6CBD");
        private static readonly IBrush EdgeIcon = Brush.Parse("#1AA1B8");
        private static readonly IBrush PillGreyBg = Brush.Parse("#3C3C3C");
        private static readonly IBrush PillDarkText = Brush.Parse("#141413");
        private static readonly IBrush PillLightText = Brush.Parse("#CCCCCC");

        private bool _isRenaming;
        private bool _isBusy;
        private string _editName = "";

        public BrowserCardViewModel(AutomationBrowserView view)
        {
            Id = view.Id;
            Name = view.Name;
            Subtitle = $"{view.Subtitle}  (port {view.Port})";
            StatusLabel = view.StatusLabel;
            AttachToolTip = $"Copy to the clipboard: {view.AttachCommand}";
            IconLetter = view.Browser.Length > 0 ? view.Browser[..1] : "?";
            IconBackground = string.Equals(view.Browser, "Edge", StringComparison.OrdinalIgnoreCase) ? EdgeIcon : ChromeIcon;

            ShowStart = view.Status == AutomationBrowserStatus.Stopped;
            ShowSignIn = view.Status == AutomationBrowserStatus.NeedsSignIn;
            // Stop is offered for the two states we KNOW are running - never merely for "not stopped",
            // which would put a Stop button on a browser whose status we have not established yet.
            ShowStop = view.Status is AutomationBrowserStatus.Ready or AutomationBrowserStatus.NeedsSignIn;
            ShowAttach = view.Status == AutomationBrowserStatus.Ready;

            // The pill's color IS the fold's dot color; only the text contrast is chosen here. Checking
            // wears the same quiet grey as stopped: it is the absence of an answer, not a fourth state.
            var quiet = view.Status is AutomationBrowserStatus.Stopped or AutomationBrowserStatus.Checking;
            PillBackground = quiet ? PillGreyBg : StatusPalette.BrushFor(view.DotColor);
            PillForeground = quiet ? PillLightText : PillDarkText;
        }

        public string Id { get; }
        public string Name { get; }
        public string Subtitle { get; }
        public string StatusLabel { get; }
        public string AttachToolTip { get; }
        public string IconLetter { get; }
        public IBrush IconBackground { get; }
        public IBrush PillBackground { get; }
        public IBrush PillForeground { get; }
        public bool ShowStart { get; }
        public bool ShowSignIn { get; }
        public bool ShowAttach { get; }
        public bool ShowStop { get; }

        public bool IsRenaming
        {
            get => _isRenaming;
            set { _isRenaming = value; Raise(nameof(IsRenaming)); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; Raise(nameof(IsBusy)); Raise(nameof(NotBusy)); }
        }

        public bool NotBusy => !_isBusy;

        public string EditName
        {
            get => _editName;
            set { _editName = value; Raise(nameof(EditName)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
