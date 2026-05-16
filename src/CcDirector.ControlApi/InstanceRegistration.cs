using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Writes this Director's metadata to the shared instances directory so the
/// CC Director Gateway can discover it. Removes the file on dispose.
///
/// File location:
///     %LOCALAPPDATA%\cc-director\config\director\instances\{directorId}.json
///
/// Other Directors and the Gateway watch this directory with a FileSystemWatcher.
/// </summary>
public sealed class InstanceRegistration : IDisposable
{
    public static string InstancesDirectory { get; } =
        Path.Combine(CcStorage.Config(), "director", "instances");

    /// <summary>How often the heartbeat re-writes the instance file. Short enough that
    /// accidental cleanups self-heal quickly; long enough not to thrash the disk.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(15);

    public string DirectorId { get; }
    public int Port { get; }
    public string FilePath { get; }
    public DirectorDto Dto { get; }

    private bool _disposed;
    private Timer? _heartbeat;

    public InstanceRegistration(string directorId, int port, string version)
    {
        DirectorId = directorId;
        Port = port;
        FilePath = Path.Combine(InstancesDirectory, $"{directorId}.json");

        Dto = new DirectorDto
        {
            DirectorId = directorId,
            Pid = Environment.ProcessId,
            StartedAt = DateTime.UtcNow,
            ControlEndpoint = $"http://127.0.0.1:{port}",
            MachineName = Environment.MachineName,
            User = Environment.UserName,
            Version = version,
            SchemaVersion = 1,
        };
    }

    /// <summary>Write the registration file once and start a heartbeat timer that
    /// re-writes it every <see cref="HeartbeatInterval"/>. The heartbeat means that
    /// if the file is accidentally deleted (operator cleanup, antivirus, etc.) it
    /// re-appears within ~15 seconds, so the Gateway re-discovers this Director.</summary>
    public void Register()
    {
        FileLog.Write($"[InstanceRegistration] Register: id={DirectorId}, port={Port}, file={FilePath}");
        try
        {
            WriteOnce();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InstanceRegistration] Register FAILED: {ex.Message}");
            throw;
        }

        _heartbeat = new Timer(_ => HeartbeatTick(), null, HeartbeatInterval, HeartbeatInterval);
    }

    private void WriteOnce()
    {
        Directory.CreateDirectory(InstancesDirectory);
        var json = JsonSerializer.Serialize(Dto, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    private void HeartbeatTick()
    {
        if (_disposed) return;
        try
        {
            // Only re-write if the file is missing (e.g. someone wiped the directory).
            // Avoids needless disk churn on a healthy file every interval.
            if (!File.Exists(FilePath))
            {
                FileLog.Write($"[InstanceRegistration] Heartbeat: file missing, re-writing {FilePath}");
                WriteOnce();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InstanceRegistration] Heartbeat FAILED: {ex.Message}");
        }
    }

    /// <summary>Delete the registration file. Safe to call multiple times.</summary>
    public void Unregister()
    {
        if (_disposed) return;
        FileLog.Write($"[InstanceRegistration] Unregister: file={FilePath}");
        _heartbeat?.Dispose();
        _heartbeat = null;
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
            FileLog.Write($"[InstanceRegistration] Unregister: done");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InstanceRegistration] Unregister FAILED: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unregister();
        _disposed = true;
    }
}
