using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Core.ErrorReports;

namespace CcDirector.Avalonia;

/// <summary>
/// Read-only browser over the per-turn review log (<see cref="TurnReviewReader"/>): a list of
/// turns (newest first) on the left, the selected turn's terminal + Wingman detail on the
/// right. Display only for now; reviewing/annotating a turn comes later.
/// </summary>
public partial class TurnReviewDialog : Window
{
    public TurnReviewDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading...";
        List<TurnRow> rows;
        try
        {
            var records = await Task.Run(() => TurnReviewReader.LoadRecent());
            rows = records.Select(TurnRow.From).ToList();
        }
        catch (Exception ex)
        {
            StatusText.Text = ShownError.Report("turn reviews", "load the turn reviews", "Failed to load reviews.", ex);
            return;
        }

        TurnList.ItemsSource = rows;
        CountText.Text = rows.Count == 0 ? "no turns logged yet" : $"{rows.Count} turn(s)";
        StatusText.Text = "";
        if (rows.Count > 0)
            TurnList.SelectedIndex = 0;
        else
            DetailPanel.IsVisible = false;
    }

    private void TurnList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TurnList.SelectedItem is not TurnRow row)
        {
            DetailPanel.IsVisible = false;
            return;
        }

        var r = row.Record;
        DetailPanel.IsVisible = true;
        DetailHeader.Text = string.IsNullOrWhiteSpace(r.SessionName) ? r.SessionId : r.SessionName;
        DetailSub.Text = $"{r.TsUtc.ToLocalTime():MMM d, yyyy  HH:mm:ss}    {r.StatusColor} - {r.StatusReason}";
        RenderScreen(r);
        TranscriptBox.Text = string.IsNullOrWhiteSpace(r.Transcript) ? "(no output this turn)" : r.Transcript;
        WingmanSaidBox.Text = string.IsNullOrWhiteSpace(r.WingmanSaid) ? "(nothing)" : r.WingmanSaid;
        ActionsList.ItemsSource = r.WingmanActions.Count == 0
            ? new List<string> { "(none)" }
            : r.WingmanActions.Select(a => $"{a.At.ToLocalTime():HH:mm:ss}  {a.Action}: {a.Detail}"
                + (string.IsNullOrWhiteSpace(a.Reason) ? "" : $"  ({a.Reason})")).ToList();
    }

    /// <summary>Render the captured screen into <c>ScreenText</c> with its terminal colours,
    /// from the record's styled <see cref="TurnReviewRecord.ScreenCells"/>.</summary>
    private void RenderScreen(TurnReviewRecord r)
    {
        // TextBlock.Inlines is null until first assigned (the getter has no lazy-init), so
        // create the collection once; later selections reuse and clear it.
        var inlines = ScreenText.Inlines ??= new InlineCollection();
        inlines.Clear();

        if (r.ScreenCells.Count == 0)
        {
            inlines.Add(new Run("(no screen captured)"));
            return;
        }

        for (int i = 0; i < r.ScreenCells.Count; i++)
        {
            foreach (var seg in r.ScreenCells[i])
            {
                var run = new Run(seg.Text);
                if (TryBrush(seg.Fg, out var fg)) run.Foreground = fg;
                if (TryBrush(seg.Bg, out var bg)) run.Background = bg;
                if (seg.Bold) run.FontWeight = FontWeight.Bold;
                inlines.Add(run);
            }
            if (i < r.ScreenCells.Count - 1)
                inlines.Add(new LineBreak());
        }
    }

    private static bool TryBrush(string? hex, out IBrush brush)
    {
        brush = Brushes.Transparent;
        if (string.IsNullOrEmpty(hex)) return false;
        try
        {
            brush = new SolidColorBrush(Color.Parse(hex));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await LoadAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>One row in the turns list.</summary>
    private sealed class TurnRow
    {
        public string When { get; init; } = "";
        public string Session { get; init; } = "";
        public string Reason { get; init; } = "";
        public IBrush Dot { get; init; } = Brushes.Gray;
        public TurnReviewRecord Record { get; init; } = new();

        public static TurnRow From(TurnReviewRecord r) => new()
        {
            When = r.TsUtc.ToLocalTime().ToString("MMM d  HH:mm:ss"),
            Session = string.IsNullOrWhiteSpace(r.SessionName) ? Short(r.SessionId) : r.SessionName,
            Reason = r.StatusReason,
            Dot = DotFor(r.StatusColor),
            Record = r,
        };

        private static string Short(string id) => id.Length > 8 ? id[..8] : id;

        /// <summary>
        /// The recorded colour's dot, through the ONE palette (<see cref="StatusPalette"/>).
        ///
        /// This is the HISTORICAL surface: it renders the colour NAME a turn-review record captured
        /// at the time, so it maps a name to a hex and folds nothing - there is no live session here
        /// to fold. What it must not do is carry its own hexes, which it did: red was #E5484D here
        /// and #EF4444 on the rail, so the same recorded "red" was two different pixels depending on
        /// which window you opened. It also knew only four names, so a recorded orange, purple,
        /// supporting, error, or grey silently fell through to a #888888 that is in no palette at all.
        /// </summary>
        private static IBrush DotFor(string color) => StatusPalette.BrushFor(color);
    }
}
