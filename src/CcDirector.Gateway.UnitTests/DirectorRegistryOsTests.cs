using System;
using System.IO;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The operating system a Director reports in its Hello is kept on its registry entry (the Systems tab of Your
/// Throttle). The Hello is client-written, so only a listed system is stored, and an older build's empty field is
/// "no statement" - it never erases what the entry already knows.
/// </summary>
public sealed class DirectorRegistryOsTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-os");
    private static readonly DateTime StartedAt = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-dros-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;

    public DirectorRegistryOsTests()
    {
        Directory.CreateDirectory(_instancesDir);
        _registry = new DirectorRegistry(_instancesDir);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private void Register(string? os)
        => _registry.RegisterFromStream("dir-1", "devlinux", "soren", "2.21.0", 42, StartedAt, Tenant, os: os);

    [Fact]
    public void RegisterFromStream_ReportedSystem_IsStoredInItsOneSpelling()
    {
        Register("Linux");

        Assert.Equal("linux", _registry.Get(Tenant, "dir-1")!.Os);
    }

    [Fact]
    public void RegisterFromStream_OlderBuildSendsNoSystem_KeepsTheKnownOne()
    {
        Register("linux");
        Register("");

        Assert.Equal("linux", _registry.Get(Tenant, "dir-1")!.Os);
    }

    [Fact]
    public void RegisterFromStream_UnlistedSystem_IsNotStored()
    {
        Register("plan9");

        Assert.Equal("", _registry.Get(Tenant, "dir-1")!.Os);
    }
}
