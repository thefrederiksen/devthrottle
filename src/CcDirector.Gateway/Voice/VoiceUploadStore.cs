using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// Gateway-side resumable upload staging for the guaranteed audio-turn front door.
///
/// The phone records with a MediaRecorder timeslice, so the audio arrives as an ordered
/// sequence of container fragments. Each fragment is stored to a per-upload dir as it lands
/// (SHA256-idempotent, so a retried chunk after a dropped connection is a free no-op), then
/// <see cref="Assemble"/> concatenates them IN ORDER back into one blob. A fragment is NOT
/// independently decodable (only fragment 0 carries the container header), so
/// reassemble-then-forward is the correct model.
///
/// Why this lives on the Gateway (not reusing the Director's <c>/voice/utterance</c> path):
/// the Director's complete step transcribes and posts text to the session - a different flow
/// that produces no audio reply. Here the Gateway buffers the chunks itself and then feeds the
/// assembled clip into the existing async voice-turn worker, so the resumable upload and the
/// audio-reply turn become one pipeline behind a single Gateway URL.
///
/// Transient by design for the voice-turn path: the per-upload dir is deleted once the turn has been
/// started (<see cref="Delete"/>), and anything that never got that far is bounded by the age-based
/// <see cref="SweepAbandoned"/>, which the Gateway host runs on a timer for the voice-turn staging root
/// (see the voice-turn upload sweep in <c>GatewayHost</c>). Both halves are needed: the success path alone
/// leaves every refused, dropped or incomplete upload staged forever, which on a hosted Gateway is recorded
/// speech accumulating with no retention bound at all.
///
/// The durable dictation path (issue #1183) adds a per-upload-id DELIVERY RECORD on top of the same
/// staging: while an upload is undelivered it stays PENDING (its chunks are retained for resume, never
/// age-swept), and on delivery or abandonment it becomes a small terminal tombstone
/// (<see cref="MarkDelivered"/> / <see cref="MarkAbandoned"/>) that discards the heavy chunk bytes but
/// keeps the outcome marker, so a delivered upload id is de-duplicated forever - across time and a
/// Gateway restart - until the client acknowledges it (<see cref="Acknowledge"/>). See
/// <see cref="DictationDeliveryRecord"/> for the model.
///
/// PARTITIONED BY TENANT (issue #1884). Every directory, chunk and record used to be keyed SOLELY by the
/// caller-supplied upload id, on one global root. A GUID is not a tenant boundary: secrecy of an identifier
/// is not authorization, and upload ids travel in client logs, retries and store-and-forward queues - so on
/// the hosted Gateway one account holding another's upload id could read its transcript, overwrite its
/// chunks, or resolve its record. An upload id is now only meaningful INSIDE its own tenant: the partition is
/// the DIRECTORY (<see cref="ForTenant"/>), so a read physically cannot open another tenant's staging, and
/// the record itself carries its tenant as a second, independent check. <see cref="TenantId.Local"/> keeps
/// today's path exactly - self-host is unchanged and nothing migrates - and a hosted account tenant lands
/// under tenants/&lt;id&gt;/. The tenant is always supplied by the caller, which resolved it from the
/// authenticated device key at the boundary; this type never guesses one.
///
/// THE TENANT IS REQUIRED AT CONSTRUCTION. There is deliberately no constructor that omits it, so an
/// unscoped store is not something to be guarded against - it cannot be built, and the attempt is a build
/// error at the call site rather than a live object with the widest possible reach. See the constructor
/// below for why the previous defaults were the wrong shape.
/// </summary>
public sealed class VoiceUploadStore
{
    /// <summary>The container directory hosting the non-local partitions, directly under the base root.</summary>
    public const string TenantPartitionDirectoryName = "tenants";

    // This partition's staging root: the base root for the local tenant, base/tenants/<id> otherwise.
    private readonly string _root;
    // The base root every partition is computed from, so ForTenant on an already-partitioned store still
    // resolves against the base rather than nesting a partition inside a partition.
    private readonly string _partitionBase;
    // The tenant this instance is bound to. Stamped onto every record written and required to match on
    // every record read.
    private readonly TenantId _tenant;
    // The pending-dictation cache, keyed by partition root and SHARED with every store this one spawns
    // through ForTenant, so one Gateway keeps one index across all its tenants. See DictationLockIndex for
    // why the session lock is answered from memory rather than by re-reading the staging root per session.
    private readonly DictationLockIndex _lockIndex;

    /// <summary>
    /// Stage under an explicit root, bound to ONE tenant.
    ///
    /// THE TENANT IS A REQUIRED ARGUMENT OF CONSTRUCTION, AND THAT IS THE WHOLE POINT OF THIS SIGNATURE.
    /// This type used to offer a no-argument constructor and a root-only constructor, both of which silently
    /// bound <see cref="TenantId.Local"/>. That made the widest scope the DEFAULT - reached by writing LESS
    /// code, not more - so a new call site could hold every account's staged audio while looking, to a
    /// reviewer, like an ordinary object creation with nothing to question. There is now no spelling of
    /// "make me an upload store" that does not name the partition it belongs to: an unscoped store is not
    /// guarded against, it is UNCONSTRUCTABLE, so there is no bypass to enumerate and none to forget.
    ///
    /// Naming <see cref="TenantId.Local"/> is still correct on self-host and still resolves to exactly the
    /// path it always did - but it is now WRITTEN DOWN at the call site, which is the difference between a
    /// decision a reviewer can see and one the compiler made on the author's behalf.
    /// </summary>
    /// <param name="root">The base staging root; the local partition is this directory itself.</param>
    /// <param name="tenant">The partition this store is bound to. Never guessed, never defaulted.</param>
    public VoiceUploadStore(string root, TenantId tenant)
        : this(PartitionRootFor(root, RequireTenant(tenant)), tenant, root, new DictationLockIndex()) { }

    private VoiceUploadStore(string root, TenantId tenant, string partitionBase, DictationLockIndex lockIndex)
    {
        _root = root;
        _tenant = tenant;
        _partitionBase = partitionBase;
        _lockIndex = lockIndex;
        // Through the index so it happens ONCE per root per process. This constructor runs on the read path -
        // GatewayHost builds a store with ForTenant for every session of every display-state fold - and
        // against the hosted Gateway's billed file share an unconditional CreateDirectory here was one more
        // metadata round trip per session every five seconds, for a directory that already exists.
        _lockIndex.EnsureRoot(_root, () => Directory.CreateDirectory(_root));
    }

    /// <summary>The tenant this store instance is bound to.</summary>
    public TenantId Tenant => _tenant;

    /// <summary>This partition's staging root on disk.</summary>
    public string Root => _root;

    /// <summary>
    /// A view of this staging bound to ONE tenant. Every path, gate, chunk and record of the returned store
    /// lives inside that tenant's partition, so an upload id from another tenant simply does not exist here -
    /// the isolation is the directory, not a predicate a later edit could forget to apply.
    /// </summary>
    public VoiceUploadStore ForTenant(TenantId tenant) =>
        new VoiceUploadStore(PartitionRootFor(_partitionBase, RequireTenant(tenant)), tenant, _partitionBase, _lockIndex);

    /// <summary>
    /// The single place a tenant is admitted into this type. An unresolved tenant is DENIED here rather than
    /// quietly becoming <see cref="TenantId.Local"/>: a default struct bypasses <see cref="TenantId"/>'s own
    /// validating constructor, so <c>default(TenantId)</c> is the one way a caller could still arrive without
    /// having decided anything, and it must not be the way in.
    /// </summary>
    private static TenantId RequireTenant(TenantId tenant)
        => tenant.IsValid
            ? tenant
            : throw new ArgumentException(
                "An upload partition needs a valid tenant; an unresolved tenant is denied, never defaulted.",
                nameof(tenant));

