using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests that read or mutate the process-wide <c>CC_DIRECTOR_ROOT</c> environment variable
/// (or start a <c>ControlApiHost</c>, which resolves storage paths from it) must not run
/// concurrently - one test setting the var would redirect another test's storage mid-run.
/// Decorating those classes with <c>[Collection("DirectorRoot")]</c> forces xUnit to run
/// them sequentially.
/// </summary>
[CollectionDefinition("DirectorRoot")]
public sealed class DirectorRootCollection
{
}
