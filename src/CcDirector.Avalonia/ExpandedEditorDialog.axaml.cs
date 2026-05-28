using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// Large, resizable editor + live preview dialog. Edits raw prompt text on the left
/// and renders a preview on the right where any line that is a path to an image file
/// is shown inline as the picture. Two modes:
///   - Prompt mode: edit a single block of text; Apply returns the edited text.
///   - Queue mode: a left rail lists a session's queued prompts; selecting one loads it
///     into the editor and edits are saved back into the queue item.
/// </summary>
public partial class ExpandedEditorDialog : Window
{
    public enum EditorMode
    {
        Prompt,
        Queue,
    }

    private sealed class RailItem
    {
        public Guid Id { get; init; }
        public string Index { get; init; } = "";
        public string Snippet { get; init; } = "";
    }

    private readonly EditorMode _mode;
    private readonly PromptQueue? _queue;
    private readonly Dictionary<string, Bitmap?> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<RailItem> _railItems = new();

    private Guid? _selectedItemId;
    private bool _suppressTextChanged;
    private bool _dirty;

    /// <summary>In prompt mode, the text the user landed on when they clicked Apply.</summary>
    public string EditedText { get; private set; } = "";

    /// <summary>Prompt mode: edit a single block of text.</summary>
    public ExpandedEditorDialog(string title, string initialText)
    {
        _mode = EditorMode.Prompt;
        InitializeComponent();

        Title = title;
        HeaderText.Text = title;

        _suppressTextChanged = true;
        EditorBox.Text = initialText ?? "";
        _suppressTextChanged = false;
        EditedText = EditorBox.Text;

        ConfigurePromptMode();
        EditorBox.TextChanged += EditorBox_TextChanged;
        EditorBox.KeyDown += EditorBox_KeyDown;
        BuildPreview();

        Loaded += (_, _) => Dispatcher.UIThread.Post(() => EditorBox.Focus());
    }

    /// <summary>Queue mode: browse and edit a session's queued prompts.</summary>
    /// <param name="initialItemId">If set, the rail selects this item on open.</param>
    public ExpandedEditorDialog(string title, PromptQueue queue, Guid? initialItemId = null)
    {
        _mode = EditorMode.Queue;
        _queue = queue;
        InitializeComponent();

        Title = title;
        HeaderText.Text = title;

        ConfigureQueueMode();
        EditorBox.TextChanged += EditorBox_TextChanged;
        QueueRailList.ItemsSource = _railItems;
        LoadQueueRail();

        Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_railItems.Count == 0)
                return;

            var index = 0;
            if (initialItemId is Guid wanted)
            {
                var found = _railItems.ToList().FindIndex(r => r.Id == wanted);
                if (found >= 0)
                    index = found;
            }