    /// <summary>
    /// True only for the EXACT form <see cref="Tenancy.TenantRegistry"/> mints: a canonical lowercase GUID.
    ///
    /// A tenant id becomes a DIRECTORY NAME here, so it must be a shape this system actually produces - not
    /// merely "characters that look harmless". Both structural aliases already found on the prompt-log
    /// partition (<c>GatewayPromptLog</c>) apply verbatim to this one:
    ///
    ///  - A character allow-list such as <c>^[A-Za-z0-9._-]{1,64}$</c> accepts <c>".."</c>, and combining the
    ///    base root with <c>tenants</c> and <c>".."</c> canonicalizes to exactly the base root - the LOCAL
    ///    partition.
    ///  - An allow-list accepting <c>A-F</c> as well as <c>a-f</c> lets two ids that differ only in case name
    ///    the SAME directory on Windows and Azure Files while being DIFFERENT identities to the
    ///    case-sensitive tenants table. That is one tenant reading another's audio through a casing alias.
    ///
    /// So this accepts ONE spelling: parse strictly, then require the value to equal its own canonical
    /// round-trip. Anything else is REFUSED rather than normalised - normalising is how two identities
    /// quietly share a folder, and this folder holds recorded AUDIO and its TRANSCRIPT.
    /// </summary>
    private static bool IsMintedAccountTenant(string value)
        => Guid.TryParseExact(value, "D", out var parsed)
           && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    /// <summary>
    /// The staging root one tenant's uploads live in. The local tenant keeps the root it has always used -
    /// self-host unchanged, nothing migrates - and every other tenant gets its own folder beneath it.
    /// </summary>
    private static string PartitionRootFor(string partitionBase, TenantId tenant)
    {
        if (tenant.IsLocal) return partitionBase;

        // Every other partition must be a minted account tenant - including the reserved SYSTEM tenant, which
        // is deliberately REFUSED rather than given a folder: no recorded audio belongs to it, so the safe
        // answer is that it has no partition at all.
        if (!IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot name an upload partition.",
                nameof(tenant));

        var combined = Path.Combine(partitionBase, TenantPartitionDirectoryName, tenant.Value);

        // Belt and braces, because the cost of being wrong here is one account reading another's dictation:
        // the result must actually LIE INSIDE the partition container. The rule above already excludes
        // traversal, so this can only fire if that rule is ever loosened - which is exactly when it is wanted.
        //
        // THIS GUARD HAS NO CANARY, DELIBERATELY, AND THAT IS NOT A COVERAGE GAP TO FILL. Measured, not
        // assumed: deleting these three lines and running the tenant-partition and dictation suites reddens
        // NOTHING. It cannot redden, because IsMintedAccountTenant above already refuses every value that
        // could escape the root - ".." and "../.." and "a/b" and any non-GUID - so no input this method can
        // legally receive reaches here with a combined path outside the container. It is unreachable by
        // construction, which is the whole reason it is belt and braces rather than the primary defence.
        //
        // So do NOT write a test for it: the only test that could exist here is one that cannot fail, and a
        // test that cannot fail is worse than no test - it reads as coverage. The condition under which this
        // guard becomes reachable, and therefore the condition under which it becomes testable and MUST be
        // given a canary, is precisely the moment someone loosens IsMintedAccountTenant. If you are here
        // because you just did that, this guard is now live and needs a test that fails when you remove it.
        var expectedRoot =
            Path.GetFullPath(Path.Combine(partitionBase, TenantPartitionDirectoryName)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(combined).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' resolves outside the upload partition root.", nameof(tenant));

        return combined;
    }

    /// <summary>
    /// The SECOND, independent check on a record: does the record on disk claim this partition's tenant?
    /// The directory already makes a cross-tenant read impossible, so this can only fire if a partition root
    /// is ever mis-computed - which is precisely the failure that must not silently hand over a transcript.
    ///
    /// An ABSENT tenant on the record is accepted ONLY for the local partition. That is not a fallback: it is
    /// the exact reading of records written before this field existed, all of which are self-host records in
    /// the local partition. In an account partition an absent tenant is refused, so a missing value can never
    /// become a way in.
    /// </summary>
    private bool BelongsHere(DictationDeliveryRecord record)
        => string.IsNullOrEmpty(record.Tenant)
            ? _tenant.IsLocal
            : string.Equals(record.Tenant, _tenant.Value, StringComparison.Ordinal);

    /// <summary>
    /// Begin (or re-open) an upload. The caller supplies a GUID id (it is also the
    /// idempotency key for the resulting turn); a missing/blank id mints a fresh one.
    /// Idempotent: re-registering the same id just ensures the folder exists.
    ///
    /// Registering an EXISTING id is a resume, and a resume is activity: it refreshes the upload's
    /// last-activity signal (see <see cref="EnsureFreshStaging"/>), so a client that comes back to finish an
    /// upload is never treated as abandoned by <see cref="SweepAbandoned"/>. Without this a resumed upload
    /// kept whatever stale timestamp it had and the age sweep would delete it out from under a live client.
    /// </summary>
    public string Register(string? uploadId)
    {
        var uid = NormalizeId(uploadId) ?? Guid.NewGuid().ToString("N");
        EnsureFreshStaging(uid);
        FileLog.Write($"[VoiceUploadStore] Register: uploadId={uid}");
        return uid;
    }

    /// <summary>True once <see cref="Register"/> has staged this upload (and it has not been swept).</summary>
    public bool Exists(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        return uid is not null && Directory.Exists(DirFor(uid));
    }

    /// <summary>
    /// The bytes this upload currently occupies on disk, optionally IGNORING one chunk index.
    ///
    /// The caller uses this to enforce the total-upload ceiling before staging another chunk. The
    /// exclusion is what makes that safe under the store's own idempotency: re-sending chunk 5 REPLACES
    /// chunk 5, it does not add to the total, so counting the copy already on disk would push a
    /// perfectly legal retry over the ceiling - and retries are the normal case on the mobile path this
    /// serves, not the exception.
    ///
    /// Returns 0 for an unknown upload. Best-effort: a chunk that vanishes mid-enumeration (the sweeper)
    /// is skipped rather than throwing, because this is a guard rail, not an accounting record.
    /// </summary>
    public long StagedBytes(string uploadId, int? excludeIndex = null)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return 0;
        var dir = DirFor(uid);
        if (!Directory.Exists(dir)) return 0;

        var exclude = excludeIndex is { } i ? ChunkPath(dir, i) : null;
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            if (exclude is not null && string.Equals(path, exclude, StringComparison.OrdinalIgnoreCase)) continue;
            try { total += new FileInfo(path).Length; }
            catch (FileNotFoundException) { /* swept mid-enumeration */ }
            catch (DirectoryNotFoundException) { /* swept mid-enumeration */ }
        }
        return total;
    }

    /// <summary>
    /// Store one chunk. Idempotent on (index, bytes): a chunk already on disk with the same
    /// SHA256 is accepted without rewriting, so retries are free. A supplied SHA that does not
    /// match the bytes is rejected so corruption never enters the assembly.
    /// </summary>
    public async Task StoreChunkAsync(string uploadId, int index, byte[] bytes, string? expectedSha, CancellationToken ct = default)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        if (index < 0) throw new InvalidOperationException("chunk index must be >= 0");
        if (bytes.Length == 0) throw new InvalidOperationException("empty chunk");

        var actualSha = Sha256Hex(bytes);
        if (!string.IsNullOrEmpty(expectedSha) &&
            !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"chunk {index} SHA mismatch: header={expectedSha} actual={actualSha}");
        }

        // Touch FIRST: storing a chunk is activity, so refresh the last-activity signal before doing the
        // work (this also creates the staging dir if it is new). Doing it up front, not at the end, means the
        // directory carries a fresh timestamp for the whole duration of the operation, so the age sweep
        // cannot judge it abandoned while this chunk is being written. It is here on EVERY successful chunk -
        // including the idempotent no-op below - because a client retrying the same chunk on a flaky link is
        // as alive as one sending a new one; judging liveness by whether a byte happened to land would cut
        // off exactly the resuming client this staging exists to serve.
        EnsureFreshStaging(uid);

        var dir = DirFor(uid);
        var path = ChunkPath(dir, index);

        // Idempotent: identical chunk already on disk -> no-op (still counted as activity, touched above).
        if (File.Exists(path) &&
            string.Equals(Sha256Hex(await File.ReadAllBytesAsync(path, ct)), actualSha, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[VoiceUploadStore] StoreChunk: uploadId={uid} index={index} already present (idempotent)");
            return;
        }

        // Atomic write (temp + move) so a half-written chunk never poisons the assembly.
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, path, overwrite: true);
        FileLog.Write($"[VoiceUploadStore] StoreChunk: uploadId={uid} index={index} bytes={bytes.Length}");
    }

    /// <summary>
    /// Reassemble chunks 0..totalChunks-1 in order. When a chunk is missing the partial upload
    /// is preserved (not discarded) and <see cref="AssembleResult.Missing"/> lists the indices the
    /// client must re-send. On success <see cref="AssembleResult.Audio"/> carries the full blob.
    /// </summary>
    public async Task<AssembleResult> AssembleAsync(string uploadId, int totalChunks, CancellationToken ct = default)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        var dir = DirFor(uid);
        if (!Directory.Exists(dir))
            return AssembleResult.Unknown();
        if (totalChunks <= 0)
            throw new InvalidOperationException("totalChunks must be > 0");

        // Assembling - whether it completes or comes back incomplete asking for more chunks - is a live
        // client working through its upload, so it is activity: refresh the last-activity signal so a slow
        // assemble/resend cycle is never judged abandoned mid-flight.
        EnsureFreshStaging(uid);

        // Completeness gate (issue #586 contract, applied here for the phone push-to-talk upload,
        // issue #592): every index 0..totalChunks-1 must be present AND non-empty. A missing OR
        // zero-byte chunk is "incomplete" - the result names the exact indices to re-send and NO
        // assembled clip is produced, so a truncated upload is refused, never transcribed.
        // ONE stat per chunk, not two. FileInfo caches what it read, so Exists and Length below are the same
        // observation - which matters here beyond tidiness: this gate's measurement is what the read-back
        // check compares against, and on a share that can answer two identical stats differently, measuring
        // with a second call would make the check argue with itself instead of with the read.
        var missing = new List<int>();
        var measured = new long[totalChunks];
        for (var i = 0; i < totalChunks; i++)
        {
            var info = new FileInfo(ChunkPath(dir, i));
            if (!info.Exists || info.Length == 0) { missing.Add(i); continue; }
            measured[i] = info.Length;
        }
        if (missing.Count > 0)
        {
            FileLog.Write($"[VoiceUploadStore] Assemble: uploadId={uid} INCOMPLETE missing={string.Join(',', missing)}");
            return AssembleResult.Incomplete(missing);
        }

        using var assembled = new MemoryStream();
        for (var i = 0; i < totalChunks; i++)
        {
            var part = await File.ReadAllBytesAsync(ChunkPath(dir, i), ct);
            // PER CHUNK, not on the total. Comparing only the sum lets two faults cancel out: one chunk
            // measured 100 reading 0 and another measured 100 reading 200 agree perfectly in aggregate, and
            // a scrambled recording would sail through into the transcriber. Each chunk is checked against
            // its own measurement, the moment it is read.
            var verdict = ReadBackVerdict(uid, i, measured[i], part.LongLength, totalChunks);
            if (verdict is not null) return verdict.Value;
            assembled.Write(part, 0, part.Length);
        }
        var bytes = assembled.ToArray();

        FileLog.Write($"[VoiceUploadStore] Assemble: uploadId={uid} chunks={totalChunks} totalBytes={bytes.Length}");
        return AssembleResult.Ok(bytes);
    }

    /// <summary>
    /// Remove ABANDONED voice-turn staging - an upload whose client dropped before completing (the staging is
    /// deleted on success only, so without this an interrupted upload would leak its recorded audio forever).
    /// Returns how many were removed.
    ///
    /// This is a DESTRUCTIVE sweep over the owner's own recorded audio, so it is deliberately lopsided: a
    /// wrong DELETE is unrecoverable, a wrong KEEP costs only disk, and the two are not close, so it leans
    /// entirely toward keeping. It therefore ENUMERATES THE ONE THING IT MAY DELETE and keeps everything
    /// else - the inverse of a deny-list. A directory is removed only when BOTH of these positively hold:
    ///
    ///  1. Its name IS a canonical upload id - the exact 32-hex-lowercase form this store itself writes (see
    ///     <see cref="IsCanonicalUploadDirName"/>). A malformed name, an almost-canonical one, an
    ///     upper-cased alias, a partial, the per-tenant partition container, a future sibling directory, or
    ///     anything else this sweep did not anticipate is NOT provably a disposable upload, so it SURVIVES.
    ///     Blocking one known name ("tenants") would delete every unknown one; admitting one known shape
    ///     deletes only what it can identify.
    ///
    ///  2. Its last-activity signal is genuinely older than <paramref name="maxAge"/>. Every successful
    ///     operation on an upload - register (including a resume of an existing id), an idempotent chunk, a
    ///     real chunk write, an assemble - refreshes that signal through <see cref="EnsureFreshStaging"/>, so
    ///     "stale" means nothing has touched it for the whole window, which is the definition of abandoned.
    ///
    /// RACE WITH A LIVE RESUME. The activity refresh and this age-check-and-delete run under the SAME
    /// per-upload gate (<see cref="WithRecordLock"/>, keyed by the canonicalized staging directory), and the
    /// age is RE-READ inside that gate immediately before the delete. So a resume that arrives concurrently
    /// is serialized against the delete for that one upload: either its refresh commits first and this sweep
    /// then reads a fresh timestamp and does not delete, or the delete commits first and the resume's own
    /// register re-creates the staging fresh. There is no window in which an upload is checked-old, then
    /// touched-by-a-resume, then deleted-anyway - the gate closes it.
    /// </summary>
    public int SweepAbandoned(TimeSpan maxAge)
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow - maxAge;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                // POSITIVELY ADMIT: delete only a directory whose name is exactly a canonical upload id.
                // Everything else - unknown, reserved, malformed, or a shape from a future change - survives,
                // aged or not, because none of them can be proven a disposable upload and a false delete is
                // unrecoverable.
                if (!IsCanonicalUploadDirName(name)) continue;

                // Under this upload's gate: re-read the age and delete atomically, so a concurrent resume
                // that refreshes activity cannot be interleaved between the check and the delete (see remarks).
                WithRecordLock(name, () =>
                {
                    try
                    {
                        if (Directory.Exists(dir) && Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        {
                            Directory.Delete(dir, recursive: true);
                            removed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[VoiceUploadStore] Sweep dir={dir} failed: {ex.Message}");
                    }
                });
            }
            // This sweep deletes staging directories by walking the root rather than through Delete, so the
            // session-lock cache is dropped wholesale and re-read once on the next question. Only when
            // something was actually removed: a sweep that deleted nothing changed nothing.
            if (removed > 0)
            {
                _lockIndex.Invalidate(_root);
                FileLog.Write($"[VoiceUploadStore] SweepAbandoned removed={removed} older than {maxAge}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] SweepAbandoned failed: {ex.Message}");
        }
        return removed;
    }

    /// <summary>
    /// Retire TERMINAL tombstones (DELIVERED / ABANDONED) that no client ever acknowledged and that are older
    /// than <paramref name="maxAge"/> (issue #1111).
    ///
    /// WHY THIS EXISTS, GIVEN THE RULE RIGHT ABOVE IT. <see cref="Acknowledge"/> is the designed retirement
    /// path and the record comment states the tombstone is retired "only on a real client ack, never by age".
    /// That is correct for an ack that is merely LATE - a lost ack should not race a delete, and the client
    /// re-acks after a re-complete. What it has no answer for is an ack that will NEVER come: a client that
    /// dropped its queue, was reinstalled, or simply never returned. Those tombstones are unreachable by the
    /// only mechanism that can retire them, so the store grows without any ceiling at all. Observed live: 28
    /// records, every one DELIVERED, the oldest three weeks old, none of them acknowledgeable by anything.
    /// This is the backstop for that case ONLY - it does not replace the ack, it bounds what the ack abandons.
    ///
    /// WHY IT IS NOT <see cref="SweepAbandoned"/>. That sweep deletes ANY canonical upload directory past the
    /// age, without reading its state. On the voice-turn staging root that is right, because idle IS abandoned
    /// there. On this root it would be wrong and dangerous: a PENDING record holds a live session lock and
    /// guards audio still queued for delivery, so age-deleting one would silently unlock a session and drop a
    /// dictation that was still owed. FAILED is likewise not terminal - <c>ClearFailed</c> can restore it to
    /// PENDING. So this method admits ONLY the two genuinely-final states and leaves everything else alone
    /// forever, however old. A stuck PENDING is a different bug and must stay visible rather than be tidied
    /// away by a cleanup that was never asked to make that judgement.
    ///
    /// THE DE-DUPE GUARANTEE. A tombstone stops a delivered id re-injecting if a client re-drives it. Deleting
    /// one after <paramref name="maxAge"/> only weakens that for a client returning after the whole window,
    /// which is why the caller sets it far beyond any real retry (see the constant at the call site) rather
    /// than to a tidy round number. Inside the window nothing changes.
    ///
    /// State and age are BOTH re-read inside the per-upload gate immediately before the delete, so a
    /// concurrent ack, re-complete, or resurrection cannot be interleaved between the check and the delete.
    /// Anything unreadable, half-written, or of an unrecognised shape SURVIVES - the same positive-admit
    /// discipline as the sweep above, because a false delete here is unrecoverable audio state.
    /// </summary>
    public int SweepResolvedTombstones(TimeSpan maxAge)
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow - maxAge;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                // POSITIVELY ADMIT, exactly as SweepAbandoned does: only a canonical upload id is a candidate.
                // The tenants container and the legacy quarantine are not canonical names, so both survive.
                if (!IsCanonicalUploadDirName(name)) continue;

                WithRecordLock(name, () =>
                {
                    try
                    {
                        if (!Directory.Exists(dir)) return;

                        // Re-read INSIDE the gate: an ack may have retired it, or a re-complete rewritten it,
                        // since the enumeration above.
                        var read = ReadRecordFile(RecordPath(dir));
                        // Only a marker we can READ is a candidate. No marker: nothing to retire. Malformed or
                        // unreadable: leave it standing - it is still the only evidence of what this upload id
                        // became, and the sweep has no business destroying evidence it cannot read (#2745).
                        if (read.Kind != DictationRecordReadKind.Present) return;
                        var record = read.Record!;
                        if (!IsRetirableTombstone(record.State)) return; // PENDING / FAILED are never aged out
                        if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) return;

                        Directory.Delete(dir, recursive: true);
                        removed++;
                        FileLog.Write($"[VoiceUploadStore] SweepResolvedTombstones retired uploadId={name} " +
                            $"state={record.State} (never acknowledged, older than {maxAge})");
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[VoiceUploadStore] SweepResolvedTombstones dir={dir} failed: {ex.Message}");
                    }
                });
            }
            // Same reason as SweepAbandoned: directories removed by walking the root, not through Delete.
            if (removed > 0)
            {
                _lockIndex.Invalidate(_root);
                FileLog.Write($"[VoiceUploadStore] SweepResolvedTombstones removed={removed} older than {maxAge}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] SweepResolvedTombstones failed: {ex.Message}");
        }
        return removed;
    }

    /// <summary>
    /// The only two states this cleanup may retire. Written as an explicit allow-list rather than "not
    /// PENDING" so a state added later is NOT swept by default - it has to be admitted deliberately.
    /// </summary>
    private static bool IsRetirableTombstone(DictationDeliveryState state)
        => state is DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned;

    /// <summary>
    /// Abandon PENDING dictations that have shown no activity for <paramref name="maxAge"/>, releasing the
    /// session lock they hold. Returns how many were expired.
    ///
    /// WHY A PENDING RECORD NEEDED A BOUND AT ALL. PENDING is the durable "there are undelivered words here"
    /// marker, and it deliberately NEVER auto-releases - that is the whole point of issue #1188, and nothing
    /// here changes it for a live dictation. But nothing released it for a DEAD one either: the record clears
    /// only when the client delivers or explicitly abandons, so a client that simply never comes back - the
    /// app closed, the phone replaced, a transcription that failed while the staging disk was full - left the
    /// marker on disk forever. Seven were found stuck on the hosted Gateway on 2026-08-28, the oldest FIVE
    /// WEEKS old, each still holding its session locked against human input. A lock nobody alive can clear is
    /// not durability, it is a session the owner can no longer type into.
    ///
    /// IT IS AN ABANDON, NOT A DELETE, AND THAT IS THE POINT. Expiring writes the ordinary ABANDONED
    /// tombstone through <see cref="MarkAbandoned"/> - the same transition the user's own "give up" button
    /// takes. So the session unlocks, the heavy chunk bytes are discarded, a small marker survives saying
    /// WHY, the upload id stays de-duplicated, and the existing tombstone sweep retires the marker later on
    /// its own schedule. Deleting the directory outright would skip the tombstone and let a late client
    /// re-drive an upload id the server had forgotten.
    ///
    /// AGE IS LAST ACTIVITY, NOT CREATION. It reads the same directory timestamp
    /// <see cref="EnsureFreshStaging"/> stamps on every register, resume, chunk and assemble, so a client
    /// that is still working - however slowly, however often it reconnects - keeps pushing its own deadline
    /// out and is never expired mid-dictation. Only silence ages.
    ///
    /// POSITIVELY ADMIT, and re-read INSIDE the gate, exactly as the two sweeps above do: only a canonical
    /// upload-id directory is a candidate, and the state and age are re-read under the upload's own lock, so
    /// a delivery landing between the enumeration and the decision wins rather than being overwritten by a
    /// stale verdict.
    /// </summary>
    public int ExpireStalePending(TimeSpan maxAge)
    {
        var expired = 0;
        var cutoff = DateTime.UtcNow - maxAge;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!IsCanonicalUploadDirName(name)) continue;

                WithRecordLock(name, () =>
                {
                    try
                    {
                        if (!Directory.Exists(dir)) return;

                        var read = ReadRecordFile(RecordPath(dir));
                        // No marker, a marker we cannot read, or any state but PENDING: not ours. A marker we
                        // cannot read holds no lock (see IsPending), so there is no lock here to release, and
                        // abandoning it would write over the only evidence of what it was (#2745). FAILED is
                        // left alone on purpose - it holds no session lock (IsPending is false while FAILED)
                        // and it is an explicit user-retryable pause, so ageing it out would cancel a retry the
                        // user was offered rather than release a lock nobody can clear.
                        if (read is not { Kind: DictationRecordReadKind.Present, Record.State: DictationDeliveryState.Pending }) return;
                        var record = read.Record!;
                        if (!BelongsHere(record)) return;               // another tenant's record: never ours to resolve
                        if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) return;

                        // The ordinary abandon transition. Re-enters this upload's gate, which is a Monitor
                        // and therefore reentrant on this thread.
                        MarkAbandoned(name, StalePendingReason);
                        expired++;
                        FileLog.Write($"[VoiceUploadStore] ExpireStalePending abandoned uploadId={name} " +
                            $"session={record.SessionId} (PENDING with no activity for more than {maxAge}; " +
                            "its session lock is released)");
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[VoiceUploadStore] ExpireStalePending dir={dir} failed: {ex.Message}");
                    }
                });
            }
            // No Invalidate here: MarkAbandoned goes through WriteRecordMarker, which drops each expired id
            // from the session-lock cache as it is written. Invalidating as well would force a needless
            // re-read of the whole partition.
            if (expired > 0)
                FileLog.Write($"[VoiceUploadStore] ExpireStalePending expired={expired} stale PENDING records older than {maxAge}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] ExpireStalePending failed: {ex.Message}");
        }
        return expired;
    }

    /// <summary>
    /// The reason stamped on an expired PENDING record. A distinct value, not the user's "user_abandoned":
    /// an operator reading the tombstone must be able to tell a dictation the USER gave up on from one the
    /// SERVER timed out, because only the second one means words were taken away from someone.
    /// </summary>
    public const string StalePendingReason = "expired_no_activity";

    /// <summary>
    /// Create the staging directory for an upload if it does not exist and stamp its last-activity signal to
    /// now, ATOMICALLY under the upload's gate. This is the one place activity is recorded, so every caller
    /// that represents a live client (register, a resume, a chunk, an assemble) refreshes the same signal the
    /// sweep judges by - liveness is an explicit touch, never an incidental side effect of a byte landing on
    /// disk. Running under the gate is what lets the sweep's age-check-and-delete not race a resume.
    /// </summary>
    private void EnsureFreshStaging(string uid)
    {
        WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            Directory.CreateDirectory(dir);
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
        });
    }

    /// <summary>
    /// THE READ-BACK CHECK, applied to ONE chunk. The completeness gate in <see cref="AssembleAsync"/>
    /// measures every chunk and refuses the upload unless all of them are present and non-empty. If the bytes
    /// we then READ for a chunk do not match that chunk's own measurement, the READ is wrong - not the upload
    /// - and the difference must never be mistaken for a short or empty recording.
    ///
    /// Deliberately per chunk rather than on the assembled total: two faults can cancel out in a sum (one
    /// chunk measured 100 reading 0, another measured 100 reading 200) and a scrambled recording would then
    /// pass a total-only check and reach the transcriber.
    ///
    /// This is not hypothetical. The hosted Gateway stages uploads on an Azure Files share, and on 2026-08-27
    /// a chunk that measured 871,724 bytes read back as ZERO twice inside five seconds; the same share
    /// reported a 177 MB log file as 0 bytes to stat while a full read returned all 177 MB. The caller's
    /// empty-audio arm responds to an empty assembly by DELETING the staging, so a single bad read was
    /// enough to throw away audio the user had already uploaded successfully. It only looked survivable
    /// because the phone still held the on-device copy and pushed the same bytes up a third time.
    ///
    /// Returning INCOMPLETE instead puts it back on the path built for exactly this: the client re-sends the
    /// named chunks, the staged bytes are KEPT, and a transient read costs one retry rather than a recording.
    /// Null means the read agreed with the measurement and assembly may proceed.
    ///
    /// Internal so it can be exercised against a known-bad pair - the trigger is a storage fault that cannot
    /// be reproduced on a local filesystem, so the decision is tested even though the fault cannot be staged.
    /// </summary>
    internal static AssembleResult? ReadBackVerdict(string uid, int index, long measured, long read, int totalChunks)
    {
        if (read == measured) return null;
        FileLog.Write($"[VoiceUploadStore] Assemble: uploadId={uid} READ-BACK MISMATCH on chunk {index} " +
            $"measured={measured} read={read} - treating as incomplete, staging KEPT");
        // Every index, not just this one: a read that disagreed with its measurement tells us nothing about
        // whether the other chunks read correctly, so the whole clip is re-sent rather than half-trusted.
        return AssembleResult.Incomplete(Enumerable.Range(0, totalChunks).ToList());
    }

    /// <summary>
    /// True only for the EXACT canonical upload-id directory name this store writes: 32 lowercase hex digits,
    /// no hyphens - the form <see cref="NormalizeId"/> produces. Deliberately strict, because this is the
    /// admit-test for a destructive delete: an upper-cased or hyphenated GUID would name the SAME upload to a
    /// human but is not a spelling this store ever creates, so it is not admitted and the directory survives.
    /// Nothing is lost by that conservatism - the store only ever creates the canonical spelling - and a
    /// surprising name surviving is exactly the safe outcome.
    /// </summary>
    private static bool IsCanonicalUploadDirName(string name)
        => Guid.TryParseExact(name, "N", out var parsed)
           && string.Equals(name, parsed.ToString("N"), StringComparison.Ordinal);

    /// <summary>The subdirectory pre-partition legacy uploads are moved into by <see cref="QuarantineLegacyUploads"/>.
    /// Not a canonical upload id, so no sweep, projection, or partition treats it as an upload or a tenant.</summary>
    public const string QuarantineDirectoryName = "_quarantine-legacy";

    /// <summary>
    /// Move aside every pre-partition upload directory sitting DIRECTLY under this store's base root, so a
    /// hosted Gateway never serves, sweeps, or projects a legacy unattributable upload as if it were live
    /// (issue #1884, the un-deny safety gate; the same move-aside shape #1933 used for pre-version rows).
    ///
    /// WHY IT IS NEEDED EVEN WITH THE DIRECTORY PARTITION. A hosted account tenant reads only
    /// <c>base/tenants/&lt;id&gt;/</c> and can never address <c>base/&lt;uploadId&gt;</c>, so the audio is
    /// already unreachable to another account. What this closes is the BASE (Local) handle's OWN passes - the
    /// age sweep and the durable PENDING projection both enumerate the base root on a hosted Gateway - which
    /// would otherwise read a legacy record as live state (a legacy PENDING record surfacing a session as
    /// locked, a legacy dir aged out and deleted). Moving the legacy dirs out of the enumerated root removes
    /// them from every such pass at once, and out of any future un-scoped read.
    ///
    /// MOVE, NEVER DELETE: the bytes are preserved under <see cref="QuarantineDirectoryName"/> for a later
    /// operator-run purge - this method loses nothing. IDEMPOTENT AND RE-ENTRANT: it admits ONLY a canonical
    /// upload-id directory, so the tenants partition container and the quarantine directory itself are never
    /// moved. A legacy source is ALWAYS moved aside - NEVER left live at the base root. When the canonical
    /// quarantine slot is already taken - an earlier run (or a concurrent worker) quarantined that id and then
    /// a rolling/older worker recreated the same base id, or an interrupted recovery is re-entered - the source
    /// is moved to a UNIQUE non-colliding name beside it (a <c>__dup-N</c> suffix) rather than left where the
    /// base age sweep and the base PENDING projection would still read it as live. The suffix is deliberately
    /// NOT a canonical upload-id shape, so a quarantined dup is never re-admitted as an upload or a tenant.
    /// Running it twice - or two workers at once - converges without error and never overwrites an existing
    /// quarantined directory; and because a moved source is gone from the base root, a second run that sees the
    /// same id at base is looking at a genuinely NEW legacy directory that must also be moved aside.
    ///
    /// Call it on the BASE (Local) handle; on an account partition there is nothing pre-partition to move and
    /// it is a no-op. Returns how many directories were moved.
    /// </summary>
    public int QuarantineLegacyUploads()
    {
        // Account partitions (base/tenants/<id>) are clean by construction - only the shared base root can
        // hold pre-partition uploads - so this runs only for the base handle.
        if (!string.Equals(_root, _partitionBase, StringComparison.Ordinal)) return 0;

        var quarantine = Path.Combine(_root, QuarantineDirectoryName);
        var moved = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                // Admit ONLY a canonical upload-id directory. The tenants container and the quarantine dir are
                // not canonical names, so they are skipped - which is exactly what makes this re-entrant.
                if (!IsCanonicalUploadDirName(name)) continue;

                try
                {
                    Directory.CreateDirectory(quarantine);
                    // Pick a target that does not already exist. The canonical slot (quarantine/<id>) first; if
                    // it is taken, a unique __dup-N sibling - so the source ALWAYS moves and is never left live
                    // at base. A null return means no free slot was found (unreasonable), in which case we log
                    // and leave the source rather than delete or overwrite anything.
                    var target = FreeQuarantineTarget(quarantine, name);
                    if (target is null)
                    {
                        FileLog.Write($"[VoiceUploadStore] quarantine {name} skipped: no free target slot");
                        continue;
                    }
                    Directory.Move(dir, target);
                    moved++;
                }
                catch (IOException ex) { FileLog.Write($"[VoiceUploadStore] quarantine {name} skipped: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { FileLog.Write($"[VoiceUploadStore] quarantine {name} skipped: {ex.Message}"); }
            }
            // Moving a directory out of the base root removes it from the PENDING projection just as a delete
            // would, so the cache is dropped for the same reason the sweeps drop it.
            if (moved > 0)
            {
                _lockIndex.Invalidate(_root);
                FileLog.Write($"[VoiceUploadStore] QuarantineLegacyUploads moved={moved} legacy upload dirs into {QuarantineDirectoryName}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] QuarantineLegacyUploads failed: {ex.Message}");
        }
        return moved;
    }

    /// <summary>
    /// The first quarantine target path for <paramref name="name"/> that does not already exist: the canonical
    /// <c>quarantine/&lt;name&gt;</c> slot when it is free, else <c>quarantine/&lt;name&gt;__dup-N</c> for the
    /// lowest N that is free. Returns null only if every candidate up to the bound is taken (unreasonable), so
    /// the caller can leave the source rather than overwrite. The suffix keeps the name NON-canonical, so a
    /// quarantined dup is never re-admitted as an upload id or a tenant by any later pass.
    /// </summary>
    private static string? FreeQuarantineTarget(string quarantine, string name)
    {
        var canonical = Path.Combine(quarantine, name);
        if (!Directory.Exists(canonical)) return canonical;
        // A bound rather than an unbounded loop: a hostile or pathological pile-up of colliding ids must not
        // spin forever. 10000 distinct dup slots per id is far beyond any real rolling-deploy collision.
        for (var n = 1; n <= 10000; n++)
        {
            var candidate = Path.Combine(quarantine, $"{name}__dup-{n}");
            if (!Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Delete the staging dir for an upload. Best-effort; called once the turn is started.</summary>
    public void Delete(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return;
        try
        {
            var dir = DirFor(uid);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] Delete uploadId={uid} failed: {ex.Message}");
        }
        // Outside the try on purpose: the staging directory is gone or was never there, and either way this
        // upload id holds no lock. Dropping it only when the delete threw no exception would leave a phantom
        // lock behind exactly when the disk is misbehaving - the case where failing OPEN is the whole point.
        _lockIndex.Removed(_root, uid);
    }

    // ====== durable delivery record (issue #1183) ===================================
    // One durable record per upload id with three states. PENDING is the ABSENCE of a terminal record
    // while the staging dir exists (its chunks are retained for resume). DELIVERED and ABANDONED are
    // terminal tombstones written as record.json AFTER the heavy chunk bytes are discarded. The tombstone
    // is the durable "already resolved" marker and is retired only by a client acknowledgment - so it is
    // exactly as long-lived as the audio it guards, and a delivered/abandoned upload id never re-injects.
    //
    // That last sentence holds ONLY because the read that guards it distinguishes every answer the file can
    // give (issue #2745). A record.json that is there but cannot be read - corrupt, half-written, locked by
    // another process, in a shape this build does not know - is evidence that this upload id was already
    // resolved. It is NOT "no record". Before #2745 the reader answered null for both, the de-dupe check
    // above register saw null, and the upload was re-opened as a fresh PENDING - the operator's own speech
    // injected a second time. See ReadRecordFile for the answers and Read for the contract.

    /// <summary>
    /// The honest read of this upload id's durable delivery record: every answer the file can give has its
    /// own <see cref="DictationRecordReadKind"/>, and only <see cref="DictationRecordReadKind.Absent"/> means
    /// "never resolved here". Read from disk, so it survives a Gateway restart (a fresh store instance over
    /// the same root finds it).
    ///
    /// A caller deciding whether to (re-)open, deliver, or overwrite MUST switch on the kind and must treat
    /// <see cref="DictationRecordReadKind.Malformed"/>, <see cref="DictationRecordReadKind.Unreadable"/> and
    /// <see cref="DictationRecordReadKind.ForeignTenant"/> as a refusal: a marker it cannot read is still a
    /// marker, and writing over it destroys the only evidence of what this upload id already became.
    /// </summary>
    public DictationRecordRead Read(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        // Not a GUID-shaped id: it cannot name a staging directory, so nothing was ever written for it.
        if (uid is null) return DictationRecordRead.Absent;
        var path = RecordPath(DirFor(uid));
        var read = ReadRecordFile(path);
        if (read.Kind != DictationRecordReadKind.Present) return read;
        // Tenant-checked as well as partitioned (issue #1884). See BelongsHere: the directory is the boundary,
        // this is the independent second opinion, and a record that fails it is refused rather than handed to
        // a caller it does not belong to. Refused with its OWN name, not as absent (issue #2745): "absent"
        // means "go ahead and open", and opening here would write this tenant's PENDING marker over another
        // tenant's record - which can only happen if a partition root was mis-computed, the very fault this
        // check exists to catch, and the one moment a silent overwrite is least acceptable.
        if (!BelongsHere(read.Record!))
        {
            FileLog.Write($"[VoiceUploadStore] Read: uploadId={uid} record belongs to another tenant " +
                $"(partition={_tenant.ToLogString()}); refused");
            return DictationRecordRead.Foreign(path);
        }
        return read;
    }

    /// <summary>
    /// The durable delivery record for this upload id when it is present and readable, or null ONLY when it
    /// is genuinely absent (an unknown id, or a staged upload that was never given a marker).
    ///
    /// Every other answer THROWS <see cref="UnreadableDictationRecordException"/> rather than returning null.
    /// This is deliberate (issue #2745): null used to stand for both "no record" and "a record I could not
    /// read", and the de-dupe guard read the second as the first. A caller that wants to act on those
    /// answers calls <see cref="Read"/> and switches on the kind; a caller that did not think about them
    /// fails loudly here instead of quietly re-opening a resolved upload. The store's own writers read
    /// through this on purpose, so none of them can compose a new marker on top of one it cannot read.
    /// </summary>
    public DictationDeliveryRecord? ReadRecord(string uploadId)
    {
        var read = Read(uploadId);
        return read.Kind switch
        {
            DictationRecordReadKind.Present => read.Record,
            DictationRecordReadKind.Absent => null,
            _ => throw new UnreadableDictationRecordException(uploadId, read),
        };
    }

    // The one place a record.json is turned into an answer. Four answers, each with a name, and NO
    // pre-check with File.Exists: that call answers false for a file it was not allowed to look at, which
    // would fold "could not look" into "absent" one line before the honest read even starts.
    private static DictationRecordRead ReadRecordFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return DictationRecordRead.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return DictationRecordRead.Absent;
        }
        catch (Exception ex)
        {
            // Locked by another process, permission denied, a directory sitting where the file should be,
            // a disk fault: the file may well be there and we could not look. Say so; never say "absent".
            FileLog.Write($"[VoiceUploadStore] ReadRecord {path} could not be read: {ex.Message}");
            return DictationRecordRead.CouldNotRead(path, ex.Message);
        }

        DictationDeliveryRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<DictationDeliveryRecord>(text, RecordJson);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[VoiceUploadStore] ReadRecord {path} is not a delivery record: {ex.Message}");
            return DictationRecordRead.NotUnderstood(path, ex.Message);
        }
        if (record is null)
        {
            FileLog.Write($"[VoiceUploadStore] ReadRecord {path} is not a delivery record: the file holds JSON null");
            return DictationRecordRead.NotUnderstood(path, "the file holds JSON null");
        }
        // A number the enum does not name deserializes without complaint, and then matches no state anywhere.
        if (!Enum.IsDefined(record.State))
        {
            var problem = $"state {(int)record.State} is not a delivery state this build knows";
            FileLog.Write($"[VoiceUploadStore] ReadRecord {path} is not a delivery record: {problem}");
            return DictationRecordRead.NotUnderstood(path, problem);
        }
        return DictationRecordRead.Found(record);
    }

    /// <summary>
    /// True when this upload id holds an explicit, READABLE PENDING marker (issue #1188): undelivered and not
    /// abandoned, its chunks retained. The enforced per-session dictation lock is a projection of this.
    ///
    /// A marker that cannot be read is NOT pending here. That is a decision, not the #2745 fold, and it goes
    /// the other way from delivery on purpose: the lock keeps the human's keyboard out of a session while a
    /// dictation is in flight, and a marker nobody can read can never finish flying - every delivery path
    /// refuses it (see <see cref="Read"/>) - so locking on it would wedge the session shut until an operator
    /// found the file, with no client action able to release it. The Core-side lock reader documents the
    /// same fail-open choice for the same reason, and the user-input source default compensates for a missed
    /// lock; nothing compensates for a re-injected dictation, which is why delivery is the side that refuses.
    /// </summary>
    public bool IsPending(string uploadId)
        => Read(uploadId) is { Kind: DictationRecordReadKind.Present, Record.State: DictationDeliveryState.Pending };

    /// <summary>
    /// Write the explicit durable PENDING marker for this upload id, carrying the owning session id (issue
    /// #1188). While a PENDING marker exists the session is LOCKED for human input (a projection read by
    /// <see cref="IsSessionLocked"/>). Called at register so the session id is on disk and the lock survives
    /// a Gateway restart. Keeps any staged chunks and overwrites a prior PENDING or FAILED marker (a retry
    /// re-entry back to PENDING); the DELIVERED/ABANDONED short-circuit means those never reach here.
    /// </summary>
    public void MarkPending(string uploadId, string sessionId)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            Directory.CreateDirectory(dir);
            // Carry the #1593 re-baseline forward: a phone that never saw our 502 simply RE-REGISTERS its
            // upload id and retries. That path lands here, so dropping the value would hand the retry back the
            // very baseline our own failed attempt invalidated - the exact drop this field exists to stop.
            // Read inside the gate, so it cannot be a value from before another writer moved it.
            WriteRecordMarker(dir, new DictationDeliveryRecord(
                DictationDeliveryState.Pending, false, false, "", null, sessionId ?? "", ExistingRebaseline(uid)));
            FileLog.Write($"[VoiceUploadStore] MarkPending: uploadId={uid} sessionId={sessionId}");
        });
    }

    /// <summary>
    /// True when any dictation upload is PENDING for this session (issue #1188): the enforced session lock is
    /// a pure projection of the durable PENDING marker - it NEVER auto-releases; it clears only when every
    /// record for the session has left PENDING (delivered / abandoned / failed). Computed from disk, so it
    /// survives a Gateway restart. The Gateway's OWN dictation injection reaches the session directly through
    /// the Director control API, not the guarded Gateway front door, so it is not blocked by this lock.
    /// </summary>
    public bool IsSessionLocked(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        return LockedSessionIds().Contains(sessionId);
    }

    /// <summary>The distinct session ids that currently hold a PENDING dictation (issue #1188).</summary>
    public IReadOnlyCollection<string> LockedSessionIds()
        // From memory, hydrating from disk at most once per partition per process. A null answer means the
        // hydration could not read the root, which this path has always reported as NO LOCK rather than
        // guessing one - see the fail-open note on DictationLockIndex.
        => _lockIndex.LockedSessions(_root, EnumeratePendingEntries)
           ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>
    /// The one disk read behind the session lock: every PENDING record in this partition, paired with its
    /// upload id. Runs once per partition per process (and again after a sweep invalidates the cache), where
    /// it used to run once per session every five seconds.
    /// </summary>
    private IEnumerable<(string UploadId, string SessionId)> EnumeratePendingEntries()
    {
        foreach (var rec in EnumeratePendingRecordsWithId())
            if (!string.IsNullOrWhiteSpace(rec.Record.SessionId))
                yield return (rec.UploadId, rec.Record.SessionId);
    }

    private IEnumerable<(string UploadId, DictationDeliveryRecord Record)> EnumeratePendingRecordsWithId()
    {
        string[] dirs;
        try { dirs = Directory.Exists(_root) ? Directory.GetDirectories(_root) : Array.Empty<string>(); }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceUploadStore] enumerate pending failed: {ex.Message}");
            dirs = Array.Empty<string>();
        }
        foreach (var dir in dirs)
        {
            // The partition container is not an upload, so skip it rather than probing it for a record.
            //
            // NO CANARY HERE EITHER, and for a different reason than the containment guard in
            // PartitionRootFor - this one is REDUNDANT rather than unreachable. Measured: removing this line
            // reddens nothing. It cannot, because the container directory holds other tenants' partitions and
            // no record.json of its own, so ReadRecordFile answers Absent and the pattern match already declines
            // it; and even if a record were somehow found there, BelongsHere on the same line refuses a record
            // belonging to another tenant. Two independent things downstream already give the right answer.
            //
            // It stays because it says what is true - a partition container is not an upload - and because it
            // stops a pointless file probe per partition on every projection. It is clarity and cost, not a
            // security boundary; the security boundary on this line is BelongsHere, and THAT one has canaries.
            if (IsPartitionContainer(dir)) continue;
            // A readable PENDING marker of our own, and nothing else: a marker that cannot be read holds no
            // lock, for the reason given on IsPending.
            if (ReadRecordFile(RecordPath(dir)) is { Kind: DictationRecordReadKind.Present, Record: { State: DictationDeliveryState.Pending } rec }
                && BelongsHere(rec))
                yield return (Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), rec);
        }
    }

    /// <summary>
    /// Transition this upload id to the durable DELIVERED tombstone: persist the submitted outcome, then
    /// discard the heavy chunk bytes (the turn is resolved and resume is no longer needed) while keeping the
    /// small marker. Idempotent: re-marking an already-delivered id rewrites the same tombstone. The
    /// tombstone is retired only by <see cref="Acknowledge"/>.
    /// </summary>
    public void MarkDelivered(string uploadId, bool submitted, bool movedOn, string transcript)
        => WriteTombstone(uploadId, uid => new DictationDeliveryRecord(
            DictationDeliveryState.Delivered, submitted, movedOn, transcript ?? "", null, ExistingSessionId(uid)));

    /// <summary>
    /// Transition this upload id to the durable ABANDONED tombstone: persist the reason and discard the
    /// chunk bytes. Terminal and not-undelivered, so the session lock is off. The abandon WRITE triggers
    /// from the surfaces are a later task; this provides the state and its read side.
    /// </summary>
    public void MarkAbandoned(string uploadId, string reason)
        => WriteTombstone(uploadId, uid => new DictationDeliveryRecord(
            DictationDeliveryState.Abandoned, false, false, "", reason ?? "", ExistingSessionId(uid)));

    /// <summary>
    /// Park this upload id as FAILED with a permanent-failure reason code (issue #1185). Unlike DELIVERED
    /// and ABANDONED, FAILED is NOT a terminal tombstone and NOT a de-dupe short-circuit: it is a
    /// user-retryable pause that KEEPS the staged chunk bytes, so an explicit retry can re-complete without
    /// a full re-upload. <see cref="IsPending"/> is false while FAILED (the session is not locked, which
    /// stops the client auto-loop); <see cref="ClearFailed"/> puts it back to PENDING for the retry.
    /// </summary>
    public void MarkFailed(string uploadId, string reasonCode)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            Directory.CreateDirectory(dir);
            // Write ONLY the marker - keep the chunk bytes (the retry re-drives them), the opposite of a
            // tombstone. Preserve the owning session id so a later ClearFailed can restore a PENDING marker
            // that re-locks it, and the #1593 re-baseline so a transcription failure on a retry cannot erase
            // what an earlier failed DELIVERY attempt already learned about the buffer. Both read inside the
            // gate, so neither can be a value from before another writer moved it.
            WriteRecordMarker(dir, new DictationDeliveryRecord(
                DictationDeliveryState.Failed, false, false, "", reasonCode ?? "", ExistingSessionId(uid),
                ExistingRebaseline(uid)));
            FileLog.Write($"[VoiceUploadStore] MarkFailed: uploadId={uid} reason={reasonCode} (chunks retained)");
        });
    }

    /// <summary>
    /// If this upload id is parked FAILED, clear it back to an explicit PENDING marker - preserving the
    /// owning session id (which re-locks the session while the retry re-drives) and KEEPING the staged chunks
    /// - so an explicit retry re-completes without a full re-upload (issue #1185, updated for #1188). A no-op
    /// returning false for any other state: DELIVERED and ABANDONED tombstones are left intact (their
    /// short-circuit stands).
    /// </summary>
    public bool ClearFailed(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return false;
        // Read-modify-write under the gate, read inside: without it, a ClearFailed that read FAILED could
        // write PENDING back over a tombstone that landed in between, resurrecting a resolved upload id.
        return WithRecordLock(uid, () =>
        {
            // Through Read, not ReadRecord: this is a query ("was there a FAILED marker to clear?") and the
            // honest answer for a marker that cannot be read is "no, and I wrote nothing" - which is what
            // false says. The endpoint has already refused the complete that would have got here for such a
            // marker (issue #2745); this line keeps the store from writing over it on its own account.
            var read = Read(uid);
            if (read.Kind != DictationRecordReadKind.Present)
            {
                if (read.Kind != DictationRecordReadKind.Absent)
                    FileLog.Write($"[VoiceUploadStore] ClearFailed: uploadId={uid} marker is {read.Kind}; not cleared ({read.Problem})");
                return false;
            }
            if (read.Record is not { State: DictationDeliveryState.Failed } failed) return false;
            try
            {
                WriteRecordMarker(DirFor(uid), new DictationDeliveryRecord(
                    DictationDeliveryState.Pending, false, false, "", null, failed.SessionId,
                    failed.RebaselineBufferBytes));
                FileLog.Write($"[VoiceUploadStore] ClearFailed: uploadId={uid} back to PENDING (chunks retained)");
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceUploadStore] ClearFailed uploadId={uid} failed: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Record the honest moved-on baseline for this upload id after one of OUR OWN delivery attempts failed
    /// (Lost Dictations mission, issue #1593). The record stays exactly where it was - this is NOT a state
    /// transition and NOT a tombstone: a PENDING record stays PENDING with its chunks intact, so the client's
    /// retry re-drives normally. Only <see cref="DictationDeliveryRecord.RebaselineBufferBytes"/> moves.
    ///
    /// MONOTONIC: the stored value only ever grows (each failed attempt adds more of its own noise, and a
    /// later fresh read is at least as honest as an earlier one), so repeated failures can never lower the
    /// baseline back into a range where our own noise reads as a real turn.
    ///
    /// A no-op returning false when there is no record yet, or when the record is already terminal
    /// (DELIVERED / ABANDONED): those are resolved and their guard has already run, so re-baselining them
    /// would move a number nothing will read again.
    /// </summary>
    public bool RecordFailedDeliveryBaseline(string uploadId, long bufferBytes)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return false;

        // Read-modify-write under the per-upload gate, with the read INSIDE the lock. Two things depend on
        // that, and the second is the serious one:
        //   1. The max-and-write is atomic, so two re-baseline writers cannot both read the old value and let
        //      the one that computed the SMALLER max land last - handing a retry a baseline we already knew
        //      was too low, which is the drop this field exists to stop. Monotonicity has to hold at the
        //      WRITE, not just in the arithmetic.
        //   2. We write only the re-baseline field onto the record AS IT IS RIGHT NOW, never a record read
        //      before the lock. Otherwise a stalled re-baseline could write a remembered PENDING record back
        //      over a tombstone that landed in the meantime and resurrect a delivered upload id (issue #1183).
        return WithRecordLock(uid, () =>
        {
            // Through Read: a marker that cannot be read gets no re-baseline written over it - false is the
            // true answer ("nothing recorded"), and the marker stays as the evidence it is (issue #2745).
            var read = Read(uid);
            if (read.Kind != DictationRecordReadKind.Present)
            {
                if (read.Kind != DictationRecordReadKind.Absent)
                    FileLog.Write($"[VoiceUploadStore] RecordFailedDeliveryBaseline: uploadId={uid} marker is {read.Kind}; " +
                        $"not written ({read.Problem})");
                return false;
            }
            var record = read.Record!;
            // Terminal is final: its guard has already run for the last time, and rewriting the tombstone here
            // is precisely how a resolved upload id would come back to life.
            if (record.State is DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned) return false;
            BetweenRecordReadAndWriteForTests?.Invoke(uid);

            var merged = Math.Max(record.RebaselineBufferBytes ?? 0, bufferBytes);
            if (merged <= (record.RebaselineBufferBytes ?? -1)) return false; // already at least this honest
            try
            {
                // Marker only - the chunk bytes stay put, because this upload is still going to be delivered.
                WriteRecordMarker(DirFor(uid), record with { RebaselineBufferBytes = merged });
                FileLog.Write($"[VoiceUploadStore] RecordFailedDeliveryBaseline: uploadId={uid} rebaseline={merged} " +
                    $"(state={record.State}, chunks retained)");
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceUploadStore] RecordFailedDeliveryBaseline uploadId={uid} failed: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Retire the terminal tombstone for this upload id once the client has acknowledged it. Idempotent: a
    /// no-op returning false when the record is already gone. The server retires a tombstone ONLY on this
    /// client ack, so a delivered/abandoned upload id is de-duplicated for as long as the client could
    /// still re-drive it; a lost ack simply leaves the tiny marker and a later re-complete re-acks.
    /// </summary>
    public bool Acknowledge(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return false;
        // Retiring the record is a record mutation, so it takes the same gate. Without it, a retirement landing
        // in the middle of another writer's read-modify-write would let that writer re-create the staging
        // directory and a marker for an upload id that had just been acknowledged away.
        return WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            if (!Directory.Exists(dir)) return false;
            try
            {
                Directory.Delete(dir, recursive: true);
                // A tombstone is terminal, so the cache already dropped this id when the tombstone was
                // written. Dropped again anyway: this is the other place a staging directory disappears, and
                // an entry that cannot be here costs nothing to remove while a future state that CAN be here
                // would otherwise leave a lock with no directory behind it.
                _lockIndex.Removed(_root, uid);
                FileLog.Write($"[VoiceUploadStore] Acknowledge: uploadId={uid} tombstone retired");
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceUploadStore] Acknowledge uploadId={uid} failed: {ex.Message}");
                return false;
            }
        });
    }

    // The owning session id already recorded for this upload id (empty when there is no record yet), so a
    // state transition preserves the session id first written by MarkPending at register (issue #1188).
    //
    // Through the STRICT ReadRecord, on purpose (issue #2745): every writer that composes a new marker asks
    // one of these two helpers first, inside the gate, and ReadRecord throws for a marker it cannot read. So
    // no writer - MarkPending, MarkDelivered, MarkAbandoned, MarkFailed - can put a fresh marker on top of a
    // corrupt, locked, or foreign one; the write fails loudly with the file named, and the evidence stays.
    private string ExistingSessionId(string uploadId) => ReadRecord(uploadId)?.SessionId ?? "";

    // The re-baseline already recorded for this upload id (null when there is no record, or no attempt has
    // failed), so a NON-TERMINAL state transition preserves what a failed delivery attempt learned (#1593).
    // The terminal tombstones deliberately do NOT carry it: their guard has already run for the last time.
    // Strict for the same reason as ExistingSessionId.
    private long? ExistingRebaseline(string uploadId) => ReadRecord(uploadId)?.RebaselineBufferBytes;

    // Write the small durable marker first (atomic temp+move), THEN discard the heavy chunk bytes, so a
    // crash between the two leaves a valid tombstone rather than orphaned chunks with no marker. Used by the
    // TERMINAL transitions (delivered/abandoned) that no longer need the audio; FAILED keeps its bytes and
    // so writes the marker alone (see MarkFailed).
    // The record is composed by a FACTORY run inside the gate, not passed in ready-made: a tombstone carries
    // the session id already on the record, and reading that outside the lock would be one more
    // read-modify-write straddling the gate - the very shape this is here to eliminate.
    private void WriteTombstone(string uploadId, Func<string, DictationDeliveryRecord> compose)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        // Under the per-upload gate like every other record write: a tombstone that lands while another writer
        // is mid read-modify-write is exactly the interleaving that would let a resolved upload id be written
        // back to PENDING and re-injected (issue #1183).
        WithRecordLock(uid, () =>
        {
            var record = compose(uid);
            var dir = DirFor(uid);
            Directory.CreateDirectory(dir);
            WriteRecordMarker(dir, record);
            foreach (var part in Directory.EnumerateFiles(dir, "*.part"))
            {
                try { File.Delete(part); }
                catch (Exception ex) { FileLog.Write($"[VoiceUploadStore] discard chunk {part} failed: {ex.Message}"); }
            }
            FileLog.Write($"[VoiceUploadStore] MarkRecord: uploadId={uid} state={record.State} " +
                $"submitted={record.Submitted} movedOn={record.MovedOn}");
        });
    }

    // Persist the record.json marker atomically (temp + move), leaving any staged chunks untouched.
    //
    // THE ONE PLACE a record is written, so it is the one place the owning tenant is stamped (issue #1884).
    // Stamped here rather than by each composer on purpose: a composer that forgot would write an
    // unattributed record, and the whole point of the field is that it cannot be forgotten.
    private void WriteRecordMarker(string dir, DictationDeliveryRecord record)
    {
        var path = RecordPath(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record with { Tenant = _tenant.Value }, RecordJson));
        File.Move(tmp, path, overwrite: true);

        // Being THE ONE PLACE a record is written makes this also the one place the in-memory session-lock
        // cache can be kept true, which is why it is updated here and not in each of the five transitions
        // that reach it: a transition added later cannot forget to do it. AFTER the move, never before - the
        // cache must only ever claim what is already durable, so a failed write leaves the cache unchanged
        // and the record and the cache cannot disagree.
        _lockIndex.RecordWritten(_root, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            record.SessionId, record.State == DictationDeliveryState.Pending);
    }

    // ====== internals ===============================================================

    // ====== the per-upload record gate ==============================================
    //
    // EVERY read-modify-write of an upload's record.json runs under this gate. Not just the re-baseline:
    // serializing the re-baseline writers against each other only is worse than useless, because the
    // dangerous interleaving is between DIFFERENT writers. A re-baseline that reads a PENDING record, then
    // stalls while MarkDelivered lands a terminal tombstone, would write its remembered PENDING record back
    // over that tombstone and RESURRECT the upload id as pending - reopening the exact re-injection door the
    // durable record exists to close (issue #1183). Hence: one gate per upload id, every writer inside it,
    // and every writer RE-READS the record inside the lock rather than trusting anything read outside it.
    //
    // Keyed by the upload's staging DIRECTORY - the resource itself, not the store object - because callers
    // construct their own VoiceUploadStore instances over the same root (the endpoint, the aggregator, and
    // the tests all do), so a gate belonging to an instance would protect nothing. Canonicalized through
    // Path.GetFullPath so two spellings of one directory cannot resolve to two different gates.
    //
    // In-process only, and deliberately so: every writer of a given staging root lives in this Gateway. It is
    // NOT a cross-process file lock and does not pretend to be; the individual marker write is already atomic
    // (temp + move), so the worst a second process could do is land a stale value - which no deployment we run
    // can produce, because one Gateway owns a staging root.
    //
    // REFCOUNTED, so the map is bounded by CONCURRENT users rather than by lifetime upload ids - it holds
    // nothing at rest. This is why there is no "prune on terminal transition": removing a gate that another
    // thread is still holding would let the next caller lock a DIFFERENT object and walk straight into the
    // section, which is the race this gate exists to prevent. An entry that nobody holds cannot be in anyone's
    // way, and an entry that somebody holds must not be removed - refcounting is just those two rules.
    private sealed class RecordGate
    {
        public int Users;
    }

    /// <summary>
    /// Test seam: invoked INSIDE the per-upload gate, between the record read and the record write of
    /// <see cref="RecordFailedDeliveryBaseline"/>. Null in production and never assigned outside tests.
    ///
    /// It exists because the tombstone-resurrection hazard is an INTERLEAVING, and an interleaving cannot be
    /// proven by hammering threads and hoping: a test that only sometimes reproduces the race is a test that
    /// will pass on a broken build. This lets one test stall the read-modify-write at the exact instant a
    /// competing tombstone writer tries to land, deterministically, in both directions - the gate holding, and
    /// the gate removed.
    /// </summary>
    internal static Action<string>? BetweenRecordReadAndWriteForTests;

    private static readonly Dictionary<string, RecordGate> _recordGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static RecordGate AcquireGate(string key)
    {
        lock (_recordGates)
        {
            if (!_recordGates.TryGetValue(key, out var gate))
            {
                gate = new RecordGate();
                _recordGates[key] = gate;
            }
            gate.Users++;
            return gate;
        }
    }

    private static void ReleaseGate(string key, RecordGate gate)
    {
        lock (_recordGates)
        {
            if (--gate.Users == 0) _recordGates.Remove(key);
        }
    }

    /// <summary>Run a record read-modify-write for this upload id under its per-upload gate.</summary>
    private T WithRecordLock<T>(string uid, Func<T> body)
    {
        var key = GateKey(uid);
        var gate = AcquireGate(key);
        try
        {
            lock (gate) return body();
        }
        finally
        {
            ReleaseGate(key, gate);
        }
    }

    private void WithRecordLock(string uid, Action body) => WithRecordLock(uid, () => { body(); return true; });

    private string GateKey(string uid) => Path.GetFullPath(DirFor(uid));

    private string DirFor(string uid) => Path.Combine(_root, uid);

    // True for the directory that HOLDS the other tenants' partitions, which is never itself an upload.
    private static bool IsPartitionContainer(string dir)
        => string.Equals(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            TenantPartitionDirectoryName, StringComparison.OrdinalIgnoreCase);

    private static string ChunkPath(string dir, int index) => Path.Combine(dir, $"{index:D5}.part");
    private static string RecordPath(string dir) => Path.Combine(dir, "record.json");

    private static readonly JsonSerializerOptions RecordJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Accept only GUID-shaped ids so the id can never escape the staging root.</summary>
    private static string? NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Guid.TryParse(id, out var g) ? g.ToString("N") : null;
    }

    /// <summary>
    /// The canonical staging form of a caller-supplied upload id, or null when it is not a GUID. Exposed so
    /// the endpoint's in-memory caches key on the SAME single spelling this store keys its directories by:
    /// two spellings of one GUID must not become two cache entries for one staging directory.
    /// </summary>
    internal static string? NormalizeUploadId(string? id) => NormalizeId(id);

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Outcome of <see cref="VoiceUploadStore.AssembleAsync"/>.</summary>
public readonly record struct AssembleResult(string Status, byte[]? Audio, IReadOnlyList<int> Missing)
{
    public static AssembleResult Ok(byte[] audio) => new("ok", audio, Array.Empty<int>());
    public static AssembleResult Incomplete(IReadOnlyList<int> missing) => new("incomplete", null, missing);
    public static AssembleResult Unknown() => new("unknown_upload", null, Array.Empty<int>());
}

