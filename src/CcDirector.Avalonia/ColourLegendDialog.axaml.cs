using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// "What the colours mean" on the desktop - the same window the Cockpit and the phone have had, and the
/// answer to the owner's <em>"if you hover over the color, it should show you what that color means"</em>
/// for the surface that is open all day.
///
/// THIS WINDOW DECIDES NOTHING AND WRITES NOTHING. Every colour, every swatch and every sentence comes
/// from the Gateway (<c>GET /gateway/session-colours</c>), which writes them beside the fold that decides
/// the colours. This renders them verbatim, exactly as the web client's <c>ColourLegend.tsx</c> does.
/// There is no built-in copy of the words to fall back on, deliberately: a stale copy compiled into an old
/// Director is how a legend comes to explain a colour the product no longer paints - which is the version
/// gap this mission exists to close.
///
/// WHAT IT DELIBERATELY DOES NOT SHOW, and this is a gap to settle rather than an oversight. The Gateway's
/// legend carries a note about the magenta sentinel, worded for the browser clients: "the screen received
/// a colour this app does not understand". On this desktop that is no longer what magenta means - since
/// the Session Cards mission, magenta means the Gateway stamped NOTHING, and a colour this build does not
/// understand paints <see cref="StatusPalette.Neutral"/>. Rendering that note here would put a sentence on
/// screen that is false about this surface, and writing a truer one here would be this client giving its
/// own reason for something, which the owner ruled against. So the two RENDERING sentinels - neither of
/// which is a colour the Gateway decides - are left out until the Gateway has words for them. The ten real
/// colours, which are the whole question a person opens this window to ask, are all here.
/// </summary>
public partial class ColourLegendDialog : Window
{
    public ColourLegendDialog()
    {
        FileLog.Write("[ColourLegendDialog] Constructor: initializing");
        InitializeComponent();

        // Show the window first, fill it after - the words may still be on their way from the Gateway.
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>
    /// Render the legend this desktop has, asking the Gateway for a fresh one if it has none yet. The
    /// cache never blocks and never throws, so this reads it, and waits only when there is nothing to
    /// show.
    /// </summary>
    private async Task LoadAsync()
    {
        try
        {
            var cache = (global::Avalonia.Application.Current as App)?.ControlApiHost?.ColourLegend;
            if (cache is null)
            {
                Fail("The Director is still starting, so it has not asked the Gateway what the colours mean yet.");
                return;
            }

            var legend = cache.Current;
            if (legend is null)
            {
                // Nothing read yet - a Director that has only just connected, or one whose last read
                // failed. Ask now rather than show an empty window - and JOIN the read the line above may
                // just have started rather than opening a second one for the same words.
                await cache.ReadNowAsync();
                legend = cache.Current;
            }

            if (legend is null)
            {
                Fail(cache.Error is { Length: > 0 } why
                    ? $"The Gateway could not be asked what the colours mean: {why}"
                    : "This Director has no Gateway to ask what the colours mean.");
                return;
            }

            Render(legend);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ColourLegendDialog] LoadAsync FAILED: {ex.Message}");
            Fail($"What the colours mean could not be loaded: {ex.Message}");
        }
    }

    /// <summary>Draw the Gateway's answer: one row per colour, then its note about the Wingman's
    /// verdicts. Verbatim - the only strings this method contributes are the punctuation around the
    /// "asks for you" answer, which the web legend renders the same way.
    ///
    /// Internal so <c>ColourLegendDialogTests</c> can fill a MOUNTED window and then measure what is on it.
    /// Reading the strings off an unmounted row is what let this window ship with nine of its ten
    /// explanations running off the right-hand edge: every string was correct and none of them fitted.</summary>
    internal void Render(SessionColourLegendDto legend)
    {
        FileLog.Write($"[ColourLegendDialog] Render: {legend.Entries.Count} colour(s)");
        ColourRows.Children.Clear();

        foreach (var entry in legend.Entries)
            ColourRows.Children.Add(Row(entry));

        if (!string.IsNullOrWhiteSpace(legend.VerdictNote))
        {
            VerdictNoteText.Text = legend.VerdictNote;
            VerdictNoteText.IsVisible = true;
        }

        StatusText.IsVisible = false;
    }

    /// <summary>One colour: its dot in the Gateway's own hex, its title, whether it asks for you, and what
    /// it means. Internal so <c>ColourLegendDialogTests</c> can assert that every string it puts on screen
    /// is one the Gateway sent, which is the whole claim this window makes.</summary>
    internal static Control Row(SessionColourLegendEntryDto entry)
    {
        var dot = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new global::Avalonia.CornerRadius(3),
            // The hex the Gateway resolved from the one canonical palette and sent beside the name, rendered
            // rather than looked up here. It is the same pixel the session's dot shows for every colour this
            // build knows - but NOT for one it does not: an unknown name paints StatusPalette.Neutral on the
            // rail while this swatch still shows the Gateway's hex, which is the version gap the mission
            // exists for. The legend is the place that can still show the real colour, so it does.
            Background = new SolidColorBrush(Color.Parse(entry.Hex)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new global::Avalonia.Thickness(0, 3, 10, 0),
        };

        var title = new TextBlock
        {
            Text = entry.Title,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var asks = new TextBlock
        {
            Text = $"Asks for you: {entry.AsksForYou}",
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new global::Avalonia.Thickness(10, 1, 0, 0),
        };

        // EVERY CONTAINER HERE MUST HAND ITS CHILDREN A FINITE WIDTH, and that is why these are Grids and not
        // horizontal StackPanels. A horizontal StackPanel measures its children with UNBOUNDED width, so
        // TextWrapping.Wrap below has nothing to wrap at and the sentence runs off the window - which is
        // exactly what this window did when it shipped: nine of its ten explanations were wider than its
        // 580-pixel rows viewport, the widest at 1570. A Grid's Auto column is measured against the width
        // that is actually left, so the words wrap inside the window instead of leaving it.
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        heading.Children.Add(title);
        Grid.SetColumn(title, 0);
        heading.Children.Add(asks);
        Grid.SetColumn(asks, 1);

        var means = new TextBlock
        {
            Text = entry.Means,
            Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new global::Avalonia.Thickness(0, 2, 0, 0),
        };

        // A VERTICAL StackPanel is fine: it passes its children the full width it was given and only leaves
        // the HEIGHT unbounded. It is the horizontal one that was the defect.
        var words = new StackPanel();
        words.Children.Add(heading);
        words.Children.Add(means);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        row.Children.Add(dot);
        Grid.SetColumn(dot, 0);
        row.Children.Add(words);
        Grid.SetColumn(words, 1);
        return row;
    }

    /// <summary>Say what went wrong, in the status line, and draw no colours. A legend window that is
    /// silently blank tells the person the colours have no meanings.</summary>
    private void Fail(string message)
    {
        FileLog.Write($"[ColourLegendDialog] Fail: {message}");
        ColourRows.Children.Clear();
        VerdictNoteText.IsVisible = false;
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse("#F0B848"));
        StatusText.IsVisible = true;
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[ColourLegendDialog] BtnClose_Click: closing");
        Close();
    }
}
