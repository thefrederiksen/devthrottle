namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE REGISTERED FACTORY, in the <c>factory_registry</c> table (Factories screen mission, phase A). One row per
/// (account, factory id). Until this existed the Gateway knew a factory only from the activity rows and triggers
/// that happened to name it - no title, no CEO, no seats, no goal, no computer - so the owner's screen could not
/// list a factory that had not written a row, nor tell a seat from a session that merely wrote one.
///
/// AN INDEX, NOT THE DEFINITION. The briefs stay on disk in the factory's folder; this row says where the folder
/// is, which seat is the CEO, which schedules run each seat, and what the owner's goal is. Moving the definitions
/// themselves onto the Gateway is a separate ruling and out of scope.
///
/// REPLACED WHOLE. Registering again overwrites the row, seats included, so the registry is always exactly the
/// last manifest a factory registered and never a merge of two of them.
/// </summary>
public sealed class FactoryRegistryEntity : TenantScopedEntity
{
    /// <summary>The factory id, in the one spelling (lower-case letters and digits joined by hyphens).</summary>
    public string Factory { get; set; } = "";

    /// <summary>The factory's name as the owner reads it.</summary>
    public string Title { get; set; } = "";

    /// <summary>The factory's folder: an absolute path on <see cref="Computer"/>.</summary>
    public string Folder { get; set; } = "";

    /// <summary>The computer (machine name) the factory runs on.</summary>
    public string Computer { get; set; } = "";

    /// <summary>The CEO's seat id, or null when the factory has no CEO. Always one of the seats when set.</summary>
    public string? CeoSeat { get; set; }

    /// <summary>The goal's text as registered, or null when the factory has no goal yet.</summary>
    public string? GoalText { get; set; }

    /// <summary>The goal file, relative to the folder, or null.</summary>
    public string? GoalFile { get; set; }

    /// <summary>The day the owner approved the goal, <c>YYYY-MM-DD</c>, or null.</summary>
    public string? GoalApprovedOn { get; set; }

    /// <summary>One line on what the factory is for, shown under its name on the Factories cards, or null.</summary>
    public string? Purpose { get; set; }

    /// <summary>The seats, as JSON (a list of <c>RegisteredFactorySeatDto</c>). Kept on the row rather than in a
    /// table of their own because they are only ever written and read together with it: a seat has no life
    /// outside its factory's registration, and a replace must never leave half of an old seat list behind.</summary>
    public string SeatsJson { get; set; } = "[]";

    /// <summary>Who registered it: <c>session &lt;id&gt;</c> or <c>the owner (&lt;credential&gt;)</c>.</summary>
    public string RegisteredBy { get; set; } = "";

    /// <summary>When it was last registered (UTC).</summary>
    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>When the owner archived it (UTC), or null while it is on the Factories list. An archived factory
    /// keeps its row, its history and its memory; it only leaves the list (Factories screen mission, round 2).
    /// Registering again does not restore it - only the owner's Restore does.</summary>
    public DateTime? ArchivedAtUtc { get; set; }

    /// <summary>Who archived it: <c>owner (&lt;credential&gt;)</c>. Null while not archived.</summary>
    public string? ArchivedBy { get; set; }

    /// <summary>The schedule ids the archive switched off, as JSON (a list of strings), so Restore switches back on
    /// exactly those and never one somebody else switched off. Null while not archived.</summary>
    public string? ArchivedSchedulesJson { get; set; }
}

/// <summary>
/// ONE POSTED GOAL NUMBER, in the <c>factory_goal_numbers</c> table (Factories screen mission, phase A). A
/// factory's CEO posts the number its goal is measured by on every run - the value, its unit, the day it is as of,
/// and a link to how it was measured. Every post is kept, so the number's history is there to read; the factory's
/// page shows the newest.
///
/// The key is a Guid the Gateway mints for each post (<see cref="GatewayMintedKeyEntity"/>): no caller ever names
/// it, so it needs no tenant in it.
/// </summary>
public sealed class FactoryGoalNumberEntity : GatewayMintedKeyEntity
{
    /// <summary>The registered factory the number is for.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The number, as text ("1,240", "not yet proven").</summary>
    public string Value { get; set; } = "";

    /// <summary>What the number counts.</summary>
    public string Unit { get; set; } = "";

    /// <summary>The day the number is as of, <c>YYYY-MM-DD</c>.</summary>
    public string AsOf { get; set; } = "";

    /// <summary>An http or https link to how it was measured.</summary>
    public string Link { get; set; } = "";

    /// <summary>The seat id that posted it - always a seat of the factory's registration at the time.</summary>
    public string PostedBy { get; set; } = "";

    /// <summary>The session that made the call, or null when it was not a session.</summary>
    public string? PostedBySession { get; set; }

    /// <summary>When it was posted (UTC). The newest is the one shown.</summary>
    public DateTime PostedAtUtc { get; set; }
}
