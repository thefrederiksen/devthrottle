using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

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
            FileLog.Write($"[TurnReviewDialog] load failed: {ex.Message}");
            StatusText.Text = "Failed to load reviews.";
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
        ScreenBox.Text = r.Screen.Count > 0 ? string.Join("\n", r.Screen) : "(no screen captured)";
        TranscriptBox.Text = string.IsNullOrWhiteSpace(r.Transcript) ? "(no output this turn)" : r.Transcript;
        WingmanSaidBox.Text = string.IsNullOrWhiteSpace(r.WingmanSaid) ? "(nothing)" : r.WingmanSaid;
        ActionsList.ItemsSource = r.WingmanActions.Count == 0
            ? new List<string> { "(none)" }
            : r.WingmanActions.Select(a => $"{a.At.ToLocalTime():HH:mm:ss}  {a.Action}: {a.Detail}"
                + (string.IsNullOrWhiteSpace(a.Reason) ? "" : $"  ({a.Reason})")).ToList();
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

        private static IBrush DotFor(string color) => color?.ToLowerInvariant() switch
        {
            "red" => new SolidColorBrush(Color.Parse("#E5484D")),
            "blue" => new SolidColorBrush(Color.Parse("#3B82F6")),
            "green" => new SolidColorBrush(Color.Parse("#22C55E")),
            "yellow" => new SolidColorBrush(Color.Parse("#F59E0B")),
            _ => new SolidColorBrush(Color.Parse("#888888")),
        };
    }
}
