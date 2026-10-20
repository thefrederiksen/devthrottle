namespace CcDirector.Setup.Engine;

/// <summary>
/// Which folder a macOS install puts the Director bundle in - and therefore where the DevThrottle
/// icon appears.
///
/// It belongs in <c>/Applications</c>. That is the folder every Mac user means by "Applications": it
/// is what the Finder sidebar's Applications entry opens, what the Applications view of Spotlight
/// lists, and where a person looks for an app they have installed. The install used to place the
/// bundle in <c>~/Applications</c> only, mirroring the per-user Windows install, and the result was an
/// install with no icon anywhere a person looks - Spotlight could find it by name, and the Finder's
/// Applications folder was empty. Linux already had the equivalent step (<see cref="LinuxDesktopEntry"/>
/// writes the application-menu entry); macOS had none.
///
/// <c>/Applications</c> is owned by root and writable by the <c>admin</c> group, so an administrator -
/// which the person installing their own Mac almost always is - writes it with no password and no
/// elevation. A person who is not an administrator cannot, and for them <c>~/Applications</c> is still
/// the right answer: it needs no permission, it is still listed by Spotlight and the Applications view,
/// and an install that stops dead because the person is a standard user would be worse than an install
/// whose icon is one folder away. So the choice is made by ASKING THE FILE SYSTEM, not by guessing from
/// group membership: the install tries the write it is about to do for real.
///
/// The choice is a pure function of that probe so a test can take either branch on any machine. The
/// probe itself (<see cref="CanCreateDirectoryIn"/>) is the only part that touches the disk.
/// </summary>
public static class MacAppsFolder
{
    /// <summary>
    /// The folder a Mac user means by "Applications". An absolute path, not composed from the home
    /// folder - it is the same path for every account on the machine.
    /// </summary>
    public const string SystemAppsDir = "/Applications";

    /// <summary>
    /// <paramref name="systemAppsDir"/> when this user can create a bundle in it, otherwise
    /// <paramref name="userAppsDir"/>.
    /// </summary>
    /// <param name="systemAppsDir">Normally <see cref="SystemAppsDir"/>.</param>
    /// <param name="userAppsDir">The per-user fallback (<c>~/Applications</c>).</param>
    /// <param name="canWrite">
    /// Answers whether a folder can be written - <see cref="CanCreateDirectoryIn"/> in production, a
    /// decision the test makes in a test.
    /// </param>
    public static string Choose(string systemAppsDir, string userAppsDir, Func<string, bool> canWrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemAppsDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAppsDir);
        ArgumentNullException.ThrowIfNull(canWrite);

        return canWrite(systemAppsDir) ? systemAppsDir : userAppsDir;
    }

    /// <summary>
    /// Can this process create a directory inside <paramref name="dir"/>? Answered by creating one and
    /// removing it again, because that is the operation the install performs: a macOS application
    /// bundle is a directory, so a probe that wrote a FILE would answer a question nobody asked, and
    /// reading the mode bits and the group list would be a guess about what the kernel will allow.
    ///
    /// A folder that does not exist answers false. <c>/Applications</c> exists on every Mac, and
    /// creating it when it is missing would be repairing the operating system, not installing an app.
    /// Every failure answers false: the caller's fallback is a folder that always works, so there is
    /// no failure here worth stopping an install over.
    /// </summary>
    public static bool CanCreateDirectoryIn(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);

        if (!Directory.Exists(dir)) return false;

        // A leading dot keeps the probe out of the Finder's and the Applications view's way for the
        // moment it exists, and the identifier keeps two installs running at once from colliding.
        var probe = Path.Combine(dir, $".devthrottle-write-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(probe);
        }
        catch (Exception ex)
        {
            // not-an-error: this is a write probe and a refusal is its answer; the caller installs into the user's own Applications folder
            EngineLog.Write($"[MacAppsFolder] cannot write {dir}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        try
        {
            Directory.Delete(probe);
        }
        catch (Exception ex)
        {
            // The write succeeded, which is the question that was asked. A probe left behind is a
            // hidden empty folder, so it is logged and nothing more.
            EngineLog.Write($"[MacAppsFolder] could not remove the write probe {probe} FAILED: {ex.Message}");
        }

        return true;
    }
}
