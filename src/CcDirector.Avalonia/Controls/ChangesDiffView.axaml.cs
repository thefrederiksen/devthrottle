using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>A changed file in the left list of the diff viewer.</summary>
public sealed class DiffFileItem
{
    public string Path { get; init; } = "";
    public string Display { get; init; } = "";
    public string StatusChar { get; init; } = "";
    public bool IsStaged { get; init; }
    public bool IsUntracked { get; init; }
    public bool Selected { get; set; }
    public ISolidColorBrush StatusBrush { get; init; } = Brushes.Gray;
    public ISolidColorBrush NameBrush => Selected ? Brushes.White : Dim;
    private static readonly ISolidColorBrush Dim = new SolidColorBrush(Color.Parse("#AAAAAA"));
}

/// <summary>One rendered row of the diff pane (a hunk header or a code line).</summary>
public sealed class DiffRowItem
{
    public string Text { get; init; } = "";
    public string OldNo { get; init; } = "";
    public string NewNo { get; init; } = "";
    public IBrush RowBg { get; init; } = Brushes.Transparent;
    public IBrush TextBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// The diff viewer: changed files on the left (staged/unstaged), the selected file's diff on the
/// right, with stage/unstage/discard and a commit box. Reads via GitDiffService/GitStatusProvider;
/// writes via the existing GitWriteService.
/// </summary>
public partial class ChangesDiffView : UserControl
{
    private static readonly IBrush AddBg = new SolidColorBrush(Color.Parse("#12301C"));
    private static readonly IBrush DelBg = new SolidColorBrush(Color.Parse("#33191B"));
    private static readonly IBrush HunkBg = new SolidColorBrush(Color.Parse("#1A2330"));
    private static readonly IBrush AddFg = new SolidColorBrush(Color.Parse("#89D9A2"));
    private static readonly IBrush DelFg = new SolidColorBrush(Color.Parse("#E39A9A"));
    private static readonly IBrush CtxFg = new SolidColorBrush(Color.Parse("#B8B8B8"));
    private static readonly IBrush HunkFg = new SolidColorBrush(Color.Parse("#6A9FD8"));
    private static readonly ISolidColorBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly ISolidColorBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly ISolidColorBrush Red = new SolidColorBrush(Color.Parse("#EF4444"));

    private readonly GitStatusProvider _status = new();
    private readonly GitDiffService _diff = new();
    private readonly GitWriteService _write = new();
    private string? _repoPath;
    private DiffFileItem? _selected;
    private List<DiffFileItem> _staged = new();
    private List<DiffFileItem> _unstaged = new();

    public ChangesDiffView()
    {
        InitializeComponent();
    }

    public void Attach(string repoPath)
    {
        FileLog.Write($"[ChangesDiffView] Attach: {repoPath}");
        _repoPath = repoPath;
        _selected = null;
        _ = RefreshAsync();
    }

    public void Detach()
    {
        _repoPath = null;
        _selected = null;
        _pendingDiscard = null;
        DiscardConfirmOverlay.IsVisible = false;
        StagedFiles.ItemsSource = null;
        UnstagedFiles.ItemsSource = null;
        DiffRows.ItemsSource = null;
        DiffFileName.Text = "Select a file to see its changes";
    }

    private async Task RefreshAsync()
    {
        var repo = _repoPath;
        if (repo is null)
            return;
        try
        {
            GitStatusProvider.InvalidateCache(repo);
            var status = await _status.GetStatusAsync(repo);
            if (_repoPath != repo)
                return;

            _staged = status.StagedChanges.Select(f => ToItem(f, staged: true)).ToList();
            _unstaged = status.UnstagedChanges.Select(f => ToItem(f, staged: false)).ToList();

            StagedFiles.ItemsSource = _staged;
            UnstagedFiles.ItemsSource = _unstaged;
            StagedHeader.IsVisible = _staged.Count > 0;
            UnstagedHeader.IsVisible = _unstaged.Count > 0;
            NoChangesText.IsVisible = _staged.Count == 0 && _unstaged.Count == 0;

            // Keep the selection if the file still exists; otherwise clear the pane.
            var keep = _selected is null
                ? null
                : _staged.Concat(_unstaged).FirstOrDefault(f => f.Path == _selected.Path && f.IsStaged == _selected.IsStaged);
            if (keep != null)
                await SelectAsync(keep);
            else
            {
                _selected = null;
                DiffRows.ItemsSource = null;
                DiffFileName.Text = "Select a file to see its changes";
                StageButton.IsVisible = UnstageButton.IsVisible = DiscardButton.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ChangesDiffView] RefreshAsync FAILED: {ex.Message}");
        }
    }

