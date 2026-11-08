using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using CcDirector.Core.Machine;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The toolbar's resource meter (the Resource Monitor mission, issue #2811, sections 5d to 5g): three bars, CPU,
/// MEM and DISK (free space on the tightest fixed drive, always shown since 2026-11-07), each coloured on its own
/// reading, and the whole meter one click target.
///
/// IT RENDERS WHAT IT IS HANDED. Every figure, word and level comes from <see cref="ResourceMeterFold"/> in
/// Core; this control only maps a level to the mock-up's colours and lays the bars out (CLAUDE.md rule 7).
///
/// AN UNREADABLE BAR IS DRAWN AS UNREADABLE, NOT AS EMPTY. An empty track reads as "idle", which is a lie the
/// owner would believe. So a bar that cannot be read loses its track entirely and shows a dashed outline
/// with a grey dash, and the tooltip says why.
///
/// ON A NARROW WINDOW (<see cref="IsCompact"/>) the bars drop and the percentages stay, so the meter never
/// pushes Cockpit and Settings off the toolbar (issue #2812). The frame keeps its colour, so a warning still
/// shows.
/// </summary>
public sealed class ResourceMeter : Button
{
    private const double TrackWidth = 44;
    private const double TrackHeight = 7;

    private readonly Gauge _cpu = new();
    private readonly Gauge _memory = new();
    private readonly Gauge _disk = new();
    private readonly StackPanel _bars;
    private readonly TextBlock _narrow;
    private bool _isCompact;

    /// <summary>Use the plain button template; this control supplies its own look.</summary>
    protected override Type StyleKeyOverride => typeof(Button);

    public ResourceMeter()
    {
        Padding = new Thickness(8, 3);
        CornerRadius = new CornerRadius(5);
        BorderThickness = new Thickness(1);
        Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(2, 0);

        _bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _bars.Children.Add(_cpu.Root);
        _bars.Children.Add(_memory.Root);
        _bars.Children.Add(_disk.Root);

        _narrow = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("#CCCCCC"),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        var content = new Panel();
        content.Children.Add(_bars);
        content.Children.Add(_narrow);
        Content = content;

        Render(ResourceMeterFold.Build(ResourceSamplerState.NotYetSampled, Environment.MachineName, 0, null, null, Array.Empty<TimelineSample>()));
    }

    /// <summary>The view last rendered, for tests.</summary>
    public ResourceMeterView? View { get; private set; }

    /// <summary>The narrow-window form: percentages only, no bars.</summary>
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (_isCompact == value) return;
            _isCompact = value;
            _bars.IsVisible = !value;
            _narrow.IsVisible = value;
            FileLog.Write($"[ResourceMeter] IsCompact: {value}");
        }
    }

    /// <summary>Draws one meter view. Call on the user-interface thread.</summary>
    public void Render(ResourceMeterView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        View = view;

        _cpu.Render(view.Cpu);
        _memory.Render(view.Memory);
        _disk.Render(view.Disk);
        _narrow.Text = view.NarrowText;

        var (background, border) = Frame(view.FrameLevel);
        Background = Brush(background);
        BorderBrush = Brush(border);

        ToolTip.SetTip(this, string.Join(Environment.NewLine, view.TooltipLines));
        AutomationProperties.SetName(this, view.AccessibleName);
    }

    // The mock-up's colours (mission section 5d to 5f), one place.
    private static (string Background, string Border) Frame(ResourceLevel level) => level switch
    {
        ResourceLevel.Critical => ("#2B1A1A", "#7A2B2B"),
        ResourceLevel.Tight => ("#2B2316", "#7A5410"),
        _ => ("#232326", "#3C3C3C"),
    };

    private static (string Fill, string Text) Tone(ResourceLevel level) => level switch
    {
        ResourceLevel.Critical => ("#F14C4C", "#FF8B8B"),
        ResourceLevel.Tight => ("#F59E0B", "#F59E0B"),
        ResourceLevel.Normal => ("#4CAF50", "#CCCCCC"),
        // A real figure with no verdict yet (the first reads, before the 60-second rule can rule): drawn in a
        // neutral grey so the figure shows without claiming a colour it was not given. Never the track's own
        // grey, which would make a real figure look like an empty bar.
        _ => ("#9A9A9A", "#CCCCCC"),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>One labelled bar: "CPU [====    ] 28%".</summary>
    private sealed class Gauge
    {
        private readonly TextBlock _label = new()
        {
            FontSize = 10,
            Foreground = Brush("#888888"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        private readonly Border _track = new()
        {
            Width = TrackWidth,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(3),
            Background = Brush("#3C3C3C"),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private readonly Border _fill = new()
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        private readonly Rectangle _unreadable = new()
        {
            Width = TrackWidth,
            Height = TrackHeight,
            Stroke = Brush("#666666"),
            StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double> { 2, 2 },
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        private readonly TextBlock _value = new()
        {
            FontSize = 11,
            MinWidth = 30,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        public Gauge()
        {
            _track.Child = _fill;
            var slot = new Panel { VerticalAlignment = VerticalAlignment.Center };
            slot.Children.Add(_track);
            slot.Children.Add(_unreadable);

            Root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            Root.Children.Add(_label);
            Root.Children.Add(slot);
            Root.Children.Add(_value);
        }

        public StackPanel Root { get; }

        public void Render(ResourceBarView bar)
        {
            _label.Text = bar.Label;
            _value.Text = bar.PercentText;
            ToolTip.SetTip(Root, bar.Tooltip);

            var (fill, text) = Tone(bar.Level);
            _value.Foreground = Brush(text);

            if (bar.Fraction is { } fraction)
            {
                _track.IsVisible = true;
                _unreadable.IsVisible = false;
                _fill.Width = TrackWidth * fraction;
                _fill.Background = Brush(fill);
            }
            else
            {
                // No track at all, so nothing can be mistaken for an empty bar.
                _track.IsVisible = false;
                _unreadable.IsVisible = true;
                _fill.Width = 0;
                _value.Foreground = Brush("#888888");
            }
        }
    }
}
