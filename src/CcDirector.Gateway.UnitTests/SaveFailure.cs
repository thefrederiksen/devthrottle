namespace CcDirector.Gateway.Tests;

/// <summary>
/// Makes a file's next save FAIL, the same way on Windows, Linux and macOS, for a test of what happens when a save fails.
///
/// WHY NOT A LOCK OR A READ-ONLY FILE. The stores these tests drive save by writing <c>&lt;file&gt;.tmp</c> and then
/// renaming it over the file. Holding the file open with no sharing, or marking it read-only, stops that only on
/// Windows: on Linux and macOS a rename replaces a file whatever its mode, and a no-sharing open is advisory. So those
/// tests passed on Windows and failed on Linux on correct code. A DIRECTORY standing where the temporary file is written
/// makes the write throw on every system, and taking it away lets the next save through.
/// </summary>
internal sealed class SaveFailure : IDisposable
{
    private readonly string _blocker;

    private SaveFailure(string blocker) => _blocker = blocker;

    /// <summary>Block the next save of <paramref name="path"/> until the returned object is disposed.</summary>
    /// <param name="path">The file the store saves to; its temporary file is <c>&lt;path&gt;.tmp</c>.</param>
    public static SaveFailure Block(string path)
    {
        var blocker = path + ".tmp";
        Directory.CreateDirectory(blocker);
        return new SaveFailure(blocker);
    }

    /// <summary>Let saves through again.</summary>
    public void Dispose() => Directory.Delete(_blocker);
}
