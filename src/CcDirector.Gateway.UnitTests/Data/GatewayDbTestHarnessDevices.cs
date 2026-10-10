using CcDirector.Gateway.Pairing;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// A <see cref="DeviceRegistry"/> over a <see cref="GatewayDbTestHarness"/> database.
///
/// The registry's path constructor opens its OWN <c>GatewayDatabase</c> beside the path it is given and runs the
/// whole migration chain - about 1.5 seconds on this machine, measured by
/// <c>DatabaseOpensAfterTheBindTests.Open_connects_and_migrates</c>. Sixteen test classes built a registry that way
/// once per test, and the migration was most of their time: 37 seconds for the 23 tests of AuthMiddlewareTests
/// alone. The harness already holds the migrated schema, built once per process and copied, so a registry over
/// <see cref="GatewayDbTestHarness.Open"/> opens the real schema through the real code path and migrates nothing.
///
/// The harness owns the database and disposes it; the registry does not. One test keeps the path constructor
/// honest: <c>DeviceRegistryTests.PathConstructor_MigratesAnEmptyFile_AndAKeySurvivesAReopen</c>.
/// </summary>
internal static class GatewayDbTestHarnessDevices
{
    /// <summary>A registry over a fresh open of the harness database, with its legacy import path in the harness
    /// directory. Call it twice on one harness to simulate a Gateway restart over the same database.</summary>
    public static DeviceRegistry OpenDevices(this GatewayDbTestHarness harness, string legacyFile = "devices.json")
        => new(harness.Open(), harness.LegacyPath(legacyFile));
}
