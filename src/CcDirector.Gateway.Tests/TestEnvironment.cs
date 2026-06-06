using System.Runtime.CompilerServices;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Assembly-wide test environment. The turn-brief pipeline (issue #185) is disabled via
/// its kill switch for every test-spun GatewayHost: the TurnEndWatcher would otherwise
/// poll each test's fake Directors on its own 2s cadence and disturb request-count
/// assertions. The brief pipeline itself is tested directly through the agent's seam
/// constructor (GatewayTurnBriefTests), never through a live host poll.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init()
        => Environment.SetEnvironmentVariable("CC_TURNBRIEFS", "0");
}
