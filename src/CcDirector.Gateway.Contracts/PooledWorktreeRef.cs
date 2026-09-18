namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The pooled worktree a session is running in: which repository's pool it came from, which slot it
/// is, where it is on disk, and the lease its holder gives back with.
///
/// ONE TYPE, THREE PLACES, because it is one fact: <see cref="SessionDto.PooledWorktree"/> reports it
/// on a live session, <see cref="WorkspaceSeat.PooledWorktree"/> keeps it on a captured seat, and
/// <see cref="NewSessionRequest.PooledWorktree"/> hands it back to the Director when that seat is
/// restored. Three shapes for the same four values would drift, and the field that drifted would be
/// the lease - the one whose absence means a slot nobody can give back.
///
/// WHY THE LEASE TRAVELS. Until this existed the lease lived only in the Director's memory, so a
/// Director restart lost it: the restored session ran in the slot with no lease, the pool kept the
/// slot in use under a holder that no longer existed, and a Director that restarted a few times
/// filled its own pool. Nothing was ever destroyed by that - cc-worktrees' landed-work check still
/// stood between the slot and any reset - but the slot never came back on its own.
///
/// The lease is not a secret and it is not authentication. It is the coordination token cc-worktrees
/// requires so one session cannot give back a slot another session is working in, it names a slot on
/// ONE machine, and the worst a leaked one can do is return a worktree - which that tool refuses
/// anyway while the work in it cannot be proven landed.
/// </summary>
public sealed class PooledWorktreeRef
{
    /// <summary>The repository whose pool the slot belongs to. NOT where the session runs - that is
    /// <see cref="Path"/>, and it is also the session's own repository path while it holds the slot.</summary>
    public string Repo { get; set; } = "";

    /// <summary>The slot's name in its pool, as <c>cc-worktrees list</c> shows it (<c>wt01</c>).</summary>
    public string Slot { get; set; } = "";

    /// <summary>The slot's directory on disk: where the agent actually works.</summary>
    public string Path { get; set; } = "";

    /// <summary>The lease from <c>cc-worktrees get</c>, which <c>cc-worktrees return</c> requires.</summary>
    public string Lease { get; set; } = "";

    /// <summary>True when all four values are present, which is the only state worth carrying.
    /// A partial record would name a slot the Director could not give back, which is worse than none:
    /// it would look like a session that had been restored properly.</summary>
    public bool IsComplete()
        => !string.IsNullOrWhiteSpace(Repo)
           && !string.IsNullOrWhiteSpace(Slot)
           && !string.IsNullOrWhiteSpace(Path)
           && !string.IsNullOrWhiteSpace(Lease);
}
