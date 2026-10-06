using CcDirector.Core.Agents;

namespace CcDirector.Core.Skills;

/// <summary>Why one skill the library holds did not reach the agent.</summary>
public enum SkillPlacementFault
{
    /// <summary>A directory we did not write already occupies the name - or a plain file, or a link of any kind,
    /// none of which this installer ever makes there (review finding SK-F21) - so the machine's own entry wins and
    /// ours was not installed.</summary>
    Shadowed,

    /// <summary>The link into the agent's own directory could not be created.</summary>
    LinkFailed,

    /// <summary>Another Director's library installed a skill of the same name and keeps it: the person's
    /// own account, or a team that installed it first (devthrottle_internal#2311). Ours was not installed.</summary>
    HeldByAnotherSource,

    /// <summary>Another Director was placing skills in the same folders and did not finish within the wait,
    /// so this placement changed nothing (review finding SK-F1). The next session start tries again.</summary>
    FolderBusy,

    /// <summary>The Gateway did not say which account its skills belong to (a Gateway older than the rule that
    /// stamps each skill with its source), so this Director changed nothing. Updating the Gateway fixes it.</summary>
    SourceUnknown,

    /// <summary>The skills were fetched for a different account than the one this Director is set up for - for
    /// instance a team key saved without its team record - so this Director changed nothing.</summary>
    SourceMismatch,

    /// <summary>The folder where copies are built is not known to be safe, so this Director changed nothing: it is
    /// inside a skills folder agents read, or already exists and was not made by this installer (review finding
    /// SK-F11), or a skills folder or the staging folder goes through a link whose target cannot be found or read
    /// (review findings SK-F9, SK-F14, SK-F20 - one refusal for every way the real place is unknown). Moving or
    /// repairing the link or the folder fixes it.</summary>
    StagingFolderUnsafe,
}

/// <summary>One skill that should have reached the agent and did not.</summary>
/// <param name="SkillId">The skill the library holds.</param>
/// <param name="Target">The directory it should have appeared in.</param>
/// <param name="Fault">What stopped it.</param>
public sealed record SkillPlacementProblem(string SkillId, string Target, SkillPlacementFault Fault);

/// <summary>
/// What actually happened when the fleet's skills were placed for one agent.
///
/// WHY THIS IS A RESULT AND NOT A LOG LINE. A central library only works if a skill published on the
/// Gateway is a skill the agent can actually read. When placement half-fails, everything still looks
/// healthy - the Gateway serves it, the store holds it, the session launches - and the only symptom
/// is an agent quietly running on instructions nobody meant it to have. That happened for real: a
/// retired installer's leftover copies occupied all three built-in names in the Claude Code directory,
/// the ownership rule correctly refused to overwrite them, and the agent went on reading a two-month
/// old copy while every other agent family read the current one. Nothing failed. Nothing was reported.
///
/// So the outcome is returned, and a caller that drops it is now visibly dropping something.
/// </summary>
public sealed record SkillPlacement(
    AgentKind Kind,
    int Held,
    int Reachable,
    IReadOnlyList<SkillPlacementProblem> Problems,
    bool StoreMissing,
    bool AgentHasNoSkillsDirectory,
    IReadOnlyList<string>? Notes = null)
{
    /// <summary>Things worth knowing that stopped nothing - for instance an unreadable link record, which is
    /// information only and was treated as empty (review finding SK-F19). Logged; never a reason a skill is missing.</summary>
    public IReadOnlyList<string> NotesOrEmpty => Notes ?? Array.Empty<string>();

    /// <summary>Nothing was expected of this placement: the agent has no skills mechanism, or the
    /// library holds nothing for this machine. Not a fault - there is nothing to be wrong.</summary>
    public bool NothingExpected => AgentHasNoSkillsDirectory || (Held == 0 && !StoreMissing);

    /// <summary>Every skill the library holds is readable by this agent.</summary>
    public bool IsComplete => !NothingExpected && !StoreMissing && Problems.Count == 0 && Reachable >= Held;

    /// <summary>The library holds skills and NONE of them reached the agent. The worst case and the
    /// quietest one, because a session with no skills looks exactly like a fleet with no skills.</summary>
    public bool IsTotalFailure => !NothingExpected && !StoreMissing && Held > 0 && Reachable == 0;

    /// <summary>One plain-English line for a human - the session log and the Director log both show
    /// this. Says the count, the reason, and what to do, because a warning that does not say what to
    /// do gets read once and ignored after that.</summary>
    public string Describe()
    {
        if (AgentHasNoSkillsDirectory)
            return $"{Kind} has no skills directory, so no fleet skills were placed.";
        if (StoreMissing)
            return "No fleet skills are on this machine yet - the Gateway has not been reached since " +
                   "this Director started. The session starts without them.";
        if (Held == 0)
            return "The fleet library holds no skills for this machine.";
        if (IsComplete)
            return $"All {Held} fleet skill(s) are in place for {Kind}.";

        var shadowed = Problems.Where(p => p.Fault == SkillPlacementFault.Shadowed).ToList();
        var failed = Problems.Where(p => p.Fault == SkillPlacementFault.LinkFailed).ToList();
        var held = Problems.Where(p => p.Fault == SkillPlacementFault.HeldByAnotherSource).ToList();
        var busy = Problems.Where(p => p.Fault == SkillPlacementFault.FolderBusy).ToList();
        var unknown = Problems.Where(p => p.Fault == SkillPlacementFault.SourceUnknown).ToList();
        var mismatch = Problems.Where(p => p.Fault == SkillPlacementFault.SourceMismatch).ToList();
        var unsafeStaging = Problems.Where(p => p.Fault == SkillPlacementFault.StagingFolderUnsafe).ToList();
        var parts = new List<string>();
        if (shadowed.Count > 0)
            parts.Add($"{shadowed.Count} blocked by a directory DevThrottle did not write " +
                      $"({string.Join(", ", shadowed.Select(p => p.SkillId))}) in {shadowed[0].Target} - " +
                      "rename or remove it and start a new session");
        if (held.Count > 0)
            parts.Add($"{held.Count} kept by another Director's library under the same name " +
                      $"({string.Join(", ", held.Select(p => p.SkillId))}) in {held[0].Target} - " +
                      "the person's own account wins a name, and between teams the first installed keeps it");
        if (busy.Count > 0)
            parts.Add($"{busy.Count} not placed because another Director was placing skills in {busy[0].Target} " +
                      "at the same moment - nothing was changed; start a new session to place them");
        if (unknown.Count > 0)
            parts.Add($"{unknown.Count} not placed because the Gateway does not say which account its skills belong " +
                      "to - nothing was changed; the Gateway must be updated");
        if (mismatch.Count > 0)
            parts.Add($"{mismatch.Count} not placed because they were fetched for a different account than this " +
                      "Director is set up for - nothing was changed; check the Director's team in Settings");
        if (unsafeStaging.Count > 0)
            parts.Add($"{unsafeStaging.Count} not placed because {unsafeStaging[0].Target} - the skills folder, or the " +
                      "folder where copies are built - could not be resolved or is not safe to use: it is a link whose " +
                      "target cannot be found, is inside a skills folder agents read, or was not made by DevThrottle - " +
                      "nothing was changed; move or repair the link or the folder");
        if (failed.Count > 0)
            parts.Add($"{failed.Count} could not be linked ({string.Join(", ", failed.Select(p => p.SkillId))})");

        return $"WARNING: only {Reachable} of {Held} fleet skill(s) reached {Kind}: {string.Join("; ", parts)}.";
    }
}
