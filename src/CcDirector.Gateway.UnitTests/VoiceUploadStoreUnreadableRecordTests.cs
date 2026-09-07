using System;
using System.IO;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2745: a dictation delivery record that is there but cannot be read is NOT "no record".
///
/// The reader used to answer null for both, and the de-dupe guard in front of register read that null as
/// "never delivered" and re-opened the upload - so the operator's own speech was injected into a live
/// session a second time. The read now has five answers, each with a name, and this file pins the store
/// half of the fix:
///
///   - <see cref="VoiceUploadStore.Read"/> names every answer: Absent, Present, Malformed, Unreadable,
///     ForeignTenant (the last is pinned in <c>VoiceUploadStoreTenantPartitionTests</c>).
///   - the strict <see cref="VoiceUploadStore.ReadRecord"/> returns null ONLY for Absent and throws for
///     the rest, so no caller can fold them back into "absent".
///   - every WRITER refuses to put a new marker on top of one it cannot read, and leaves the file exactly as
///     it found it.
///   - the sweeps leave such a marker standing.
///   - the session LOCK does not project from it - a decision, documented on IsPending, that is pinned
///     here so it stays a decision and cannot drift back into being an accident.
///
/// The endpoint half - a re-register, a re-complete and an abandon against such a marker are all refused
/// with nothing injected and the file untouched - is host-bound and lives in the parked Gateway.Tests suite
/// (<c>UnreadableDictationTombstoneTests</c>).
///
/// REVERT-PROVABLE: restore the old <c>ReadRecordFile</c> (catch everything, return null) and every
/// Malformed assertion here goes red on the claim, not on a crash; the writer tests go red on the file
/// having been overwritten.
/// </summary>
public sealed class VoiceUploadStoreUnreadableRecordTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-upload-unreadable-" + Guid.NewGuid().ToString("N"));

    private readonly VoiceUploadStore _store;

    public VoiceUploadStoreUnreadableRecordTests() => _store = new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    // ===== the answers, each by name ================================================================

    [Fact]
    public void An_unknown_id_is_Absent_and_the_strict_read_is_null()
    {
        // The control for every refusal below: a genuinely absent record is still the one answer that says
        // "go ahead", and the strict read still hands back null for it.
        var id = Guid.NewGuid().ToString();

        var read = _store.Read(id);

        Assert.Equal(DictationRecordReadKind.Absent, read.Kind);
        Assert.False(read.Refuses);
        Assert.Null(_store.ReadRecord(id));
    }

    [Fact]
    public void A_delivered_tombstone_is_Present_with_its_record()
    {
        var id = Guid.NewGuid().ToString();
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: "said once");

        var read = _store.Read(id);

        Assert.Equal(DictationRecordReadKind.Present, read.Kind);
        Assert.False(read.Refuses);
        Assert.Equal(DictationDeliveryState.Delivered, read.Record!.State);
        Assert.Equal("said once", _store.ReadRecord(id)!.Transcript);
    }

    [Fact]
    public void A_corrupt_tombstone_is_Malformed_not_Absent_and_the_strict_read_throws()
    {
        // The defect itself, at the reader. A delivered tombstone whose bytes are no longer a delivery record.
        var id = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(id, "{ this is not json");

        var read = _store.Read(id);

        Assert.Equal(DictationRecordReadKind.Malformed, read.Kind);
        Assert.True(read.Refuses, "a marker that cannot be understood must refuse, never permit");
        Assert.Null(read.Record);
        Assert.Equal(path, read.Path);
        Assert.False(string.IsNullOrWhiteSpace(read.Problem), "the refusal must say what was wrong");

        var ex = Assert.Throws<UnreadableDictationRecordException>(() => _store.ReadRecord(id));
        Assert.Equal(DictationRecordReadKind.Malformed, ex.Read.Kind);
        Assert.Contains(path, ex.Message);
    }

    [Theory]
    [InlineData("")]                                              // an empty file - a write that never finished
    [InlineData("null")]                                          // JSON null: parses, and is nothing
    [InlineData("[]")]                                            // valid JSON, not an object
    [InlineData("{}")]                                            // valid, empty: every property defaults, and State's default is PENDING
    [InlineData("{\"garbage\":1}")]                               // valid, unrelated: the same default PENDING
    [InlineData("{\"State\":\"Pending\"}")]                       // names a state but nothing else a record carries
    [InlineData("{\"state\":\"Delivered\",\"submitted\":true,\"movedOn\":false,\"transcript\":\"x\",\"reason\":null}")] // wrong case: the serializer would default all of it
    [InlineData("{\"State\":\"Zombie\",\"Submitted\":true,\"MovedOn\":false,\"Transcript\":\"\",\"Reason\":null}")] // a state name this build does not know
    [InlineData("{\"State\":99,\"Submitted\":true,\"MovedOn\":false,\"Transcript\":\"\",\"Reason\":null}")]         // a state NUMBER the enum does not name: deserializes, matches nothing
    public void Every_shape_that_is_not_a_delivery_record_is_Malformed(string bytes)
    {
        var id = Guid.NewGuid().ToString();
        CorruptDeliveredTombstone(id, bytes);

        var read = _store.Read(id);

        Assert.Equal(DictationRecordReadKind.Malformed, read.Kind);
        Assert.Throws<UnreadableDictationRecordException>(() => _store.ReadRecord(id));
    }

    [Fact]
    public void A_directory_where_the_record_should_be_is_Unreadable_not_Absent()
    {
        // "Could not look", in a form every platform produces: the path exists and is not a file. The old
        // reader's File.Exists pre-check answered false here, which folded this straight into "absent".
        var id = Guid.NewGuid().ToString();
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: "said once");
        var path = RecordPath(id);
        File.Delete(path);
        Directory.CreateDirectory(path);

        var read = _store.Read(id);

        Assert.Equal(DictationRecordReadKind.Unreadable, read.Kind);
        Assert.True(read.Refuses);
        Assert.Equal(path, read.Path);
        Assert.Throws<UnreadableDictationRecordException>(() => _store.ReadRecord(id));
    }

    [Fact]
    public void A_tombstone_held_open_by_another_writer_is_Unreadable_not_Absent()
    {
        // The locked-file case from the issue. Only Windows enforces a share lock against a reader, so the
        // claim is only askable there; elsewhere the directory test above covers "could not look".
        if (!OperatingSystem.IsWindows()) return;

        var id = Guid.NewGuid().ToString();
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: "said once");
        var path = RecordPath(id);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var read = _store.Read(id);

            Assert.Equal(DictationRecordReadKind.Unreadable, read.Kind);
            Assert.True(read.Refuses);
            Assert.Throws<UnreadableDictationRecordException>(() => _store.ReadRecord(id));
        }

        // Released: the same marker reads as what it always was. The refusal was about the moment, not the file.
        Assert.Equal(DictationRecordReadKind.Present, _store.Read(id).Kind);
    }

    // ===== no writer puts a new marker on top of one it cannot read ===================================

    [Theory]
    [InlineData("{ this is not json")]   // the syntax case
    [InlineData("{}")]                   // the SHAPE case: valid JSON whose every property defaults, State to PENDING
    public void MarkPending_over_a_corrupt_tombstone_refuses_and_leaves_the_file_byte_for_byte(string bytes)
    {
        // The write that re-opens an upload. This is what register used to reach after the fold.
        var id = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(id, bytes);
        var before = File.ReadAllBytes(path);

        Assert.Throws<UnreadableDictationRecordException>(() => _store.MarkPending(id, Guid.NewGuid().ToString()));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(_store.IsPending(id), "a refused re-open must not have produced a PENDING marker");
        Assert.Equal(DictationRecordReadKind.Malformed, _store.Read(id).Kind);
    }

    [Fact]
    public void MarkDelivered_and_MarkAbandoned_over_a_corrupt_tombstone_refuse_and_leave_the_file()
    {
        // The two terminal writers share one path; both are asked, because the abandon leg reaches one of
        // them directly from the phone.
        var id = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(id, "{ this is not json");
        var before = File.ReadAllBytes(path);

        Assert.Throws<UnreadableDictationRecordException>(() => _store.MarkDelivered(id, true, false, "again"));
        Assert.Throws<UnreadableDictationRecordException>(() => _store.MarkAbandoned(id, "user_abandoned"));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void MarkFailed_ClearFailed_and_the_rebaseline_over_a_corrupt_tombstone_write_nothing()
    {
        var id = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(id, "{ this is not json");
        var before = File.ReadAllBytes(path);

        Assert.Throws<UnreadableDictationRecordException>(() => _store.MarkFailed(id, "audio_too_large"));
        Assert.False(_store.ClearFailed(id));
        Assert.False(_store.RecordFailedDeliveryBaseline(id, 4096));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ===== the sweeps and the lock ==================================================================

    [Fact]
    public void The_tombstone_sweep_leaves_a_corrupt_tombstone_standing()
    {
        var id = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(id, "{ this is not json");

        // Positive control: the sweep really runs and really retires an aged READABLE tombstone, so the
        // survival below is a decision and not a sweep that did nothing.
        var control = Guid.NewGuid().ToString();
        _store.MarkDelivered(control, submitted: true, movedOn: false, transcript: "old");
        var removed = _store.SweepResolvedTombstones(TimeSpan.FromDays(-1));
        Assert.Equal(1, removed);
        Assert.False(_store.Exists(control));

        Assert.True(File.Exists(path), "the sweep must not destroy a marker it could not read");
    }

    [Fact]
    public void The_stale_pending_sweep_leaves_a_corrupt_marker_standing()
    {
        var id = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(id, "{ this is not json");
        var before = File.ReadAllBytes(path);

        // Positive control: an aged readable PENDING really is abandoned by this sweep.
        var control = Guid.NewGuid().ToString();
        _store.Register(control);
        _store.MarkPending(control, Guid.NewGuid().ToString());
        Assert.Equal(1, _store.ExpireStalePending(TimeSpan.FromDays(-1)));
        Assert.Equal(DictationDeliveryState.Abandoned, _store.ReadRecord(control)!.State);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_corrupt_marker_does_not_lock_a_session()
    {
        // The documented decision on IsPending: the lock projection fails open, because nothing a client can
        // do would ever clear a "receiving a dictation" state held by a marker every delivery path refuses.
        // What that projection drives is display (the session's dictation badge); sends are never refused by
        // source, so a missed lock costs a badge and cannot cost a delivery. Pinned so a future reader sees it
        // asserted, not assumed.
        var id = Guid.NewGuid().ToString();
        var session = Guid.NewGuid().ToString();
        _store.Register(id);
        _store.MarkPending(id, session);
        Assert.True(_store.IsSessionLocked(session)); // positive control: a readable PENDING does lock

        File.WriteAllText(RecordPath(id), "{ this is not json");

        // The lock projection is cached per store instance and hydrated from disk once, so the question is
        // put to a FRESH store over the same root - which is also exactly what a Gateway restart does.
        var restarted = new VoiceUploadStore(_root, TenantId.Local);
        Assert.False(restarted.IsPending(id));
        Assert.False(restarted.IsSessionLocked(session));
        Assert.DoesNotContain(session, restarted.LockedSessionIds());
    }

    // ===== helpers ================================================================================

    // A real DELIVERED tombstone written by the store itself, then its bytes replaced: the directory, the
    // file, and the fact that this id WAS delivered are all genuine. Returns the record path.
    private string CorruptDeliveredTombstone(string id, string bytes)
    {
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: "said once");
        var path = RecordPath(id);
        Assert.True(File.Exists(path));
        File.WriteAllText(path, bytes, Encoding.UTF8);
        return path;
    }

    private string RecordPath(string id) => Path.Combine(_root, Guid.Parse(id).ToString("N"), "record.json");
}
