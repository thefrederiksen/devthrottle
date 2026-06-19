namespace CcDirector.Core.Configuration;

/// <summary>
/// How an agent entry's command line is composed. The two modes are mutually exclusive so the
/// behavior is never ambiguous (the old design appended <c>--model</c> on top of a free-text
/// override, which could not promise "use exactly what I typed").
/// </summary>
public enum LaunchMode
{
    /// <summary>
    /// CC Director composes the command line from the selected preset plus the chosen model
    /// (via the driver's model flag). The free-text override is ignored in this mode.
    /// </summary>
    Guided,

    /// <summary>
    /// The user owns the whole argument string: CC Director uses the free-text override verbatim
    /// and appends nothing - no preset, no model flag.
    /// </summary>
    Custom,
}
