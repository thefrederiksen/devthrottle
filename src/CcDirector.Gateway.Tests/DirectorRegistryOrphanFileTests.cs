using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #891: the stale sweeper must complete an orphan instance-file delete that failed once,
/// so a dead Director's file cannot linger on disk and resurrect as a phantom on the next restart.
/// </summary>
public sealed class DirectorRegistryOrphanFileTests
{
    private static string WriteInstanceFile(string dir, string id, int pid)
    {
        var path = Path.Combine(dir, $"{id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new DirectorDto
        {
            DirectorId = id,
            Pid = pid,
            ControlEndpoint = "http://127.0.0.1:63302",
            Source = "file",
        }));
        return path;
    }

    [Fact]
    public void OrphanSweep_KeepsFileWithLivePid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // This test process is alive, so its pid must not be treated as an orphan.
            var path = WriteInstanceFile(dir, Guid.NewGuid().ToString(), Environment.ProcessId);

            using var reg = new DirectorRegistry(dir);
            reg.SweepOrphanInstanceFiles();

            Assert.True(File.Exists(path), "a file whose process is still alive must not be deleted");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
        }
    }

    [Fact]
    public void OrphanSweep_KeepsFileWithUnstampedPid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // pid <= 0 predates pid stamping: death cannot be proven, so the file is left alone.
            var path = WriteInstanceFile(dir, Guid.NewGuid().ToString(), 0);

            using var reg = new DirectorRegistry(dir);
            reg.SweepOrphanInstanceFiles();

            Assert.True(File.Exists(path), "a file with no usable pid must not be deleted");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
        }
    }
}
