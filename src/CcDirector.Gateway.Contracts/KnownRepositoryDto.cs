namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One repository on one machine, as the Gateway knows it. Unlike the Director's recent repository
/// registry, this record is durable and is not pruned with session history.
///
/// It is ONE LIST WITH TWO HALVES (the one-repository-list mission, phase 3): a repository that has been
/// opened carries the time it was last used, and a repository a Director found under a registered root
/// folder but nobody has ever opened carries no time at all and says so in <see cref="NeverOpened"/>.
///
/// THE ORDER OF THE LIST IS THE GATEWAY'S RULING, and it is the order the array arrives in: most recently
/// used first, never-opened beneath everything that has been used. No client re-sorts it and no client
/// decides for itself what a missing time means - that is Critical Rule 7 (CLAUDE.md) applied to a list
/// instead of a verdict. A client that ruled for itself would, the first time it met a row it did not
/// expect, render something plausible rather than something true.
/// </summary>
public sealed class KnownRepositoryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>
    /// When a session last started in this repository, or null when nobody ever has. Null is a fact, not
    /// a missing value: see <see cref="NeverOpened"/>, which says the same thing without asking a client
    /// to interpret an absence.
    /// </summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>
    /// The Gateway's verdict, stamped rather than inferred: this repository was found under a registered
    /// root folder and has never been opened, so it sorts beneath everything that has been. It is set
    /// from <see cref="LastUsed"/> in exactly one place, so the two can never disagree, and it exists so
    /// that no client writes a conditional deciding what an absent date MEANS.
    /// </summary>
    public bool NeverOpened { get; set; }
}
