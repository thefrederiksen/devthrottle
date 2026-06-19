using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.Definitions;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ListElementsCommand
{
    public static int Execute(AutomationBase automation, string? windowTitle, int? pid, string? type, int depth)
    {
        var window = WindowFinder.Resolve(automation, windowTitle, pid);

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
            controlType = Safe(() => e.ControlType.ToString()) ?? "",
            boundingRect = SafeRect(e)
        }).ToArray();

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions.Default));
        return 0;
    }

    private static object FormatRect(System.Drawing.Rectangle r)
    {
        return new { x = r.X, y = r.Y, width = r.Width, height = r.Height };
    }

    private static string? Safe(Func<string?> read)
    {
        try { return read(); } catch { return null; }
    }

    private static object SafeRect(FlaUI.Core.AutomationElements.AutomationElement e)
    {
        try { return FormatRect(e.BoundingRectangle); }
        catch { return new { x = 0, y = 0, width = 0, height = 0 }; }
    }
}
