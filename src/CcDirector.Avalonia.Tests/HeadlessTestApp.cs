using System;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using CcDirector.Avalonia.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Minimal Avalonia headless app so [AvaloniaFact] tests can construct real controls (the onboarding
/// wizard's Skip seam, issue #1809). Real drawing is ON (Skia, headless drawing off) so a test can call
/// CaptureRenderedFrame() and get a picture of the window rather than a blank frame; tests that assert
/// only side effects are unaffected.
/// </summary>
internal sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // THE APPLICATION'S OWN BUTTON POLICY, loaded from the file App.axaml loads. Without this the
        // harness renders buttons the Fluent theme's way and a test asserting our policy proves nothing
        // about the shipped application - it would pass on a build where the policy had been deleted.
        Styles.Add(new StyleInclude(new Uri("avares://cc-director/Styles/"))
        {
            Source = new Uri("avares://cc-director/Styles/ButtonStyles.axaml"),
        });
        // The brushes App.axaml defines, so a real MainWindow can be constructed and driven here (ruling
        // R20's compose-box route). Same keys, same values; a window that reads one it does not find
        // gets Avalonia's unset marker, which is not a brush.
        Resources["PanelBackground"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#1E1E1E"));
        Resources["SidebarBackground"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#252526"));
        Resources["ButtonBackground"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#3C3C3C"));
        Resources["ButtonHover"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#505050"));
        Resources["TextForeground"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#CCCCCC"));
        Resources["AccentBrush"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#007ACC"));
        Resources["SelectedItemBrush"] = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#094771"));
    }
}

internal static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
