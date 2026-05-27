using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CcDirectorClient.Voice;

namespace CcDirectorClient;

/// <summary>
/// The phone port of the Gateway's Transcripts page (transcripts.html). Lists the
/// recordings uploaded from the phone and transcribed, lets the user read each
/// transcript, edit its metadata, save it to the vault, or delete it. Loads GET
/// /ingest/recordings once on appear (manual Refresh re-fetches); a card expands
/// to lazily GET the transcript text. Same surface as transcripts.html, minus the
/// in-page audio playback and the desktop "Copy agent info" tool, which are
/// intentionally omitted.
/// </summary>
public partial class TranscriptsPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    // Badge colors (match transcripts.html: green "transcribed", amber other state).
    private static readonly Color GreenBg = Color.FromArgb("#16331F");
    private static readonly Color GreenStroke = Color.FromArgb("#2C6B45");
    private static readonly Color GreenFg = Color.FromArgb("#5FD08A");
    private static readonly Color AmberBg = Color.FromArgb("#33280E");
    private static readonly Color AmberStroke = Color.FromArgb("#6B5418");
    private static readonly Color AmberFg = Color.FromArgb("#E8B339");

    private readonly ObservableCollection<RecordingRow> _recordings = new();

    private bool _loaded;
    private bool _loading;

    public ObservableCollection<RecordingRow> Recordings => _recordings;

    public TranscriptsPage()
    {
        InitializeComponent();
        ListPanel.BindingContext = this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
            _ = LoadAsync();
    }

    // ===== load =============================================================

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        LoadingLabel.IsVisible = !_loaded;
        StatusLabel.Text = "Loading...";
        ErrorCard.IsVisible = false;
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            var items = await gateway.GetRecordingsAsync();
            Render(items);
            _loaded = true;
            LoadingLabel.IsVisible = false;
            StatusLabel.Text = "";
        }
        catch (Exception ex)
        {
            LoadingLabel.IsVisible = false;
            StatusLabel.Text = "";
            ErrorLabel.Text = "Failed to load: " + ex.Message;
            ErrorCard.IsVisible = true;
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void Render(List<RecordingItem> items)
    {
        _recordings.Clear();
        foreach (var item in items)
            _recordings.Add(BuildRow(item));
        EmptyLabel.IsVisible = items.Count == 0;
    }

    private RecordingRow BuildRow(RecordingItem item)
    {
        var row = new RecordingRow
        {
            RecordingId = item.RecordingId,
            Title = item.Title,
            Subtitle = item.Subtitle,
            Summary = item.Summary,
            State = item.State,
            HasTranscript = item.HasTranscript,
            InVault = item.InVault,
            MetaLine = BuildMetaLine(item),
            EditTitle = item.Title,
            EditSubtitle = item.Subtitle,
            EditSummary = item.Summary,
        };
        ApplyBadge(row);
        ApplyVaultState(row);
        return row;
    }

    // ===== card expand / lazy transcript load ==============================

    private async void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not RecordingRow row) return;
        if (row.Expanded)
        {
            row.Expanded = false;
            return;
        }
        row.Expanded = true;
        if (row.TranscriptLoaded) return;

        if (!row.HasTranscript)
        {
            // No transcript text stored. Match the web's wording.
            var done = IsDone(row.State);
            row.TranscriptText = done
                ? "(no transcript text stored)"
                : $"Not transcribed yet (state: {row.State}).";
            row.TranscriptLoaded = true;
            return;
        }

        row.TranscriptText = "Loading transcript...";
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            var text = await gateway.GetTranscriptTextAsync(row.RecordingId);
            row.TranscriptText = string.IsNullOrEmpty(text) ? "(no transcript text stored)" : text;
            row.TranscriptLoaded = true;
        }
        catch (Exception ex)
        {
            // Surface the real failure rather than a generic placeholder; allow a
            // retry by leaving TranscriptLoaded false so the next expand re-fetches.
            row.TranscriptText = "Failed to load transcript: " + ex.Message;
        }
    }

    // ===== save details (meta) ==============================================

    private async void OnSaveDetailsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not RecordingRow row) return;
        b.IsEnabled = false;
        row.SaveMessage = "saving...";
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            var updated = await gateway.UpdateRecordingMetaAsync(
                row.RecordingId,
                row.EditTitle ?? "",
                row.EditSubtitle ?? "",
                row.EditSummary ?? "");
            row.Title = updated.Title;
            row.Subtitle = updated.Subtitle;
            row.Summary = updated.Summary;
            row.EditTitle = updated.Title;
            row.EditSubtitle = updated.Subtitle;
            row.EditSummary = updated.Summary;
            row.SaveMessage = "saved";
        }
        catch (Exception ex)
        {
            row.SaveMessage = "";
            await DisplayAlert("Save failed", ex.Message, "OK");
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    // ===== save to vault (promote) ==========================================

    private async void OnSaveToVaultClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not RecordingRow row) return;
        if (!row.CanPromote) return;
        b.IsEnabled = false;
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            await gateway.PromoteRecordingAsync(row.RecordingId);
            row.InVault = true;
            ApplyVaultState(row);
        }
        catch (Exception ex)
        {
            b.IsEnabled = true;
            await DisplayAlert("Save to vault failed", ex.Message, "OK");
        }
    }

    // ===== delete ===========================================================

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not RecordingRow row) return;
        var vaultNote = row.InVault
            ? "\n\nThis recording is saved in the vault; that copy is kept."
            : "";
        var ok = await DisplayAlert(
            $"Delete \"{row.Title}\"?",
            $"This removes the local transcript and its audio.{vaultNote}\n\nThis cannot be undone.",
            "Delete", "Cancel");
        if (!ok) return;
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            await gateway.DeleteRecordingAsync(row.RecordingId);
            _recordings.Remove(row);
            EmptyLabel.IsVisible = _recordings.Count == 0;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Delete failed", ex.Message, "OK");
        }
    }

    // ===== burger menu ======================================================

    // Top-right burger menu: switch between Talk, Recorder, Exes, Dictionary and Transcripts.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet(
            "Go to", "Cancel", null, "Talk", "Recorder", "Exes", "Dictionary", "Transcripts");
        if (choice == "Talk")
            await Shell.Current.GoToAsync("//TalkPage");
        else if (choice == "Recorder")
            await Shell.Current.GoToAsync("//MainPage");
        else if (choice == "Exes")
            await Shell.Current.GoToAsync("//ExesPage");
        else if (choice == "Dictionary")
            await Shell.Current.GoToAsync("//DictionaryPage");
        else if (choice == "Transcripts")
            await Shell.Current.GoToAsync("//TranscriptsPage");
    }

    // ===== formatting (ported from transcripts.html) =======================

    private static bool IsDone(string state)
        => string.Equals(state, "transcribed", StringComparison.Ordinal)
           || string.Equals(state, "filed", StringComparison.Ordinal);

    private static string BuildMetaLine(RecordingItem item)
        => $"{FormatDate(item.StartedAt)} - {FormatDuration(item.DurationMs)} - {item.Segments} segment{(item.Segments == 1 ? "" : "s")}";

    private static string FormatDate(string iso)
    {
        if (string.IsNullOrEmpty(iso)) return "-";
        return DateTimeOffset.TryParse(iso, out var dt)
            ? dt.LocalDateTime.ToString("g")
            : iso;
    }

    private static string FormatDuration(long ms)
    {
        var totalSec = (int)Math.Round(ms / 1000.0);
        var m = totalSec / 60;
        var s = totalSec % 60;
        return $"{m:00}:{s:00}";
    }

    // Set the state badge (green "transcribed" when done, else the raw state in amber).
    private static void ApplyBadge(RecordingRow row)
    {
        if (IsDone(row.State))
        {
            row.StateBadgeText = "transcribed";
            row.StateBadgeBg = GreenBg;
            row.StateBadgeStroke = GreenStroke;
            row.StateBadgeFg = GreenFg;
        }
        else
        {
            row.StateBadgeText = string.IsNullOrEmpty(row.State) ? "?" : row.State;
            row.StateBadgeBg = AmberBg;
            row.StateBadgeStroke = AmberStroke;
            row.StateBadgeFg = AmberFg;
        }
    }

    // Set the "Save to vault" button state: "In vault" + disabled when already
    // promoted, disabled until transcribed, otherwise an enabled "Save to vault".
    private static void ApplyVaultState(RecordingRow row)
    {
        if (row.InVault)
        {
            row.VaultButtonText = "In vault";
            row.CanPromote = false;
        }
        else if (!IsDone(row.State))
        {
            row.VaultButtonText = "Save to vault";
            row.CanPromote = false;
        }
        else
        {
            row.VaultButtonText = "Save to vault";
            row.CanPromote = true;
        }
    }

    // ===== row view-model ===================================================

    /// <summary>
    /// One recording card. Implements INotifyPropertyChanged because a card's
    /// state changes live after the initial render (expand, lazily loaded
    /// transcript, save-detail message, badge + vault button after promotion).
    /// </summary>
    public sealed class RecordingRow : INotifyPropertyChanged
    {
        public string RecordingId { get; init; } = "";
        public bool HasTranscript { get; init; }
        public bool TranscriptLoaded { get; set; }

        private string _title = "";
        public string Title { get => _title; set => Set(ref _title, value); }

        private string _subtitle = "";
        public string Subtitle
        {
            get => _subtitle;
            set { if (Set(ref _subtitle, value)) Raise(nameof(HasSubtitle)); }
        }

        public bool HasSubtitle => !string.IsNullOrEmpty(_subtitle);

        // Summary is shown only in the editable field; kept for round-tripping.
        public string Summary { get; set; } = "";

        public string MetaLine { get; init; } = "";
        public string State { get; init; } = "";

        private bool _expanded;
        public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

        private string _transcriptText = "";
        public string TranscriptText { get => _transcriptText; set => Set(ref _transcriptText, value); }

        private string _editTitle = "";
        public string EditTitle { get => _editTitle; set => Set(ref _editTitle, value); }

        private string _editSubtitle = "";
        public string EditSubtitle { get => _editSubtitle; set => Set(ref _editSubtitle, value); }

        private string _editSummary = "";
        public string EditSummary { get => _editSummary; set => Set(ref _editSummary, value); }

        private string _saveMessage = "";
        public string SaveMessage { get => _saveMessage; set => Set(ref _saveMessage, value); }

        private bool _inVault;
        public bool InVault { get => _inVault; set => Set(ref _inVault, value); }

        private string _vaultButtonText = "Save to vault";
        public string VaultButtonText { get => _vaultButtonText; set => Set(ref _vaultButtonText, value); }

        private bool _canPromote;
        public bool CanPromote { get => _canPromote; set => Set(ref _canPromote, value); }

        private string _stateBadgeText = "";
        public string StateBadgeText { get => _stateBadgeText; set => Set(ref _stateBadgeText, value); }

        private Color _stateBadgeBg = AmberBg;
        public Color StateBadgeBg { get => _stateBadgeBg; set => Set(ref _stateBadgeBg, value); }

        private Color _stateBadgeStroke = AmberStroke;
        public Color StateBadgeStroke { get => _stateBadgeStroke; set => Set(ref _stateBadgeStroke, value); }

        private Color _stateBadgeFg = AmberFg;
        public Color StateBadgeFg { get => _stateBadgeFg; set => Set(ref _stateBadgeFg, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string? name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
    }
}
