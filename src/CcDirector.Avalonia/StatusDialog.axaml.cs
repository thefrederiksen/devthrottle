using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

public partial class StatusDialog : Window
{
    public StatusDialog()
    {
        FileLog.Write("[StatusDialog] Constructor: initializing");
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            await LoadDataAsync();
        };
    }

    private async Task LoadDataAsync()
    {
        FileLog.Write("[StatusDialog] LoadDataAsync: reading status data");

        var versionTask = Task.Run(GetClaudeVersion);
        var configTask = Task.Run(ReadClaudeJson);

        var version = await versionTask;
        var config = await configTask;

        VersionText.Text = version;

        if (config != null)
        {
            var account = config["oauthAccount"];
            AccountNameText.Text = account?["name"]?.GetValue<string>() ?? "(not set)";
            EmailText.Text = account?["emailAddress"]?.GetValue<string>() ?? "(not set)";
            InstallMethodText.Text = config["installMethod"]?.GetValue<string>() ?? "(not set)";

            var startups = config["numStartups"];
            StartupsText.Text = startups != null ? startups.ToString() : "(not set)";
        }
        else
        {
            // Four fields, one failure: reported once, as the first of them is shown.
            var unreadable = ShownError.Report("Claude status", "read the Claude configuration", "(unable to read)",
                reported: "the Claude configuration file could not be read, so the account fields show (unable to read)");
            AccountNameText.Text = unreadable;
            EmailText.Text = unreadable;
            InstallMethodText.Text = unreadable;
            StartupsText.Text = unreadable;
        }

        LoadingText.IsVisible = false;
        StatusGrid.IsVisible = true;

        FileLog.Write($"[StatusDialog] LoadDataAsync: version={version}");
    }

    private static string GetClaudeVersion()
    {
        FileLog.Write("[StatusDialog] GetClaudeVersion: running claude --version");
        try
        {
            var psi = new ProcessStartInfo("claude", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd()?.Trim() ?? "Unknown";
            proc?.WaitForExit();
            return output;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StatusDialog] GetClaudeVersion FAILED: {ex.Message}");
            return "Unable to determine";
        }
    }

    private static JsonNode? ReadClaudeJson()
    {
        FileLog.Write("[StatusDialog] ReadClaudeJson: reading ~/.claude.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".claude.json");

        if (!File.Exists(path))
        {
            FileLog.Write("[StatusDialog] ReadClaudeJson: file not found");
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[StatusDialog] ReadClaudeJson FAILED: {ex.Message}");
            return null;
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[StatusDialog] BtnClose_Click: closing dialog");
        Close();
    }
}
