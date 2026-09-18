using System;
using System.IO;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A DIRECTOR ID BELONGS TO THE CREDENTIAL THAT FIRST REGISTERED IT (the Message Load mission, inspection 11
/// ruling 1, closed by inspection 12 finding 1). The hub binds a Director id to the device key that
/// authenticated its stream; the registry keeps that binding so an HTTP route can tell the Director's own calls
/// from another key of the same account - and REFUSES a Hello from a different key of that account, because a
/// binding the next Hello could rewrite was no check at all.
///
/// These drive the registry directly. The same refusal over a real connection, and what it stops a second
/// workstation key doing, is <c>WorkspaceRestoreRouteTests.A_second_workstation_key_cannot_take_the_Directors_id_by_saying_Hello_as_it</c>.
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

    /// <summary>
    /// The hole inspection 12 found, now closed. Before the fix this call SUCCEEDED and made the second key the
    /// Director; a test asserting that take-over is what codified the hole, so this asserts the refusal instead.
    /// </summary>
    [Fact]
    public void RegisterFromStream_ASecondKeyOfTheAccountNamingABoundId_IsRefusedAndTheBindingDoesNotMove()
    {
        Hello(Account, "director-a", KeyA);

        var refused = Assert.Throws<DirectorIdBoundToAnotherCredentialException>(() => Hello(Account, "director-a", KeyB));

        Assert.Equal("director-a", refused.DirectorId);
        Assert.Equal(Account, refused.Tenant);
        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a", KeyA));
        Assert.False(_registry.IsRegisteredByCredential(Account, "director-a", KeyB));
    }

    /// <summary>The question the hub asks before it writes any connection state.</summary>
    [Fact]
    public void IsBoundToAnotherCredential_OnlyAnotherKeyOfTheSameAccountAndTheSameId_IsTrue()
    {
        Hello(Account, "director-a", KeyA);

        Assert.True(_registry.IsBoundToAnotherCredential(Account, "director-a", KeyB));
        Assert.False(_registry.IsBoundToAnotherCredential(Account, "director-a", KeyA));
        Assert.False(_registry.IsBoundToAnotherCredential(Account, "director-a", null));
        Assert.False(_registry.IsBoundToAnotherCredential(Account, "director-never-seen", KeyB));
        Assert.False(_registry.IsBoundToAnotherCredential(OtherAccount, "director-a", KeyB));
    }

    [Fact]
    public void RegisterFromStream_TheSameKeyReconnecting_KeepsTheBindingAndRefreshesTheEntry()
    {
        Hello(Account, "director-a", KeyA);

        Hello(Account, "director-a", KeyA);
        Hello(Account, "director-a", KeyA);

        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a", KeyA));
    }

    [Fact]
    public void RegisterFromStream_OneMachineKeyMayCarrySeveralDirectorIds()
    {
        Hello(Account, "director-a", KeyA);
        Hello(Account, "director-a2", KeyA);

        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a", KeyA));
        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a2", KeyA));
    }

    [Fact]
    public void RegisterFromStream_TheSameIdInADifferentAccount_IsNotTheSameBinding()
    {
        Hello(Account, "director-a", KeyA);

        Hello(OtherAccount, "director-a", KeyB);

        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a", KeyA));
        Assert.True(_registry.IsRegisteredByCredential(OtherAccount, "director-a", KeyB));
    }

    /// <summary>
    /// A registration that names no credential is NO STATEMENT: it refreshes the entry and leaves the binding
    /// alone. If it cleared the binding, a caller could unbind an id with a credential-less Hello and then claim
    /// it on its own key - the take-over above, in two steps.
    /// </summary>
    [Fact]
    public void RegisterFromStream_ACredentiallessHello_DoesNotClearTheBinding()
    {
        Hello(Account, "director-a", KeyA);

        Hello(Account, "director-a", null);

        Assert.True(_registry.IsRegisteredByCredential(Account, "director-a", KeyA));
        Assert.True(_registry.IsBoundToAnotherCredential(Account, "director-a", KeyB));
    }

    /// <summary>The one way a binding ends: the entry is removed. That is how a re-enrolled Director takes its
    /// own id back once the old registration is gone.</summary>
    [Fact]
    public void RegisterFromStream_AfterTheEntryIsRemoved_AnotherKeyMayRegisterTheSameId()
    {
        Hello(TenantId.Local, "director-local", KeyA);
        Assert.True(_registry.IsBoundToAnotherCredential(TenantId.Local, "director-local", KeyB));

        Assert.True(_registry.Remove("director-local"));

        Assert.False(_registry.IsBoundToAnotherCredential(TenantId.Local, "director-local", KeyB));
        Hello(TenantId.Local, "director-local", KeyB);
        Assert.True(_registry.IsRegisteredByCredential(TenantId.Local, "director-local", KeyB));
        Assert.False(_registry.IsRegisteredByCredential(TenantId.Local, "director-local", KeyA));
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
