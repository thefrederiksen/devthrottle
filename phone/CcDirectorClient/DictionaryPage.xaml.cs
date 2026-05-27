using System.Collections.ObjectModel;
using CcDirectorClient.Voice;

namespace CcDirectorClient;

/// <summary>
/// The phone port of the Gateway's Dictionary page (dictionary.html). Edits the one
/// shared STT dictation glossary used by both phone-recording transcription and
/// desktop voice-to-type: the vocabulary biased into speech-to-text, the known
/// mistranscription corrections (correct term -> wrong spellings), and the cleanup
/// profiles. Loads GET /ingest/dictionary into an in-memory working copy, marks the
/// page dirty on any change, and writes the whole thing back with PUT
/// /ingest/dictionary on Save. Same surface as dictionary.html.
/// </summary>
public partial class DictionaryPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    // The in-memory working copy. Bound row collections feed the BindableLayouts so
    // adds/removes update the screen live; dirty flips when any of them changes.
    private readonly ObservableCollection<VocabRow> _vocabulary = new();
    private readonly ObservableCollection<MistransRow> _mistranscriptions = new();
    private readonly ObservableCollection<ProfileRow> _profiles = new();

    private bool _dirty;
    private bool _loaded;

    public ObservableCollection<VocabRow> Vocabulary => _vocabulary;
    public ObservableCollection<MistransRow> Mistranscriptions => _mistranscriptions;
    public ObservableCollection<ProfileRow> Profiles => _profiles;

    public DictionaryPage()
    {
        InitializeComponent();
        VocabPanel.BindingContext = this;
        MistransPanel.BindingContext = this;
        ProfilesPanel.BindingContext = this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
            _ = LoadAsync();
    }

    // ===== load =============================================================

    private async Task LoadAsync()
    {
        LoadingLabel.IsVisible = true;
        ErrorCard.IsVisible = false;
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            var model = await gateway.GetDictionaryAsync();
            ApplyModel(model);
            _loaded = true;
            ContentPanel.IsVisible = true;
            LoadingLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            LoadingLabel.IsVisible = false;
            ErrorLabel.Text = "Failed to load: " + ex.Message;
            ErrorCard.IsVisible = true;
        }
    }

    // Replace the working copy with a freshly loaded/saved model and reset dirty.
    private void ApplyModel(DictionaryModel model)
    {
        _vocabulary.Clear();
        foreach (var term in model.Vocabulary)
            _vocabulary.Add(new VocabRow { Term = term });

        _mistranscriptions.Clear();
        foreach (var kv in model.CommonMistranscriptions)
        {
            var row = new MistransRow { Term = kv.Key };
            foreach (var v in kv.Value)
                row.Variants.Add(new VariantRow { Value = v, Parent = row });
            _mistranscriptions.Add(row);
        }

        _profiles.Clear();
        foreach (var kv in model.Profiles)
            _profiles.Add(new ProfileRow { Name = kv.Key, CleanupEnabled = kv.Value });

        SetDirty(false);
        RefreshEmptyStates();
    }

    // Build the model to send to the server from the current working copy.
    private DictionaryModel ToModel()
    {
        var model = new DictionaryModel
        {
            Vocabulary = _vocabulary.Select(v => v.Term).ToList(),
        };
        foreach (var m in _mistranscriptions)
            model.CommonMistranscriptions[m.Term] = m.Variants.Select(v => v.Value).ToList();
        foreach (var p in _profiles)
            model.Profiles[p.Name] = p.CleanupEnabled;
        return model;
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        SaveButton.IsEnabled = dirty;
        if (dirty) SavedLabel.Text = "";
    }

    private void RefreshEmptyStates()
    {
        VocabEmptyLabel.IsVisible = _vocabulary.Count == 0;
        MistransEmptyLabel.IsVisible = _mistranscriptions.Count == 0;
        ProfilesEmptyLabel.IsVisible = _profiles.Count == 0;
    }

    // ===== save =============================================================

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!_dirty) return;
        SaveButton.IsEnabled = false;
        SavedLabel.Text = "saving...";
        SavedLabel.TextColor = Color.FromArgb("#8A93A6");
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            var saved = await gateway.SaveDictionaryAsync(ToModel());
            ApplyModel(saved);
            SavedLabel.TextColor = Color.FromArgb("#5FD08A");
            SavedLabel.Text = "Saved";
        }
        catch (Exception ex)
        {
            SavedLabel.Text = "";
            SaveButton.IsEnabled = true;
            await DisplayAlert("Save failed", ex.Message, "OK");
        }
    }

    // ===== vocabulary =======================================================

    private void OnAddVocabClicked(object? sender, EventArgs e)
    {
        var term = (VocabAddEntry.Text ?? "").Trim();
        if (string.IsNullOrEmpty(term)) return;
        VocabAddEntry.Text = "";
        if (_vocabulary.Any(v => string.Equals(v.Term, term, StringComparison.Ordinal))) return;
        _vocabulary.Add(new VocabRow { Term = term });
        RefreshEmptyStates();
        SetDirty(true);
    }

    private void OnRemoveVocabClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not VocabRow row) return;
        _vocabulary.Remove(row);
        RefreshEmptyStates();
        SetDirty(true);
    }

    // ===== common mistranscriptions =========================================

    private void OnAddTermClicked(object? sender, EventArgs e)
    {
        var term = (TermAddEntry.Text ?? "").Trim();
        if (string.IsNullOrEmpty(term)) return;
        TermAddEntry.Text = "";
        if (_mistranscriptions.Any(m => string.Equals(m.Term, term, StringComparison.Ordinal))) return;
        _mistranscriptions.Add(new MistransRow { Term = term });
        RefreshEmptyStates();
        SetDirty(true);
    }

    private void OnRemoveTermClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not MistransRow row) return;
        _mistranscriptions.Remove(row);
        RefreshEmptyStates();
        SetDirty(true);
    }

    private void OnAddVariantClicked(object? sender, EventArgs e)
    {
        // The Add button carries the row as its CommandParameter; the Entry's
        // Completed event carries it via the row bound as its BindingContext.
        MistransRow? row = sender switch
        {
            Button b => b.CommandParameter as MistransRow,
            Entry entry => entry.BindingContext as MistransRow,
            _ => null,
        };
        if (row is null) return;
        var variant = (row.NewVariant ?? "").Trim();
        if (string.IsNullOrEmpty(variant)) return;
        row.NewVariant = "";
        if (row.Variants.Any(v => string.Equals(v.Value, variant, StringComparison.Ordinal))) return;
        row.Variants.Add(new VariantRow { Value = variant, Parent = row });
        SetDirty(true);
    }

    private void OnRemoveVariantClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not VariantRow row) return;
        row.Parent?.Variants.Remove(row);
        SetDirty(true);
    }

    // ===== profiles =========================================================

    private void OnAddProfileClicked(object? sender, EventArgs e)
    {
        var name = (ProfileAddEntry.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        ProfileAddEntry.Text = "";
        if (_profiles.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal))) return;
        _profiles.Add(new ProfileRow { Name = name, CleanupEnabled = true });
        RefreshEmptyStates();
        SetDirty(true);
    }

    private void OnRemoveProfileClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not ProfileRow row) return;
        if (!row.CanRemove) return; // the "default" profile cannot be removed
        _profiles.Remove(row);
        RefreshEmptyStates();
        SetDirty(true);
    }

    private void OnProfileToggled(object? sender, ToggledEventArgs e)
    {
        // The Switch's IsToggled is two-way bound to the row, so the working copy is
        // already updated; just mark the page dirty. Ignore the toggle the framework
        // fires while binding the initial value (before the page is loaded).
        if (_loaded) SetDirty(true);
    }

    // Top-right burger menu: switch between Talk, Recorder, Exes, Dictionary and Transcripts.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Talk", "Recorder", "Exes", "Dictionary", "Transcripts");
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

    // ===== row view-models ==================================================

    public sealed class VocabRow
    {
        public string Term { get; init; } = "";
    }

    public sealed class MistransRow
    {
        public string Term { get; init; } = "";
        public ObservableCollection<VariantRow> Variants { get; } = new();
        // Bound (two-way) to this term's "add wrong spelling" entry.
        public string NewVariant { get; set; } = "";
    }

    public sealed class VariantRow
    {
        public string Value { get; init; } = "";
        public MistransRow? Parent { get; init; }
    }

    public sealed class ProfileRow
    {
        public string Name { get; init; } = "";
        public bool CleanupEnabled { get; set; }
        // The "default" profile cannot be removed (matches dictionary.html).
        public bool CanRemove => !string.Equals(Name, "default", StringComparison.OrdinalIgnoreCase);
    }
}
