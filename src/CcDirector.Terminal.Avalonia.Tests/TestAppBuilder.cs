using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using CcDirector.Terminal.Avalonia.Tests;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Avalonia headless app for the render tests. UseHeadlessDrawing = false routes drawing through
/// real Skia so CaptureRenderedFrame produces actual pixels we can assert on.
/// </summary>
internal sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

internal static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
