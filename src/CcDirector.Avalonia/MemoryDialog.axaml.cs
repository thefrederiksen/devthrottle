using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

public partial class MemoryDialog : Window
{
    private readonly string _userClaudeMdPath;
    private readonly string? _projectClaudeMdPath;
    private readonly string? _autoMemoryPath;

    public MemoryDialog()
    {
        InitializeComponent();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _userClaudeMdPath = Path.Combine(home, ".claude", "CLAUDE.md");
    }

    public MemoryDialog(string? repoPath = null)
    {
        FileLog.Write($"[MemoryDialog] Constructor: repoPath={repoPath ?? "(null)"}");
        InitializeComponent();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _userClaudeMdPath = Path.Combine(home, ".claude", "CLAUDE.md");

        if (!string.IsNullOrEmpty(repoPath))
        {
            _projectClaudeMdPath = Path.Combine(repoPath, "CLAUDE.md");

            // Build project key: replace each path separator and colon with a dash individually
            // E.g., C:\Projects\my-app -> C--Projects-my-app
            var projectKey = repoPath.Replace('\\', '-').Replace('/', '-').Replace(':', '-');
            _autoMemoryPath = Path.Combine(home, ".claude", "projects", projectKey, "memory", "MEMORY.md");
        }

        Loaded += async (_, _) =>
        {
            await LoadDataAsync();
        };
    }

    private async Task LoadDataAsync()
    {
        FileLog.Write("[MemoryDialog] LoadDataAsync: reading memory files");

        var userContent = await Task.Run(() => ReadFileContent(_userClaudeMdPath));
        var projectContent = _projectClaudeMdPath != null
            ? await Task.Run(() => ReadFileContent(_projectClaudeMdPath))
            : null;
        var autoMemoryContent = _autoMemoryPath != null
            ? await Task.Run(() => ReadFileContent(_autoMemoryPath))
            : null;

        UserPathText.Text = _userClaudeMdPath;
        UserMemoryText.Text = userContent ?? "";

        if (_projectClaudeMdPath != null)
        {
            ProjectPathText.Text = _projectClaudeMdPath;
            ProjectMemoryText.Text = projectContent ?? "";
        }
        else
        {
            ProjectPathText.Text = "(no project selected)";
            ProjectMemoryText.IsEnabled = false;
        }

        if (_autoMemoryPath != null)
        {
            AutoMemoryPathText.Text = _autoMemoryPath;
            AutoMemoryText.Text = autoMemoryContent ?? "";
        }
        else
        {
            AutoMemoryPathText.Text = "(no project selected)";
            AutoMemoryText.IsEnabled = false;
        }

        FileLog.Write("[MemoryDialog] LoadDataAsync: complete");
    }

    private static string? ReadFileContent(string path)
    {
        FileLog.Write($"[MemoryDialog] ReadFileContent: path={path}");
        if (!File.Exists(path))
        {
            FileLog.Write("[MemoryDialog] ReadFileContent: file not found");
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MemoryDialog] ReadFileContent FAILED: {ex.Message}");
            return null;
        }
    }

    private void SaveFile(string path, string content, string label)
    {
        FileLog.Write($"[MemoryDialog] SaveFile: path={path}, label={label}");

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, content);
            SaveStatusText.Text = $"{label} saved";
            FileLog.Write($"[MemoryDialog] SaveFile: {label} saved successfully");
        }
        catch (Exception ex)
        {
            SaveStatusText.Text = ShownError.Report("memory", $"save {label}", $"Save failed: {ex.Message}", ex);
        }
    }

    private void BtnSaveUser_Click(object? sender, RoutedEventArgs e)
    {
        SaveFile(_userClaudeMdPath, UserMemoryText.Text ?? "", "User CLAUDE.md");
    }

    private void BtnSaveProject_Click(object? sender, RoutedEventArgs e)
    {
        if (_projectClaudeMdPath == null) return;
        SaveFile(_projectClaudeMdPath, ProjectMemoryText.Text ?? "", "Project CLAUDE.md");
    }

    private void BtnSaveAutoMemory_Click(object? sender, RoutedEventArgs e)
    {
        if (_autoMemoryPath == null) return;
        SaveFile(_autoMemoryPath, AutoMemoryText.Text ?? "", "Auto-Memory");
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[MemoryDialog] BtnClose_Click: closing dialog");
        Close();
    }
}
