using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// The wizard's view of "is DevThrottle already installed on this machine?".
/// Delegates to the shared engine (InstallLayout + InstalledStateReader) - the same
/// source of truth the command-line installer uses - so the wizard and the engine can
/// never disagree about the machine. The wizard previously kept its own File.Exists
/// check on &lt;root&gt;/bin/cc-director, a path that does not exist in the macOS layout
/// (the Director there is the "Director.app" bundle in the Applications folder), so every Mac
/// opened in fresh-install mode with update and repair unreachable (issue #1736).
/// </summary>
public static class InstallDetector
{
    public static bool IsInstalled() => ReadDirector().Present;

    public static string? GetInstalledVersion() => ReadDirector().Version;

    private static InstalledComponent ReadDirector()
    {
        var layout = InstallLayout.Default();
        return new InstalledStateReader(layout).Read(ComponentRegistry.Director);
    }
}
