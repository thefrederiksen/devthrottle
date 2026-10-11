using CcDirector.Core.Utilities;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching contract 2.4: several Directors can run on one machine as one operating system user, and they
/// share one default Claude config. Exactly one of them is the KEY HOLDER - the one holding an exclusive lock on a
/// file in the key store's folder, opened with no sharing and kept open for the life of the process. Only the key
/// holder reports usage and executes a switch.
///
/// The operating system releases the lock when the holding process exits, however it exits, so a crashed holder
/// never leaves the machine without one: the next Director to try takes it.
/// </summary>
public sealed class LicenseKeyHolderLock : IDisposable
{
    public const string FileName = "key-holder.lock";

    private readonly string _path;
    private readonly object _gate = new();
    private FileStream? _held;
    private bool _disposed;

    public LicenseKeyHolderLock(string directorFolder)
    {
        if (string.IsNullOrWhiteSpace(directorFolder)) throw new ArgumentException("A folder is required", nameof(directorFolder));
        _path = Path.Combine(directorFolder, FileName);
    }

    /// <summary>True while this process holds the lock.</summary>
    public bool IsHeld
    {
        get { lock (_gate) return _held is not null; }
    }

    /// <summary>
    /// Takes the lock when no other process holds it. Returns true only on the call that took it (false when it was
    /// already held by this process, or another process holds it), so the caller can send its "connected" report
    /// exactly once per take.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_disposed || _held is not null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            try
            {
                _held = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another Director holds it. That is the normal answer for every Director but one.
                return false;
            }
            WriteHolder(_held);
            FileLog.Write($"[LicenseKeyHolderLock] TryAcquire: this Director is the key holder (pid={Environment.ProcessId}, file={_path})");
            return true;
        }
    }

    /// <summary>Records who holds it, for a person reading the folder. Nothing reads this back.</summary>
    private static void WriteHolder(FileStream stream)
    {
        var text = System.Text.Encoding.UTF8.GetBytes($"pid={Environment.ProcessId} since={DateTime.UtcNow:O}\n");
        stream.SetLength(0);
        stream.Write(text);
        stream.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_held is null) return;
            _held.Dispose();
            _held = null;
            FileLog.Write("[LicenseKeyHolderLock] Dispose: released the key holder lock");
        }
    }
}
