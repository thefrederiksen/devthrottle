namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE ONE RULE for reading a <see cref="MachineRestartCapabilityDto"/> as permission to proceed with a
/// guarded restart. Issue #2725 (restart epic, Phase 6).
///
/// It is read in three places - when a session asks, when the owner accepts, and on the Director
/// immediately before it asks its launcher - and they must agree, so the rule lives once, here, in the
/// contracts both ends share. A second spelling on the Director would be free to drift from the
/// Gateway's, and the Director's is the one that runs after every session has been closed.
///
/// == AGAINST THE KNOWN-SAFE VALUES. The verdict must be <see cref="RestartVerdict.CanRestart"/> AND
/// the guard must be <see cref="CapabilityState.Available"/>. <see cref="RestartVerdict.Unknown"/> and
/// <see cref="CapabilityState.Unknown"/> are refusals, in the machine's own words: an unknown is not a
/// no, and it is not a yes either. A value this build has never heard of - written by a newer Gateway -
/// is a refusal for the same reason.
///
/// THE GUARD IS REQUIRED, NOT ADVISORY. A restart sent to a launcher that cannot see the only-if-empty
/// condition goes ahead on a half-drained Director and reports success; by the time the unacknowledged
/// answer comes back the sessions are gone. Phase 1's answer says whether the launcher declared the
/// condition, and this refuses when it did not.
/// </summary>
public static class DirectorRestartGate
{
    /// <summary>The reason a guarded restart must NOT be sent to this machine, or null when it may.</summary>
    public static string? Refusal(MachineRestartCapabilityDto capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.Verdict == RestartVerdict.CanRestart && capability.GuardedRestart == CapabilityState.Available)
            return null;

        var sentences = new List<string>();
        if (capability.Verdict != RestartVerdict.CanRestart) sentences.Add(capability.Reason);
        if (capability.GuardedRestart != CapabilityState.Available) sentences.Add(capability.GuardedRestartReason);
        return string.Join(" ", sentences.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>The refusal's short code: <c>cannot_restart</c> when the verdict is the problem,
    /// <c>restart_not_guarded</c> when only the guard is.</summary>
    public static string RefusalCode(MachineRestartCapabilityDto capability)
        => capability.Verdict == RestartVerdict.CanRestart ? "restart_not_guarded" : "cannot_restart";
}
