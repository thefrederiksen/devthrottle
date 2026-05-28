using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using IoPath = System.IO.Path;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace WingmanRenderHarness;

// Headless + Skia render harness for the Wingman tab changes (#1-#5).
//
// It renders the REAL Markdown.Avalonia control and faithful replicas of the
// reordered briefing banner and the Speak playback dialog onto the same Fluent
// Dark theme the app uses, then captures each scene to a PNG. This proves the
// markdown table fix (#3) and dark-theme theming, the briefing hierarchy (#4),
// the Play vs Speak disambiguation (#2), and the Stop-dialog layout (#1) without
// needing a live Director, a real session, or a turn that happens to emit a table.
internal static class Program
{
    // The exact markdown that rendered as raw pipes in the bug screenshot, plus a
    // few more elements (heading, bold, bullets, inline + block code) to show breadth.
    private const string SampleMarkdown =
        "Claude triaged your uncommitted work into groups:\n\n" +
        "| Group | What it covers |\n" +
        "| --- | --- |\n" +
        "| A | Wingman auto-explain toggle + yellow translating state |\n" +
        "| B | FIFO voice queue + Hold (desktop + phone) |\n" +
        "| C | Android voice / TTS / dictation rewrite |\n" +
        "| D | cc-outlook move + create_folder + folders --ids |\n" +
        "| E | install-gateway-tray $args bugfix |\n\n" +
        "**A, B and C** are really one voice push and could collapse into a single commit. " +
        "`D` and `E` are independent.\n\n" +
        "- Delete the stray `_*.png` and `*.fixbak` files\n" +
        "- Add them to `.gitignore`\n" +
        "- Then commit the five groups\n\n" +
        "```bash\ngit add -A && git commit -m \"feat(wingman): ...\"\n```";

    private static string _outDir = "";

    [STAThread]
    private static void Main()
    {
        _outDir = IoPath.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(_outDir);

        AppBuilder.Configure<HarnessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Render("01_before_plain_textblock", BeforeCard(), 760, 360);
        Render("02_after_markdown", AfterCard(), 760, 460);
        Render("03_briefing_collapsed", Briefing(expanded: false), 760, 360);
        Render("04_briefing_expanded", Briefing(expanded: true), 760, 560);
        Render("05_speak_dialog", SpeakDialogContent(), 480, 300);

        Console.WriteLine("RENDER OK -> " + _outDir);
    }

    // ---- Scenes ------------------------------------------------------------