/// <summary>
/// The lifecycle of one durable dictation upload id (issue #1183, extended by #1185). PENDING is
/// undelivered and not abandoned - its chunks are retained in full for resume, and while it is PENDING the
/// session is locked. DELIVERED (the turn was injected) and ABANDONED (the dictation was given up) are both
/// terminal tombstones - not-undelivered, so the session lock is off - kept until the client acknowledges
/// them. FAILED (a permanent transcription failure) is a PARKED, USER-RETRYABLE pause - NOT a terminal
/// tombstone and NOT a de-dupe short-circuit: it keeps its chunk bytes, leaves the session unlocked (so the
/// client auto-loop stops), and an explicit retry clears it back to PENDING to re-drive.
/// </summary>
public enum DictationDeliveryState { Pending, Delivered, Abandoned, Failed }

/// <summary>
/// A dictation delivery record (issue #1183, extended by #1185 and #1188): the durable marker for an upload
/// id. PENDING is an explicit marker carrying the owning <see cref="SessionId"/> (the enforced session lock
/// is a projection of it). For DELIVERED it holds the submitted outcome so a re-complete returns the
/// identical result without injecting a second turn; for ABANDONED it holds the drop reason; for FAILED it
/// holds the permanent-failure reason code (in <see cref="Reason"/>) and its chunks are kept for an explicit
/// retry. Every state carries the <see cref="SessionId"/> so a transition (e.g. FAILED back to PENDING)
/// preserves the owning session.
/// </summary>
/// <param name="RebaselineBufferBytes">
/// The honest moved-on baseline for this upload id after one of OUR OWN delivery attempts failed (Lost
/// Dictations mission, issue #1593), or null when no attempt has failed. A failed submit types the text into
/// the terminal and clears it again, growing the session buffer by thousands of bytes that the moved-on guard
/// cannot tell apart from real turns - so a retry judged against the phone's record-time baseline gets dropped
/// as stale by noise the failure itself made. The guard therefore judges a retry against the LARGER of the
/// request's baseline and this value. It lives on the SERVER record, not in the phone's request, so a phone
/// that never saw the 502 still retries against the honest baseline. Null (absent) in every record written
/// before this field existed, which reads back as "no attempt has failed" - the correct meaning.
/// </param>
/// <param name="Tenant">
/// The tenant that owns this upload (issue #1884). Stamped by <see cref="VoiceUploadStore"/> on every write
/// from the partition the record is written into - callers never supply it - and required to match on every
/// read. The directory partition is the boundary; this field is the independent second check, so a
/// mis-computed partition root cannot silently hand one account another's audio or transcript. Empty in
/// records written before the field existed, which are self-host records and are accepted only in the local
/// partition.
/// </param>
public sealed record DictationDeliveryRecord(
    DictationDeliveryState State,
    bool Submitted,
    bool MovedOn,
    string Transcript,
    string? Reason,
    string SessionId = "",
    long? RebaselineBufferBytes = null,
    string Tenant = "");

