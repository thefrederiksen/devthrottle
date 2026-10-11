using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE STORAGE ROOT IS A VALUE ON THE OPTIONS. Production reads it once, from the process root, in
/// <see cref="GatewayHostOptions.FromEnvironment"/>; a test states one. An explicit record that omits it runs on
/// the process root, which is what every host did before the property existed - until the last host-building test
/// states its root and the initialiser goes (gateway-tests.md section 3.3, step 1).
/// </summary>
public sealed class GatewayHostOptionsStorageRootTests
{
    [Fact]
    public void Options_built_from_the_environment_carry_the_process_root()
    {
        Assert.Equal(CcStorage.Root(), GatewayHostOptions.FromEnvironment().StorageRoot);
    }

    [Fact]
    public void An_explicit_record_that_omits_the_root_runs_on_the_process_root()
    {
        Assert.Equal(CcStorage.Root(), new GatewayHostOptions { Hosted = true }.StorageRoot);
    }

    [Fact]
    public void A_stated_root_is_kept_as_stated()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-options-root-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(root, new GatewayHostOptions { StorageRoot = root }.StorageRoot);
    }
}
