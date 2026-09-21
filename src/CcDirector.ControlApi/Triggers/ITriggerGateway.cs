using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Triggers;

/// <summary>What asking the Gateway for this Director's checks came to.</summary>
public enum TriggerFetchKind
{
    /// <summary>The Gateway answered with the checks this Director runs (possibly none).</summary>
    Assigned,
    /// <summary>The Gateway serves no triggers: its factory agents switch is off, or it predates triggers. The
    /// Director runs nothing.</summary>
    Off,
    /// <summary>The Gateway could not be asked, or answered with an error. The Director runs nothing this time.</summary>
    Failed,
}

/// <summary>The answer to one fetch.</summary>
public sealed record TriggerFetch(TriggerFetchKind Kind, IReadOnlyList<TriggerAssignmentDto> Triggers, string? Detail)
{
    public static TriggerFetch Off(string detail) => new(TriggerFetchKind.Off, Array.Empty<TriggerAssignmentDto>(), detail);
    public static TriggerFetch Failed(string detail) => new(TriggerFetchKind.Failed, Array.Empty<TriggerAssignmentDto>(), detail);
}

/// <summary>
/// The Director's two calls about triggers (the Website Business Factory mission, product track): fetch the checks
/// this Director runs, and report what one check produced. Production is <see cref="GatewayClient"/>; tests pass a
/// fake so the runner is proven without a Gateway.
/// </summary>
public interface ITriggerGateway
{
    Task<TriggerFetch> FetchTriggersAsync(string directorId, CancellationToken ct);

    /// <summary>Report one check. Returns null when the Gateway recorded it, or why it did not.</summary>
    Task<string?> ReportTriggerCheckAsync(string directorId, string triggerId, TriggerCheckReport report, CancellationToken ct);
}
