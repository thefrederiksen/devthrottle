namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// Which door the owner came through (mission 10.1). The two doors open the same dialog; the only
/// difference is the title and the confirm button's words.
/// </summary>
public enum SmartShutdownDoor
{
    /// <summary>The main window is closing: the dialog says "Smart shutdown".</summary>
    WindowClose,

    /// <summary>File, Smart Restart: the dialog says "Smart Restart".</summary>
    FileMenu,
}
