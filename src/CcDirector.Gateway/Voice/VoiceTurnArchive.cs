using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// Durable, disk-backed store of completed voice-turn replies (issue: guaranteed audio-turn).
///
/// The in-memory <see cref="GatewayTurnJobStore"/> is the hot path for a turn in flight, but it
/// has a 10-minute TTL and is lost on a Gateway restart. This archive is the durability layer:
/// when a turn finishes, its result (summary text, transcript, and the reply MP3) is written
/// here keyed by <c>turnId</c>, so the reply "sits in the session" and is retrievable hours
/// later and across restarts. It also records the originating <c>uploadId</c> so a retried
/// completion finds the already-finished turn instead of starting a duplicate.
///
/// Layout: <c>{CcStorage.VoiceTurnArchive()}/tenants/&lt;tenant&gt;/&lt;turnId&gt;/meta.json</c> plus
/// <c>reply.mp3</c> (present only when TTS produced audio). One directory per turn, purged after
/// <see cref="RetentionHours"/>.
///
/// PARTITIONED BY TENANT (Hosted Multi-Tenancy, VOICE V1). A turn's transcript, its summary and its
/// reply audio are all customer content, so the partition is the DIRECTORY: a read for tenant A builds
/// a path under tenant A's folder and physically cannot open tenant B's turn, whatever turn id it is
/// handed. The tenant id is canonicalized before it becomes a path component and a shape this system
/// does not mint is REFUSED, never coerced - the same rule as <see cref="Prompts.GatewayPromptLog"/>.
/// Every public method takes the tenant as a REQUIRED parameter; there is no bare-turn-id read.
///
/// Best-effort: a persistence failure must never break a live turn, so the writers swallow and
/// log their own exceptions.
/// </summary>
public sealed class VoiceTurnArchive
{
    /// <summary>How long a completed turn's result stays retrievable.</summary>
    public const int RetentionHours = 24;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _root;
    // The owning host's deployment signal, fixed at construction (see GatewayHostOptions).
    private readonly bool _hosted;

    public VoiceTurnArchive(bool hosted) : this(hosted, CcStorage.VoiceTurnArchive()) { }

    /// <summary>Test seam: archive under an explicit root instead of the shared storage dir.</summary>
    /// <param name="hosted">The owning host's deployment signal (<see cref="GatewayHost.Hosted"/>). REQUIRED.</param>
    /// <param name="root">The archive root.</param>
    public VoiceTurnArchive(bool hosted, string root)
    {
        _hosted = hosted;
        _root = root;
        Directory.CreateDirectory(_root);
        MigrateLegacyUnpartitionedTurns();
    }

