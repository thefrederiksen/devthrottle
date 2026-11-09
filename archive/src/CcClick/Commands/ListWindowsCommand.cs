using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using CcClick.Helpers;

namespace CcClick.Commands;

public static class ListWindowsCommand
{
    public static int Execute(AutomationBase automation, string? filter)
    {
        var windows = WindowFinder.FindWindows(automation, filter);

        var result = windows.Select(w =>
        {
            int processId = 0;
            string processName = "";
            try
            {
                processId = w.Properties.ProcessId.Value;
                processName = Process.GetProcessById(processId).ProcessName;
            }
            catch { }

            return new
            {
                title = w.Name ?? "",
                processName,
                processId,
                handle = SafeGetHandle(w)
            };
        }).ToArray();

        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions.Default));
        return 0;
    }

    private static long SafeGetHandle(FlaUI.Core.AutomationElements.AutomationElement e)
    {
        try { return e.Properties.NativeWindowHandle.ValueOrDefault.ToInt64(); }
        catch { return 0; }
    }
}