    private DiffFileItem ToItem(GitFileEntry f, bool staged) => new()
    {
        Path = f.FilePath,
        Display = f.FilePath,
        StatusChar = f.StatusChar,
        IsStaged = staged,
        IsUntracked = f.Status == GitFileStatus.Untracked,
        StatusBrush = f.Status switch
        {
            GitFileStatus.Added or GitFileStatus.Untracked => Green,
            GitFileStatus.Deleted => Red,
            _ => Amber,
        },
    };

    private async void FileRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if ((sender as Border)?.DataContext is DiffFileItem item)
                await SelectAsync(item);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ChangesDiffView] FileRow_PointerPressed FAILED: {ex.Message}");
        }
    }

    private async Task SelectAsync(DiffFileItem item)
    {
        var repo = _repoPath;
        if (repo is null)
            return;
        foreach (var f in _staged.Concat(_unstaged)) f.Selected = false;
        item.Selected = true;
        _selected = item;

        DiffFileName.Text = item.Path;
        StageButton.IsVisible = !item.IsStaged;
        UnstageButton.IsVisible = item.IsStaged;
        DiscardButton.IsVisible = !item.IsStaged && !item.IsUntracked;

        IReadOnlyList<FileDiff> diffs;
        if (item.IsUntracked)
        {
            var one = await _diff.UntrackedAsync(repo, item.Path);
            diffs = one is null ? Array.Empty<FileDiff>() : new[] { one };
        }
        else
        {
            diffs = item.IsStaged
                ? await _diff.StagedAsync(repo, item.Path)
                : await _diff.UnstagedAsync(repo, item.Path);
        }

        DiffRows.ItemsSource = BuildRows(diffs);
        // Repaint selection highlight (items are plain objects, not observable).
        StagedFiles.ItemsSource = null; StagedFiles.ItemsSource = _staged;
        UnstagedFiles.ItemsSource = null; UnstagedFiles.ItemsSource = _unstaged;
    }

    /// <summary>Pure: parsed file diffs to rendered rows (unit-tested).</summary>
    internal static IReadOnlyList<DiffRowItem> BuildRows(IReadOnlyList<FileDiff> diffs)
    {
        var rows = new List<DiffRowItem>();
        foreach (var file in diffs)
        {
            if (file.IsBinary)
            {
                rows.Add(new DiffRowItem { Text = "(binary file - no text diff)", TextBrush = CtxFg, RowBg = HunkBg });
                continue;
            }
            foreach (var hunk in file.Hunks)
            {
                rows.Add(new DiffRowItem { Text = hunk.Header, TextBrush = HunkFg, RowBg = HunkBg });
                foreach (var line in hunk.Lines)
                {
                    rows.Add(new DiffRowItem
                    {
                        Text = line.Text,
                        OldNo = line.OldNumber?.ToString() ?? "",
                        NewNo = line.NewNumber?.ToString() ?? "",
                        RowBg = line.Kind switch
                        {
                            DiffLineKind.Added => AddBg,
                            DiffLineKind.Removed => DelBg,
                            _ => Brushes.Transparent,
                        },
                        TextBrush = line.Kind switch
                        {
                            DiffLineKind.Added => AddFg,
                            DiffLineKind.Removed => DelFg,
                            _ => CtxFg,
                        },
                    });
                }
            }
        }
        if (rows.Count == 0)
            rows.Add(new DiffRowItem { Text = "(no differences)", TextBrush = CtxFg });
        return rows;
    }

    private async void StageButton_Click(object? sender, RoutedEventArgs e) => await WriteActionAsync(
        item => _write.StageAsync(_repoPath!, new[] { item.Path }));

    private async void UnstageButton_Click(object? sender, RoutedEventArgs e) => await WriteActionAsync(
        item => _write.UnstageAsync(_repoPath!, new[] { item.Path }));

    // ----- discard, behind a plain-words confirmation (inspection finding F11) -----

    private IReadOnlyList<string>? _pendingDiscard;

    /// <summary>
    /// Test seam: when set, replaces the real discard write. Lets a headless test observe whether
    /// (and for which files) the destructive write ran, without touching a repository.
    /// </summary>
    internal Func<string, IReadOnlyList<string>, Task<GitWriteResult>>? DiscardServiceOverride { get; set; }

    /// <summary>
    /// Discard never acts directly: it first shows what would be permanently deleted and waits for
    /// an explicit "Delete changes". One click must never destroy tracked work.
    /// </summary>
    private void DiscardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_repoPath is null || _selected is null)
            return;
        ShowDiscardConfirmation(new[] { _selected.Path });
    }

    internal void ShowDiscardConfirmation(IReadOnlyList<string> files)
    {
        _pendingDiscard = files;
        DiscardConfirmTitle.Text = DiscardWarning(files.Count);
        DiscardConfirmFiles.Text = string.Join(Environment.NewLine, files);
        DiscardConfirmOverlay.IsVisible = true;
    }

    /// <summary>The plain-English statement of loss shown before any discard. Pure and unit-tested.</summary>
    internal static string DiscardWarning(int fileCount)
        => $"This permanently deletes your changes in {fileCount} file{(fileCount == 1 ? "" : "s")}. There is no undo.";

    private void KeepChangesButton_Click(object? sender, RoutedEventArgs e) => KeepChanges();

    internal void KeepChanges()
    {
        _pendingDiscard = null;
        DiscardConfirmOverlay.IsVisible = false;
    }

    private async void DeleteChangesButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await DeleteChangesAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ChangesDiffView] DeleteChangesButton_Click FAILED: {ex.Message}");
        }
    }

    internal async Task DeleteChangesAsync()
    {
        var files = _pendingDiscard;
        KeepChanges(); // clear the pending state and hide the overlay either way
        if (files is null || files.Count == 0 || _repoPath is null)
            return;
        var result = DiscardServiceOverride is { } discard
            ? await discard(_repoPath, files)
            : await _write.DiscardAsync(_repoPath, files);
        if (!result.Success)
            FileLog.Write($"[ChangesDiffView] discard failed: {result.Error}");
        await RefreshAsync();
    }

    private async Task WriteActionAsync(Func<DiffFileItem, Task<GitWriteResult>> action)
    {
        try
        {
            if (_repoPath is null || _selected is null)
                return;
            var result = await action(_selected);
            if (!result.Success)
                FileLog.Write($"[ChangesDiffView] write action failed: {result.Error}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ChangesDiffView] write action FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Commit the staged changes (issue #1107, item 5).
    ///
    /// The commit message was cleared only AFTER the await returned, so a second click during the commit
    /// read the same non-empty message and ran a SECOND git commit. The second one failed (nothing left
    /// staged) and its failure went to FileLog only, so the user saw nothing at all - and there was no busy
    /// state for the duration either.
    ///
    /// A failed commit is now reported on screen. A commit that git refused is not an edge case worth
    /// hiding: it is the moment the user most needs to know why.
    /// </summary>
    private async void CommitButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;

        await BusyAction.RunAsync(button, "source control changes", "commit the changes", async () =>
        {
            var repo = _repoPath;
            var message = CommitMessage.Text;
            if (repo is null || string.IsNullOrWhiteSpace(message))
                return;

            var result = await _write.CommitAsync(repo, message.Trim());
            if (result.Success)
            {
                CommitMessage.Text = "";
            }
            else
            {
                FileLog.Write($"[ChangesDiffView] commit failed: {result.Error}");
                await RefreshAsync();
                throw new InvalidOperationException(result.Error ?? "git refused the commit.");
            }

            await RefreshAsync();
        },
        "Committing...",
        owner: TopLevel.GetTopLevel(this) as Window,
        failureTitle: "Could not commit");
    }
}
