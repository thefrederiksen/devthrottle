using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.Input;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class TypeTextCommand
{
    public static int Execute(AutomationBase automation, string windowTitle, string? name, string? id, string text)
    {
        var window = WindowFinder.FindWindow(automation, windowTitle);
        var element = ElementFinder.FindElement(automation, window, name, id);

        // Try the Value pattern first (most reliable for text inputs)
        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern != null)
        {
            valuePattern.SetValue(text);
        }
        else
        {
            // Fall back to focus + keyboard
            element.Focus();
            Keyboard.Type(text);
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            typed = text,
            target = element.Name ?? element.AutomationId ?? "element"
        }, JsonOptions.Default));
        return 0;
    }
}
