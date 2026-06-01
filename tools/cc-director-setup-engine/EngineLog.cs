namespace CcDirector.Setup.Engine;

/// <summary>
/// Minimal logging seam for the engine. The host (CLI, WPF, Director, Gateway)
/// sets <see cref="Sink"/> to route engine log lines to its own log file /
/// console. Defaults to a no-op so the library never assumes an output target.
/// </summary>
public static class EngineLog
{
    /// <summary>Destination for engine log lines. Null = discard.</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);
}
