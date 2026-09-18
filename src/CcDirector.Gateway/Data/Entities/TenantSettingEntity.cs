namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One per-tenant setting override: a single (tenant, key) -&gt; value row in the <c>tenant_settings</c>
/// table (issue #2017, the hosted Settings page). This is the per-tenant HOME the hosted deny (issue #1863)
/// demanded before the AI / voice / notification settings could be served on the shared Gateway:
/// each of those values used to live process-globally in <c>config.json</c> with no tenant dimension, so one
/// subscriber changing "the thinking model" would change it for every tenant. A row here overrides the
/// operator's global default FOR ONE TENANT ONLY.
///
/// KEYING mirrors <see cref="MissionNoteEntity"/>: the setting <see cref="Key"/> (a fixed, ordinally-compared
/// identifier from <see cref="Settings.TenantSettingKeys"/>) is namespaced per tenant, so the primary key is
/// the COMPOSITE (tenant_id, Key). With Key alone two tenants overriding the same setting would collide at the
/// database; scoping the key by tenant lets each tenant own its own value for a given key. <c>tenant_id</c> and
/// the deny-by-default global query filter are inherited from <see cref="TenantScopedEntity"/>.
///
/// <see cref="Value"/> is the raw stored string exactly as the typed resolver serialized it (a model id, a
/// voice name, a snooze-presets list, an IANA time-zone id). The resolver owns parsing and validation; this
/// row stores the opaque text. An ABSENT row means "no override" - the resolver returns the operator global
/// default, never another tenant's value.
/// </summary>
public sealed class TenantSettingEntity : TenantScopedEntity
{
    /// <summary>The setting identifier (a fixed key from <see cref="Settings.TenantSettingKeys"/>). Part of the
    /// composite primary key with <c>tenant_id</c>; ordinally compared (SQLite BINARY / Postgres "C").</summary>
    public string Key { get; set; } = "";

    /// <summary>The overriding value for this tenant, as the typed resolver serialized it. Opaque text here.</summary>
    public string Value { get; set; } = "";

    /// <summary>When this override was last written (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
