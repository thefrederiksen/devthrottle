using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE SHARED PROBE CONTRACT, RUN AGAINST THE macOS PROBE ON THE REAL MAC RUNNING THE TEST.
///
/// The tests are the ones written once in <see cref="ResourceProbeContractLiveTests"/>; this class only
/// says which probe they are run against. That is the whole point of the contract: three platform
/// probes, three Developers, one interface, and the figure most likely to diverge between them - a
/// process's share of the processor, which is per core on one operating system and per machine on
/// another - proved here under a known load rather than discovered in the Cockpit as a nonsense number.
///
/// WHY THIS IS A SEPARATE PROOF FROM <see cref="MacOsResourceProbeLiveTests"/>, which already reads this
/// machine through this probe: those tests were written by the person who wrote the probe, against the
/// behaviour that probe has. These were written against the INTERFACE, by someone else, before this
/// probe existed. A single-platform suite cannot substitute for them, because it cannot fail for the
/// reason they exist to catch - a reading that is self-consistent on a Mac and means something different
/// from the Linux and Windows reading of the same field.
///
/// IT CARRIES <see cref="LiveMachinePlatformAttribute"/> BECAUSE IT MUST. The probe calls Mach, IOKit,
/// <c>statfs</c> and <c>libproc</c>, which exist only on a Mac, so on a Linux or Windows machine with
/// the opt-in set these tests SKIP with the reason rather than running and failing on the first Mach
/// call. A contract class that declares no platform is reported as an error by the discoverer, which is
/// what makes "run the whole project on each machine" a safe instruction.
///
/// HOW TO RUN IT: on a Mac, with <c>CC_DIRECTOR_LIVE_MACHINE_TESTS=1</c> set.
/// </summary>
[Trait("Category", "Resources")]
[LiveMachinePlatform(LiveMachinePlatform.MacOs)]
public sealed class MacOsResourceProbeContractLiveTests : ResourceProbeContractLiveTests
{
    protected override IResourceProbe CreateProbe() => new MacOsResourceProbe();

    protected override string ProbeName => "the macOS probe";
}