/// <summary>
/// Every answer a read of an upload id's record.json can give (issue #2745). The reader used to have two -
/// a record, or null - and null stood for both "no record" and "a record I could not read". The de-dupe
/// guard at register read the second as the first and re-opened a delivered upload, so the operator's
/// speech was injected twice. Now each answer has a name, and only <see cref="Absent"/> means "go ahead".
/// </summary>
public enum DictationRecordReadKind
{
    /// <summary>No record.json for this upload id: never resolved here. The only answer that permits opening.</summary>
    Absent,
    /// <summary>A record this build can read and that belongs to this partition's tenant.</summary>
    Present,
    /// <summary>
    /// A record.json is there and its bytes were read, but it is not a delivery record this build understands:
    /// invalid JSON, a half-written file, a state value the enum does not name. It is evidence that this upload
    /// id was already resolved by SOMETHING, and it must not be re-opened or written over.
    /// </summary>
    Malformed,
    /// <summary>
    /// Could not look: the file could not be read at all - locked by another process, permission denied, a
    /// disk fault. Whether a record is there is unknown, and unknown is not "absent".
    /// </summary>
    Unreadable,
    /// <summary>
    /// A readable record stamped for a different tenant than this partition (issue #1884). The directory
    /// partition makes this unreachable unless a partition root was mis-computed, which is exactly when writing
    /// over it would be worst. Refused, and never treated as absent.
    /// </summary>
    ForeignTenant,
}

