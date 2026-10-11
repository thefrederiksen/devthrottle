using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The collection for every class that touches PROCESS-WIDE STATE and has no more specific serial collection: a real
/// agent process through the session manager or the test shell, an environment variable, a static product seam (the
/// memory probe, the shadow log, the delivery-failure tallies, the background jobs, the file log's observers and
/// redirects, the send notices' line observer), or the storage root. A class that only runs git or a script in its own
/// temporary folder touches nothing process-wide and runs in parallel. Defined with DisableParallelization,
/// so xUnit runs it with nothing beside it; the rest of the assembly runs four wide (see TestParallelization.cs).
/// </summary>
[CollectionDefinition("ProcessWideState", DisableParallelization = true)]
public sealed class ProcessWideStateCollection
{
}

/// <summary>The classes that pin CC_DIRECTOR_ROOT; serial for the same reason, kept under the name they already use.</summary>
[CollectionDefinition("CcStorageRoot", DisableParallelization = true)]
public sealed class CcStorageRootCollection
{
}

/// <summary>The one class that pins the browser launcher's detection cache; serial, kept under the name it already uses.</summary>
[CollectionDefinition("BrowserLauncherDetectCache", DisableParallelization = true)]
public sealed class BrowserLauncherDetectCacheCollection
{
}