            QueueRailList.SelectedIndex = index;
        });
    }

    // Parameterless constructor for the XAML designer.
    public ExpandedEditorDialog() : this("Editor", "") { }

    private void ConfigurePromptMode()
    {
        QueueRail.IsVisible = false;
        SaveButton.IsVisible = false;
        PrimaryButton.Content = "Apply";
        CloseButton.Content = "Cancel";
    }

    private void ConfigureQueueMode()
    {
        QueueRail.IsVisible = true;
        PrimaryButton.IsVisible = false;
        SaveButton.IsVisible = true;
        CloseButton.Content = "Close";
        EditorBox.IsEnabled = false;
    }

    private void LoadQueueRail()
    {
        if (_queue == null) return;

        _railItems.Clear();
        var items = _queue.Items;
        for (int i = 0; i < items.Count; i++)
            _railItems.Add(BuildRailItem(i, items[i]));
    }

    private static RailItem BuildRailItem(int index, PromptQueueItem item)
    {
        var snippet = item.Text.ReplaceLineEndings(" ").Trim();
        if (snippet.Length > 120)
            snippet = snippet[..120] + "...";

        return new RailItem
        {
            Id = item.Id,
            Index = $"#{index + 1}",
            Snippet = snippet,
        };
    }

    private void QueueRailList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Save edits to the previously selected item before switching away.
        SaveCurrentIfDirty();

        if (QueueRailList.SelectedItem is not RailItem rail || _queue == null)
        {
            _selectedItemId = null;
            EditorBox.IsEnabled = false;
            _suppressTextChanged = true;
            EditorBox.Text = "";
            _suppressTextChanged = false;
            BuildPreview();
            return;
        }

        var item = _queue.FindById(rail.Id);
        _selectedItemId = item?.Id;

        _suppressTextChanged = true;
        EditorBox.Text = item?.Text ?? "";
        _suppressTextChanged = false;

        _dirty = false;
        EditorBox.IsEnabled = item != null;
        BuildPreview();
    }

    private void EditorBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        if (_mode == EditorMode.Prompt)
            EditedText = EditorBox.Text ?? "";
        else
            _dirty = true;

        BuildPreview();
    }

    private void EditorBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Enter applies in prompt mode for quick keyboard flow.
        if (_mode == EditorMode.Prompt && e.Key == Key.Enter
            && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            EditedText = EditorBox.Text ?? "";
            Close(true);
        }
    }

    private void SaveCurrentIfDirty()
    {
        if (!_dirty || _queue == null || _selectedItemId is not Guid id)
            return;

        var newText = EditorBox.Text ?? "";
        _queue.UpdateText(id, newText);
        _dirty = false;

        // Refresh the rail snippet for the edited item without rebuilding selection.
        var railIndex = _railItems.ToList().FindIndex(r => r.Id == id);
        if (railIndex >= 0)
            _railItems[railIndex] = BuildRailItem(railIndex, new PromptQueueItem { Id = id, Text = newText });
    }

    private void BuildPreview()
    {
        PreviewPanel.Children.Clear();

        var segments = PromptContentParser.Parse(EditorBox.Text);
        if (segments.Count == 0)
        {
            PreviewPanel.Children.Add(new TextBlock
            {
                Text = "(empty)",
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontStyle = FontStyle.Italic,
                FontSize = 12,
            });
            return;
        }

        foreach (var segment in segments)
        {
            if (segment.Kind == PromptSegmentKind.Text)
            {
                PreviewPanel.Children.Add(new SelectableTextBlock
                {
                    Text = segment.Content,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                    FontSize = 13,
                });
            }
            else
            {
                AddImageSegment(segment.Content);
            }
        }
    }

    private void AddImageSegment(string path)
    {
        PreviewPanel.Children.Add(new TextBlock
        {
            Text = path,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0xA2, 0xF7)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var bitmap = LoadBitmap(path);
        if (bitmap != null)
        {
            PreviewPanel.Children.Add(new Image
            {
                Source = bitmap,
                MaxWidth = 560,
                MaxHeight = 420,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        else
        {
            PreviewPanel.Children.Add(new TextBlock
            {
                Text = "(image not found on disk)",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x66, 0x66)),
                FontSize = 11,
                FontStyle = FontStyle.Italic,
            });
        }
    }

    private Bitmap? LoadBitmap(string path)
    {
        if (_bitmapCache.TryGetValue(path, out var cached))
            return cached;

        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path))
                bitmap = new Bitmap(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ExpandedEditorDialog] LoadBitmap FAILED: path={path}: {ex.Message}");
        }

        _bitmapCache[path] = bitmap;
        return bitmap;
    }

    // Speak: dictate into the editor at the caret via the same in-process SpeakDialog the
    // Terminal tab uses (NAudio capture -> dictation library -> cleaned transcript, no
    // browser, no localhost roundtrip). Lets the user talk about the screenshot they can
    // see in the preview without losing their place. If the dialog finishes with Send and
    // we are in prompt mode, treat that as Apply.
    private async void DictateButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EditorBox.IsEnabled)
            {
                FileLog.Write("[ExpandedEditorDialog] DictateButton_Click: editor not editable, ignoring");
                return;
            }
            var options = (global::Avalonia.Application.Current as App)?.SessionManager?.Options;
            if (options is null || string.IsNullOrWhiteSpace(options.ResolveOpenAiKey()))
            {
                FileLog.Write("[ExpandedEditorDialog] DictateButton_Click: no OpenAI key configured");
                HeaderText.Text = "Dictation needs an OPENAI_API_KEY env var or Voice.OpenAiKey in appsettings.json.";
                return;
            }

            // Snapshot the caret BEFORE opening the dialog; focus moves away and some
            // controls reset CaretIndex to 0 on focus loss, which would prepend instead.
            var existingBefore = EditorBox.Text ?? "";
            var caretBefore = EditorBox.CaretIndex;
            if (caretBefore < 0 || caretBefore > existingBefore.Length)
                caretBefore = existingBefore.Length;

            FileLog.Write("[ExpandedEditorDialog] DictateButton_Click: opening SpeakDialog");
            var dlg = new global::CcDirector.Avalonia.Voice.SpeakDialog(options);
            await dlg.ShowDialog(this);
            var transcript = dlg.ResultText;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                FileLog.Write("[ExpandedEditorDialog] DictateButton_Click: dialog returned no text");
                return;
            }

            InsertAtCaret(transcript!, caretBefore);
            FileLog.Write($"[ExpandedEditorDialog] DictateButton_Click: inserted {transcript!.Length} chars at caret={caretBefore}, shouldSubmit={dlg.ShouldSubmit}");

            if (dlg.ShouldSubmit && _mode == EditorMode.Prompt)
            {
                EditedText = EditorBox.Text ?? "";
                Close(true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ExpandedEditorDialog] DictateButton_Click FAILED: {ex.Message}");
            HeaderText.Text = "Dictation failed: " + ex.Message;
        }
    }

    private void InsertAtCaret(string text, int caret)
    {
        var existing = EditorBox.Text ?? "";
        if (caret < 0 || caret > existing.Length) caret = existing.Length;
        var prefix = existing[..caret];
        var suffix = existing[caret..];
        var needsSpaceBefore = prefix.Length > 0 && !char.IsWhiteSpace(prefix[^1]);
        var needsSpaceAfter = suffix.Length > 0 && !char.IsWhiteSpace(suffix[0]);
        var insert = (needsSpaceBefore ? " " : "") + text + (needsSpaceAfter ? " " : "");
        EditorBox.Text = prefix + insert + suffix;
        EditorBox.CaretIndex = prefix.Length + insert.Length;
        EditorBox.Focus();
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentIfDirty();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ExpandedEditorDialog] SaveButton_Click FAILED: {ex.Message}");
        }
    }

    private void PrimaryButton_Click(object? sender, RoutedEventArgs e)
    {
        EditedText = EditorBox.Text ?? "";
        Close(true);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_mode == EditorMode.Queue)
            {
                SaveCurrentIfDirty();
                Close(true);
            }
            else
            {
                Close(false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ExpandedEditorDialog] CloseButton_Click FAILED: {ex.Message}");
            Close(false);
        }
    }
}
