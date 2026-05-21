using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public partial class PdfViewerControl : UserControl, IFileViewer
{
    private string? _filePath;

    public string? FilePath => _filePath;
    public bool IsDirty => false;
    public event Action? DisplayNameChanged;

    public PdfViewerControl()
    {
        InitializeComponent();
    }

    public Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[PdfViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        FilePathText.Text = filePath;
        ToolTip.SetTip(FilePathText, filePath);
        LoadingText.IsVisible = true;

        var uri = new Uri(Path.GetFullPath(filePath));
        WebViewHost.Url = uri;

        LoadingText.IsVisible = false;
        FileLog.Write($"[PdfViewer] Navigated to: {uri.AbsoluteUri}");
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
        return _filePath != null ? Path.GetFileName(_filePath) : "Untitled.pdf";
    }

    private void OpenExternalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            FileLog.Write($"[PdfViewer] Opened with default app: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PdfViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }
}
