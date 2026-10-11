using Xunit;

namespace CcDirector.Engine.Tests;

/// <summary>The classes that start real processes through the job executor; serial, with nothing beside them.</summary>
[CollectionDefinition("RealProcesses", DisableParallelization = true)]
public sealed class RealProcessesCollection
{
}