    /// <summary>
    /// True only for the EXACT form the tenant registry mints: a canonical lowercase GUID. The tenant id
    /// becomes a DIRECTORY NAME here, so it must be a shape this system actually produces rather than merely
    /// "characters that look harmless" - <c>".."</c> is built from harmless characters and canonicalizes to
    /// the parent partition, and an id differing only in letter case is a different identity to the
    /// case-sensitive tenants table while naming the SAME directory on Windows and Azure Files. One spelling
    /// only: parse strictly, then require the value to equal its own canonical round-trip.
    /// </summary>
    private static bool IsMintedAccountTenant(string value)
        => Guid.TryParseExact(value, "D", out var parsed)
           && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    /// <summary>
    /// The directory holding ONE tenant's archived turns. <see cref="TenantId.Local"/> is the fixed literal
    /// "local"; every other partition must be a minted account tenant. The reserved <see cref="TenantId.System"/>
    /// identity is deliberately refused rather than given a partition - no voice turn belongs to it.
    /// </summary>
    public string PartitionDirectoryFor(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A voice-turn archive partition needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
        if (!tenant.IsLocal && !IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot name a voice-turn archive partition.", nameof(tenant));

        var combined = Path.Combine(_root, "tenants", tenant.Value);

        // Belt and braces, because the cost of being wrong here is one tenant reading another's transcript
        // and playing its reply audio: the result must actually LIE INSIDE the partition root. The guard
        // above already excludes traversal, so this can only fire if it is ever loosened.
        var expectedRoot = Path.GetFullPath(Path.Combine(_root, "tenants")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(combined).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' resolves outside the voice-turn archive partition root.", nameof(tenant));

        return combined;
    }

    /// <summary>
    /// Deal, once, with turns archived BEFORE this store was partitioned - they sit directly under the root
    /// with no tenant recorded anywhere. The mode is the REQUIRED <c>hosted</c> constructor argument (read once
    /// at startup into <c>GatewayHostOptions</c>), never an optional one a caller could omit (an omitted
    /// argument would fail open into "keep").
    ///
    ///  - HOSTED: DELETE them. A turn whose owning account cannot be established must not be handed to a
    ///    guess; a lost turn is the cheap outcome, a mis-attributed transcript is a disclosure.
    ///  - SELF-HOST: MOVE them into the Local partition. One tenant, so attribution is unambiguous.
    /// </summary>
    private void MigrateLegacyUnpartitionedTurns()
    {
        try
        {
            var legacy = Directory.EnumerateDirectories(_root)
                .Where(d => !string.Equals(Path.GetFileName(d), "tenants", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (legacy.Count == 0) return;

            if (_hosted)
            {
                foreach (var dir in legacy) Directory.Delete(dir, recursive: true);
                FileLog.Write($"[VoiceTurnArchive] hosted: deleted {legacy.Count} pre-partition turn(s) - they carry no tenant, and a turn whose owner cannot be established is deleted rather than guessed");
                return;
            }

            var localDir = PartitionDirectoryFor(TenantId.Local);
            Directory.CreateDirectory(localDir);
            foreach (var dir in legacy)
            {
                var target = Path.Combine(localDir, Path.GetFileName(dir));
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(dir, target);
            }
            FileLog.Write($"[VoiceTurnArchive] self-host: moved {legacy.Count} pre-partition turn(s) into the local tenant partition (one tenant, so attribution is unambiguous)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] pre-partition turn migration FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Persist a completed turn's reply. Writes meta.json (always) and reply.mp3 (when audio is
    /// present), then purges aged turns. Best-effort: failures are logged, never thrown.
    /// </summary>
    public void Save(TenantId tenant, VoiceTurnArchiveRecord record, byte[]? replyAudio)
    {
        try
        {
            Purge(tenant);

            var dir = DirFor(tenant, record.TurnId);
            if (dir is null)
            {
                FileLog.Write($"[VoiceTurnArchive] Save: rejected non-GUID turnId={record.TurnId}");
                return;
            }
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(record, JsonOpts));
            if (replyAudio is { Length: > 0 })
                File.WriteAllBytes(Path.Combine(dir, "reply.mp3"), replyAudio);

            FileLog.Write($"[VoiceTurnArchive] Save: turnId={record.TurnId} sid={record.SessionId} " +
                          $"uploadId={record.UploadId} audioBytes={replyAudio?.Length ?? 0}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] Save FAILED turnId={record.TurnId}: {ex.Message}");
        }
    }

    /// <summary>The archived record for <paramref name="turnId"/>, or null when absent/aged-out.</summary>
    public VoiceTurnArchiveRecord? Get(TenantId tenant, string turnId)
    {
        var dir = DirFor(tenant, turnId);
        if (dir is null) return null;
        return ReadMeta(dir);
    }

    /// <summary>The reply MP3 bytes for <paramref name="turnId"/>, or null when there is no
    /// archived audio (no key configured at turn time, or turn absent/aged-out).</summary>
    public byte[]? GetAudio(TenantId tenant, string turnId)
    {
        var dir = DirFor(tenant, turnId);
        if (dir is null) return null;
        var path = Path.Combine(dir, "reply.mp3");
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] GetAudio FAILED turnId={turnId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Completed turns for a session, newest first, optionally only those at/after
    /// <paramref name="sinceUtc"/>. Scans the (bounded, retention-limited) archive dirs.
    /// </summary>
    public IReadOnlyList<VoiceTurnArchiveRecord> ListForSession(TenantId tenant, string sessionId, DateTime? sinceUtc = null)
    {
        var results = new List<VoiceTurnArchiveRecord>();
        try
        {
            foreach (var dir in EnumerateTurnDirs(tenant))
            {
                var rec = ReadMeta(dir);
                if (rec is null) continue;
                if (!string.Equals(rec.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                if (sinceUtc is { } since && rec.CreatedAtUtc < since) continue;
                results.Add(rec);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] ListForSession FAILED sid={sessionId}: {ex.Message}");
        }
        return results.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// The completed turn that originated from <paramref name="uploadId"/>, or null. Used to make a
    /// retried completion idempotent even after the in-memory job has expired.
    /// </summary>
    public VoiceTurnArchiveRecord? FindByUpload(TenantId tenant, string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) return null;
        try
        {
            foreach (var dir in EnumerateTurnDirs(tenant))
            {
                var rec = ReadMeta(dir);
                if (rec is not null && string.Equals(rec.UploadId, uploadId, StringComparison.OrdinalIgnoreCase))
                    return rec;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] FindByUpload FAILED uploadId={uploadId}: {ex.Message}");
        }
        return null;
    }

    // ====== internals ===============================================================

    private VoiceTurnArchiveRecord? ReadMeta(string dir)
    {
        var path = Path.Combine(dir, "meta.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VoiceTurnArchiveRecord>(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnArchive] ReadMeta skip {Path.GetFileName(dir)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Every archived turn directory inside ONE tenant's partition (empty when it has none).</summary>
    private IEnumerable<string> EnumerateTurnDirs(TenantId tenant)
    {
        var dir = PartitionDirectoryFor(tenant);
        return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : Enumerable.Empty<string>();
    }

    /// <summary>Delete one tenant's turn dirs older than the retention window. Best-effort.</summary>
    private void Purge(TenantId tenant)
    {
        var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
        foreach (var dir in EnumerateTurnDirs(tenant))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceTurnArchive] Purge skip {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
    }

    /// <summary>Per-turn dir INSIDE the tenant's partition, or null when the turn id is not GUID-shaped
    /// (so it can never escape the partition). The tenant folder comes first, so a valid turn id belonging
    /// to another tenant simply names a path that does not exist here.</summary>
    private string? DirFor(TenantId tenant, string turnId)
        => Guid.TryParse(turnId, out var g) ? Path.Combine(PartitionDirectoryFor(tenant), g.ToString("N")) : null;
}

/// <summary>
/// The persisted form of one completed voice turn. <see cref="HasAudio"/> mirrors the presence of
/// reply.mp3 so a list view can show "has a spoken reply" without opening the audio file.
/// </summary>
public sealed class VoiceTurnArchiveRecord
{
    public string TurnId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string Stage { get; set; } = "reply";
    public string Transcript { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool HasAudio { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
