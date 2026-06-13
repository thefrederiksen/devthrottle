using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Test collection for tests that mutate the process-wide <c>CC_DIRECTOR_ROOT</c> environment
/// variable (which redirects config.json / storage to a temp directory they create and delete).
/// Tests in this collection run serially with respect to each other, so two of them never
/// redirect / delete the shared root at the same time. xUnit runs different collections in
/// parallel by default, so placing every CC_DIRECTOR_ROOT-mutating test in ONE collection
/// keeps them from racing each other while still letting the rest of the suite parallelize.
/// </summary>
[CollectionDefinition("ConfigEnvSerial", DisableParallelization = true)]
public sealed class ConfigEnvSerialCollection
{
}
