using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public partial class HtmlViewerControl : UserControl, IFileViewer
{
    private string? _filePath;

    public string? FilePath => _filePath;
    public bool IsDirty => false;
    public event Action? DisplayNameChanged;

    public HtmlViewerControl()
    {
        InitializeComponent();
    }

    public Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[HtmlViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        FilePathText.Text = filePath;
        ToolTip.SetTip(FilePathText, filePath);
        LoadingText.IsVisible = true;

        var url = PathToFileUri(filePath);
        WebViewHost.Url = url;

        LoadingText.IsVisible = false;
        FileLog.Write($"[HtmlViewer] Navigated to: {url.AbsoluteUri}");
        return Task.CompletedTask;
    }

    public void ShowLoadError(string message)
    {
        LoadingText.Text = $"Failed to load: {message}";
        LoadingText.IsVisible = true;
    }

    public Task SaveAsync() => Task.CompletedTask;

    public string GetDisplayName()
    {
        return _filePath != null ? Path.GetFileName(_filePath) : "Untitled.html";
    }

    private static Uri PathToFileUri(string path)
    {
        return new Uri(Path.GetFullPath(path));
    }

    private void ReloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        try
        {
            WebViewHost.Url = PathToFileUri(_filePath);
            FileLog.Write($"[HtmlViewer] Reload: {_filePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HtmlViewer] ReloadButton_Click FAILED: {ex.Message}");
        }
    }

    private void OpenExternalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
            FileLog.Write($"[HtmlViewer] Opened with default app: {_filePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HtmlViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }
}
