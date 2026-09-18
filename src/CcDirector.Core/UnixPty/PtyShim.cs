using System.Runtime.Versioning;
using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.UnixPty;

/// <summary>
/// Provides the macOS controlling-terminal shim (<c>ccd-ptyshim</c>, see the header comment in
/// <c>ccd-ptyshim.c</c>) as an on-disk executable. The shim is compiled at build time and embedded
/// into this assembly - the Mac Director ships as a single self-contained file, so nothing can rely
/// on loose files next to the executable. This class extracts it on demand to a content-addressed
/// path under the storage root, so every Director version and build slot on the machine shares one
/// file per distinct shim build, and a stale copy can never be executed.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class PtyShim
{
    private const string ResourceName = "ccd-ptyshim";
    private static readonly object ExtractLock = new();

    /// <summary>
    /// Return the absolute path of an executable shim, extracting it from the embedded resource
    /// if it is not on disk yet. Throws when the resource is missing (a broken macOS build) or
    /// the extraction fails - a Director that cannot establish controlling terminals must fail
    /// loudly at spawn time, not silently produce the garbled-terminal behavior this shim fixes.
    /// </summary>
    public static string EnsurePresent()
    {
        var assembly = typeof(PtyShim).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "Embedded resource 'ccd-ptyshim' is missing from CcDirector.Core. " +
                "This assembly was not built on macOS; the macOS PTY backend cannot run without it.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var shimBytes = memory.ToArray();

        // Content-addressed file name: same bytes -> same path, new shim build -> new path.
        var hash = Convert.ToHexString(SHA256.HashData(shimBytes))[..16].ToLowerInvariant();
        // The machine's tool directory, not this Director's folder: the shim is one file per set of
        // bytes and every Director on the machine can use the same one.
        var binDir = CcStorage.Bin();
        var shimPath = Path.Combine(binDir, $"{ResourceName}-{hash}");

        if (File.Exists(shimPath))
            return shimPath;

        lock (ExtractLock)
        {
            if (File.Exists(shimPath))
                return shimPath;

            FileLog.Write($"[PtyShim] EnsurePresent: extracting {shimBytes.Length} bytes to {shimPath}");
            Directory.CreateDirectory(binDir);

            // Write to a private temp name, set the execute bit, then rename into place.
            // The rename is atomic on the same volume, so a concurrent Director process
            // either wins the rename or sees the winner's finished file - never a torn one.
            var tempPath = Path.Combine(binDir, $".{ResourceName}-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tempPath, shimBytes);
                File.SetUnixFileMode(tempPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(tempPath, shimPath);
            }
            catch (IOException) when (File.Exists(shimPath))
            {
                // Another process finished the rename first; its file is identical by construction.
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[PtyShim] EnsurePresent FAILED: {ex.Message}");
                try { File.Delete(tempPath); } catch { /* best-effort cleanup of the temp file */ }
                throw;
            }

            FileLog.Write($"[PtyShim] EnsurePresent: shim ready at {shimPath}");
            return shimPath;
        }
    }
}
