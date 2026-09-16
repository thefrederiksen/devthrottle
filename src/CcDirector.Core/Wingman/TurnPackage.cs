namespace CcDirector.Core.Wingman;

/// <summary>
/// Raw material about one completed turn, assembled mechanically - no model, no parsing
/// heuristics.
///
/// WHAT THIS IS NOW, AND WHAT IT IS NOT. It was the input to the turn-BRIEF pipeline
/// (TURN_BRIEFING.md section 3, box [1]). That pipeline is gone: its writer was retired in issue
/// #549 and its contract, its generator and the builder that filled this record were deleted in
/// 2026-09 with the rest of the closed set. What a stop MEANS is now decided by the turn verdict
/// (docs/architecture/wingman/TURN_VERDICT.md), which assembles its own package from its own
/// builder on the Gateway and never touches this type.
///
/// IT SURVIVES BECAUSE ONE LIVE CALLER USES IT AS A SHAPE: <see cref="DictatedPromptResolver"/>
/// takes a TurnPackage and returns one with any dictated file reference resolved to the file's
/// words. Nothing constructs one from a transcript any more.
/// </summary>
public sealed record TurnPackage(
    Guid SessionId,
    int TurnCount,
    string? FirstUserPrompt,
    string? LastUserPrompt,
    string? LastAssistantText,
    bool ReplyPending,
    string TranscriptDelta,
    string ScreenTail,
    string? RollingIntent,
    IReadOnlyList<string> PriorRailLines,
    string? CurrentHeadline = null,
    string? ParkedComposerText = null);
