using System.Text;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The decision log and the kept-after-acknowledgement record on <see cref="VoiceUploadStore"/> (Voice Delivery
/// mission, phase 1). The endpoint-level proofs, through the real routes, are in DictationDecisionRecordTests;
/// these pin the store's own transitions, the acknowledgement's deletions, every reader's view of an
/// acknowledged upload, and the sweep that retires it.
/// </summary>
[Collection(VoiceUploadStoreRecordHookCollection.Name)]
public sealed class VoiceUploadStoreDecisionLogTests : IDisposable
{
    private const string Words = "zebra quartz marmalade the owner said this";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-decisions-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceUploadStore _store;
    private readonly string _sessionId = Guid.NewGuid().ToString();

    public VoiceUploadStoreDecisionLogTests() => _store = new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
    }

    private string Dir(string id) => Path.Combine(_root, VoiceUploadStore.NormalizeUploadId(id)!);
    private string[] Names(string id) => _store.ReadDecisions(id).Lines.Select(l => l.Decision).ToArray();

    private async Task<string> OpenWithChunkAsync()
    {
        var id = _store.OpenPending(Guid.NewGuid().ToString(), _sessionId).UploadId;
        await _store.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes("audio-bytes"), null);
        return id;
    }

    [Fact]
    public async Task TheStoreTransitions_EachWriteTheirDecision_InOrder()
    {
        // Proves every store transition writes its own line, so a new caller of the store cannot forget one.
        var id = await OpenWithChunkAsync();
        _store.MarkFailed(id, "transcription_error");
        Assert.True(_store.ClearFailed(id));
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: Words);

        Assert.Equal(new[]
        {
            DeliveryDecisions.Received, DeliveryDecisions.Failed, DeliveryDecisions.ClearedFailed, DeliveryDecisions.Delivered,
        }, Names(id));
        var delivered = _store.ReadDecisions(id).Lines.Last();
        Assert.Equal(Words.Length, delivered.Facts!.Characters);
        Assert.True(delivered.Facts.Submitted);
    }

    [Fact]
    public void MovedOnAndSessionExited_AreNamedByTheirCause()
    {
        // Proves the two not-submitted resolutions, which share their flags, are told apart in the log.
        var movedOn = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(movedOn, submitted: false, movedOn: true, transcript: Words);
        var exited = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(exited, submitted: false, movedOn: true, transcript: "", reason: DeliveryDecisions.SessionExited);

        Assert.Equal(DeliveryDecisions.MovedOn, Names(movedOn).Last());
        Assert.Equal(DeliveryDecisions.SessionExited, Names(exited).Last());
    }

    [Fact]
    public void TheEndpointsExitedReason_IsTheDecisionName()
    {
        // Proves the endpoint's session-exited reason is the exact string the store names its decision by,
        // so an exited session can never be logged as moved-on by a spelling drift.
        Assert.Equal(DeliveryDecisions.SessionExited, CcDirector.Gateway.Api.GatewayDictationEndpoint.ExitedSessionReason);
    }

    [Fact]
    public void AbandonAndExpiry_WriteAbandonedWithTheirReason()
    {
        // Proves a user abandon and a server expiry both write abandoned, each with its own reason.
        var id = _store.OpenPending(null, _sessionId).UploadId;
        Assert.True(_store.Abandon(id, "user_abandoned").Abandoned);
        var stale = _store.OpenPending(null, _sessionId).UploadId;
        Directory.SetLastWriteTimeUtc(Dir(stale), DateTime.UtcNow.AddDays(-3));
        Assert.Equal(1, _store.ExpireStalePending(TimeSpan.FromHours(24)));

        Assert.Equal("user_abandoned", _store.ReadDecisions(id).Lines.Last().Facts!.Reason);
        Assert.Equal(VoiceUploadStore.StalePendingReason, _store.ReadDecisions(stale).Lines.Last().Facts!.Reason);
    }

    [Fact]
    public async Task Acknowledge_DeletesTheAudioAndTheWords_AndKeepsTheRecordAndDecisions()
    {
        // Proves the Delivery Lead's condition: after acknowledgement the chunks and the transcript text are
        // gone, while record.json and decisions.jsonl remain, and the kept record says what it was.
        var id = await OpenWithChunkAsync();
        File.WriteAllText(Path.Combine(Dir(id), "00001.part.tmp"), "half-written audio");
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: Words);
        await File.WriteAllTextAsync(Path.Combine(Dir(id), "00002.part"), "a late chunk");
        Assert.Contains("zebra", File.ReadAllText(Path.Combine(Dir(id), "record.json"))); // precondition

        Assert.True(_store.Acknowledge(id));

        Assert.Equal(new[] { "decisions.jsonl", "record.json" },
            Directory.EnumerateFileSystemEntries(Dir(id)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        var recordText = File.ReadAllText(Path.Combine(Dir(id), "record.json"));
        Assert.DoesNotContain("zebra", recordText);
        Assert.DoesNotContain("marmalade", recordText);

        var log = _store.ReadDecisions(id);
        Assert.True(log.Found);
        Assert.Equal(DictationDeliveryState.Acknowledged, log.Record.Record!.State);
        Assert.Equal(DictationDeliveryState.Delivered, log.Record.Record.AcknowledgedFrom);
        Assert.Equal(Words.Length, log.Record.Record.TranscriptCharacters);
        Assert.Equal(DeliveryDecisions.Acknowledged, log.Lines.Last().Decision);
        Assert.True(log.Lines.Last().Facts!.BytesDeleted > 0);
    }

    [Fact]
    public async Task AnAcknowledgedUpload_IsGoneToEveryReader()
    {
        // Proves every reader treats an acknowledged upload exactly as it treated a deleted one: no record, not
        // staged, no audio, not pending, no session lock on either side, and a second ack is a no-op.
        var id = await OpenWithChunkAsync();
        Assert.True(_store.IsSessionLocked(_sessionId)); // precondition: a PENDING upload holds the lock
        Assert.True(DictationLockReader.IsSessionLocked(_root, _sessionId));

        Assert.True(_store.Acknowledge(id));

        Assert.Null(_store.ReadRecord(id));
        Assert.Equal(DictationRecordReadKind.Absent, _store.Read(id).Kind);
        Assert.False(_store.Exists(id));
        Assert.False(_store.IsPending(id));
        Assert.Equal("unknown_upload", (await _store.AssembleAsync(id, 1)).Status);
        Assert.False(_store.IsSessionLocked(_sessionId));
        Assert.Empty(_store.LockedSessionIds());
        Assert.False(DictationLockReader.IsSessionLocked(_root, _sessionId));
        Assert.DoesNotContain(_sessionId, DictationLockReader.LockedSessionIds(_root));
        // A fresh store over the same root (a Gateway restart) hydrates no lock from it either.
        Assert.Empty(new VoiceUploadStore(_root, TenantId.Local).LockedSessionIds());
        Assert.False(_store.Acknowledge(id), "a second acknowledgement is a no-op");
        Assert.Equal(0, _store.ExpireStalePending(TimeSpan.Zero));
    }

    [Fact]
    public async Task AnEmptyRecording_IsRetiredLikeAnAcknowledgement_WithItsOwnDecision_AndIsGoneToEveryReader()
    {
        // Proves the empty-recording outcome deletes the audio, keeps record.json and decisions.jsonl with its
        // own decision and reason, and leaves the upload gone to every reader exactly as the old delete did.
        var id = await OpenWithChunkAsync();
        Assert.True(DictationLockReader.IsSessionLocked(_root, _sessionId)); // precondition: the lock is held

        Assert.True(_store.ResolveEmptyRecording(id));

        Assert.Equal(new[] { "decisions.jsonl", "record.json" },
            Directory.EnumerateFileSystemEntries(Dir(id)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { DeliveryDecisions.Received, DeliveryDecisions.EmptyRecording }, Names(id));
        var log = _store.ReadDecisions(id);
        Assert.Equal(DictationDeliveryState.Acknowledged, log.Record.Record!.State);
        Assert.Equal(DictationDeliveryState.Pending, log.Record.Record.AcknowledgedFrom);
        Assert.Equal(DeliveryDecisions.EmptyRecording, log.Record.Record.Reason);
        Assert.Equal(DeliveryDecisions.EmptyRecording, log.Lines.Last().Facts!.Reason);

        Assert.Equal(DictationRecordReadKind.Absent, _store.Read(id).Kind);
        Assert.False(_store.Exists(id));
        Assert.Equal("unknown_upload", (await _store.AssembleAsync(id, 1)).Status);
        Assert.False(_store.IsSessionLocked(_sessionId));
        Assert.False(DictationLockReader.IsSessionLocked(_root, _sessionId));
        Assert.False(_store.ResolveEmptyRecording(id), "a retired upload is not retired twice");
        Assert.False(_store.Acknowledge(id), "a later acknowledgement is a no-op, as it was after the delete");
        Assert.Equal(new[] { DeliveryDecisions.Received, DeliveryDecisions.EmptyRecording }, Names(id));
    }

    [Fact]
    public void AReRegisterAfterAcknowledge_OpensAfresh_AndTheLogCarriesOn()
    {
        // Proves a re-register of an acknowledged id opens it as a fresh PENDING upload, as it did after the
        // directory was deleted, with the earlier decisions still above the new ones.
        var id = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: Words);
        Assert.True(_store.Acknowledge(id));

        var again = _store.OpenPending(id, _sessionId);

        Assert.True(again.Opened);
        Assert.Equal(DictationRecordReadKind.Absent, again.Before.Kind);
        Assert.True(_store.IsPending(id));
        Assert.Equal(new[]
        {
            DeliveryDecisions.Received, DeliveryDecisions.Delivered, DeliveryDecisions.Acknowledged, DeliveryDecisions.Received,
        }, Names(id));
    }

    [Fact]
    public void TheTombstoneSweep_RetiresAnOldAcknowledgedRecord_AndKeepsAYoungerOne()
    {
        // Proves the kept record is bounded: past the retention it is retired, inside it it stays.
        var old = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(old, submitted: true, movedOn: false, transcript: Words);
        Assert.True(_store.Acknowledge(old));
        var young = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(young, submitted: true, movedOn: false, transcript: Words);
        Assert.True(_store.Acknowledge(young));
        Directory.SetLastWriteTimeUtc(Dir(old), DateTime.UtcNow.AddDays(-31));
        Directory.SetLastWriteTimeUtc(Dir(young), DateTime.UtcNow.AddDays(-29));

        Assert.Equal(1, _store.SweepResolvedTombstones(TimeSpan.FromDays(30)));

        Assert.False(Directory.Exists(Dir(old)));
        Assert.True(Directory.Exists(Dir(young)));
        Assert.True(_store.ReadDecisions(young).Found);
    }

    [Fact]
    public void TheDecisionLog_NeverHoldsTheWords()
    {
        // Proves no transition writes the transcript into decisions.jsonl - only its length.
        var id = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: Words);
        _store.Acknowledge(id);

        var text = File.ReadAllText(Path.Combine(Dir(id), "decisions.jsonl"));
        Assert.DoesNotContain("zebra", text);
        Assert.DoesNotContain("marmalade", text);
        Assert.DoesNotContain(Words, text);
        Assert.Contains($"\"characters\":{Words.Length}", text);
    }

    [Fact]
    public void AnotherTenantsUploadId_IsNotFound()
    {
        // Proves the read is partitioned: the same upload id in another tenant's partition is not found.
        var id = _store.OpenPending(null, _sessionId).UploadId;
        var other = _store.ForTenant(new TenantId(Guid.NewGuid().ToString("D")));

        Assert.True(_store.ReadDecisions(id).Found); // positive control
        Assert.False(other.ReadDecisions(id).Found);
        Assert.Empty(other.ReadDecisions(id).Lines);
    }

    [Fact]
    public void RecordDecision_ForAnUnknownUpload_WritesNothingAndCreatesNothing()
    {
        // Proves a decision for an upload that has no directory is refused rather than creating one, which
        // would make an unregistered id look staged.
        var id = Guid.NewGuid().ToString();

        Assert.False(_store.RecordDecision(id, DeliveryDecisions.Retried));
        Assert.False(_store.RecordDecision("not-a-guid", DeliveryDecisions.Retried));
        Assert.False(Directory.Exists(Dir(id)));
    }

    [Fact]
    public void AHalfWrittenLine_IsReportedInPlace_AndTheRestStillRead()
    {
        // Proves one damaged line (a crash mid-append) never hides the lines around it.
        var id = _store.OpenPending(null, _sessionId).UploadId;
        File.AppendAllText(Path.Combine(Dir(id), "decisions.jsonl"), "{\"atUtc\":\"2026-09-25T0\n");
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: Words);

        Assert.Equal(new[] { DeliveryDecisions.Received, DeliveryDecisions.UnreadableLine, DeliveryDecisions.Delivered }, Names(id));
    }

    [Fact]
    public void ErrorText_IsCut_ToItsLimit()
    {
        // Proves a long error message cannot turn the log into a store of arbitrary text.
        var facts = new DeliveryDecisionFacts { Error = new string('x', 5_000) };

        Assert.Equal(DeliveryDecisionFacts.MaxErrorLength, facts.Error!.Length);
    }
}
