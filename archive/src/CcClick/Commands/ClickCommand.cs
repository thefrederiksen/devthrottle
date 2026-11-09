using System.Drawing;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.Input;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ClickCommand
{
    public static int Execute(AutomationBase automation, string? windowTitle, string? name, string? id, string? xy, bool rightClick = false)
    {
        if (!string.IsNullOrEmpty(xy))
        {
            var parts = xy.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var x) || !int.TryParse(parts[1].Trim(), out var y))
                throw new InvalidOperationException("--xy must be in format \"x,y\" (e.g. \"500,300\")");

            if (rightClick)
                Mouse.Click(new Point(x, y), MouseButton.Right);
            else
                Mouse.Click(new Point(x, y));
            Console.WriteLine(JsonSerializer.Serialize(new { clicked = "xy", x, y, rightClick }, JsonOptions.Default));
            return 0;
        }

        if (string.IsNullOrEmpty(windowTitle))
            throw new InvalidOperationException("--window is required unless --xy is used");

        var window = WindowFinder.FindWindow(automation, windowTitle);
        var element = ElementFinder.FindElement(automation, window, name, id);

        if (rightClick)
            element.RightClick();
        else
            element.Click();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            clicked = element.Name ?? element.AutomationId ?? "element",
            automationId = element.AutomationId ?? "",
            name = element.Name ?? ""
        }, JsonOptions.Default));
        return 0;
    }
}
