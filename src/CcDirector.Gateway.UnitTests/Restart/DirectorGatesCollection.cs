using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// The test classes that take one of the Director's ONE-AT-A-TIME gates: <c>DirectorDrain</c> allows one
/// drain per process and <c>DirectorRestartCycle</c> one cycle per process, and both gates are static
/// because in the product there is exactly one Director per process.
///
/// A test process is not a Director, and xUnit runs separate classes side by side. While only one class
/// ran drains and only one ran cycles, each was alone behind its gate. The tests of the cycle over the
/// real drain take BOTH, so without this collection a drain test and a cycle test running at the same
/// moment would refuse each other - a failure that says "already running" and is about the test runner,
/// not the product. Classes in one collection run one after another; nothing else is changed.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DirectorGatesCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "DirectorGates";
}
