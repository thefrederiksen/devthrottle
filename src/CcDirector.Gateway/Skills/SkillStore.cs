using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Skills;

/// <summary>
/// The Gateway's persisted skill register - the central skill library (devthrottle_internal issue
/// 995). Skills live in the EF data layer (<c>skills</c> / <c>skill_versions</c> / <c>skill_files</c>):
/// the head row is identity and lifecycle, every piece of content is an immutable version row, and the
/// built-in set (<see cref="BuiltInSkills"/>) is written in by <see cref="BuiltInSkillSeeder"/> at
/// construction.
///
/// WHY THIS EXISTS. Skills used to be copied onto every machine by the installer, so fixing one word
/// needed a release and every machine drifted the moment one was not re-installed - and only one agent
/// family ever saw them. Held here, a skill is written once and reachable by every agent on every
/// machine immediately. This register is an ADDITIONAL source of skills, not the only one: a machine's
/// own skills and a repository's own skills are untouched by all of this and take precedence on a name
/// clash. What the library adds is reach, not ownership.
///
/// THE SHARED LIBRARY. The built-ins are DevThrottle's: seeded once into the partition that is ambient
/// at CONSTRUCTION (the reserved System tenant on the hosted Gateway, Local on self-host) and served
/// READ-ONLY to every tenant from there. A tenant's register is the shared built-ins UNION the
/// tenant's own skills; id-scoped reads resolve the owning partition (the library always wins - it can
/// never be shadowed by a tenant skill, and creating a skill under a built-in id is refused). This is
/// the same deliberate cross-tenant read the workflow library makes: exactly one foreign partition,
/// built-in rows only, never written to on a request path - the ONLY writer of library content is the
/// boot-time seeder.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over
/// a fresh pooled context, preserving the single-writer invariant.
/// </summary>
public sealed class SkillStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The partition the built-in library lives in: the tenant that was ambient when this
    /// store was constructed (System on hosted, Local on self-host).</summary>
    // Captured by InitializeCore, which may run after construction (the Gateway defers it so its listener
    // can bind first), so this cannot be readonly. It is still written EXACTLY ONCE: InitializeCore is
    // guarded by _initialized and never runs twice. Empty until then, and nothing reads it before the
    // readiness gate opens.
    private string _libraryTenant = "";

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <param name="deferInitialize">
    /// When true the constructor validates arguments and stops; the caller must call
    /// <see cref="Initialize"/> once the database is open. The Gateway passes true so its listener can bind
    /// BEFORE any database work - the load below used to sit in front of the bind, and a slow database
    /// therefore delayed it past the platform's container-start deadline (#2383, #2585).
    ///
    /// The caller MUST run Initialize inside the same ambient tenant scope the constructor would have had.
    /// Nothing is served in the meantime: the readiness gate refuses every request but /healthz.
    /// </param>
    public SkillStore(GatewayDatabase db, bool deferInitialize = false)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));

        if (!deferInitialize)
            InitializeCore();
    }

    /// <summary>
    /// Run the deferred load. Idempotent, and a no-op for an instance whose constructor already did it.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        InitializeCore();
    }

    /// <summary>True once the load has run.</summary>
    public bool IsInitialized => _initialized;

    private bool _initialized;

    private void InitializeCore()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            _libraryTenant = ctx.ActiveTenant!;
            BuiltInSkillSeeder.Seed(ctx);
        }
        _initialized = true;
    }


    /// <summary>A fresh context scoped to the shared library partition. Read-only by contract on
    /// request paths (the seeder is the only library writer); caller disposes.</summary>
    private GatewayDbContext CreateLibraryContext() => _db.CreateContext(new TenantId(_libraryTenant));

    /// <summary>Open the context for the partition that OWNS skill <paramref name="key"/>: the shared
    /// library when the id is a built-in there, otherwise the ambient tenant's own partition. On
    /// self-host both are the same partition. Caller disposes.</summary>
    private GatewayDbContext OpenOwningContext(string key)
    {
        var library = CreateLibraryContext();
        if (library.Skills.AsNoTracking().Any(h => h.Id == key && h.IsBuiltIn))
            return library;
        library.Dispose();
        return _db.CreateContext();
    }

    /// <summary>True when <paramref name="key"/> is a built-in living in the shared library.</summary>
    private bool IsLibraryBuiltIn(string key)
    {
        using var library = CreateLibraryContext();
        return library.Skills.AsNoTracking().Any(h => h.Id == key && h.IsBuiltIn);
    }

    /// <summary>The ambient tenant's on/off choice for a built-in, or null when none was made.</summary>
    private bool? TenantChoiceFor(string key)
    {
        using var ctx = _db.CreateContext();
        return ctx.SkillTenantOverrides.AsNoTracking()
            .Where(o => o.SkillId == key)
            .Select(o => (bool?)o.Enabled)
            .FirstOrDefault();
    }

    /// <summary>The ambient tenant's on/off choices for a set of built-in ids, absent ids omitted.</summary>
    private Dictionary<string, bool> TenantChoicesFor(IReadOnlyCollection<string> keys)
    {
        using var ctx = _db.CreateContext();
        return ctx.SkillTenantOverrides.AsNoTracking()
            .Where(o => keys.Contains(o.SkillId))
            .ToList()
            .ToDictionary(o => o.SkillId, o => o.Enabled, StringComparer.Ordinal);
    }

    /// <summary>A built-in head's enabled state as THIS tenant sees it: the tenant's own choice when
    /// one exists, else the library's state. User-owned heads keep their head-row switch.</summary>
    private bool EffectiveEnabled(SkillEntity head) =>
        head.IsBuiltIn ? TenantChoiceFor(head.Id) ?? head.Enabled : head.Enabled;

    /// <summary>Why the agent path refuses a switched-off skill, in words that are true for THIS
    /// tenant: an optional built-in nobody here has turned on was never disabled by anyone, and saying
    /// "the owner disabled it" would send an agent to ask the owner about a choice he never made.</summary>
    private string OffRefusal(SkillEntity head, string key)
    {
        var optionalNeverChosen = head.IsBuiltIn && !head.Enabled && TenantChoiceFor(head.Id) is null;
        if (optionalNeverChosen)
            return $"Skill '{key}' is an OPTIONAL DevThrottle skill and is not turned on for this " +
                   "account. Ask the owner before turning it on. It is turned on in " +
                   "the cockpit's Skills page or with: cc-devthrottle skill enable " + key;
        return $"Skill '{key}' is turned OFF on this fleet. The owner disabled it, so it must " +
               "not be used - do not work around this by reading its history. It can be " +
               "switched back on in the cockpit or with: cc-devthrottle skill enable " + key;
    }

    /// <summary>
    /// Built-ins are READ-ONLY: they are DevThrottle's, maintained by shipping a new binary, and the
    /// only writer of their content is the boot-time seeder. Every authoring write refuses a built-in
    /// id with the pointer at the sanctioned customization path - clone.
    /// </summary>
    private void RefuseBuiltInEdit(string key, string verb)
    {
        if (IsLibraryBuiltIn(key))
            throw new SkillValidationException(
                $"'{key}' is a built-in DevThrottle skill and cannot be {verb}. Built-ins are " +
                "maintained by DevThrottle and update with the Gateway itself. To customize it, " +
                "clone it into your own skill: cc-devthrottle skill clone " + key + " <new-id>");
    }

    // ---- reads ------------------------------------------------------------------------------------

    /// <summary>
    /// Every skill with a published version, projected from that published version, in stable register
    /// order (creation order; the seeder stamps the built-ins so they keep their shipped order).
    /// Draft-only and archived skills are omitted. OFF skills ARE listed - the register must show one
    /// to switch it back on - and carry <see cref="SkillDto.Enabled"/> false so the briefing renderer
    /// leaves them out.
    /// </summary>
    public IReadOnlyList<SkillDto> ListPublished()
    {
        lock (_gate)
        {
            var result = new List<SkillDto>();
            var builtInIds = new HashSet<string>(StringComparer.Ordinal);

            using (var library = CreateLibraryContext())
            {
                foreach (var dto in ReadPublished(library, builtIns: true, exclude: null))
                {
                    builtInIds.Add(dto.Id);
                    result.Add(dto);
                }
            }

            // Fold this tenant's own on/off choices over the library's shipped state. At this point the
            // result holds ONLY built-ins, so the stamp cannot touch a user row.
            var choices = TenantChoicesFor(builtInIds);
            foreach (var dto in result)
            {
                if (choices.TryGetValue(dto.Id, out var chosen))
                    dto.Enabled = chosen;
            }

            using (var ctx = _db.CreateContext())
            {
                // The exclusion is the shadow guard's belt-and-suspenders: a tenant row that somehow
                // shares a built-in id must not double-list.
                result.AddRange(ReadPublished(ctx, builtIns: false, exclude: builtInIds));
            }

            return result;
        }
    }

    /// <summary>One register pass over one partition: published heads, projected from their published
    /// versions, in creation order.</summary>
    private static IReadOnlyList<SkillDto> ReadPublished(
        GatewayDbContext ctx, bool builtIns, HashSet<string>? exclude)
    {
        var heads = ctx.Skills.AsNoTracking()
            .Where(h => h.IsBuiltIn == builtIns && !h.Archived && h.PublishedVersion != null)
            .ToList()
            .Where(h => exclude is null || !exclude.Contains(h.Id))
            .OrderBy(h => h.CreatedUtc)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .ToList();
        if (heads.Count == 0)
            return Array.Empty<SkillDto>();

        var ids = heads.Select(h => h.Id).ToList();
        var versions = ctx.SkillVersions.AsNoTracking()
            .Where(v => ids.Contains(v.SkillId) && v.Status == SkillVersionStatus.Published)
            .ToList()
            .ToDictionary(v => v.SkillId, StringComparer.Ordinal);
        var draftIds = ctx.SkillVersions.AsNoTracking()
            .Where(v => v.Status == SkillVersionStatus.Draft)
            .Select(v => v.SkillId)
            .ToHashSet(StringComparer.Ordinal);

        // File COUNTS, never file content: the listing is what every briefing is built from, so it
        // carries a number and the bodies stay behind the fetch.
        var versionIds = versions.Values.Select(v => v.Id).ToList();
        var fileCounts = ctx.SkillFiles.AsNoTracking()
            .Where(f => versionIds.Contains(f.VersionId))
            .Select(f => f.VersionId)
            .ToList()
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        return heads
            .Select(h => ToDto(h, versions[h.Id], draftIds.Contains(h.Id),
                fileCounts.TryGetValue(versions[h.Id].Id, out var count) ? count : 0))
            .ToList();
    }

    /// <summary>One skill by id (case-insensitive), projected from its published version. Null when the
    /// skill is absent, archived, or has never been published.</summary>
    public SkillDto? GetPublished(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var key = NormalizeId(id);

        lock (_gate)
        {
            using var ctx = OpenOwningContext(key);
            var head = ctx.Skills.AsNoTracking().FirstOrDefault(h => h.Id == key);
            if (head is null || head.Archived || head.PublishedVersion is null)
                return null;

            var version = ctx.SkillVersions.AsNoTracking().FirstOrDefault(
                v => v.SkillId == head.Id && v.Status == SkillVersionStatus.Published);
            if (version is null)
                throw new InvalidOperationException(
                    $"Skill '{head.Id}' claims published version {head.PublishedVersion} but no " +
                    "published version row exists. The skill store is inconsistent; refusing to serve " +
                    "a half-skill.");

            var hasDraft = ctx.SkillVersions.AsNoTracking().Any(
                v => v.SkillId == head.Id && v.Status == SkillVersionStatus.Draft);
            var fileCount = ctx.SkillFiles.AsNoTracking().Count(f => f.VersionId == version.Id);
            var dto = ToDto(head, version, hasDraft, fileCount);
            if (head.IsBuiltIn)
                dto.Enabled = EffectiveEnabled(head);
            return dto;
        }
    }

    /// <summary>
    /// The raw skill body - the agent read path, and the only place a body is ever served. With an
    /// explicit version, any PUBLISHED or SUPERSEDED row serves (immutable history) but never the
    /// draft: a draft is mutable, so serving it as pinned history would be a lie. Without a version,
    /// the published version of a live skill serves, and a draft-only or archived skill yields null.
    ///
    /// THE OFF SWITCH APPLIES TO EVERY READ THROUGH THIS METHOD, PINNED OR NOT - and that is where
    /// skills deliberately DIFFER from workflows. A workflow lets a pinned read through because a
    /// seated run pinned a version and its conduct must not vanish mid-run. Nothing pins a skill
    /// across time: an agent fetching one is always asking "give me the current instructions", and
    /// the command line resolves the head version and then asks for it BY NUMBER. Exempting pinned
    /// reads here would therefore have made the owner's switch bypassable by the exact command every
    /// agent uses - which is how it was found, by switching a skill off and watching it serve anyway.
    /// History and authoring still read an old version through the versions detail route; this is the
    /// AGENT path, and off means off on it.
    /// </summary>
    /// <exception cref="SkillValidationException">The skill exists but is switched OFF.</exception>
    public string? GetBody(string id, int? version)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = OpenOwningContext(key);
            var head = ctx.Skills.AsNoTracking().FirstOrDefault(h => h.Id == key);
            if (head is { Archived: false } && !EffectiveEnabled(head))
                throw new SkillValidationException(OffRefusal(head, key));
            var row = ResolveVersionRow(ctx, key, version);
            return row?.BodyMarkdown;
        }
    }

    /// <summary>One supporting file resolved for serving: the BYTES that belong on disk and the
    /// content type to serve them as. Binary files are decoded here, so a caller writes what it is
    /// given and never has to know how the bytes were stored.</summary>
    public sealed record SkillFilePayload(string FileName, byte[] Bytes, string ContentType, bool Executable);

    /// <summary>One supporting file, resolved like <see cref="GetBody"/> - including its off-switch
    /// rule. A switched-off skill's FILES must refuse too: a skill is its body and its files together,
    /// and letting the scripts through while refusing the instructions would leave the switch
    /// half-closed. <paramref name="filePath"/> is the file's relative path inside the skill.</summary>
    /// <exception cref="SkillValidationException">The skill exists but is switched OFF.</exception>
    public SkillFilePayload? GetFile(string id, string filePath, int? version)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        var key = NormalizeId(id);
        var path = filePath.Trim();
        lock (_gate)
        {
            using var ctx = OpenOwningContext(key);
            var head = ctx.Skills.AsNoTracking().FirstOrDefault(h => h.Id == key);
            if (head is { Archived: false } && !EffectiveEnabled(head))
                throw new SkillValidationException(
                    $"Skill '{key}' is turned OFF on this fleet, so its files are not served either. " +
                    "It can be switched back on with: cc-devthrottle skill enable " + key);
            var row = ResolveVersionRow(ctx, key, version);
            if (row is null)
                return null;
            var file = ctx.SkillFiles.AsNoTracking()
                .FirstOrDefault(f => f.VersionId == row.Id && f.FileName == path);
            if (file is null)
                return null;

            var isBase64 = SkillValidation.NormalizeEncoding(file.Encoding) == SkillValidation.EncodingBase64;
            var bytes = isBase64
                ? Convert.FromBase64String(file.Content)
                : System.Text.Encoding.UTF8.GetBytes(file.Content);
            // A binary file served as text would be corrupted by the encoding on the way out, so the
            // content type follows the stored encoding rather than being guessed from the extension.
            var contentType = isBase64 ? "application/octet-stream" : "text/plain; charset=utf-8";
            return new SkillFilePayload(file.FileName, bytes, contentType, file.Executable);
        }
    }

    /// <summary>The version history, newest first, no content bodies. Null when no such skill.</summary>
    public IReadOnlyList<SkillVersionInfoDto>? ListVersions(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = OpenOwningContext(key);
            if (!ctx.Skills.Any(h => h.Id == key))
                return null;
            return ctx.SkillVersions.AsNoTracking()
                .Where(v => v.SkillId == key)
                .ToList()
                .OrderByDescending(v => v.Version)
                .Select(v => new SkillVersionInfoDto
                {
                    Version = v.Version,
                    Status = v.Status,
                    ContentHash = v.ContentHash,
                    AuthoredBy = v.AuthoredBy,
                    ChangeNote = v.ChangeNote,
                    CreatedUtc = v.CreatedUtc,
                    PublishedUtc = v.PublishedUtc,
                })
                .ToList();
        }
    }

    /// <summary>One version's complete content snapshot, files included. Null when absent.</summary>
    public SkillVersionDetailDto? GetVersionDetail(string id, int version)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            using var ctx = OpenOwningContext(key);
            var row = ctx.SkillVersions.AsNoTracking().FirstOrDefault(
                v => v.SkillId == key && v.Version == version);
            if (row is null)
                return null;
            var files = ctx.SkillFiles.AsNoTracking().Where(f => f.VersionId == row.Id).ToList();
            return ToVersionDetail(row, files);
        }
    }

    // ---- authoring --------------------------------------------------------------------------------
    // Drafts are the safety boundary: every write lands on the single mutable draft row; nothing any
    // agent reads changes until publish, and publish is one SaveChanges.

    /// <summary>Create a new skill as a draft (invisible to the register and every briefing until
    /// published).</summary>
    /// <exception cref="SkillValidationException">The content violates the rules (HTTP 400).</exception>
    /// <exception cref="SkillConflictException">The id is already taken (HTTP 409).</exception>
    public SkillVersionDetailDto CreateDraft(SkillContentRequest content)
    {
        SkillValidation.ValidateId(content?.Id);
        SkillValidation.ValidateDraft(content!);
        var id = NormalizeId(content!.Id!);

        lock (_gate)
        {
            // The library can never be shadowed: a tenant creating "move-session" would otherwise mint
            // a skill the resolution rules could not serve (the built-in always wins by id).
            if (IsLibraryBuiltIn(id))
                throw new SkillConflictException(
                    $"'{id}' is a built-in DevThrottle skill; its id is reserved. Create your skill " +
                    "under a different id, or clone the built-in.");

            using var ctx = _db.CreateContext();
            if (ctx.Skills.Any(h => h.Id == id))
                throw new SkillConflictException($"A skill with id '{id}' already exists.");

            var now = DateTime.UtcNow;
            var head = new SkillEntity
            {
                Id = id,
                TenantId = ctx.ActiveTenant!,
                IsBuiltIn = false,
                Archived = false,
                LatestVersion = 1,
                PublishedVersion = null,
                ShippedContentHash = null,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            ctx.Skills.Add(head);
            var (version, files) = BuildDraftRow(ctx, id, versionNumber: 1, content, now);
            ctx.SkillVersions.Add(version);
            ctx.SkillFiles.AddRange(files);
            ctx.SaveChanges();

            FileLog.Write($"[SkillStore] CreateDraft: id={id}, v1 draft, authoredBy={version.AuthoredBy}");
            return ToVersionDetail(version, files);
        }
    }

    /// <summary>
    /// Replace the skill's draft content wholesale, minting the draft version if none exists. The
    /// optional If-Match hash is compared against the row the draft builds on: a mismatch means the
    /// caller edited a stale copy, and the write is refused rather than clobbering someone else's
    /// edit. Null when no such skill exists.
    /// </summary>
    public SkillVersionDetailDto? UpdateDraft(string id, SkillContentRequest content, string? ifMatchHash)
    {
        var key = NormalizeId(id);
        SkillValidation.ValidateDraft(content);

        lock (_gate)
        {
            RefuseBuiltInEdit(key, "edited");
            using var ctx = _db.CreateContext();
            var head = ctx.Skills.FirstOrDefault(h => h.Id == key);
            if (head is null)
                return null;
            if (head.Archived)
                throw new SkillValidationException($"Skill '{key}' is archived.");

            var draft = ctx.SkillVersions.FirstOrDefault(
                v => v.SkillId == key && v.Status == SkillVersionStatus.Draft);
            var baseline = draft ?? ctx.SkillVersions.FirstOrDefault(
                v => v.SkillId == key && v.Status == SkillVersionStatus.Published);
            if (ifMatchHash is not null && baseline is not null &&
                !string.Equals(ifMatchHash, baseline.ContentHash, StringComparison.Ordinal))
                throw new SkillConflictException(
                    "The skill changed since you read it (content hash mismatch). Pull the current " +
                    "content and reapply your edit.");

            var now = DateTime.UtcNow;
            SkillVersionEntity row;
            List<SkillFileEntity> files;
            if (draft is not null)
            {
                (row, files) = BuildDraftRow(ctx, key, draft.Version, content, draft.CreatedUtc, existing: draft);
                var oldFiles = ctx.SkillFiles.Where(f => f.VersionId == draft.Id).ToList();
                ctx.SkillFiles.RemoveRange(oldFiles);
                ctx.SkillFiles.AddRange(files);
            }
            else
            {
                var nextVersion = head.LatestVersion + 1;
                (row, files) = BuildDraftRow(ctx, key, nextVersion, content, now);
                ctx.SkillVersions.Add(row);
                ctx.SkillFiles.AddRange(files);
                head.LatestVersion = nextVersion;
            }
            head.UpdatedUtc = now;
            ctx.SaveChanges();

            FileLog.Write($"[SkillStore] UpdateDraft: id={key}, v{row.Version}, hash={row.ContentHash[..12]}");
            return ToVersionDetail(row, files);
        }
    }

    /// <summary>Publish the draft: the draft becomes the published version, the previous published
    /// version is superseded, all in one atomic save. From this moment every agent that fetches the
    /// skill gets the new content - there is nothing to deploy. Null when no such skill exists.</summary>
    public SkillDto? Publish(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            RefuseBuiltInEdit(key, "published over");
            using var ctx = _db.CreateContext();
            var head = ctx.Skills.FirstOrDefault(h => h.Id == key);
            if (head is null)
                return null;
            if (head.Archived)
                throw new SkillValidationException($"Skill '{key}' is archived.");

            var draft = ctx.SkillVersions.FirstOrDefault(
                v => v.SkillId == key && v.Status == SkillVersionStatus.Draft);
            if (draft is null)
                throw new SkillValidationException($"Skill '{key}' has no draft to publish.");
            SkillValidation.ValidateForPublish(draft.BodyMarkdown);

            var now = DateTime.UtcNow;
            var previous = ctx.SkillVersions.FirstOrDefault(
                v => v.SkillId == key && v.Status == SkillVersionStatus.Published);
            if (previous is not null)
                previous.Status = SkillVersionStatus.Superseded;
            draft.Status = SkillVersionStatus.Published;
            draft.PublishedUtc = now;
            head.PublishedVersion = draft.Version;
            head.UpdatedUtc = now;
            ctx.SaveChanges();

            var fileCount = ctx.SkillFiles.AsNoTracking().Count(f => f.VersionId == draft.Id);
            FileLog.Write($"[SkillStore] Publish: id={key}, v{draft.Version} published" +
                          (previous is null ? "" : $", v{previous.Version} superseded"));
            return ToDto(head, draft, hasDraft: false, fileCount);
        }
    }

    /// <summary>
    /// Clone a skill's PUBLISHED content into a new tenant-owned skill - the sanctioned customization
    /// path for the read-only built-ins, and equally usable on the tenant's own skills. The clone
    /// copies everything the published version carries - summary, triggers, body, supporting files -
    /// into version 1 of a fresh id, born PUBLISHED and fully editable; provenance is recorded on the
    /// version row's change note. Null when the source does not exist or has no published version.
    /// </summary>
    /// <exception cref="SkillValidationException">The new id is invalid.</exception>
    /// <exception cref="SkillConflictException">The new id is taken or is a built-in id.</exception>
    public SkillDto? Clone(string sourceId, string newId, string authoredBy)
    {
        var sourceKey = NormalizeId(sourceId);
        SkillValidation.ValidateId(newId);
        var id = NormalizeId(newId);

        lock (_gate)
        {
            if (IsLibraryBuiltIn(id))
                throw new SkillConflictException(
                    $"'{id}' is a built-in DevThrottle skill; its id is reserved. Pick a different id.");

            SkillVersionEntity sourceVersion;
            List<SkillFileEntity> sourceFiles;
            using (var src = OpenOwningContext(sourceKey))
            {
                var sourceHead = src.Skills.AsNoTracking().FirstOrDefault(h => h.Id == sourceKey);
                if (sourceHead is null || sourceHead.Archived || sourceHead.PublishedVersion is null)
                    return null;
                sourceVersion = src.SkillVersions.AsNoTracking().First(
                    v => v.SkillId == sourceKey && v.Status == SkillVersionStatus.Published);
                sourceFiles = src.SkillFiles.AsNoTracking()
                    .Where(f => f.VersionId == sourceVersion.Id)
                    .ToList();
            }

            using var ctx = _db.CreateContext();
            if (ctx.Skills.Any(h => h.Id == id))
                throw new SkillConflictException($"A skill with id '{id}' already exists.");

            var now = DateTime.UtcNow;
            var head = new SkillEntity
            {
                Id = id,
                TenantId = ctx.ActiveTenant!,
                IsBuiltIn = false,
                Archived = false,
                LatestVersion = 1,
                PublishedVersion = 1,
                ShippedContentHash = null,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            var version = new SkillVersionEntity
            {
                TenantId = ctx.ActiveTenant!,
                SkillId = id,
                Version = 1,
                Status = SkillVersionStatus.Published,
                Name = sourceVersion.Name,
                Summary = sourceVersion.Summary,
                Triggers = sourceVersion.Triggers.ToList(),
                BodyMarkdown = sourceVersion.BodyMarkdown,
                License = sourceVersion.License,
                Compatibility = sourceVersion.Compatibility,
                AllowedTools = sourceVersion.AllowedTools,
                Metadata = new Dictionary<string, string>(sourceVersion.Metadata),
                ContentHash = sourceVersion.ContentHash, // identical content, identical bundle hash
                AuthoredBy = string.IsNullOrWhiteSpace(authoredBy) ? "unknown" : authoredBy.Trim(),
                ChangeNote = $"Cloned from '{sourceKey}' v{sourceVersion.Version}.",
                CreatedUtc = now,
                PublishedUtc = now,
            };
            var files = sourceFiles.Select(f => new SkillFileEntity
            {
                TenantId = ctx.ActiveTenant!,
                VersionId = version.Id,
                FileName = f.FileName,
                Content = f.Content,
                Encoding = f.Encoding,
                Executable = f.Executable,
                ContentHash = f.ContentHash,
            }).ToList();

            ctx.Skills.Add(head);
            ctx.SkillVersions.Add(version);
            ctx.SkillFiles.AddRange(files);
            ctx.SaveChanges();

            FileLog.Write($"[SkillStore] Clone: '{sourceKey}' v{sourceVersion.Version} -> '{id}' v1 " +
                          $"({files.Count} files), authoredBy={version.AuthoredBy}");
            return ToDto(head, version, hasDraft: false, files.Count);
        }
    }

    /// <summary>Archive (soft-delete) a user-defined skill. Its versions remain readable by explicit
    /// version. False when no such skill exists.</summary>
    public bool Archive(string id)
    {
        var key = NormalizeId(id);
        lock (_gate)
        {
            if (IsLibraryBuiltIn(key))
                throw new SkillValidationException(
                    $"Skill '{key}' is built in and can never be deleted. Switch it off instead.");

            using var ctx = _db.CreateContext();
            var head = ctx.Skills.FirstOrDefault(h => h.Id == key);
            if (head is null)
                return false;
            if (head.IsBuiltIn)
                throw new SkillValidationException(
                    $"Skill '{key}' is built in and can never be deleted. Switch it off instead.");

            head.Archived = true;
            head.UpdatedUtc = DateTime.UtcNow;
            ctx.SaveChanges();
            FileLog.Write($"[SkillStore] Archive: id={key}");
            return true;
        }
    }

    /// <summary>
    /// The owner's switch: turn a skill on or off - built-ins included. Off leaves the skill out of
    /// every agent's launch briefing and refuses the default fetch, and deletes NOTHING; the flip is
    /// instant both ways for every subsequent read fleet-wide. False when no such skill exists.
    /// </summary>
    public bool SetEnabled(string id, bool enabled, string by)
    {
        // A governance change has an actor, always: who switched a fleet capability off is never
        // allowed to be nobody.
        if (string.IsNullOrWhiteSpace(by))
            throw new SkillValidationException(
                "Turning a skill on or off requires 'by' - who is making the change.");
        var key = NormalizeId(id);
        lock (_gate)
        {
            // A BUILT-IN's switch is per tenant: the shared library head row is read-only to tenants,
            // so the choice upserts into the tenant's own override row and the read paths fold it.
            if (IsLibraryBuiltIn(key))
            {
                using var ctx = _db.CreateContext();
                var choice = ctx.SkillTenantOverrides.FirstOrDefault(o => o.SkillId == key);
                if (choice is null)
                {
                    choice = new SkillTenantOverrideEntity
                    {
                        TenantId = ctx.ActiveTenant!,
                        SkillId = key,
                    };
                    ctx.SkillTenantOverrides.Add(choice);
                }
                choice.Enabled = enabled;
                choice.UpdatedBy = by.Trim();
                choice.UpdatedAtUtc = DateTime.UtcNow;
                ctx.SaveChanges();
                FileLog.Write($"[SkillStore] SetEnabled: id={key}, enabled={enabled}, by={by.Trim()} " +
                              "(built-in - per-tenant override)");
                return true;
            }

            using (var ctx = _db.CreateContext())
            {
                var head = ctx.Skills.FirstOrDefault(h => h.Id == key);
                if (head is null || head.Archived)
                    return false;

                head.Enabled = enabled;
                head.UpdatedUtc = DateTime.UtcNow;
                ctx.SaveChanges();
                FileLog.Write($"[SkillStore] SetEnabled: id={key}, enabled={enabled}, by={by.Trim()}");
                return true;
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private SkillVersionEntity? ResolveVersionRow(GatewayDbContext ctx, string key, int? version)
    {
        if (version.HasValue)
            return ctx.SkillVersions.AsNoTracking().FirstOrDefault(
                v => v.SkillId == key && v.Version == version.Value &&
                     v.Status != SkillVersionStatus.Draft);

        var head = ctx.Skills.AsNoTracking().FirstOrDefault(h => h.Id == key);
        if (head is null || head.Archived || head.PublishedVersion is null)
            return null;
        return ctx.SkillVersions.AsNoTracking().FirstOrDefault(
            v => v.SkillId == key && v.Status == SkillVersionStatus.Published);
    }

    /// <summary>Build (or rebuild, for an existing draft) the draft version row and its file rows from
    /// a full-replacement content request, computing the canonical bundle hash.</summary>
    private static (SkillVersionEntity Row, List<SkillFileEntity> Files) BuildDraftRow(
        GatewayDbContext ctx,
        string skillId,
        int versionNumber,
        SkillContentRequest content,
        DateTime createdUtc,
        SkillVersionEntity? existing = null)
    {
        var triggers = (content.Triggers ?? new List<string>()).Select(t => t.Trim()).ToList();
        var body = content.BodyMarkdown ?? "";
        var metadata = content.Metadata ?? new Dictionary<string, string>();
        var hashedFiles = (content.Files ?? new List<SkillFileDto>())
            .Select(f =>
            {
                var encoding = SkillValidation.NormalizeEncoding(f.Encoding);
                var carrier = f.Content ?? "";
                // Hash the bytes that will land on disk, not the string that carried them - validation
                // has already proved a base64 payload decodes, so this cannot throw here.
                var bytes = encoding == SkillValidation.EncodingBase64
                    ? Convert.FromBase64String(carrier)
                    : System.Text.Encoding.UTF8.GetBytes(carrier);
                return new
                {
                    f.FileName,
                    Content = carrier,
                    Encoding = encoding,
                    f.Executable,
                    Hash = SkillContentHash.ForFileBytes(bytes),
                };
            })
            .ToList();
        var contentHash = SkillContentHash.ForBundle(
            content.Name!, content.Summary!, triggers, body,
            hashedFiles.Select(f => new SkillContentHash.HashedFile(
                f.FileName, f.Hash, f.Encoding, f.Executable)),
            content.License, content.Compatibility, content.AllowedTools, metadata);

        var row = existing ?? new SkillVersionEntity
        {
            TenantId = ctx.ActiveTenant!,
            SkillId = skillId,
            Version = versionNumber,
            Status = SkillVersionStatus.Draft,
            CreatedUtc = createdUtc,
        };
        row.Name = content.Name!;
        row.Summary = content.Summary!.Trim();
        row.Triggers = triggers;
        row.BodyMarkdown = body;
        row.License = content.License;
        row.Compatibility = content.Compatibility;
        row.AllowedTools = content.AllowedTools;
        row.Metadata = metadata;
        row.ContentHash = contentHash;
        row.AuthoredBy = string.IsNullOrWhiteSpace(content.AuthoredBy) ? "unknown" : content.AuthoredBy!;
        row.ChangeNote = content.ChangeNote;

        var files = hashedFiles.Select(f => new SkillFileEntity
        {
            TenantId = ctx.ActiveTenant!,
            VersionId = row.Id,
            FileName = f.FileName,
            Content = f.Content,
            Encoding = f.Encoding,
            Executable = f.Executable,
            ContentHash = f.Hash,
        }).ToList();
        return (row, files);
    }

    private static SkillVersionDetailDto ToVersionDetail(
        SkillVersionEntity row, IReadOnlyList<SkillFileEntity> files) => new()
    {
        SkillId = row.SkillId,
        Version = row.Version,
        Status = row.Status,
        Name = row.Name,
        Summary = row.Summary,
        Triggers = row.Triggers.ToList(),
        BodyMarkdown = row.BodyMarkdown,
        Files = files
            // Stable path order, so a pulled directory and a re-pulled one list identically and a
            // client comparing two detail reads is comparing content, not ordering.
            .OrderBy(f => f.FileName, StringComparer.Ordinal)
            .Select(f => new SkillFileInfoDto
            {
                FileName = f.FileName,
                ContentHash = f.ContentHash,
                Content = f.Content,
                Encoding = f.Encoding,
                Executable = f.Executable,
            }).ToList(),
        License = row.License,
        Compatibility = row.Compatibility,
        AllowedTools = row.AllowedTools,
        Metadata = new Dictionary<string, string>(row.Metadata),
        ContentHash = row.ContentHash,
        AuthoredBy = row.AuthoredBy,
        ChangeNote = row.ChangeNote,
        CreatedUtc = row.CreatedUtc,
        PublishedUtc = row.PublishedUtc,
    };

    private static string NormalizeId(string id) => (id ?? "").Trim().ToLowerInvariant();

    private static SkillDto ToDto(
        SkillEntity head, SkillVersionEntity version, bool hasDraft, int fileCount) => new()
    {
        Id = head.Id,
        Name = version.Name,
        Summary = version.Summary,
        Triggers = version.Triggers.ToList(),
        Version = version.Version,
        IsBuiltIn = head.IsBuiltIn,
        UpdatedUtc = head.UpdatedUtc,
        HasDraft = hasDraft,
        ContentHash = version.ContentHash,
        FileCount = fileCount,
        Enabled = head.Enabled,
        // The Gateway's editability verdict: built-ins are read-only, tenant-owned skills are the
        // caller's to edit. Clients render this verbatim (rule 7 - the client is dumb).
        Editable = !head.IsBuiltIn,
    };
}
