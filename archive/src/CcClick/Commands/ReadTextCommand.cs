using System.Text.Json;
using FlaUI.Core;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ReadTextCommand
{
    public static int Execute(AutomationBase automation, string windowTitle, string? name, string? id)
    {
        var window = WindowFinder.FindWindow(automation, windowTitle);
        var element = ElementFinder.FindElement(automation, window, name, id);

        // Try multiple ways to read text
        string text = "";

        // 1. Value pattern (text boxes, combo boxes)
        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern != null)
        {
            text = valuePattern.Value.ValueOrDefault ?? "";
        }

        // 2. Text pattern (rich text controls)
        if (string.IsNullOrEmpty(text))
        {
            var textPattern = element.Patterns.Text.PatternOrDefault;
            if (textPattern != null)
            {
                text = textPattern.DocumentRange.GetText(-1) ?? "";
            }
        }

        // 3. Fall back to Name property
        if (string.IsNullOrEmpty(text))
        {
            text = element.Name ?? "";
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            name = element.Name ?? "",
            automationId = element.AutomationId ?? "",
            text
        }, JsonOptions.Default));
        return 0;
    }
}
