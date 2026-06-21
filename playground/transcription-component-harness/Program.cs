using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CcDirector.Avalonia.Controls;
using IoPath = System.IO.Path;

namespace TranscriptionComponentHarness;

// Headless + Skia render harness for the shared transcription component (issue #588).
//
// It instantiates the REAL CcDirector.Avalonia.Controls.TranscriptionComponent in
// each variant + state and captures each to a PNG. This proves the actual shipping
// control (not a replica) renders every state of both variants without needing a
// live Director, an interactive desktop, or any audio capture.
internal static class Program
{
    private const string SampleText =
        "Set up a research agent to check every place we run transcription, and make sure " +
        "cc-director uses the same flow everywhere. Do not change my wording, only fix the " +
        "dictionary terms.";

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

        // Full variant - the states the proof target names plus error.
        Render("full-recording", FullComponent(c => DriveRecording(c)), 560, 480);
        Render("full-transcribing", FullComponent(c => c.ShowTranscribing()), 560, 320);
        Render("full-review", FullComponent(c => c.ShowReview(SampleText, 2)), 560, 380);
        Render("full-error", FullComponent(c => c.ShowError("Microphone not available")), 560, 320);

        // Small variant - recording and transcribing (the proof target).
        Render("small-recording", SmallComponent(c => DriveRecording(c)), 520, 80);
        Render("small-transcribing", SmallComponent(c => c.ShowTranscribing()), 520, 80);

        Console.WriteLine("RENDER OK -> " + _outDir);
    }

    private static TranscriptionComponent FullComponent(Action<TranscriptionComponent> drive)
    {
        var c = new TranscriptionComponent { Variant = TranscriptionVariant.Full };
        c.SetMicrophones(
            new List<string> { "Default - Microphone Array (Realtek)", "Headset Microphone" },
            "Default - Microphone Array (Realtek)");
        drive(c);
        return c;
    }

    private static TranscriptionComponent SmallComponent(Action<TranscriptionComponent> drive)
    {
        var c = new TranscriptionComponent { Variant = TranscriptionVariant.Small };
        drive(c);
        return c;
    }

    private static void DriveRecording(TranscriptionComponent c)
    {
        c.ShowRecording();
        c.UpdateTimer(TimeSpan.FromSeconds(12.4));
        // A fixed, plausible waveform so the screenshot is stable run to run.
        double[] pattern = { 0.30, 0.55, 0.85, 0.65, 0.95, 0.48, 0.78, 0.36, 0.62, 0.90, 0.42, 0.72, 0.33, 0.82, 0.52 };
        c.UpdateLevels(pattern);
    }

    private static void Render(string name, Control content, int width, int height)
    {
        var host = new Border
        {
            Background = Brush.Parse("#1E1E1E"),
            Padding = new Thickness(20),
            Child = content,
        };

        var window = new Window
        {
            Width = width,
            Height = height,
            SystemDecorations = SystemDecorations.None,
            Background = Brush.Parse("#1E1E1E"),
            Content = host,
        };

        window.Show();

        // Pump layout + force a render tick, then capture.
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var frame = window.CaptureRenderedFrame();
        var path = IoPath.Combine(_outDir, name + ".png");
        if (frame is null)
            Console.WriteLine($"WARN: no frame for {name}");
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
