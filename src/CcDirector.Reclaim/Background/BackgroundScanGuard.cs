using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Background;

/// <summary>
/// The guard against a second background scan starting while one runs.
///
/// There is one disk however many programs are running on it, so two scans at once is the same disk
/// read twice over for nothing. The guard is a file held open for the length of the scan and shared
/// with nobody: the operating system refuses the second opening, in this process or in any other, and
/// lets go of the file by itself when the process holding it goes away - so a scan that crashed can
/// never leave the machine locked out of scanning, which a marker file written and deleted by hand
/// would.
/// </summary>
public sealed class BackgroundScanGuard : IDisposable
{
    /// <summary>The name of the guard file inside a store folder.</summary>
    public const string GuardFileName = "background-scan.guard";

    private readonly FileStream _held;

    private BackgroundScanGuard(FileStream held) => _held = held;

    /// <summary>
    /// Take the guard, or return null when a scan already holds it.
    /// </summary>
    /// <param name="storeDirectory">The store folder every scan of this machine writes into.</param>
    public static BackgroundScanGuard? TryAcquire(string storeDirectory)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("A store directory cannot be blank.", nameof(storeDirectory));

        Directory.CreateDirectory(storeDirectory);
        var guardPath = Path.Combine(storeDirectory, GuardFileName);

        try
        {
            var held = new FileStream(guardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            FileLog.Write($"[BackgroundScanGuard] TryAcquire: taken, guard={guardPath}");
            return new BackgroundScanGuard(held);
        }
        catch (IOException ex) when (ex is not DirectoryNotFoundException and not FileNotFoundException
                                     && File.Exists(guardPath))
        {
            // The file is there and would not open: somebody holds it. Any other failure - no folder,
            // no permission - is not "a scan is running" and is not caught here.
            FileLog.Write($"[BackgroundScanGuard] TryAcquire: refused, a scan already holds {guardPath}");
            return null;
        }
    }

    /// <summary>Let go of the guard.</summary>
    public void Dispose()
    {
        _held.Dispose();
        FileLog.Write("[BackgroundScanGuard] Dispose: released");
    }
}
