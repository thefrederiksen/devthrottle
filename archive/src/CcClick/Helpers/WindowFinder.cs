using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;

namespace CcClick.Helpers;

public static class WindowFinder
{
    /// <summary>
    /// Find all visible top-level windows, optionally filtered by title substring.
    /// </summary>
    public static AutomationElement[] FindWindows(AutomationBase automation, string? filter = null)
    {
        var desktop = automation.GetDesktop();
        var allChildren = desktop.FindAllChildren();

        var windows = allChildren
            .Where(e => e.ControlType == ControlType.Window)
            .Where(e => !IsOffscreen(e))
            .Where(e => !string.IsNullOrEmpty(e.Name));

        if (!string.IsNullOrEmpty(filter))
        {
            windows = windows.Where(e =>
                e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return windows.ToArray();
    }

    /// <summary>
    /// Find a single window by title substring. Throws if not found or ambiguous.
    /// </summary>
    public static AutomationElement FindWindow(AutomationBase automation, string title)
    {
        var matches = FindWindows(automation, title);

        if (matches.Length == 0)
            throw new InvalidOperationException($"No window found matching \"{title}\"");

        if (matches.Length > 1)
        {
            // Prefer exact match
            var exact = matches.FirstOrDefault(e =>
                e.Name.Equals(title, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var names = string.Join(", ", matches.Select(e => $"\"{e.Name}\""));
            throw new InvalidOperationException(
                $"Multiple windows match \"{title}\": {names}. Be more specific.");
        }

        return matches[0];
    }

    private static bool IsOffscreen(AutomationElement e)
    {
        try { return e.IsOffscreen; }
        catch { return false; }
    }
}
