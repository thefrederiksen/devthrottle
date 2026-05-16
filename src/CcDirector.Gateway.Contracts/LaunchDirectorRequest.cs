namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /directors on the Gateway. Spawns a new Director process.
/// </summary>
public sealed class LaunchDirectorRequest
{
    /// <summary>Optional workspace name to open. If empty, opens with default state.</summary>
    public string? Workspace { get; set; }

    /// <summary>How long to wait for the new Director to register itself (ms). Default 30000.</summary>
    public int TimeoutMs { get; set; } = 30_000;
}

/// <summary>
/// Body of DELETE /directors/{id}.
/// </summary>
public sealed class ShutdownDirectorRequest
{
    /// <summary>If true, kill the process if the graceful shutdown fails or hangs.</summary>
    public bool Force { get; set; } = false;

    /// <summary>How long to wait for graceful shutdown before forcing (if Force=true). Default 15000.</summary>
    public int TimeoutMs { get; set; } = 15_000;
}
