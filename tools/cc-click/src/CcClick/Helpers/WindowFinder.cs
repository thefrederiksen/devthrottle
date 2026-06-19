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
    /// Resolve a single target window. When <paramref name="pid"/> is set it wins (an
    /// exact, unambiguous match even when several windows share a title - e.g. multiple
    /// CC Director instances). Otherwise falls back to the title substring.
    /// </summary>
    public static AutomationElement Resolve(AutomationBase automation, string? title, int? pid)
    {
        if (pid is int p && p > 0)
            return FindWindowByPid(automation, p);
        if (!string.IsNullOrEmpty(title))
            return FindWindow(automation, title);
        throw new InvalidOperationException("Either --window or --pid is required");
    }

    /// <summary>
    /// Find the visible top-level window owned by a given process id. Throws if none.
    /// When a process has several top-level windows, prefers a named one (the main window).
    /// </summary>
    public static AutomationElement FindWindowByPid(AutomationBase automation, int pid)
    {
        var desktop = automation.GetDesktop();
        var matches = desktop.FindAllChildren()
            .Where(e => e.ControlType == ControlType.Window)
            .Where(e => !IsOffscreen(e))
            .Where(e => SafeProcessId(e) == pid)
            .ToArray();

        if (matches.Length == 0)
            throw new InvalidOperationException($"No visible window found for process id {pid}");

        var named = matches.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
        return named ?? matches[0];
    }

    private static int SafeProcessId(AutomationElement e)
    {
        try { return e.Properties.ProcessId.Value; }
        catch { return -1; }
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
