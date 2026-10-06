using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Settings;

/// <summary>
/// THIS Gateway's stable id (devthrottle_internal#2311, review finding SK-F3): one value per Gateway database,
/// created the first time it is asked for and never regenerated. A Director names the library its skills came
/// from by this id plus the tenant, never by the address it reached the Gateway at - so a new domain, a move
/// to TLS or a changed address leaves the same person owning the same skills.
///
/// Stored as one <c>tenant_settings</c> row under the reserved <see cref="TenantId.System"/> tenant, key
/// <see cref="TenantSettingKeys.GatewayInstanceId"/>; no schema change.
///
/// WHAT FOLLOWS FROM "ONE VALUE PER DATABASE". A Gateway restored from a backup of its own database keeps its id,
/// which is right: it is the same Gateway and serves the same tenants. A SECOND deployment started from a COPY of
/// one database also carries the same id, and its tenants carry the same tenant ids, so a Director would see the
/// two as one library. That is only correct if the copy replaces the original; a copy meant to run beside it must
/// have this row deleted before it starts, and then makes its own.
/// </summary>
public sealed class GatewayInstanceIdentity
{
    private readonly TenantSettingsStore _settings;
    private readonly object _gate = new();
    private string? _id;

    public GatewayInstanceIdentity(TenantSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The id, read once and then held; created on the very first read of a new database.</summary>
    public string Get()
    {
        lock (_gate)
        {
            if (_id is not null)
                return _id;
            _id = _settings.GetOrAdd(TenantId.System, TenantSettingKeys.GatewayInstanceId,
                () => Guid.NewGuid().ToString("N"), DateTime.UtcNow);
            FileLog.Write($"[GatewayInstanceIdentity] Get: this Gateway is {_id}");
            return _id;
        }
    }
}
