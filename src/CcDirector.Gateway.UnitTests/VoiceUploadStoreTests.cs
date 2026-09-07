using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="VoiceUploadStore"/> - the Gateway-side resumable upload staging
/// behind the guaranteed audio-turn front door. Each test stages under an isolated temp root.
/// </summary>
[Collection(VoiceUploadStoreRecordHookCollection.Name)]
public sealed class VoiceUploadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-upload-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceUploadStore _store;

    public VoiceUploadStoreTests() => _store = new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void Register_NoId_MintsGuidShapedId()
    {
        var id = _store.Register(null);
        Assert.True(Guid.TryParse(id, out _));
        Assert.True(_store.Exists(id));
    }

    [Fact]
    public void Register_SuppliedGuid_IsReused()
    {
        var key = Guid.NewGuid().ToString();
        var id = _store.Register(key);
        Assert.Equal(Guid.Parse(key).ToString("N"), id);
    }

    [Fact]
    public async Task Assemble_AllChunksPresent_ConcatenatesInOrder()
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);

        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("ok", result.Status);
        Assert.Equal("AAABBBCCC", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task Assemble_MissingChunk_ReportsIncompleteWithIndices()
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        // chunk 1 deliberately not sent
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);

        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("incomplete", result.Status);
        Assert.Equal(new[] { 1 }, result.Missing);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task Assemble_ZeroByteChunk_RefusedAsIncomplete()
    {
        // Issue #592: a TRUNCATED upload (a chunk landed but is empty/zero-byte) must be refused
        // by the completeness gate, never transcribed. The gate treats a zero-byte chunk the same
        // as a missing one (the #586 contract) and names the index to re-send.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);
        // Simulate a truncated landing of chunk 1: the file exists but is empty.
        var chunkPath = Path.Combine(_root, Guid.Parse(id).ToString("N"), "00001.part");
        await File.WriteAllBytesAsync(chunkPath, Array.Empty<byte>());

        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("incomplete", result.Status);
        Assert.Equal(new[] { 1 }, result.Missing);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task Assemble_ResumeAfterMissing_Succeeds()
    {
        // The whole point: a partial upload is preserved, the client re-sends only what is
        // missing, and the second complete succeeds without re-sending the landed chunks.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);
        Assert.Equal("incomplete", (await _store.AssembleAsync(id, 3)).Status);

        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);
        var result = await _store.AssembleAsync(id, 3);

        Assert.Equal("ok", result.Status);
        Assert.Equal("AAABBBCCC", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task StoreChunk_IdenticalRetry_IsIdempotentNoOp()
    {
        var id = _store.Register(null);
        var bytes = Bytes("AAA");
        await _store.StoreChunkAsync(id, 0, bytes, Sha(bytes));
        await _store.StoreChunkAsync(id, 0, bytes, Sha(bytes)); // retry, no throw

        var result = await _store.AssembleAsync(id, 1);
        Assert.Equal("AAA", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task StoreChunk_ShaMismatch_IsRejected()
    {
        var id = _store.Register(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.StoreChunkAsync(id, 0, Bytes("AAA"), Sha(Bytes("not-the-same"))));
    }

    [Fact]
    public async Task Assemble_UnknownUpload_ReportsUnknown()
    {
        var result = await _store.AssembleAsync(Guid.NewGuid().ToString(), 1);
        Assert.Equal("unknown_upload", result.Status);
    }

    [Fact]
    public async Task Delete_RemovesStaging()
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        _store.Delete(id);
        Assert.False(_store.Exists(id));
    }

    [Fact]
    public async Task SweepAbandoned_RemovesStaleUploads_KeepsFresh()
    {
        // Issue #1006: an upload whose client dropped before completing must not leak forever. The
        // sweep removes staging dirs older than maxAge and leaves recent ones untouched.
        var stale = _store.Register(null);
        await _store.StoreChunkAsync(stale, 0, Bytes("old"), null);
        var fresh = _store.Register(null);
        await _store.StoreChunkAsync(fresh, 0, Bytes("new"), null);

        // Age the stale upload's staging dir two hours into the past.
        var staleDir = Path.Combine(_root, Guid.Parse(stale).ToString("N"));
        Directory.SetLastWriteTimeUtc(staleDir, DateTime.UtcNow.AddHours(-2));

        var removed = _store.SweepAbandoned(TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(_store.Exists(stale));
        Assert.True(_store.Exists(fresh));
    }

    [Fact]
    public async Task SweepAbandoned_DoesNotDescendIntoThePartitionContainer()
    {
        // The per-tenant partition container holds OTHER tenants' staging roots, not uploads. It is a plain
        // directory under the same root, so an age sweep that treated it as an upload would delete every
        // tenant's staging in one call the first time the container itself went quiet. The container is aged
        // well past the cut-off here precisely so that only the skip can keep it alive - and the upload
        // inside it must survive too, which proves the sweep did not descend into it either.
        var tenantsDir = Path.Combine(_root, VoiceUploadStore.TenantPartitionDirectoryName);
        var tenantUpload = Path.Combine(tenantsDir, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tenantUpload);
        await File.WriteAllBytesAsync(Path.Combine(tenantUpload, "00000.part"), Bytes("other tenant audio"));

        var stale = _store.Register(null);
        await _store.StoreChunkAsync(stale, 0, Bytes("old"), null);
        var staleDir = Path.Combine(_root, Guid.Parse(stale).ToString("N"));

        var longAgo = DateTime.UtcNow.AddHours(-9);
        Directory.SetLastWriteTimeUtc(staleDir, longAgo);
        Directory.SetLastWriteTimeUtc(tenantsDir, longAgo);

        var removed = _store.SweepAbandoned(TimeSpan.FromHours(1));

        // Only the real abandoned upload went; the container and everything under it stayed.
        Assert.Equal(1, removed);
        Assert.False(_store.Exists(stale));
        Assert.True(Directory.Exists(tenantsDir));
        Assert.True(File.Exists(Path.Combine(tenantUpload, "00000.part")));
    }

    // The idle-age guarantee must hold for the RESUME path, which the first cut got wrong: an upload that a
    // live client comes back to must never be swept. It holds because EVERY successful operation refreshes
    // the activity signal - so each operation is pinned in ITS OWN test, exercising ONLY that operation on an
    // aged upload. Split deliberately: a single test doing several operations would let their touches mask
    // one another, and a touch that can be individually removed while the test stays green is not proven.
    // Each of these reddens if and only if its own operation's EnsureFreshStaging touch is removed.

    [Fact]
    public async Task SweepAbandoned_ReRegisterAlone_KeepsAnAgedUpload()
    {
        // Stage an upload, age it past the limit, then do ONLY a re-register of the existing id (no chunk
        // retry, no assemble). Re-registering is a resume and therefore activity, so the upload must survive.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("hello"), null);
        var dir = Path.Combine(_root, Guid.Parse(id).ToString("N"));
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddHours(-9));

        var reopened = _store.Register(id);   // the only operation under test
        Assert.Equal(Guid.Parse(id).ToString("N"), reopened);

        var removed = _store.SweepAbandoned(TimeSpan.FromHours(4));

        Assert.Equal(0, removed);
        Assert.True(_store.Exists(id));
    }

    [Fact]
    public async Task SweepAbandoned_IdenticalChunkRetryAlone_KeepsAnAgedUpload()
    {
        // Stage an upload, age it, then do ONLY an idempotent re-send of the identical chunk (no re-register,
        // no assemble). The retry writes no byte - it is a no-op on disk - but it IS a live client working,
        // so it refreshes activity and the upload must survive.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("hello"), null);
        var dir = Path.Combine(_root, Guid.Parse(id).ToString("N"));
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddHours(-9));

        await _store.StoreChunkAsync(id, 0, Bytes("hello"), null);   // the only operation under test

        var removed = _store.SweepAbandoned(TimeSpan.FromHours(4));

        Assert.Equal(0, removed);
        Assert.True(_store.Exists(id));
    }

    [Fact]
    public async Task SweepAbandoned_AssembleAlone_KeepsAnAgedUpload()
    {
        // Stage a complete single-chunk upload, age it, then do ONLY an assemble/complete (no re-register, no
        // chunk retry). Completing is a live client finishing its upload, so it refreshes activity and the
        // staging must survive the sweep that runs alongside the turn it kicks off.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("hello"), null);
        var dir = Path.Combine(_root, Guid.Parse(id).ToString("N"));
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddHours(-9));

        var assembled = await _store.AssembleAsync(id, 1);   // the only operation under test
        Assert.Equal("ok", assembled.Status);

        var removed = _store.SweepAbandoned(TimeSpan.FromHours(4));

        Assert.Equal(0, removed);
        Assert.True(_store.Exists(id));
    }

    [Fact]
    public void SweepAbandoned_KeepsAgedDirectoriesThatAreNotCanonicalUploadIds()
    {
        // The sweep deletes only what it can positively identify as a disposable upload - a directory whose
        // name IS a canonical 32-hex upload id. Anything else survives, aged or not: an unknown name, an
        // almost-canonical name, a partition container, a future sibling directory. A deny-list that skipped
        // only one known name would recursively delete every unanticipated directory, including a
        // future-partition directory with real data under it. Each of these is aged well past the cut-off so
        // that only the positive admit-test can keep it alive.
        void AgedDirWithSentinel(string name)
        {
            var d = Path.Combine(_root, name);
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, "sentinel.txt"), Bytes("keep me"));
            Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddHours(-9));
        }

        AgedDirWithSentinel("not-an-upload-id");                       // plainly not an id
        AgedDirWithSentinel(Guid.NewGuid().ToString("D"));            // hyphenated GUID - a spelling this store never writes
        AgedDirWithSentinel(Guid.NewGuid().ToString("N").ToUpperInvariant()); // upper-cased 32-hex - not the canonical form
        AgedDirWithSentinel(VoiceUploadStore.TenantPartitionDirectoryName);   // the future-partition container

        var removed = _store.SweepAbandoned(TimeSpan.FromHours(1));

        Assert.Equal(0, removed);
        foreach (var name in new[]
                 {
                     "not-an-upload-id",
                     VoiceUploadStore.TenantPartitionDirectoryName,
                 })
        {
            Assert.True(Directory.Exists(Path.Combine(_root, name)));
            Assert.True(File.Exists(Path.Combine(_root, name, "sentinel.txt")));
        }
    }

    // ===== durable delivery record (issue #1183) =====================================================

    [Fact]
    public async Task PendingChunks_AreRetainedPastTheOldOneHourWindow_AndStillAssemble()
    {
        // Acceptance criterion 1: a PENDING upload's chunks are kept until it becomes delivered or
        // abandoned - never age-swept. A clip whose staging is well past the old one-hour window still
        // assembles in full (the dictation path no longer runs the age sweep that used to delete it).
        var id = _store.Register(null);
        _store.MarkPending(id, Guid.NewGuid().ToString()); // the explicit PENDING marker written at register
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);

        // Age the staging dir two hours into the past - well beyond the old one-hour cut.
        var dir = Path.Combine(_root, Guid.Parse(id).ToString("N"));
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddHours(-2));

        Assert.True(_store.IsPending(id), "an undelivered upload stays PENDING");
        var result = await _store.AssembleAsync(id, 2);
        Assert.Equal("ok", result.Status);
        Assert.Equal("AAABBB", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task RestartWithFreshStoreInstance_AssemblesPendingChunksFromDisk()
    {
        // Acceptance criterion 2: the record and its chunks live on disk, so a Gateway restart still finds
        // them. Stage in one store, then assemble a still-PENDING id from a FRESH store over the same root.
        var id = _store.Register(null);
        _store.MarkPending(id, Guid.NewGuid().ToString());
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);
        await _store.StoreChunkAsync(id, 2, Bytes("CCC"), null);

        var afterRestart = new VoiceUploadStore(_root, TenantId.Local);
        Assert.True(afterRestart.IsPending(id));
        var result = await afterRestart.AssembleAsync(id, 3);
        Assert.Equal("ok", result.Status);
        Assert.Equal("AAABBBCCC", Encoding.UTF8.GetString(result.Audio!));
    }

    [Fact]
    public async Task MarkDelivered_DiscardsChunkBytes_KeepsMarker_SurvivesFreshInstance()
    {
        // Acceptance criterion 3: delivery writes a durable DELIVERED tombstone holding the submitted
        // outcome; the heavy chunk bytes MAY be discarded (resume is no longer needed) but the small marker
        // remains and survives a restart. A fresh store over the same root returns the same outcome.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);

        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: "hello there");

        var dir = Path.Combine(_root, Guid.Parse(id).ToString("N"));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.part")); // chunk bytes discarded
        Assert.True(File.Exists(Path.Combine(dir, "record.json"))); // marker kept
        Assert.False(_store.IsPending(id), "a delivered upload is terminal, not pending");

        var afterRestart = new VoiceUploadStore(_root, TenantId.Local);
        var record = afterRestart.ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Delivered, record!.State);
        Assert.True(record.Submitted);
        Assert.False(record.MovedOn);
        Assert.Equal("hello there", record.Transcript);
    }

    [Fact]
    public void MarkAbandoned_WritesAbandonedTombstone_SurvivesFreshInstance()
    {
        // Acceptance criterion 6: an abandoned upload id becomes a durable ABANDONED tombstone holding the
        // reason, so its read side returns a clear dropped outcome across a restart.
        var id = _store.Register(null);

        _store.MarkAbandoned(id, "user cancelled");

        Assert.False(_store.IsPending(id), "an abandoned upload is terminal, not pending");
        var record = new VoiceUploadStore(_root, TenantId.Local).ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Abandoned, record!.State);
        Assert.False(record.Submitted);
        Assert.Equal("user cancelled", record.Reason);
    }

    [Fact]
    public void Acknowledge_RetiresTombstone_AndIsIdempotent()
    {
        // Acceptance criterion 5: the tombstone is retired ONLY on the client ack, and ack is idempotent -
        // a second ack (a re-ack after a lost first ack) is a harmless no-op.
        var id = _store.Register(null);
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: "done");
        Assert.NotNull(_store.ReadRecord(id));

        Assert.True(_store.Acknowledge(id), "the first ack retires the tombstone");
        Assert.Null(_store.ReadRecord(id));
        Assert.False(_store.Exists(id));
        Assert.False(_store.Acknowledge(id), "acking an already-retired id is a no-op");
    }

    [Fact]
    public async Task ReadRecord_PendingMarker_And_UnknownUpload()
    {
        // PENDING is now an EXPLICIT marker (issue #1188): a registered upload reads back State==Pending, and
        // the terminal short-circuit (delivered/abandoned only) does NOT fire for it. An unknown id (no
        // marker at all) reads null.
        var pending = _store.Register(null);
        _store.MarkPending(pending, Guid.NewGuid().ToString());
        await _store.StoreChunkAsync(pending, 0, Bytes("AAA"), null);
        Assert.Equal(DictationDeliveryState.Pending, _store.ReadRecord(pending)!.State);
        Assert.Null(_store.ReadRecord(Guid.NewGuid().ToString()));
    }

    // ===== the parked FAILED state (issue #1185) ====================================================

    [Fact]
    public async Task MarkFailed_ParksTheRecord_KeepsChunkBytes_AndIsNotPending()
    {
        // FAILED is a parked, user-retryable pause: it writes the marker with the reason code but - unlike a
        // delivered/abandoned tombstone - KEEPS the staged chunk bytes so an explicit retry can re-complete.
        // IsPending is false (so the session is not locked and the client auto-loop stops).
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);

        _store.MarkFailed(id, "audio_too_large");

        var dir = Path.Combine(_root, Guid.Parse(id).ToString("N"));
        Assert.Equal(2, Directory.EnumerateFiles(dir, "*.part").Count()); // chunk bytes RETAINED
        Assert.False(_store.IsPending(id), "a FAILED record is not pending (the session is not locked)");
        var record = _store.ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Failed, record!.State);
        Assert.Equal("audio_too_large", record.Reason);
    }

    [Fact]
    public async Task ClearFailed_ReturnsAFailedRecordToPending_KeepingChunks()
    {
        // An explicit retry clears the FAILED marker back to PENDING (deletes only record.json) while keeping
        // the chunks, so the retry re-drives and can still assemble without a full re-upload.
        var id = _store.Register(null);
        var sid = Guid.NewGuid().ToString();
        _store.MarkPending(id, sid);
        await _store.StoreChunkAsync(id, 0, Bytes("AAA"), null);
        await _store.StoreChunkAsync(id, 1, Bytes("BBB"), null);
        _store.MarkFailed(id, "unsupported_format");

        Assert.True(_store.ClearFailed(id), "clearing a FAILED record returns true");

        // Under the explicit-PENDING model (issue #1188) clearing FAILED restores a PENDING marker carrying
        // the SAME session id (so the session re-locks for the retry), NOT a deleted record.
        Assert.True(_store.IsPending(id), "after clearing, the record is PENDING again");
        var restored = _store.ReadRecord(id);
        Assert.Equal(DictationDeliveryState.Pending, restored!.State);
        Assert.Equal(sid, restored.SessionId);
        var result = await _store.AssembleAsync(id, 2);
        Assert.Equal("ok", result.Status);
        Assert.Equal("AAABBB", Encoding.UTF8.GetString(result.Audio!)); // chunks survived the clear
    }

    [Fact]
    public void ClearFailed_IsANoOpForDeliveredAbandonedOrUnknown()
    {
        // ClearFailed must ONLY touch a FAILED record - it must never disturb a DELIVERED or ABANDONED
        // tombstone (their short-circuit stands) or an unknown id.
        var delivered = _store.Register(null);
        _store.MarkDelivered(delivered, submitted: true, movedOn: false, transcript: "done");
        var abandoned = _store.Register(null);
        _store.MarkAbandoned(abandoned, "cancelled");

        Assert.False(_store.ClearFailed(delivered));
        Assert.False(_store.ClearFailed(abandoned));
        Assert.False(_store.ClearFailed(Guid.NewGuid().ToString()));

        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(delivered)!.State);
        Assert.Equal(DictationDeliveryState.Abandoned, _store.ReadRecord(abandoned)!.State);
    }

    // ===== the enforced session lock projection (issue #1188) =======================================

    [Fact]
    public void MarkPending_LocksTheSession_CarryingTheSessionIdOnDisk()
    {
        var id = _store.Register(null);
        var sid = Guid.NewGuid().ToString();

        _store.MarkPending(id, sid);

        Assert.True(_store.IsPending(id));
        Assert.Equal(sid, _store.ReadRecord(id)!.SessionId);
        Assert.True(_store.IsSessionLocked(sid), "a PENDING record locks its session");
        Assert.False(_store.IsSessionLocked(Guid.NewGuid().ToString()), "an unrelated session is not locked");
        // Restart-safe: a fresh store over the same root recomputes the lock from disk.
        Assert.True(new VoiceUploadStore(_root, TenantId.Local).IsSessionLocked(sid));
    }

    [Fact]
    public void IsSessionLocked_ClearsWhenTheRecordLeavesPending()
    {
        var sid = Guid.NewGuid().ToString();
        var delivered = _store.Register(null); _store.MarkPending(delivered, sid);
        var abandoned = _store.Register(null); _store.MarkPending(abandoned, sid);
        var failed = _store.Register(null); _store.MarkPending(failed, sid);
        Assert.True(_store.IsSessionLocked(sid));

        // The lock is a pure projection: it clears only when EVERY record for the session leaves PENDING.
        _store.MarkDelivered(delivered, submitted: true, movedOn: false, transcript: "hi");
        Assert.True(_store.IsSessionLocked(sid), "still locked - two records remain PENDING");
        _store.MarkAbandoned(abandoned, "cancelled");
        Assert.True(_store.IsSessionLocked(sid), "still locked - one record remains PENDING");
        _store.MarkFailed(failed, "audio_too_large");
        Assert.False(_store.IsSessionLocked(sid), "unlocked - no PENDING record remains");
    }

    [Fact]
    public void LockedSessionIds_ReturnsTheDistinctPendingSessions()
    {
        var sidA = Guid.NewGuid().ToString();
        var sidB = Guid.NewGuid().ToString();
        _store.MarkPending(_store.Register(null), sidA);
        _store.MarkPending(_store.Register(null), sidA); // same session, two uploads
        _store.MarkPending(_store.Register(null), sidB);
        var deliveredId = _store.Register(null);
        _store.MarkPending(deliveredId, Guid.NewGuid().ToString());
        _store.MarkDelivered(deliveredId, submitted: true, movedOn: false, transcript: "x"); // not pending

        var locked = _store.LockedSessionIds();

        Assert.Equal(2, locked.Count);
        Assert.Contains(sidA, locked);
        Assert.Contains(sidB, locked);
    }

    // ===== the failed-delivery re-baseline (Lost Dictations mission, issue #1593) ==================

    [Fact]
    public void RecordFailedDeliveryBaseline_IsMonotonic_AndNeverLowersAnHonestBaseline()
    {
        var uploadId = _store.Register(null);
        _store.MarkPending(uploadId, Guid.NewGuid().ToString());

        Assert.True(_store.RecordFailedDeliveryBaseline(uploadId, 9_000));
        Assert.Equal(9_000, _store.ReadRecord(uploadId)!.RebaselineBufferBytes);

        // A LOWER later read (a lagging push stream) must never walk the honest baseline back down.
        Assert.False(_store.RecordFailedDeliveryBaseline(uploadId, 4_000));
        Assert.Equal(9_000, _store.ReadRecord(uploadId)!.RebaselineBufferBytes);

        // A higher one moves it up: a second failed attempt only ever added more of our own noise.
        Assert.True(_store.RecordFailedDeliveryBaseline(uploadId, 12_000));
        Assert.Equal(12_000, _store.ReadRecord(uploadId)!.RebaselineBufferBytes);
    }

    [Fact]
    public void RecordFailedDeliveryBaseline_UnderConcurrentWriters_KeepsTheLARGESTValue()
    {
        // The read-modify-write must be atomic. Unlocked, two writers can both read the old value and the one
        // that computed the SMALLER max can land last, overwriting the larger - handing a retry a baseline we
        // had already proven too low, which is exactly the drop the re-baseline exists to stop. Many writers
        // over a shared value, from SEPARATE store instances over the same root (the gate is on the staging
        // directory, not the object), with the largest deliberately not last.
        var uploadId = _store.Register(null);
        _store.MarkPending(uploadId, Guid.NewGuid().ToString());

        var values = Enumerable.Range(1, 64).Select(i => (long)i * 1_000).ToArray();
        var shuffled = values.OrderBy(v => (v * 7919) % 101).ToArray();
        Parallel.ForEach(shuffled, v => new VoiceUploadStore(_root, TenantId.Local).RecordFailedDeliveryBaseline(uploadId, v));

        Assert.Equal(values.Max(), _store.ReadRecord(uploadId)!.RebaselineBufferBytes);
    }

    [Fact]
    public void RecordFailedDeliveryBaseline_CannotResurrectATombstoneThatLandsMidWrite()
    {
        // THE INTERLEAVING THAT MATTERS (found by inspection of the #1593 fix). Serializing the re-baseline
        // writers against each other is not enough: the dangerous race is between DIFFERENT writers. A
        // re-baseline reads a PENDING record, stalls, MarkDelivered lands a terminal tombstone, and the
        // re-baseline then writes its remembered PENDING record back over it. The upload id is resurrected as
        // pending, and #1183's durable de-dupe - the only thing standing between a delivered dictation and a
        // SECOND injection of the same turn - is gone.
        //
        // Driven deterministically: the re-baseline is stalled at the exact instant between its read and its
        // write, and a competing MarkDelivered is fired from another thread right then. With every record write
        // behind one per-upload gate, that MarkDelivered CANNOT land inside the window - it waits, so the
        // stall times out and the tombstone lands cleanly afterwards. With the gate removed it lands
        // immediately, the re-baseline overwrites it, and the record comes back as Pending.
        var uploadId = _store.Register(null);
        _store.MarkPending(uploadId, Guid.NewGuid().ToString());

        var tombstoneLanded = new ManualResetEventSlim(false);
        Task? competing = null;
        VoiceUploadStore.BetweenRecordReadAndWriteForTests = _ =>
        {
            // Fire the competing terminal write from another thread, then give it every chance to land BEFORE
            // this re-baseline writes. The gate is what must stop it; nothing else here does.
            competing = Task.Run(() =>
            {
                new VoiceUploadStore(_root, TenantId.Local).MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "delivered words");
                tombstoneLanded.Set();
            });
            // A bounded wait, NOT a barrier: under the gate this must time out (the tombstone is blocked behind
            // us, which is the whole point), so waiting forever would hang rather than pass.
            tombstoneLanded.Wait(TimeSpan.FromSeconds(1));
        };
        try
        {
            _store.RecordFailedDeliveryBaseline(uploadId, 9_000);
        }
        finally
        {
            VoiceUploadStore.BetweenRecordReadAndWriteForTests = null;
        }
        competing?.Wait(TimeSpan.FromSeconds(5));

        // The delivered tombstone stands. A re-complete of this upload id returns the cached delivered outcome
        // and never injects a second turn.
        var record = _store.ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Delivered, record.State);
        Assert.True(record.Submitted);
        Assert.Equal("delivered words", record.Transcript);
    }

    [Fact]
    public void RecordFailedDeliveryBaseline_IsANoOpForATerminalRecord()
    {
        // A resolved upload's guard has already run for the last time; re-baselining it would move a number
        // nothing will ever read, and must not disturb the tombstone.
        var delivered = _store.Register(null);
        _store.MarkPending(delivered, Guid.NewGuid().ToString());
        _store.MarkDelivered(delivered, submitted: false, movedOn: true, transcript: "dropped words");
        Assert.False(_store.RecordFailedDeliveryBaseline(delivered, 9_000));
        Assert.Null(_store.ReadRecord(delivered)!.RebaselineBufferBytes);
        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(delivered)!.State);

        var abandoned = _store.Register(null);
        _store.MarkPending(abandoned, Guid.NewGuid().ToString());
        _store.MarkAbandoned(abandoned, "cancelled");
        Assert.False(_store.RecordFailedDeliveryBaseline(abandoned, 9_000));
        Assert.Null(_store.ReadRecord(abandoned)!.RebaselineBufferBytes);

        // And an id with no record at all is simply not re-baselineable.
        Assert.False(_store.RecordFailedDeliveryBaseline(Guid.NewGuid().ToString(), 9_000));
    }

    [Fact]
    public void NonTerminalTransitions_PreserveTheReBaseline()
    {
        // A re-register (the phone never saw our 502) and a transcription failure on a retry both rewrite the
        // marker. Either dropping the re-baseline would hand the next retry back the very baseline our own
        // failed attempt invalidated.
        var sid = Guid.NewGuid().ToString();
        var uploadId = _store.Register(null);
        _store.MarkPending(uploadId, sid);
        _store.RecordFailedDeliveryBaseline(uploadId, 9_000);

        _store.MarkPending(uploadId, sid); // re-register
        Assert.Equal(9_000, _store.ReadRecord(uploadId)!.RebaselineBufferBytes);

        _store.MarkFailed(uploadId, "transcription_error");
        Assert.Equal(9_000, _store.ReadRecord(uploadId)!.RebaselineBufferBytes);

        _store.ClearFailed(uploadId);
        Assert.Equal(9_000, _store.ReadRecord(uploadId)!.RebaselineBufferBytes);
        Assert.Equal(DictationDeliveryState.Pending, _store.ReadRecord(uploadId)!.State);
    }

    [Fact]
    public void TerminalTransitions_PreserveTheSessionId()
    {
        // A state transition preserves the session id first written by MarkPending (so a later ClearFailed
        // can restore a PENDING marker that re-locks the right session).
        var sid = Guid.NewGuid().ToString();
        var d = _store.Register(null); _store.MarkPending(d, sid); _store.MarkDelivered(d, true, false, "hi");
        var a = _store.Register(null); _store.MarkPending(a, sid); _store.MarkAbandoned(a, "cancelled");
        var f = _store.Register(null); _store.MarkPending(f, sid); _store.MarkFailed(f, "audio_too_large");

        Assert.Equal(sid, _store.ReadRecord(d)!.SessionId);
        Assert.Equal(sid, _store.ReadRecord(a)!.SessionId);
        Assert.Equal(sid, _store.ReadRecord(f)!.SessionId);
    }
}
