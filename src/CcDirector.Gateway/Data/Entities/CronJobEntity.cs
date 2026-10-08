using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of a cron job (<see cref="CronJobDto"/>) in the EF data layer. One row per job in the
/// <c>cron_jobs</c> table, keyed by the store-minted <see cref="Id"/> (a code-supplied <c>cj_...</c> token,
/// never a database default). The nested <see cref="Target"/> and <see cref="Action"/> are mapped as EF
/// owned types serialized to a JSON column each (the "bulky sub-doc -> JSON in a column" pattern), reusing
/// the same contract types so the store maps field-for-field.
///
/// This is a separate type from the wire <see cref="CronJobDto"/> on purpose: the entity carries the
/// storage-only <see cref="TenantScopedEntity.TenantId"/> column, which must not leak onto the REST
/// contract. The store translates between the two.
/// </summary>
public sealed class CronJobEntity : TenantScopedEntity
{
    /// <summary>The job's id (primary key). Minted in code by the store; never a database default.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ScheduleKind { get; set; } = "";
    public string? CronExpression { get; set; }
    public string? RunAt { get; set; }
    public string TimeZoneId { get; set; } = "";

    /// <summary>The factory this schedule's sessions are born into (Factory Memory mission, phase 1), or null.
    /// See <see cref="Contracts.CronJobDto.Factory"/> for why writing it is limited to a person or a session
    /// already in that factory.</summary>
    public string? Factory { get; set; }

    /// <summary>The seat of <see cref="Factory"/> this schedule runs (issue #3650), or null. Always set together with
    /// the factory; see <see cref="Contracts.CronJobDto.Seat"/>.</summary>
    public string? Seat { get; set; }

    /// <summary>The target machine. Mapped as an owned type serialized to a JSON column.</summary>
    public CronJobTarget Target { get; set; } = new();

    /// <summary>What the fire runs. Mapped as an owned type serialized to a JSON column.</summary>
    public CronJobAction Action { get; set; } = new();

    public bool PreventOverlap { get; set; } = true;
    public string NotifyOn { get; set; } = CronNotify.None;
    public string? NotifyWebhookUrl { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? LastFiredUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public string? LastStatus { get; set; }
}
