using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ListElementsCommand
{
    public static int Execute(AutomationBase automation, string windowTitle, string? type, int depth)
    {
        var window = WindowFinder.FindWindow(automation, windowTitle);

        ControlType? controlType = null;
        if (!string.IsNullOrEmpty(type))
        {
            if (Enum.TryParse<ControlType>(type, ignoreCase: true, out var ct))
                controlType = ct;
            else
                throw new InvalidOperationException(
                    $"Unknown control type \"{type}\". Valid types: {string.Join(", ", Enum.GetNames<ControlType>())}");
        }

        var elements = ElementFinder.FindAll(automation, window, controlType, depth);

        var result = elements.Select(e => new
        {
            name = Safe(() => e.Name) ?? "",
            automationId = Safe(() => e.AutomationId) ?? "",
            controlType = Safe(() => e.ControlType.ToString()) ?? "Unknown",
            boundingRect = FormatRect(e.BoundingRectangle)
        }).ToArray();

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions.Default));
        return 0;
    }

    private static object FormatRect(System.Drawing.Rectangle r)
    {
        return new { x = r.X, y = r.Y, width = r.Width, height = r.Height };
    }

    private static string? Safe(Func<string?> accessor)
    {
        try { return accessor(); }
        catch { return null; }
    }
}
