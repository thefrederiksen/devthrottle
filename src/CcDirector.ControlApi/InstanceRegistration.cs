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
    public static string InstancesDirectory { get; } = CcStorage.DirectorInstances();

    /// <summary>How often the heartbeat re-writes the instance file. Short enough that
    /// accidental cleanups self-heal quickly; long enough not to thrash the disk.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(15);

    public string DirectorId { get; }
    public string FilePath { get; }
    public DirectorDto Dto { get; }

    private readonly string _instancesDirectory;
    private bool _disposed;
    private readonly CcDirector.Core.Background.BackgroundJobs _jobs;
    private CcDirector.Core.Background.BackgroundJob? _heartbeat;

    /// <param name="instancesDirectory">
    /// Override the shared instances directory. Tests pass an isolated temp directory so test
    /// Directors never appear in a real Gateway's discovery (and vice versa). Production omits it.
    /// </param>
    public InstanceRegistration(string directorId, string version, string? instancesDirectory = null,
        string? displayName = null, CcDirector.Core.Background.BackgroundJobs? jobs = null)
    {
        _jobs = jobs ?? CcDirector.Core.Background.BackgroundJobs.Default;
        DirectorId = directorId;
        _instancesDirectory = instancesDirectory ?? InstancesDirectory;
        FilePath = Path.Combine(_instancesDirectory, $"{directorId}.json");

        Dto = new DirectorDto
        {
            DirectorId = directorId,
            Pid = Environment.ProcessId,
            StartedAt = DateTime.UtcNow,
            // Remove-the-network-port mission, phase 5: there is no control endpoint. The Director
            // listens on nothing; a reader that wants to ACT on this Director uses the process id
            // and the named lifecycle signals, and everything else goes through the Gateway. Empty
            // (not absent) so old readers of this file deserialize cleanly.
            ControlEndpoint = "",
            MachineName = Environment.MachineName,
            User = Environment.UserName,
            // devthrottle_internal#1176: the instance's editable display name, so file-discovered
            // Directors carry the same label a tunnel Hello reports. Empty = unnamed.
            DisplayName = displayName?.Trim() ?? "",
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
        FileLog.Write($"[InstanceRegistration] Register: id={DirectorId}, file={FilePath}");
        try
        {
            WriteOnce();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[InstanceRegistration] Register FAILED: {ex.Message}");
            throw;
        }

        // A slow-and-steady job on the scheduler (docs/BackgroundWork.md): re-writes the file only when it is missing.
        _heartbeat = _jobs.Register(
            new CcDirector.Core.Background.BackgroundJobSpec("Instance registration heartbeat", CcDirector.Core.Background.BackgroundJobTier.SlowAndSteady, HeartbeatInterval, "the registration is disposed"),
            _ => { HeartbeatTick(); return Task.CompletedTask; });
        _heartbeat.StartTimer();
    }

    private void WriteOnce()
    {
        Directory.CreateDirectory(_instancesDirectory);
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
