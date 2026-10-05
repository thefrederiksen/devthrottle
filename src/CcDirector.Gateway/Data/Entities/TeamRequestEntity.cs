namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One request a person on a team wrote in their own words (devthrottle_internal#2308). It goes to the team's Owner
/// and Managers, in a Requests list of their own, and never to an agent: no session, prompt or Director ever reads
/// this table.
///
/// THE STORED STATE is one of <c>sent</c>, <c>accepted</c>, <c>declined</c> ("Not doing this") or <c>done</c>
/// (<see cref="Teams.TeamRequestStates"/>). Every change of state, and the send itself, is one
/// <see cref="TeamRequestChangeEntity"/> row, so the sender can read who changed it, when, and why.
///
/// The id is minted by the Gateway (<see cref="GatewayMintedKeyEntity"/>), never taken from a caller.
///
/// TENANT-SCOPED to the TEAM: <see cref="TenantScopedEntity.TenantId"/> is the team's id, which is the team's tenant
/// id. A request is read and written only through a context scoped to that team, so one team's requests can never be
/// read from another team, by the tenant filter as well as by the store.
/// </summary>
public sealed class TeamRequestEntity : GatewayMintedKeyEntity
{
    /// <summary>The account subject of the person who sent it, stamped by the server from the signed-in person's own
    /// key, never taken from the request body. Personally identifying: never logged.</summary>
    public string SenderSubject { get; set; } = "";

    /// <summary>The request, in the sender's own words, trimmed. Never logged.</summary>
    public string Text { get; set; } = "";

    /// <summary>sent, accepted, declined or done.</summary>
    public string State { get; set; } = "";

    /// <summary>When it was sent (UTC).</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary>When its state last changed (UTC); the send time until the first change.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One step in a request's trail (devthrottle_internal#2308): it was sent, or its state was changed - by whom, when,
/// and, for "Not doing this", why. Rows are only ever added, never changed, so the trail is the record.
/// Tenant-scoped to the team, like <see cref="TeamRequestEntity"/>.
/// </summary>
public sealed class TeamRequestChangeEntity : GatewayMintedKeyEntity
{
    /// <summary>The request (<see cref="TeamRequestEntity.Id"/>).</summary>
    public Guid RequestId { get; set; }

    /// <summary>The state the request was in after this step: sent, accepted, declined or done.</summary>
    public string State { get; set; } = "";

    /// <summary>The account subject of the person who made this step. Personally identifying: never logged.</summary>
    public string BySubject { get; set; } = "";

    /// <summary>When (UTC).</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>Why, for "Not doing this" - required there; null on every other step.</summary>
    public string? Reason { get; set; }
}
