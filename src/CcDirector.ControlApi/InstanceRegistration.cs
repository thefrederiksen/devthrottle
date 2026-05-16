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

    public string DirectorId { get; }
    public int Port { get; }
    public string FilePath { get; }
    public DirectorDto Dto { get; }

    private bool _disposed;

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

    /// <summary>Write the registration file. Idempotent (overwrites). Logs every step.</summary>
    public void Register()
    {
        FileLog.Write($"[InstanceRegistration] Register: id={DirectorId}, port={Port}, file={FilePath}");
        try
        {
            Directory.CreateDirectory(InstancesDirectory);
            var json = JsonSerializer.Serialize(Dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
            FileLog.Write($"[InstanceRegistration] Register: wrote {json.Length} bytes");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InstanceRegistration] Register FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Delete the registration file. Safe to call multiple times.</summary>
    public void Unregister()
    {
        if (_disposed) return;
        FileLog.Write($"[InstanceRegistration] Unregister: file={FilePath}");
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
