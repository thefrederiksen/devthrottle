using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

public partial class GitHubRepoPickerDialog : Window
{
    private List<RepoItem> _allRepos = [];

    public string? SelectedUrl { get; private set; }

    public GitHubRepoPickerDialog()
    {
        FileLog.Write("[GitHubRepoPickerDialog] Constructor: initializing");
        InitializeComponent();
        Loaded += async (_, _) => await LoadReposAsync();
        RepoList.SelectionChanged += (_, _) =>
            BtnSelect.IsEnabled = RepoList.SelectedItem is not null;
        FileLog.Write("[GitHubRepoPickerDialog] Constructor: complete");
    }

    private async Task LoadReposAsync()
    {
        FileLog.Write("[GitHubRepoPickerDialog] LoadReposAsync: starting");
        StatusText.Text = "Loading repositories...";
        FilterBox.IsEnabled = false;

        try
        {
            var json = await RunGhAsync(
                "repo list --limit 100 --json name,url,description,isPrivate,updatedAt");

            if (json is null)
                return; // error already shown

            var repos = JsonSerializer.Deserialize<List<GhRepo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (repos is null || repos.Count == 0)
            {
                StatusText.Text = "No repositories found.";
                FileLog.Write("[GitHubRepoPickerDialog] LoadReposAsync: no repositories found");
                return;
            }

            _allRepos = repos
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new RepoItem(r))
                .ToList();
            RepoList.ItemsSource = _allRepos;
            StatusText.Text = $"{_allRepos.Count} repositories";
            FilterBox.IsEnabled = true;
            FilterBox.Focus();
            FileLog.Write($"[GitHubRepoPickerDialog] LoadReposAsync: loaded {_allRepos.Count} repositories");
        }
        catch (JsonException ex)
        {
            StatusText.Text = ShownError.Report("GitHub repository picker", "read the repository list from gh", "Failed to parse response from gh CLI.", ex);
        }
    }

    private async Task<string?> RunGhAsync(string arguments)
    {
        FileLog.Write($"[GitHubRepoPickerDialog] RunGhAsync: {arguments}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                StatusText.Text = ShownError.Report("GitHub repository picker", "start the gh command line", "Failed to start gh CLI.");
                return null;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                StatusText.Text = ShownError.Report("GitHub repository picker", "list the repositories with gh",
                    error.Contains("auth login")
                        ? "gh CLI is not authenticated. Run 'gh auth login' first."
                        : $"gh error: {error.Trim().Split('\n').FirstOrDefault()}",
                    reported: $"gh exited with code {proc.ExitCode}: {error.Trim().Split('\n').FirstOrDefault()}");
                return null;
            }

            FileLog.Write("[GitHubRepoPickerDialog] RunGhAsync: success");
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "gh CLI not found. Install it from https://cli.github.com";
            // not-an-error: the gh command line is optional, and the dialog tells the person how to install it
            FileLog.Write("[GitHubRepoPickerDialog] RunGhAsync: gh CLI not found");
            return null;
        }
    }

    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filter))
        {
            RepoList.ItemsSource = _allRepos;
            StatusText.Text = $"{_allRepos.Count} repositories";
        }
        else
        {
            var filtered = _allRepos
                .Where(r => r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || (r.Description ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            RepoList.ItemsSource = filtered;
            StatusText.Text = $"{filtered.Count} of {_allRepos.Count} repositories";
        }
    }

    private void RepoList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RepoList.SelectedItem is RepoItem repo)
            AcceptSelection(repo);
    }

    private void BtnSelect_Click(object? sender, RoutedEventArgs e)
    {
        if (RepoList.SelectedItem is RepoItem repo)
            AcceptSelection(repo);
    }

    private void AcceptSelection(RepoItem repo)
    {
        FileLog.Write($"[GitHubRepoPickerDialog] AcceptSelection: {repo.Url}");
        SelectedUrl = repo.Url;
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[GitHubRepoPickerDialog] BtnCancel_Click");
        Close(false);
    }

    // --- Data models ---

    internal sealed class GhRepo
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Description { get; set; }
        public bool IsPrivate { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    internal sealed class RepoItem
    {
        private static readonly ISolidColorBrush PrivateBg = new SolidColorBrush(Color.FromRgb(0x6E, 0x40, 0x00));
        private static readonly ISolidColorBrush PrivateFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x3D));
        private static readonly ISolidColorBrush PublicBg = new SolidColorBrush(Color.FromRgb(0x1B, 0x4B, 0x2A));
        private static readonly ISolidColorBrush PublicFg = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));

        public string Name { get; }
        public string Url { get; }
        public string? Description { get; }
        public string BadgeText { get; }
        public ISolidColorBrush BadgeBackground { get; }
        public ISolidColorBrush BadgeForeground { get; }
        public bool HasDescription { get; }

        public RepoItem(GhRepo repo)
        {
            Name = repo.Name;
            Url = repo.Url;
            Description = repo.Description;
            BadgeText = repo.IsPrivate ? "private" : "public";
            BadgeBackground = repo.IsPrivate ? PrivateBg : PublicBg;
            BadgeForeground = repo.IsPrivate ? PrivateFg : PublicFg;
            HasDescription = !string.IsNullOrWhiteSpace(repo.Description);
        }
    }
}
