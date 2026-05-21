using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

public partial class CodeViewerControl : UserControl, IFileViewer
{
    private const int MaxFileSizeBytes = 512_000; // 500 KB

    private string? _filePath;
    private bool _isDirty;
    private bool _suppressTextChanged;
    private bool _wordWrap;

    public string? FilePath => _filePath;
    public bool IsDirty => _isDirty;
    public event Action? DisplayNameChanged;

    /// <summary>
    /// Maps file extensions to AvaloniaEdit built-in highlighting definition names.
    /// </summary>
    private static readonly Dictionary<string, string> HighlightingMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs", "C#" },
        { ".py", "Python" },
        { ".js", "JavaScript" },
        { ".jsx", "JavaScript" },
        { ".ts", "JavaScript" },
        { ".tsx", "JavaScript" },
        { ".json", "Json" },
        { ".xml", "XML" },
        { ".xaml", "XML" },
        { ".axaml", "XML" },
        { ".csproj", "XML" },
        { ".fsproj", "XML" },
        { ".vbproj", "XML" },
        { ".props", "XML" },
        { ".targets", "XML" },
        { ".svg", "XML" },
        { ".css", "CSS" },
        { ".sql", "TSQL" },
        { ".ps1", "PowerShell" },
        { ".java", "Java" },
        { ".cpp", "C++" },
        { ".c", "C++" },
        { ".h", "C++" },
        { ".hpp", "C++" },
        { ".php", "PHP" },
    };

    public CodeViewerControl()
    {
        InitializeComponent();
        ApplyDarkTheme();
        UpdateWrapButton();
        Editor.TextChanged += Editor_TextChanged;
        KeyDown += OnKeyDown;
    }

    public async Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[CodeViewer] LoadFileAsync: {filePath}");

        _filePath = filePath;
        _isDirty = false;
        FilePathText.Text = filePath;
        ToolTip.SetTip(FilePathText, filePath);
        LoadingText.IsVisible = true;

        var (content, truncated) = await Task.Run(() =>
        {
            var info = new FileInfo(filePath);
            if (info.Length > MaxFileSizeBytes)
            {
                using var reader = new StreamReader(filePath);
                var buf = new char[MaxFileSizeBytes];
                int read = reader.Read(buf, 0, buf.Length);
                return (new string(buf, 0, read), true);
            }
            return (File.ReadAllText(filePath), false);
        });

        _suppressTextChanged = true;
        Editor.Text = content;
        _suppressTextChanged = false;

        // Apply syntax highlighting after setting text (AvaloniaEdit requires this order)
        ApplyHighlighting(filePath);

        if (truncated)
        {
            Editor.IsReadOnly = true;
            FilePathText.Text = $"{filePath}  [TRUNCATED - file exceeds 500 KB]";
        }

        LoadingText.IsVisible = false;
        UpdateSaveButton();

        FileLog.Write($"[CodeViewer] LoadFileAsync complete: {filePath}, length={content.Length}, truncated={truncated}");
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

        FileLog.Write($"[CodeViewer] Save: {_filePath}");
        var text = Editor.Text;
        await Task.Run(() => File.WriteAllText(_filePath, text));
        _isDirty = false;
        UpdateSaveButton();
        DisplayNameChanged?.Invoke();
        FileLog.Write($"[CodeViewer] Save complete: {_filePath}");
    }

    public string GetDisplayName()
    {
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
        return _isDirty ? $"*{name}" : name;
    }

    private void ApplyHighlighting(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (HighlightingMap.TryGetValue(ext, out var highlightingName))
        {
            var definition = HighlightingManager.Instance.GetDefinition(highlightingName);
            if (definition != null)
            {
                Editor.SyntaxHighlighting = definition;
                FileLog.Write($"[CodeViewer] Applied highlighting: {highlightingName} for {ext}");
                return;
            }
        }

        // Let AvaloniaEdit try to detect by extension
        var detected = HighlightingManager.Instance.GetDefinitionByExtension(ext);
        if (detected != null)
        {
            Editor.SyntaxHighlighting = detected;
            FileLog.Write($"[CodeViewer] Auto-detected highlighting: {detected.Name} for {ext}");
            return;
        }

        FileLog.Write($"[CodeViewer] No highlighting found for {ext}");
    }

    private void ApplyDarkTheme()
    {
        Editor.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Editor.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
        Editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85));
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
        Editor.TextArea.SelectionForeground = null; // keep syntax colors in selection
    }

    private void UpdateSaveButton()
    {
        SaveButton.IsVisible = _isDirty;
    }

    private void UpdateWrapButton()
    {
        WrapToggleButton.Content = _wordWrap ? "No Wrap" : "Wrap";
    }

    private void WrapToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _wordWrap = !_wordWrap;
            Editor.WordWrap = _wordWrap;
            UpdateWrapButton();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeViewer] WrapToggleButton_Click FAILED: {ex.Message}");
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
            FileLog.Write($"[CodeViewer] SaveButton_Click FAILED: {ex.Message}");
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_suppressTextChanged) return;

            if (!_isDirty)
            {
                _isDirty = true;
                UpdateSaveButton();
                DisplayNameChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeViewer] Editor_TextChanged FAILED: {ex.Message}");
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
                FileLog.Write($"[CodeViewer] Ctrl+S FAILED: {ex.Message}");
            }
        }
    }

    private void OpenExternalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
            FileLog.Write($"[CodeViewer] Opened with default app: {FilePath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeViewer] OpenExternalButton_Click FAILED: {ex.Message}");
        }
    }

}
