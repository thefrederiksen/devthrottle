using System;
using System.IO;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE REGISTRY LOOKS AT FILES IT DOES NOT OWN, AND A LOOK MUST NEVER BLOCK THE OWNER.
///
/// The registry watches a directory and reads every <c>*.json</c> in it the instant it changes. It used to read with
/// <c>File.ReadAllText</c>, which holds the file so that nobody else may write it. On 12 October 2026 that turned a
/// mission create into an internal error: a test's <c>missions.json</c> sat in the watched directory, the registry
/// was reading it, and the mission store's next write was refused with "the file is being used by another process" -
/// once in a release gate, and 6 runs in 30 on a busy machine when it was looked for. A Director rewriting its own
/// instance file is the same collision in the product.
///
/// These tests hold the file the way a writer holds it and require the registry to read it anyway. That is the same
/// fact as "the registry's read lets a writer in", stated in the order that needs no second thread and no timing:
/// two handles on one file are allowed or refused by what each one shares, whichever opened first.
///
/// Revert-prove: put <c>File.ReadAllText</c> back in <c>DirectorRegistry.ReadWithoutBlockingTheWriter</c> and both
/// go red.
/// </summary>
public sealed class DirectorRegistryDoesNotBlockTheWriterTests : IDisposable
{
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-registry-sharing-" + Guid.NewGuid().ToString("N"));

    public DirectorRegistryDoesNotBlockTheWriterTests() => Directory.CreateDirectory(_instancesDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    /// <summary>The hold <c>File.WriteAllText</c> takes on a file: write access, others may only read.</summary>
    private static FileStream HeldAsAWriterHoldsIt(string path) =>
        new(path, FileMode.Open, FileAccess.Write, FileShare.Read);

    [Fact]
    public void A_file_its_owner_holds_open_for_writing_is_still_read()
    {
        var path = Path.Combine(_instancesDir, "held.json");
        File.WriteAllText(path, "[ \"some other store's file\" ]");

        using (HeldAsAWriterHoldsIt(path))
        {
            Assert.Equal("[ \"some other store's file\" ]", DirectorRegistry.ReadWithoutBlockingTheWriter(path));
        }
    }

    [Fact]
    public void A_Director_whose_instance_file_is_being_written_is_still_discovered()
    {
        // The whole path, not only the helper: the registry's own discovery reads through it.
        var path = Path.Combine(_instancesDir, "dir-sharing.json");
        File.WriteAllText(path, $"{{\"directorId\":\"dir-sharing\",\"pid\":{Environment.ProcessId}}}");

        using var registry = new DirectorRegistry(_instancesDir);
        using (HeldAsAWriterHoldsIt(path))
        {
            registry.Start();   // reads every file already in the directory before it returns

            Assert.Contains(registry.ListDirectors(TenantId.Local), d => d.DirectorId == "dir-sharing");
        }
    }
}
