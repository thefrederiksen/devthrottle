using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// What happened when this Director asked the Gateway for the ONE repository list (the
/// one-repository-list mission, phase 6). The caller needs more than a list-or-null, because the
/// Director's New Session dialog is allowed to fall back to its own local scan for exactly one of
/// these outcomes and must never fall back for another.
/// </summary>
public enum KnownRepositoryListOutcome
{
    /// <summary>
    /// The Gateway answered with the list. WHATEVER IT CONTAINS, INCLUDING NOTHING. An empty list is the
    /// Gateway saying this machine has no repositories, and a client that treated that as a failure and
    /// showed its own list instead would hide the very defect this mission exists to end.
    /// </summary>
    Served,

    /// <summary>This Director has no Gateway configured, so there was nobody to ask.</summary>
    NotConfigured,

    /// <summary>The request never completed: no connection, a timeout, or a cancelled call.</summary>
    Unreachable,

    /// <summary>
    /// The Gateway answered, and the answer was not the list - an error status, or a body that is not a
    /// list. This is NOT the same as unreachable: something is wrong and <see cref="KnownRepositoryListResult.Reason"/>
    /// carries the Gateway's own words so a screen can show them rather than invent an explanation.
    /// </summary>
    Refused,
}

/// <summary>
/// The answer to one ask for the one repository list: the outcome, the rows when there are rows, and the
/// reason when there is one to give.
/// </summary>
/// <param name="Outcome">Which of the four things happened.</param>
/// <param name="Repositories">The rows, IN THE ORDER THE GATEWAY SERVED THEM. The order is the Gateway's
/// ruling (Critical Rule 7) and nothing between here and the screen re-orders it. Empty for every outcome
/// but <see cref="KnownRepositoryListOutcome.Served"/>.</param>
/// <param name="Reason">The Gateway's own words, or the transport's, when there are any. Null when the
/// list was served.</param>
public sealed record KnownRepositoryListResult(
    KnownRepositoryListOutcome Outcome,
    IReadOnlyList<KnownRepositoryDto> Repositories,
    string? Reason)
{
    /// <summary>The Gateway answered with the list.</summary>
    public static KnownRepositoryListResult Served(IReadOnlyList<KnownRepositoryDto> repositories) =>
        new(KnownRepositoryListOutcome.Served, repositories, null);

    /// <summary>There is no Gateway on this Director to ask.</summary>
    public static KnownRepositoryListResult NotConfigured(string reason) =>
        new(KnownRepositoryListOutcome.NotConfigured, Array.Empty<KnownRepositoryDto>(), reason);

    /// <summary>The ask never reached a Gateway.</summary>
    public static KnownRepositoryListResult Unreachable(string reason) =>
        new(KnownRepositoryListOutcome.Unreachable, Array.Empty<KnownRepositoryDto>(), reason);

    /// <summary>The Gateway answered, and the answer was not the list.</summary>
    public static KnownRepositoryListResult Refused(string reason) =>
        new(KnownRepositoryListOutcome.Refused, Array.Empty<KnownRepositoryDto>(), reason);
}
