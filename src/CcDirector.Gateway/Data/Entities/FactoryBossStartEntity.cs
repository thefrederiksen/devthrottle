namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE SESSION THE GATEWAY STARTED AS A FACTORY'S BOSS for a session in no factory - the owner's own agent (issue #4351)
/// - in <c>factory_boss_starts</c> (issue #4598). That start gives the Boss the rules-only kit a child that INHERITS a
/// seat is also given, so the kit cannot say which of the two a session is; this row can. It is the Gateway's own
/// record, written by the spawn door only after it approved the start and the Director confirmed the session, so no
/// caller, body or Director push can write one. A verified restore of a Boss (a workspace restore claim the Director
/// sends on its own credential) writes one for the new session, naming the session it restored.
///
/// A session with a row here, a rules-only kit and the same factory and seat on its history row was STARTED AS that
/// seat is the Boss (<see cref="History.SessionFactoryLookup.SeatIsInherited"/>): it publishes, decides the factory
/// inbox's bundles, starts seats and is the Boss the inbox wake rings (the owner's ruling of 6 November 2026, which
/// overturned the Tech Lead's ruling of 5 November 2026 for this case). No row means inherited,
/// exactly as before this table existed - so a lost row takes authority away and never grants it. A Smart Restart
/// reopen and an interrupted-session continuation of a recorded Boss carry the row to the session that continues it;
/// a handover does not, so a handed-over Boss is started as the Boss again.
///
/// RETENTION: none. One small row per Boss start, kept for as long as the account exists; a row whose session has aged
/// out of <c>session_history</c> (90 days) is never read, because the read starts from the history row.
/// </summary>
public sealed class FactoryBossStartEntity : TenantScopedEntity
{
    /// <summary>The session that was started as the Boss - the Director-minted session GUID, composite with the tenant.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The factory it was started in, in its one spelling.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The Boss seat it was started as.</summary>
    public string Seat { get; set; } = "";

    /// <summary>Who asked: the session in no factory that started it, or, for a restore, the session it restored.</summary>
    public string StartedBy { get; set; } = "";

    /// <summary><see cref="FactoryBossStartKinds.Started"/> or <see cref="FactoryBossStartKinds.Restored"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>When the Gateway recorded it (UTC).</summary>
    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>How a Boss start came to be recorded.</summary>
public static class FactoryBossStartKinds
{
    /// <summary>A session in no factory started the Boss.</summary>
    public const string Started = "started";

    /// <summary>A Director restored a session that was itself recorded as started as the Boss.</summary>
    public const string Restored = "restored";
}
