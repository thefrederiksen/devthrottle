using System.Collections.ObjectModel;
using CcDirectorClient.Voice;

namespace CcDirectorClient;

/// <summary>
/// The phone port of the Gateway's Exes page. Lists the Director executables
/// physically running on the Gateway's computer (with their sessions nested) and
/// the local build slots 1-4, and lets the user kill a Director, build and start a
/// slot, or delete a slot's built exe. Polls GET /exes/list about every three
/// seconds while the page is in front; the poll is suspended while a build runs so
/// it never refreshes over an in-flight build. Same surface as exes.html.
/// </summary>
public partial class ExesPage : ContentPage
{
    private const string PrefServer = "gateway_url";
    private const string PrefToken = "gateway_token";

    private static readonly Color DotGreen = Color.FromArgb("#5FD08A");
    private static readonly Color DotBlue = Color.FromArgb("#2B6CB0");
    private static readonly Color DotYellow = Color.FromArgb("#E8B339");
    private static readonly Color DotRed = Color.FromArgb("#E5484D");
    private static readonly Color DotGray = Color.FromArgb("#5A6378");

    private static readonly Color BadgeBlue = Color.FromArgb("#2B6CB0");
    private static readonly Color StatusGreen = Color.FromArgb("#5FD08A");
    private static readonly Color StatusBlueColor = Color.FromArgb("#2B6CB0");
    private static readonly Color StatusMuted = Color.FromArgb("#5A6378");

    private static readonly Color NameNormal = Color.FromArgb("#E6EAF2");
    private static readonly Color NameMuted = Color.FromArgb("#8A93A6");

    private readonly ObservableCollection<DirectorRow> _directors = new();
    private readonly ObservableCollection<SlotRow> _slots = new();

    // ~3s poll while the page is visible (started in OnAppearing, stopped in
    // OnDisappearing). Never polls over an in-flight build (_buildBusy).
    private readonly IDispatcherTimer _pollTimer;
    private bool _loading;
    private bool _buildBusy;

    public ObservableCollection<DirectorRow> Directors => _directors;
    public ObservableCollection<SlotRow> Slots => _slots;

    public ExesPage()
    {
        InitializeComponent();
        DirectorsPanel.BindingContext = this;
        SlotsPanel.BindingContext = this;

        _pollTimer = Dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(3);
        _pollTimer.Tick += async (_, _) => await LoadAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
        _pollTimer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _pollTimer.Stop();
    }

