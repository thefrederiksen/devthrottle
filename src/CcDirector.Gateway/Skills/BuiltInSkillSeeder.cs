using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Skills;

/// <summary>
/// Writes the shipped built-in skills (<see cref="BuiltInSkills"/> + their embedded bodies) into the
/// persisted skill store at startup. Built-ins are READ-ONLY: they are DevThrottle's, this seeder is
/// the ONLY writer of their content, and the register always tracks the RUNNING binary:
///
///  - Absent skill: seeded as version 1, published, with the shipped content.
///  - Present with a different published hash than this binary ships: the shipped bundle is published
///    as the next version and the previous one is superseded - in BOTH directions, so a rollback
///    republishes the older body and the minted version row is the honest record of what the fleet was
///    served. Superseded versions remain readable forever by explicit version.
///
/// Built-ins keep their shipped register order via deliberately staggered CreatedUtc stamps (the
/// register lists in creation order).
/// </summary>
public static class BuiltInSkillSeeder
{
    /// <summary>Seed or upgrade every built-in skill inside the given context. Called by the store's
    /// constructor under its write lock; saves once per changed skill.</summary>
    public static void Seed(GatewayDbContext ctx) => Seed(ctx, BuiltInSkills.All());

    /// <summary>Seed against an explicit definition list. The production path passes
    /// <see cref="BuiltInSkills.All"/>; tests pass a modified copy (for example a definition marked
    /// <see cref="SkillDefinition.ShipsSwitchedOff"/>) because no shipped skill may need to be one.</summary>
    internal static void Seed(GatewayDbContext ctx, IReadOnlyList<SkillDefinition> definitions)
    {
        if (ctx is null)
            throw new ArgumentNullException(nameof(ctx));
        if (definitions is null)
            throw new ArgumentNullException(nameof(definitions));

        var baseUtc = DateTime.UtcNow;

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var shippedHash = ShippedHash(definition);

            var head = ctx.Skills.FirstOrDefault(h => h.Id == definition.Id);
            if (head is null)
            {
                SeedFresh(ctx, definition, shippedHash, baseUtc.AddMilliseconds(i));
                continue;
            }

            if (!head.IsBuiltIn)
                CompletePromotion(ctx, head);
            ReconcileShippedSwitch(ctx, head, definition);

            // The invariant is that the PUBLISHED version IS the shipped bundle. Compare the published
            // hash - not merely the recorded shipped hash - so a row that drifted for any reason is
            // brought back to what this binary ships.
            var published = ctx.SkillVersions.AsNoTracking().FirstOrDefault(
                v => v.SkillId == head.Id && v.Status == SkillVersionStatus.Published);
            if (published is not null &&
                string.Equals(published.ContentHash, shippedHash, StringComparison.Ordinal))
            {
                if (!string.Equals(head.ShippedContentHash, shippedHash, StringComparison.Ordinal))
                {
                    head.ShippedContentHash = shippedHash; // heal a stale record; content already right
                    ctx.SaveChanges();
                }
                continue; // the fleet is already served exactly what this binary ships.
            }

            UpgradeShippedContent(ctx, head, definition, shippedHash);
        }
    }

    /// <summary>The canonical bundle hash of what THIS binary ships for a built-in definition.</summary>
    public static string ShippedHash(SkillDefinition definition) =>
        SkillContentHash.ForBundle(
            definition.Name, definition.Summary, definition.Triggers,
            BuiltInSkills.BodyFor(definition.Id), Array.Empty<SkillContentHash.HashedFile>());

    /// <summary>Build a fully-populated PUBLISHED version row from the running binary's shipped
    /// content, so "what shipped content becomes as a version row" has exactly one definition.</summary>
    public static SkillVersionEntity BuildShippedVersion(
        GatewayDbContext ctx, SkillDefinition definition, int versionNumber, DateTime createdUtc) => new()
    {
        TenantId = ctx.ActiveTenant!,
        SkillId = definition.Id,
        Version = versionNumber,
        Status = SkillVersionStatus.Published,
        Name = definition.Name,
        Summary = definition.Summary,
        Triggers = definition.Triggers.ToList(),
        BodyMarkdown = BuiltInSkills.BodyFor(definition.Id),
        ContentHash = ShippedHash(definition),
        AuthoredBy = "gateway:shipped",
        ChangeNote = "Shipped built-in content.",
        CreatedUtc = createdUtc,
        PublishedUtc = createdUtc,
    };

    private static void SeedFresh(
        GatewayDbContext ctx, SkillDefinition definition, string shippedHash, DateTime createdUtc)
    {
        var head = new SkillEntity
        {
            Id = definition.Id,
            TenantId = ctx.ActiveTenant!,
            IsBuiltIn = true,
            Archived = false,
            Enabled = !definition.ShipsSwitchedOff,
            LatestVersion = 1,
            PublishedVersion = 1,
            ShippedContentHash = shippedHash,
            CreatedUtc = createdUtc,
            UpdatedUtc = createdUtc,
        };
        ctx.Skills.Add(head);
        ctx.SkillVersions.Add(BuildShippedVersion(ctx, definition, versionNumber: 1, createdUtc));
        ctx.SaveChanges();
        FileLog.Write($"[BuiltInSkillSeeder] Seeded built-in skill: id={definition.Id}, v1, " +
                      $"hash={shippedHash[..12]}, shipsSwitchedOff={definition.ShipsSwitchedOff}");
    }

    /// <summary>
    /// PROMOTION: a skill the running binary ships is found as a TENANT row - it was published from a
    /// session and only later became part of the product (issue #3240). Two things happen, in BOTH the
    /// "content already matches" and the "content must be republished" cases:
    ///
    ///  1. The owner's on/off on that row is HIS CHOICE, so it is carried into his override row exactly
    ///     as it stands - on or off. Promotion never changes what he sees: a skill he switched off stays
    ///     off, and a skill he published and uses stays on even if the product ships it optional.
    ///  2. The row becomes built-in. Before this, only the republish path set IsBuiltIn, so a row whose
    ///     content already matched stayed a tenant row forever - writable, and read through its head
    ///     rather than the override.
    /// An override that already exists is the newer, explicit choice and is left alone.
    /// </summary>
    private static void CompletePromotion(GatewayDbContext ctx, SkillEntity head)
    {
        if (!ctx.SkillTenantOverrides.Any(o => o.SkillId == head.Id))
        {
            ctx.SkillTenantOverrides.Add(new SkillTenantOverrideEntity
            {
                TenantId = ctx.ActiveTenant!,
                SkillId = head.Id,
                Enabled = head.Enabled,
                UpdatedBy = "gateway:promotion",
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        head.IsBuiltIn = true;
        ctx.SaveChanges();
        FileLog.Write($"[BuiltInSkillSeeder] Promoted tenant skill to built-in: id={head.Id}, " +
                      $"owner's choice kept as enabled={head.Enabled}");
    }

    /// <summary>
    /// Keep a built-in's LIBRARY on/off in step with what this binary ships. For a built-in that row is
    /// nobody's choice - an account's choice lives in its own override row (SkillStore.SetEnabled never
    /// writes a built-in's head, and CompletePromotion moves a promoted row's choice out first) - so it
    /// means exactly one thing: "on or off for every account that has not chosen". The read paths fold
    /// an account's choice over it.
    /// </summary>
    private static void ReconcileShippedSwitch(GatewayDbContext ctx, SkillEntity head, SkillDefinition definition)
    {
        var shipped = !definition.ShipsSwitchedOff;
        if (head.Enabled == shipped)
            return;
        head.Enabled = shipped;
        ctx.SaveChanges();
        FileLog.Write($"[BuiltInSkillSeeder] Built-in on/off set to the shipped state: id={head.Id}, " +
                      $"enabled={shipped}");
    }

    private static void UpgradeShippedContent(
        GatewayDbContext ctx, SkillEntity head, SkillDefinition definition, string shippedHash)
    {
        var published = ctx.SkillVersions.FirstOrDefault(
            v => v.SkillId == head.Id && v.Status == SkillVersionStatus.Published);

        var now = DateTime.UtcNow;
        if (published is not null)
            published.Status = SkillVersionStatus.Superseded;
        var nextVersion = head.LatestVersion + 1;
        ctx.SkillVersions.Add(BuildShippedVersion(ctx, definition, nextVersion, now));
        head.LatestVersion = nextVersion;
        head.PublishedVersion = nextVersion;
        head.UpdatedUtc = now;
        head.ShippedContentHash = shippedHash;
        // PROMOTION. A skill can reach this branch as a TENANT row - it was published from a session
        // with 'skill publish' and only later became part of the product. Seeding its content is not
        // enough: IsBuiltIn is what makes it read-only, undeletable, and immune to a tenant skill
        // taking its id, and only SeedFresh used to set it. Without this line a promoted skill has the
        // shipped BODY but none of the protection, so the next 'skill push' overwrites what the product
        // ships and the seeder silently overwrites that back on the next restart - a flip-flop nobody
        // owns. Found while promoting the seven fleet-law skills (issue #3240).
        head.IsBuiltIn = true;
        ctx.SaveChanges();
        FileLog.Write($"[BuiltInSkillSeeder] Upgraded built-in skill: id={head.Id}, " +
                      $"v{nextVersion} published from shipped content, hash={shippedHash[..12]}");
    }
}