/// <summary>
/// The result of <see cref="VoiceUploadStore.Read"/>: the kind, the record (only when
/// <see cref="Kind"/> is <see cref="DictationRecordReadKind.Present"/>), and for the refusals the path of
/// the file and what was wrong with it, so a log line or an error response can name the fix.
/// </summary>
public sealed record DictationRecordRead(DictationRecordReadKind Kind, DictationDeliveryRecord? Record, string? Path, string? Problem)
{
    public static readonly DictationRecordRead Absent = new(DictationRecordReadKind.Absent, null, null, null);

    public static DictationRecordRead Found(DictationDeliveryRecord record)
        => new(DictationRecordReadKind.Present, record, null, null);

    public static DictationRecordRead NotUnderstood(string path, string problem)
        => new(DictationRecordReadKind.Malformed, null, path, problem);

    public static DictationRecordRead CouldNotRead(string path, string problem)
        => new(DictationRecordReadKind.Unreadable, null, path, problem);

    public static DictationRecordRead Foreign(string path)
        => new(DictationRecordReadKind.ForeignTenant, null, path, "the record is stamped for another tenant");

    /// <summary>
    /// True for every answer that forbids opening, delivering, or writing over this upload id: anything but a
    /// record we can read or a record that is genuinely not there.
    /// </summary>
    public bool Refuses => Kind is not (DictationRecordReadKind.Present or DictationRecordReadKind.Absent);

    /// <summary>One line, ASCII, naming the kind, the file and the problem - for logs and error bodies.</summary>
    public string Describe(string uploadId)
        => $"the delivery record for upload {uploadId} is {Kind}: {Problem} (file: {Path})";
}

/// <summary>
/// Thrown by the strict <see cref="VoiceUploadStore.ReadRecord"/> for any answer other than a readable record
/// or a genuinely absent one (issue #2745). Carries the full <see cref="Read"/> so the handler that catches it
/// can say which file, and what was wrong, rather than only that something was.
/// </summary>
public sealed class UnreadableDictationRecordException : InvalidOperationException
{
    public DictationRecordRead Read { get; }

    public UnreadableDictationRecordException(string uploadId, DictationRecordRead read)
        : base(read.Describe(uploadId) + "; refusing to treat it as absent")
    {
        Read = read;
    }
}
