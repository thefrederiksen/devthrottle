using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The Windows live tests run ONE CLASS AT A TIME. Several of them put a known load on the machine - a
/// thread spinning on every processor, one core held flat out - and then measure it. Run beside each
/// other, one test's load becomes the next test's noise: the one-core share check and the detached-child
/// check both failed while the run-queue check was spinning every processor, and both passed alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsMachineLoad
{
    public const string Name = "Windows machine load";
}