    // ===== load + render ====================================================

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        // Never refresh while a build is running (the slot button is mid-flight) or
        // while a previous load is still in progress.
        if (_buildBusy || _loading) return;
        try
        {
            _loading = true;
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            var data = await gateway.GetExesAsync();
            Render(data);
            ErrorCard.IsVisible = false;
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Failed to load: " + ex.Message;
            ErrorCard.IsVisible = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render(ExesData data)
    {
        StatsLabel.Text =
            $"{data.Directors.Count} director{(data.Directors.Count == 1 ? "" : "s")} on {data.MachineName}";

        var hasRepo = !string.IsNullOrEmpty(data.RepoRoot);
        RepoNoticeCard.IsVisible = !hasRepo;

        NoDirectorsLabel.IsVisible = data.Directors.Count == 0;
        _directors.Clear();
        foreach (var d in data.Directors)
            _directors.Add(BuildDirectorRow(d));

        SlotsUnavailableLabel.IsVisible = !hasRepo;
        SlotsPanel.IsVisible = hasRepo;
        _slots.Clear();
        if (hasRepo)
            foreach (var s in data.Slots)
                _slots.Add(BuildSlotRow(s));
    }

    private static DirectorRow BuildDirectorRow(ExesDirector d)
    {
        var slotLabel = d.Slot.HasValue ? $"slot {d.Slot.Value}" : "no slot";
        var meta = $"PID {d.Pid} - port {PortOf(d.ControlEndpoint)} - v{(string.IsNullOrEmpty(d.Version) ? "?" : d.Version)} - up {RelativeTime(d.StartedAt)}";

        var sessions = new List<SessionRow>();
        var hasError = !string.IsNullOrEmpty(d.SessionError);
        string notice;
        bool showNotice;
        if (hasError)
        {
            notice = "sessions unavailable: " + d.SessionError;
            showNotice = true;
        }
        else if (d.Sessions.Count == 0)
        {
            notice = "No sessions.";
            showNotice = true;
        }
        else
        {
            notice = "";
            showNotice = false;
            foreach (var s in d.Sessions)
                sessions.Add(BuildSessionRow(s));
        }

        return new DirectorRow
        {
            DirectorId = d.DirectorId,
            Pid = d.Pid,
            Slot = d.Slot ?? 0,
            SlotLabel = slotLabel,
            SlotBadgeColor = d.Slot.HasValue ? BadgeBlue : DotGray,
            Meta = meta,
            ExePath = d.ExePath,
            HasExePath = !string.IsNullOrEmpty(d.ExePath),
            SessionsNotice = notice,
            ShowSessionsNotice = showNotice,
            Sessions = sessions,
        };
    }

    private static SessionRow BuildSessionRow(ExesSession s)
    {
        var named = !string.IsNullOrEmpty(s.Name);
        return new SessionRow
        {
            Name = named ? s.Name : "(unnamed)",
            NameColor = named ? NameNormal : NameMuted,
            NameAttributes = named ? FontAttributes.Bold : FontAttributes.Italic,
            Agent = string.IsNullOrEmpty(s.Agent) ? "?" : s.Agent,
            StateLine = $"{RepoBasename(s.RepoPath)} - {HumanizeState(s.ActivityState)}",
            Dot = DotFor(s.StatusColor),
        };
    }

    private SlotRow BuildSlotRow(ExesSlot s)
    {
        var running = s.Running is not null;
        string status;
        Color statusColor;
        if (s.Running is { } run)
        {
            status = $"running (PID {run.Pid})";
            statusColor = StatusBlueColor;
        }
        else if (s.Exists)
        {
            status = "built, stopped";
            statusColor = StatusGreen;
        }
        else
        {
            status = "not built";
            statusColor = StatusMuted;
        }

        var sub = s.Exists
            ? $"{FormatSize(s.SizeBytes)} - built {RelativeTime(s.LastBuiltUtc)} ago"
            : "no exe in local_builds";

        return new SlotRow
        {
            Slot = s.Slot,
            Title = $"Slot {s.Slot}",
            Status = status,
            StatusColor = statusColor,
            Sub = sub,
            BuildButtonText = "Build & start",
            CanBuild = !running && !_buildBusy,
            CanDelete = s.Exists && !running,
        };
    }

    // ===== actions ==========================================================

    private async void OnKillDirectorClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not DirectorRow row) return;
        var label = row.Slot > 0 ? $"slot {row.Slot}" : $"PID {row.Pid}";
        var ok = await DisplayAlert(
            $"Kill Director {label}?",
            $"This terminates the process (PID {row.Pid}) and ALL of its running sessions. Unsaved work in those sessions will be lost.",
            "Kill", "Cancel");
        if (!ok) return;
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            await gateway.KillDirectorAsync(row.DirectorId);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Kill failed", ex.Message, "OK");
        }
        finally
        {
            await LoadAsync();
        }
    }

    private async void OnDeleteSlotClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not SlotRow row) return;
        var ok = await DisplayAlert(
            $"Delete the slot {row.Slot} build?",
            $"This removes local_builds/cc-director-avalonia{row.Slot}.exe from disk. You can rebuild it with Build & start.",
            "Delete", "Cancel");
        if (!ok) return;
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            await gateway.DeleteSlotAsync(row.Slot);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Delete failed", ex.Message, "OK");
        }
        finally
        {
            await LoadAsync();
        }
    }

    private async void OnBuildStartClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not SlotRow row) return;
        var ok = await DisplayAlert(
            $"Build slot {row.Slot} and launch it?",
            $"This runs the build script (about a minute) and then starts cc-director-avalonia{row.Slot}.exe. The slot must not already be running.",
            "Build & start", "Cancel");
        if (!ok) return;

        // Block the poll so it never refreshes over the in-flight build, and show
        // the button as busy. The poll resumes in the finally.
        _buildBusy = true;
        button.IsEnabled = false;
        button.Text = "Building...";
        try
        {
            var gateway = new GatewayClient(
                Preferences.Get(PrefServer, ""), Preferences.Get(PrefToken, ""));
            var pid = await gateway.BuildStartSlotAsync(row.Slot);
            await DisplayAlert("Build complete",
                $"Slot {row.Slot} built and started (PID {pid}).", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Build & start failed", ex.Message, "OK");
        }
        finally
        {
            _buildBusy = false;
            button.Text = "Build & start";
            await LoadAsync();
        }
    }

    // Top-right burger menu: switch between the Talk, Recorder, Exes, Dictionary and Transcripts pages.
    private async void OnNavMenuClicked(object? sender, TappedEventArgs e)
    {
        var choice = await DisplayActionSheet("Go to", "Cancel", null, "Sessions", "Notes", "Exes", "Dictionary", "Transcripts");
        if (choice == "Sessions")
            await Shell.Current.GoToAsync("//TalkPage");
        else if (choice == "Notes")
            await Shell.Current.GoToAsync("//MainPage");
        else if (choice == "Exes")
            await Shell.Current.GoToAsync("//ExesPage");
        else if (choice == "Dictionary")
            await Shell.Current.GoToAsync("//DictionaryPage");
        else if (choice == "Transcripts")
            await Shell.Current.GoToAsync("//TranscriptsPage");
    }

    // ===== formatting (ported from exes.html) ==============================

    private static Color DotFor(string statusColor) => statusColor?.ToLowerInvariant() switch
    {
        "green" => DotGreen,
        "blue" => DotBlue,
        "yellow" => DotYellow,
        "red" => DotRed,
        _ => DotGray,
    };

    private static string PortOf(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return "?";
        var m = System.Text.RegularExpressions.Regex.Match(endpoint, @":(\d+)/?$");
        return m.Success ? m.Groups[1].Value : "?";
    }

    private static string RepoBasename(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(no repo)";
        var norm = path.Replace('\\', '/').TrimEnd('/');
        var i = norm.LastIndexOf('/');
        return i >= 0 ? norm.Substring(i + 1) : norm;
    }

    private static string HumanizeState(string state) => state switch
    {
        "WaitingForInput" => "Waiting for input",
        "WaitingForPerm" => "Waiting for permission",
        "Idle" => "Idle",
        "Working" => "Working",
        "Starting" => "Starting",
        "Exited" => "Exited",
        _ => string.IsNullOrEmpty(state) ? "-" : state,
    };

    private static string RelativeTime(DateTime? when)
    {
        if (when is null) return "-";
        var utc = when.Value.Kind == DateTimeKind.Utc ? when.Value : when.Value.ToUniversalTime();
        var sec = (long)Math.Max(0, (DateTime.UtcNow - utc).TotalSeconds);
        if (sec < 60) return sec + "s";
        var m = sec / 60;
        if (m < 60) return m + "m";
        var h = m / 60;
        if (h < 24) return h + "h " + (m % 60) + "m";
        return (h / 24) + "d";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        var mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1) return mb.ToString("0.0") + " MB";
        return (bytes / 1024) + " KB";
    }

    // ===== row view-models ==================================================

    public sealed class DirectorRow
    {
        public string DirectorId { get; init; } = "";
        public int Pid { get; init; }
        public int Slot { get; init; }
        public string SlotLabel { get; init; } = "";
        public Color SlotBadgeColor { get; init; } = DotGray;
        public string Meta { get; init; } = "";
        public string ExePath { get; init; } = "";
        public bool HasExePath { get; init; }
        public string SessionsNotice { get; init; } = "";
        public bool ShowSessionsNotice { get; init; }
        public List<SessionRow> Sessions { get; init; } = new();
    }

    public sealed class SessionRow
    {
        public string Name { get; init; } = "";
        public Color NameColor { get; init; } = NameNormal;
        public FontAttributes NameAttributes { get; init; } = FontAttributes.Bold;
        public string Agent { get; init; } = "";
        public string StateLine { get; init; } = "";
        public Color Dot { get; init; } = DotGray;
    }

    public sealed class SlotRow
    {
        public int Slot { get; init; }
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
        public Color StatusColor { get; init; } = StatusMuted;
        public string Sub { get; init; } = "";
        public string BuildButtonText { get; init; } = "Build & start";
        public bool CanBuild { get; init; }
        public bool CanDelete { get; init; }
    }
}
