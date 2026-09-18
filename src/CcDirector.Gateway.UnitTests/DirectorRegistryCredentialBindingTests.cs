using System;
using System.IO;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Which credential a Director said Hello on (the Message Load mission, inspection 11, ruling 1). The hub binds a
/// Director id to the device key that authenticated its stream; the registry keeps that binding so an HTTP route
/// can tell the Director's own calls from another key of the same account. These drive the registry directly.
/// </summary>
public sealed class DirectorRegistryCredentialBindingTests : IDisposable
{
    private static readonly TenantId Account = new("tenant-binding");
    private static readonly TenantId OtherAccount = new("tenant-binding-other");
    private const string KeyA = "device:workstation-a";
    private const string KeyB = "device:workstation-b";

    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-drcb-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;

    public DirectorRegistryCredentialBindingTests()
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

    private void Hello(TenantId tenant, string directorId, string? credential)
        => _registry.RegisterFromStream(directorId, "MAC", "soren", "1.0", 42, DateTime.UtcNow, tenant, "", credential);

    [Fact]
    public void IsRegisteredByCredential_TheHellosKey_IsTheDirector_AnotherKeyOfTheAccountIsNot()
    {
        Hello(Account, "director-a", KeyA);

        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a", KeyA));
        Assert.False(_registry.IsRegisteredByCredential(Account, "director-a", KeyB));
        Assert.False(_registry.IsRegisteredByCredential(Account, "director-a", null));
        Assert.False(_registry.IsRegisteredByCredential(OtherAccount, "director-a", KeyA));
        Assert.False(_registry.IsRegisteredByCredential(Account, "director-unknown", KeyA));
    }

    [Fact]
    public void IsRegisteredByCredential_OneMachineKeyMayCarrySeveralDirectors_AndAReconnectRebinds()
    {
        Hello(Account, "director-a", KeyA);
        Hello(Account, "director-a2", KeyA);
        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a2", KeyA));

        Hello(Account, "director-a", KeyB);
        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a", KeyB));
        Assert.False(_registry.IsRegisteredByCredential(Account, "director-a", KeyA));

        // A registration that names no credential leaves nothing that can prove itself to be this Director.
        Hello(Account, "director-a", null);
        Assert.False(_registry.IsRegisteredByCredential(Account, "director-a", KeyB));
    }

    [Fact]
    public void IsRegisteredByCredential_AfterTheDirectorLeavesAndAnotherRegistersItsIdWithoutAKey_IsFalse()
    {
        Hello(TenantId.Local, "director-local", KeyA);
        Assert.True(_registry.Remove("director-local"));

        Hello(TenantId.Local, "director-local", null);
        Assert.False(_registry.IsRegisteredByCredential(TenantId.Local, "director-local", KeyA));
    }
}
