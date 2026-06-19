using System.Drawing;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ClickCommand
{
    /// <summary>
    /// Invoke an element robustly. A label found by --name is often a TextBlock nested
    /// inside a Button (the Button carries the click handler, not the text). So prefer
    /// the Invoke pattern on the element or its nearest ancestor that supports it;
    /// only fall back to a physical mouse click when nothing on the chain is invokable.
    /// </summary>
    private static void SmartClick(AutomationElement element)
    {
        var cur = element;
        for (int i = 0; i < 8 && cur != null; i++)
        {
            try
            {
                if (cur.Patterns.Invoke.IsSupported)
                {
                    cur.Patterns.Invoke.Pattern.Invoke();
                    return;
                }
            }
            catch { /* property/pattern not supported on this node - keep climbing */ }
            try { cur = cur.Parent; } catch { break; }
        }
        element.Click();
    }

    private static string SafeName(AutomationElement e)
    {
        try { return e.Name ?? ""; } catch { return ""; }
    }

    private static string SafeAutomationId(AutomationElement e)
    {
        try { return e.AutomationId ?? ""; } catch { return ""; }
    }

    public static int Execute(AutomationBase automation, string? windowTitle, int? pid, string? name, string? id, string? xy)
    {
        if (!string.IsNullOrEmpty(xy))
        {
            // Click at absolute screen coordinates
            var parts = xy.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var x) || !int.TryParse(parts[1].Trim(), out var y))
                throw new InvalidOperationException("--xy must be in format \"x,y\" (e.g. \"500,300\")");

            Mouse.Click(new Point(x, y));
            Console.WriteLine(JsonSerializer.Serialize(new { clicked = "xy", x, y }, JsonOptions.Default));
            return 0;
        }

        if (string.IsNullOrEmpty(windowTitle) && !(pid is int pp && pp > 0))
            throw new InvalidOperationException("--window or --pid is required unless --xy is used");

        var window = WindowFinder.Resolve(automation, windowTitle, pid);
        var element = ElementFinder.FindElement(automation, window, name, id);

        var elName = SafeName(element);
        var elId = SafeAutomationId(element);
        SmartClick(element);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            clicked = !string.IsNullOrEmpty(elName) ? elName : (!string.IsNullOrEmpty(elId) ? elId : "element"),
            automationId = elId,
            name = elName
        }, JsonOptions.Default));
        return 0;
    }
}
