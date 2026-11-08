using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Inbox;

/// <summary>Where an account's website key stands, with no key in it.</summary>
/// <param name="State">One of <see cref="WebsiteInboxKeyStore.States"/>.</param>
public sealed record WebsiteInboxKeyStatus(string State, DateTime? CreatedAtUtc, DateTime? RevokedAtUtc, DateTime? LastUsedAtUtc);

/// <summary>What a presented key is: unknown, revoked, or the account it writes to.</summary>
public sealed record WebsiteInboxKeyResolution(bool Known, bool Revoked, TenantId? Tenant)
{
    public static readonly WebsiteInboxKeyResolution Unknown = new(false, false, null);
}

/// <summary>
/// THE WEBSITE'S KEY TO ONE ACCOUNT'S YOUR INBOX (brief E): made, shown once, rotated and revoked by the owner from his
/// own phone or Cockpit, and resolved here when the website presents it. Only the SHA-256 of the key is stored. The key
/// can add a Your inbox message to the one account it was made for, and nothing else - no other route knows this kind of
/// key at all.
/// </summary>
public sealed class WebsiteInboxKeyStore
{
    /// <summary>Where a key stands. The words the settings card shows for each are folded in <c>Api.WebsiteInboxEndpoints</c>.</summary>
    public static class States
    {
        public const string None = "none";
        public const string Active = "active";
        public const string Revoked = "revoked";
    }

    /// <summary>What every key starts with, so a key pasted in the wrong place is recognisable.</summary>
    public const string KeyPrefix = "dtwi_";

    private readonly GatewayDatabase _db;

    public WebsiteInboxKeyStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The lower-case hexadecimal SHA-256 of a key, the only form ever stored.</summary>
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>
    /// Make a new key for <paramref name="tenant"/> and return it - the only time it is ever seen. A key the account had
    /// before is replaced, so it stops working at once.
    /// </summary>
    public string Mint(TenantId tenant, string deviceId, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var key = KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var at = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.WebsiteInboxKeys.FirstOrDefault(k => k.TenantId == tenant.Value);
        var replaced = row is not null;
        if (row is null)
        {
            row = new WebsiteInboxKeyEntity { TenantId = tenant.Value };
            ctx.WebsiteInboxKeys.Add(row);
        }
        row.KeyHash = Hash(key);
        row.CreatedAtUtc = at;
        row.CreatedByDeviceId = deviceId.Length <= 200 ? deviceId : deviceId[..200];
        row.RevokedAtUtc = null;
        row.LastUsedAtUtc = null;
        ctx.SaveChanges();
        FileLog.Write($"[WebsiteInboxKeyStore] Mint: tenant={tenant.ToLogString()} replaced={replaced}");
        return key;
    }

    /// <summary>Where the account's key stands.</summary>
    public WebsiteInboxKeyStatus Status(TenantId tenant)
    {
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.WebsiteInboxKeys.AsNoTracking().FirstOrDefault(k => k.TenantId == tenant.Value);
        if (row is null) return new WebsiteInboxKeyStatus(States.None, null, null, null);
        return new WebsiteInboxKeyStatus(row.RevokedAtUtc is null ? States.Active : States.Revoked, row.CreatedAtUtc, row.RevokedAtUtc, row.LastUsedAtUtc);
    }

    /// <summary>Revoke the account's key. Answers the new status; revoking when there is none, or again, changes nothing.</summary>
    public WebsiteInboxKeyStatus Revoke(TenantId tenant, DateTime nowUtc)
    {
        using (var ctx = _db.CreateUnscopedContext())
        {
            var row = ctx.WebsiteInboxKeys.FirstOrDefault(k => k.TenantId == tenant.Value);
            if (row is not null && row.RevokedAtUtc is null)
            {
                row.RevokedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
                ctx.SaveChanges();
                FileLog.Write($"[WebsiteInboxKeyStore] Revoke: tenant={tenant.ToLogString()}");
            }
        }
        return Status(tenant);
    }

    /// <summary>What a presented key is. The hash is looked up, then compared again in fixed time.</summary>
    public WebsiteInboxKeyResolution Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return WebsiteInboxKeyResolution.Unknown;
        var hash = Hash(key);
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.WebsiteInboxKeys.AsNoTracking().FirstOrDefault(k => k.KeyHash == hash);
        if (row is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.KeyHash), Encoding.ASCII.GetBytes(hash)))
            return WebsiteInboxKeyResolution.Unknown;
        if (row.RevokedAtUtc is not null)
            return new WebsiteInboxKeyResolution(true, true, null);
        return new WebsiteInboxKeyResolution(true, false, new TenantId(row.TenantId));
    }

    /// <summary>Record that the account's key was just used to write a message.</summary>
    public void Touch(TenantId tenant, DateTime nowUtc)
    {
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.WebsiteInboxKeys.FirstOrDefault(k => k.TenantId == tenant.Value);
        if (row is null) return;
        row.LastUsedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        ctx.SaveChanges();
    }
}