    // BEFORE: a plain TextBlock, the way Claude prose used to render. The pipe
    // table shows as literal characters (the reported bug).
    private static Control BeforeCard()
    {
        var body = new TextBlock
        {
            Text = SampleMarkdown,
            Foreground = Brush.Parse("#CCCCCC"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        return ClaudeCard("BEFORE - plain TextBlock (markdown shows as raw text)", body, "#7C2D12");
    }

    // AFTER: the real MarkdownScrollViewer, the same control now used in the
    // transcript and the briefing. The table, bold, bullets and code render.
    private static Control AfterCard()
    {
        var md = new Markdown.Avalonia.MarkdownScrollViewer
        {
            Markdown = SampleMarkdown,
            Margin = new Thickness(0, 8, 0, 0),
        };
        return ClaudeCard("AFTER - MarkdownScrollViewer (#3 fix)", md, "#2D7D46");
    }

    private static Border ClaudeCard(string caption, Control body, string accent)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Border
        {
            Background = Brush.Parse("#64748B"),
            CornerRadius = new CornerRadius(4),
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = "T",
                Foreground = Brushes.White,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        header.Children.Add(new TextBlock
        {
            Text = "Claude",
            Foreground = Brush.Parse("#E0E0E0"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var inner = new StackPanel();
        inner.Children.Add(header);
        inner.Children.Add(body);

        var card = new Border
        {
            Background = Brush.Parse("#252526"),
            BorderBrush = Brush.Parse("#3C3C3C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(8),
            Padding = new Thickness(12, 10),
            Child = inner,
        };

        var cap = new TextBlock
        {
            Text = caption,
            Foreground = Brush.Parse(accent),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(10, 8, 10, 0),
        };

        var wrap = new StackPanel();
        wrap.Children.Add(cap);
        wrap.Children.Add(card);
        return new Border { Background = Brush.Parse("#1E1E1E"), Child = wrap };
    }

    // The reordered briefing banner: headline -> orange WHAT CLAUDE WANTS ->
    // collapsed WHAT HAPPENED expander -> blue VOICE PREVIEW with a Play button.
    private static Control Briefing(bool expanded)
    {
        var stack = new StackPanel { Spacing = 10 };

        // Header row
        var headerRow = new DockPanel();
        var wing = new TextBlock
        {
            Text = "WINGMAN",
            Foreground = Brush.Parse("#5FD08A"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var meta = new TextBlock
        {
            Text = "opus  3s ago",
            Foreground = Brush.Parse("#888888"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(meta, Dock.Right);
        headerRow.Children.Add(meta);
        headerRow.Children.Add(wing);
        stack.Children.Add(headerRow);

        // Headline
        stack.Children.Add(new TextBlock
        {
            Text = "Waiting on which commit groups and approvals.",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        });

        // WHAT CLAUDE WANTS (orange) - now directly under the headline
        var whatNextInner = new StackPanel { Spacing = 4 };
        whatNextInner.Children.Add(new TextBlock
        {
            Text = "WHAT CLAUDE WANTS YOU TO DO NEXT",
            Foreground = Brush.Parse("#F59E0B"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
        });
        whatNextInner.Children.Add(new TextBlock
        {
            Text = "Tell me which groups to commit, and whether to collapse A+B+C. " +
                   "I also need a yes on the delete and gitignore actions before touching anything.",
            Foreground = Brushes.White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new Border
        {
            Background = Brush.Parse("#3A2A1B"),
            BorderBrush = Brush.Parse("#D97706"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = whatNextInner,
        });

        // WHAT HAPPENED - collapsed expander, quick line in the header, markdown detail in the body
        var expHeader = new StackPanel { Spacing = 2 };
        expHeader.Children.Add(new TextBlock
        {
            Text = "WHAT HAPPENED",
            Foreground = Brush.Parse("#5FD08A"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
        });
        expHeader.Children.Add(new TextBlock
        {
            Text = "Claude triaged the uncommitted work into commit / delete / ignore groups.",
            Foreground = Brush.Parse("#E5E5E5"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new Expander
        {
            IsExpanded = expanded,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Header = expHeader,
            Content = new Markdown.Avalonia.MarkdownScrollViewer
            {
                Markdown = SampleMarkdown,
                Margin = new Thickness(0, 6, 0, 0),
            },
        });

        // VOICE PREVIEW (blue) with the Play button
        var voiceInner = new StackPanel { Spacing = 6 };
        var voiceRow = new DockPanel();
        var playBtn = new Button
        {
            Background = Brush.Parse("#3B82F6"),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 3),
        };
        var playContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        playContent.Children.Add(new ShapePath { Data = Geometry.Parse("M0,0 L8,5 L0,10 Z"), Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        playContent.Children.Add(new TextBlock { Text = "Play", VerticalAlignment = VerticalAlignment.Center });
        playBtn.Content = playContent;
        DockPanel.SetDock(playBtn, Dock.Right);
        voiceRow.Children.Add(playBtn);
        voiceRow.Children.Add(new TextBlock
        {
            Text = "VOICE PREVIEW (what Play reads aloud)",
            Foreground = Brush.Parse("#3B82F6"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        voiceInner.Children.Add(voiceRow);
        voiceInner.Children.Add(new TextBlock
        {
            Text = "While you were away, Claude sorted your uncommitted work into groups. " +
                   "It is waiting before doing anything.",
            Foreground = Brush.Parse("#CCCCCC"),
            FontSize = 13,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new Border
        {
            Background = Brush.Parse("#1B2A3A"),
            BorderBrush = Brush.Parse("#3B82F6"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = voiceInner,
        });

        return new Border
        {
            Background = Brush.Parse("#16241B"),
            BorderBrush = Brush.Parse("#2D7D46"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 10),
            Child = stack,
        };
    }

    // Replica of the SpeakPlaybackDialog content (#1): status + read-along + Stop.
    private static Control SpeakDialogContent()
    {
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var headerStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 10) };
        headerStack.Children.Add(new TextBlock
        {
            Text = "Playing the briefing aloud",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#E6EAF2"),
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = "Speaking...",
            FontSize = 12,
            Foreground = Brush.Parse("#5FD08A"),
        });
        Grid.SetRow(headerStack, 0);
        grid.Children.Add(headerStack);

        var body = new Border
        {
            Background = Brush.Parse("#0A0E1A"),
            BorderBrush = Brush.Parse("#2A3550"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = "While you were away, Claude sorted your uncommitted work into groups: " +
                       "some to delete, a couple to gitignore, and the rest split into commits. " +
                       "It is waiting before doing anything.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse("#E6EAF2"),
                FontSize = 14,
                LineHeight = 21,
            },
        };
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        var stopBtn = new Button
        {
            Content = "Stop",
            Width = 100,
            Height = 32,
            Background = Brush.Parse("#B91C1C"),
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
        };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        btnRow.Children.Add(stopBtn);
        Grid.SetRow(btnRow, 2);
        grid.Children.Add(btnRow);

        return new Border { Background = Brush.Parse("#141B2E"), Child = grid };
    }

    // ---- Render plumbing ---------------------------------------------------

    private static void Render(string name, Control content, int width, int height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            SystemDecorations = SystemDecorations.None,
            Background = Brush.Parse("#1E1E1E"),
            Content = content,
        };

        window.Show();

        // Pump layout + force a render tick, then capture.
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var frame = window.CaptureRenderedFrame();
        var path = IoPath.Combine(_outDir, name + ".png");
        if (frame is null)
        {
            Console.WriteLine($"WARN: no frame for {name}");
        }
        else
        {
            frame.Save(path);
            Console.WriteLine($"saved {name}.png ({width}x{height})");
        }

        window.Close();
    }
}

internal sealed class HarnessApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }
}
