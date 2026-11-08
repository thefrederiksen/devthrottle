using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Instances;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Avalonia.Media.Immutable;

namespace CcDirector.Avalonia;

public partial class SelectDirectorDialog : Window
{
    private static readonly IBrush RunningBrush = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush StoppedBrush = new ImmutableSolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush CheckingBrush = new ImmutableSolidColorBrush(Color.Parse("#AAAAAA"));

    private readonly ObservableCollection<InstanceRow> _rows = new();

    /// <summary>The slug the user chose to launch, once the dialog closes with true.</summary>
    public string? LaunchSlug { get; private set; }

    /// <summary>True when the user asked to create a new instance instead of launching one.</summary>
    public bool WantsNew { get; private set; }

    public SelectDirectorDialog(string? header = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(header))
            HeaderText.Text = header;
        InstanceList.ItemsSource = _rows;
        Loaded += OnLoaded;
    }

    // Parameterless constructor for the XAML designer.
    public SelectDirectorDialog() : this(null) { }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Populate immediately for a responsive UI, then probe liveness in the background.
        try
        {
            var instances = NamedInstanceRegistry.List();
            foreach (var inst in instances)
                _rows.Add(new InstanceRow(inst));
            if (_rows.Count > 0)
                InstanceList.SelectedIndex = 0;
            _ = ProbeAllAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SelectDirectorDialog] Load FAILED: {ex.Message}");
        }
    }

    private async Task ProbeAllAsync()
    {
        var tasks = _rows.Select(async row =>
        {
            var running = await Task.Run(() => IsInstanceRunning(row.Slug));
            await Dispatcher.UIThread.InvokeAsync(() => row.SetStatus(running));
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Whether a live Director owns this instance, decided from the registration the running
    /// process wrote (process id certified by its start time), never from a socket - the
    /// Remove-the-network-port mission deleted the Director's listener, so there is no port to
    /// probe and never will be again. Ambiguous (more than one live claimant) still means
    /// SOMETHING is running there, which is what this dialog's green dot claims.
    /// </summary>
    private static bool IsInstanceRunning(string slug)
    {
        try
        {
            var instanceHome = Path.Combine(InstanceContext.SharedRoot, "instances", slug);
            // The default instance may predate the per-instance layout; its old registrations
            // live flat at the shared root, exactly as the launcher reads them.
            var legacyFlat = string.Equals(slug, InstanceContext.DefaultSlug, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(InstanceContext.SharedRoot, "config", "director", "instances")
                : null;
            var lookup = new DirectorInstanceLocator(instanceHome, legacyFlat).Resolve();
            // Everything except NotRunning means SOMETHING live is holding that instance, which is
            // exactly what this dialog's green dot claims. Listed as a negative rather than as a set of
            // positives on purpose: a new outcome added later defaults to "running" here, which is the
            // safe direction for a liveness indicator - the unsafe direction is showing an instance as
            // free when a process is sitting in it and having the user start a second one.
            return lookup.Outcome != DirectorResolution.NotRunning;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SelectDirectorDialog] liveness for slug={slug} FAILED: {ex.Message}");
            return false;
        }
    }

    private void InstanceList_DoubleTapped(object? sender, RoutedEventArgs e) => LaunchSelected();

    private void BtnLaunch_Click(object? sender, RoutedEventArgs e) => LaunchSelected();

    private void BtnNew_Click(object? sender, RoutedEventArgs e)
    {
        WantsNew = true;
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void LaunchSelected()
    {
        if (InstanceList.SelectedItem is not InstanceRow row)
            return;
        LaunchSlug = row.Slug;
        Close(true);
    }

    /// <summary>One selectable instance row; status updates in place as the liveness probe returns.</summary>
    public sealed class InstanceRow : INotifyPropertyChanged
    {
        private string _status = "checking…";
        private IBrush _statusBrush = CheckingBrush;

        public InstanceRow(NamedInstance inst)
        {
            Slug = inst.Name;
            DisplayName = inst.DisplayName;
            var gateway = string.IsNullOrWhiteSpace(inst.GatewayUrl) ? "no gateway" : inst.GatewayUrl;
            Meta = $"slug {inst.Name} · {gateway}";
        }

        public string Slug { get; }
        public string DisplayName { get; }
        public string Meta { get; }

        public string Status
        {
            get => _status;
            private set { _status = value; OnChanged(nameof(Status)); }
        }

        public IBrush StatusBrush
        {
            get => _statusBrush;
            private set { _statusBrush = value; OnChanged(nameof(StatusBrush)); }
        }

        public void SetStatus(bool running)
        {
            Status = running ? "running" : "stopped";
            StatusBrush = running ? RunningBrush : StoppedBrush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
