using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CcDirector.Avalonia.Helpers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public partial class MarkdownViewerControl : UserControl, IFileViewer
{
    private string? _filePath;
    private string _rawContent = "";
    private bool _isPreviewMode = true;
    private bool _isDirty;
    private bool _suppressTextChanged;

    public string? FilePath => _filePath;
    public bool IsDirty => _isDirty;
    public event Action? DisplayNameChanged;

    public MarkdownViewerControl()
    {
        InitializeComponent();
        UpdateModeButton();
        EditorBox.AddHandler(TextInputEvent, EditorBox_TextInput, RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[MarkdownViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        _isDirty = false;
        FilePathText.Text = filePath;
        ToolTip.SetTip(FilePathText, filePath);
        LoadingText.IsVisible = true;
        PreviewWebView.IsVisible = false;
        EditorBox.IsVisible = false;

        var content = await Task.Run(() => File.ReadAllText(filePath));
        _rawContent = content;

        if (_isPreviewMode)
            RenderPreview();
        else
            ShowEditor();

        LoadingText.IsVisible = false;
        FileLog.Write($"[MarkdownViewer] LoadFileAsync complete: {filePath}, length={content.Length}");
    }

    public void ShowLoadError(string message)
    {
        LoadingText.Text = $"Failed to load: {message}";
        LoadingText.IsVisible = true;
    }

    public async Task SaveAsync()
    {
        if (_filePath == null || !_isDirty)
            return;

        FileLog.Write($"[MarkdownViewer] Save: {_filePath}");
        await Task.Run(() => File.WriteAllText(_filePath, _rawContent));
        _isDirty = false;
        UpdateSaveButton();
        DisplayNameChanged?.Invoke();
        FileLog.Write($"[MarkdownViewer] Save complete: {_filePath}");
    }

    public string GetDisplayName()
    {
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled.md";
        return _isDirty ? $"*{name}" : name;
    }

    private void RenderPreview()
    {
        _isPreviewMode = true;

        if (EditorBox.IsVisible)
            _rawContent = EditorBox.Text ?? "";

        PreviewWebView.HtmlContent = MarkdownHtmlRenderer.Render(_rawContent);
        PreviewWebView.IsVisible = true;
        EditorBox.IsVisible = false;
        UpdateModeButton();
        UpdateSaveButton();
    }

    private void ShowEditor()
    {
        _isPreviewMode = false;
        _suppressTextChanged = true;
        EditorBox.Text = _rawContent;
        _suppressTextChanged = false;
        EditorBox.IsVisible = true;
        PreviewWebView.IsVisible = false;
        UpdateModeButton();
        UpdateSaveButton();
        EditorBox.Focus();
    }

    private void UpdateModeButton()
    {
        ModeToggleButton.Content = _isPreviewMode ? "Source" : "Preview";
    }

    private void UpdateSaveButton()
    {
        SaveButton.IsVisible = _isDirty;
    }

    private void ModeToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_isPreviewMode)
                ShowEditor();
            else
                RenderPreview();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] ModeToggleButton_Click FAILED: {ex.Message}");
        }
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] SaveButton_Click FAILED: {ex.Message}");
        }
    }

    private void EditorBox_TextInput(object? sender, TextInputEventArgs e)
    {
        try
        {
            if (_suppressTextChanged) return;

            _rawContent = EditorBox.Text ?? "";
            if (!_isDirty)
            {
                _isDirty = true;
                UpdateSaveButton();
                DisplayNameChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] EditorBox_TextInput FAILED: {ex.Message}");
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            try
            {
                await SaveAsync();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MarkdownViewer] Ctrl+S FAILED: {ex.Message}");
            }
        }
    }

    private void OpenExternalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            FileLog.Write($"[MarkdownViewer] Opened with default app: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MarkdownViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }
}
