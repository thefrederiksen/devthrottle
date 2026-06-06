using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>Pluggable brief generation. Since issue #187 deleted the Director-side
/// pipeline (the orchestrator and the side `claude --print` generator), the only
/// implementation is the stub - the GATEWAY's warm-brain brief agent generates real
/// briefs via <see cref="TurnBriefContract"/> and uses the stub as its degrade tier.</summary>
public interface ITurnBriefGenerator
{
    /// <summary>Generator identity recorded on briefs ("gateway-brain", "stub").</summary>
    string Id { get; }

    /// <summary>Interpret one turn. Null = generation failed; the caller degrades.</summary>
    Task<TurnBriefDto?> GenerateAsync(TurnPackage package, CancellationToken ct);
}

/// <summary>The last-resort degrade tier - an honest "turn N completed" marker, never
/// invented content. Used by the Gateway brief agent when the warm brain fails.</summary>
public sealed class StubTurnBriefGenerator : ITurnBriefGenerator
{
    public string Id => "stub";

    public Task<TurnBriefDto?> GenerateAsync(TurnPackage package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Task.FromResult<TurnBriefDto?>(new TurnBriefDto
        {
            SessionId = package.SessionId.ToString(),
            TurnNumber = package.TurnCount,
            GeneratedAtUtc = DateTime.UtcNow,
            Model = Id,
            Degraded = true,
            DegradeTier = "stub",
            // Carried forward, never invented: a failed wingman read must not amnesia the
            // session's standing chapter title, and never starts a chapter (NewChapter=false).
            Headline = package.CurrentHeadline ?? "",
            NewChapter = false,
            TurnTitle = "",
            Intent = package.RollingIntent ?? "(no brief yet - wingman unavailable)",
            Did = new List<string>(),
            NeedsYou = null,
        });
    }
}
