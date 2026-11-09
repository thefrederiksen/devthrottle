using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.Capturing;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ScreenshotCommand
{
    public static int Execute(AutomationBase automation, string? windowTitle, string output)
    {
        CaptureImage capture;

        if (!string.IsNullOrEmpty(windowTitle))
        {
            var window = WindowFinder.FindWindow(automation, windowTitle);
            capture = Capture.Element(window);
        }
        else
        {
            capture = Capture.MainScreen();
        }

        var fullPath = Path.GetFullPath(output);
        capture.ToFile(fullPath);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            saved = fullPath,
            width = capture.Bitmap.Width,
            height = capture.Bitmap.Height
        }, JsonOptions.Default));
        return 0;
    }
}
