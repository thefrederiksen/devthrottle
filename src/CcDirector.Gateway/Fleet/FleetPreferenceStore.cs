using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// The owner's standing preferences for the Fleet Manager - "stop asking me about draft posts, just stage
/// them" - over the <c>fleet_preferences</c> table (the Fleet Manager mission, step 3).
///
/// THE TEXT IS THE OWNER'S WORDS, STORED EXACTLY AS GIVEN: not trimmed, not reworded. A blank preference is
/// refused rather than stored, because a preference nobody can read is one the Fleet Manager would act on
/// without knowing what it says.
///
/// Tenant-partitioned by construction, like <see cref="FleetOutcomeStore"/>.
/// </summary>
public sealed class FleetPreferenceStore
{
    /// <summary>The longest preference accepted.</summary>
    public const int MaxTextLength = 2000;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetPreferenceStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Keep one preference and return it as stored.</summary>
    /// <param name="createdBy">The calling session id, or <see cref="FleetOutcomeStore.OwnerCaller"/>.</param>
    /// <exception cref="ArgumentException">The text is blank or too long.</exception>
    public FleetPreferenceDto Add(TenantId tenant, string? text, string createdBy, DateTime nowUtc)
    {
        FileLog.Write($"[FleetPreferenceStore] Add: tenant={tenant}, createdBy={createdBy}, length={text?.Length ?? 0}");
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("text is required: the owner's preference, in their own words");
            if (text.Length > MaxTextLength)
                throw new ArgumentException($"text is {text.Length} characters; the most accepted is {MaxTextLength}");
            if (string.IsNullOrWhiteSpace(createdBy))
                throw new ArgumentException($"createdBy is required: a session id or '{FleetOutcomeStore.OwnerCaller}'");

            var entity = new FleetPreferenceEntity
            {
                Text = text,
                CreatedAtUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime(),
                CreatedBy = createdBy,
            };
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetPreferences.Add(entity);
                ctx.SaveChanges();
            }

            FileLog.Write($"[FleetPreferenceStore] Add: stored id={entity.Id}");
            return ToDto(entity);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetPreferenceStore] Add FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Every preference this account holds, oldest first - the order the owner gave them.</summary>
    public IReadOnlyList<FleetPreferenceDto> List(TenantId tenant)
    {
        FileLog.Write($"[FleetPreferenceStore] List: tenant={tenant}");
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetPreferences.AsNoTracking()
            .OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id)
            .ToList();
        FileLog.Write($"[FleetPreferenceStore] List: returned={rows.Count}");
        return rows.Select(ToDto).ToList();
    }

    /// <summary>Remove one preference. False when this account holds none with that id.</summary>
    public bool Delete(TenantId tenant, Guid id)
    {
        FileLog.Write($"[FleetPreferenceStore] Delete: tenant={tenant}, id={id}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.FleetPreferences.FirstOrDefault(p => p.Id == id);
            if (row is null)
            {
                FileLog.Write($"[FleetPreferenceStore] Delete: id={id}, result=not found");
                return false;
            }
            ctx.FleetPreferences.Remove(row);
            ctx.SaveChanges();
            FileLog.Write($"[FleetPreferenceStore] Delete: id={id}, result=removed");
            return true;
        }
    }

    private static FleetPreferenceDto ToDto(FleetPreferenceEntity e) => new()
    {
        Id = e.Id.ToString(),
        Text = e.Text,
        CreatedAtUtc = e.CreatedAtUtc,
        CreatedBy = e.CreatedBy,
    };
}
