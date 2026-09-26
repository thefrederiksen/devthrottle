using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Whether a "Send anyway" claim becomes the prompt's delivery id (<see cref="VoiceUploadStore.ResolveDeliveryClaim"/>),
/// and what it writes to the recording's decision log (Voice Delivery mission, phase 1, review findings 1 to 3). The
/// proof through the real acknowledge and prompt routes, with the Director's refusal, is in
/// DeliveryIdIsGatewayAuthoritativeTests; these pin the store's rule state by state and the claim window's edges.
/// </summary>
[Collection(VoiceUploadStoreRecordHookCollection.Name)]
public sealed class VoiceUploadStoreDeliveryClaimTests : IDisposable
{
    private const string Words = "zebra quartz marmalade the owner said this";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-claims-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceUploadStore _store;
    private readonly string _sessionId = Guid.NewGuid().ToString();

    public VoiceUploadStoreDeliveryClaimTests() => _store = new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
    }

    private string Dir(string id) => Path.Combine(_root, VoiceUploadStore.NormalizeUploadId(id)!);
    private DeliveryDecisionLine LastLine(string id) => _store.ReadDecisions(id).Lines.Last();
    private DateTime ResolvedAt(string id) => _store.ReadDecisions(id).Record.Record!.ResolvedAtUtc!.Value;

    private string MovedOnAndAcknowledged()
    {
        var id = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(id, submitted: false, movedOn: true, transcript: Words, reason: DeliveryDecisions.TooOld);
        Assert.True(_store.Acknowledge(id));
        return id;
    }

    [Fact]
    public void AnAcknowledgedRecording_OfThisSession_IsBelieved_AndTheDecisionWritten()
    {
        // Proves review finding 1 at the store: the recording every real "Send anyway" names - acknowledged - is seen,
        // although Read still answers Absent for it to every other caller.
        var id = MovedOnAndAcknowledged();
        Assert.Equal(DictationRecordReadKind.Absent, _store.Read(id).Kind);

        var claim = _store.ResolveDeliveryClaim(id, _sessionId, Words.Length, DateTime.UtcNow);

        Assert.True(claim.Verified, claim.Why);
        Assert.Equal(VoiceUploadStore.NormalizeUploadId(id), claim.UploadId);
        var line = LastLine(id);
        Assert.Equal(DeliveryDecisions.ClaimVerified, line.Decision);
        Assert.Equal(nameof(DictationDeliveryState.Acknowledged), line.Facts!.State);
        Assert.Equal(nameof(DictationDeliveryState.Delivered), line.Facts.PreviousState);
        Assert.Equal(Words.Length, line.Facts.Characters);
    }

    [Fact]
    public void EveryStateThatNamesARealRecording_IsBelieved()
    {
        // Proves the five states a real recording can be in are all believed for this session.
        var pending = _store.OpenPending(null, _sessionId).UploadId;
        var failed = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkFailed(failed, "transcription_error");
        var delivered = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(delivered, submitted: true, movedOn: false, transcript: Words);
        var abandoned = _store.OpenPending(null, _sessionId).UploadId;
        Assert.True(_store.Abandon(abandoned, "user_abandoned").Abandoned);
        var acknowledged = MovedOnAndAcknowledged();

        foreach (var id in new[] { pending, failed, delivered, abandoned, acknowledged })
        {
            var claim = _store.ResolveDeliveryClaim(id, _sessionId, 5, DateTime.UtcNow);
            Assert.True(claim.Verified, $"{id}: {claim.Why}");
        }
    }

    [Fact]
    public void AnotherSessionsRecording_IsDropped_AndTheDropWritten()
    {
        // Proves a recording of the caller's own account but a DIFFERENT session is refused, and the log says why.
        var id = MovedOnAndAcknowledged();

        var claim = _store.ResolveDeliveryClaim(id, Guid.NewGuid().ToString(), 5, DateTime.UtcNow);

        Assert.False(claim.Verified);
        Assert.Equal(DeliveryDecisions.ClaimDropped, LastLine(id).Decision);
        Assert.Equal("another-session", LastLine(id).Facts!.Reason);
    }

    [Fact]
    public void AnUploadThatIsNotHere_IsDropped_AndNoDirectoryIsCreated()
    {
        // Proves a claim for an id never registered here - or registered in another account's partition - writes
        // nothing and creates nothing: a dropped claim must not make an unknown id look staged.
        var never = Guid.NewGuid().ToString();
        var other = _store.ForTenant(new TenantId("22222222-2222-2222-2222-222222222222"));
        var othersId = other.OpenPending(null, _sessionId).UploadId;
        var othersLines = other.ReadDecisions(othersId).Lines.Count;

        Assert.False(_store.ResolveDeliveryClaim(never, _sessionId, 5, DateTime.UtcNow).Verified);
        Assert.False(_store.ResolveDeliveryClaim(othersId, _sessionId, 5, DateTime.UtcNow).Verified);

        Assert.False(Directory.Exists(Dir(never)));
        Assert.False(Directory.Exists(Dir(othersId)));
        Assert.Equal(othersLines, other.ReadDecisions(othersId).Lines.Count);
    }

    [Fact]
    public void TheAcknowledgement_KeepsTheResolutionTime_NotItsOwn()
    {
        // Proves the claim window runs from when the recording was resolved, not from the acknowledgement, which can
        // come days later and would stretch the window past the Director's record.
        var id = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(id, submitted: false, movedOn: true, transcript: Words, reason: DeliveryDecisions.TooOld);
        var resolved = ResolvedAt(id);
        Thread.Sleep(20);

        Assert.True(_store.Acknowledge(id));

        Assert.Equal(resolved, ResolvedAt(id));
    }

    [Fact]
    public void AClaimJustInsideTheWindow_IsBelieved()
    {
        // Proves the edge on the inside: a recording resolved a minute short of the claim window still carries its id.
        var id = MovedOnAndAcknowledged();

        var claim = _store.ResolveDeliveryClaim(id, _sessionId, 5, ResolvedAt(id) + DeliveryRetention.ClaimWindow - TimeSpan.FromMinutes(1));

        Assert.True(claim.Verified, claim.Why);
        Assert.Equal(DeliveryDecisions.ClaimVerified, LastLine(id).Decision);
    }

    [Fact]
    public void AClaimJustOutsideTheWindow_IsDropped_AndTheDropWritten()
    {
        // Proves the edge on the outside: a minute past the window the Director may have forgotten the id, so the claim
        // is dropped - and written, with why and when the recording was resolved - and the words go as typed.
        var id = MovedOnAndAcknowledged();
        var resolved = ResolvedAt(id);

        var claim = _store.ResolveDeliveryClaim(id, _sessionId, 5, resolved + DeliveryRetention.ClaimWindow + TimeSpan.FromMinutes(1));

        Assert.False(claim.Verified);
        var line = LastLine(id);
        Assert.Equal(DeliveryDecisions.ClaimDropped, line.Decision);
        Assert.Equal("outside-claim-window", line.Facts!.Reason);
        Assert.Equal(resolved, line.Facts.ResolvedAtUtc);
    }

    [Fact]
    public void AResolvedRecordWithNoResolutionTime_IsDropped_NotGuessed()
    {
        // Proves a resolved record written before resolution times were kept is dropped with its own reason, rather
        // than having an age invented for it.
        var id = _store.OpenPending(null, _sessionId).UploadId;
        _store.MarkDelivered(id, submitted: true, movedOn: false, transcript: Words);
        var path = Path.Combine(Dir(id), "record.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.Remove("ResolvedAtUtc"), "the record did not carry ResolvedAtUtc under that name");
        File.WriteAllText(path, json.ToJsonString());

        var claim = _store.ResolveDeliveryClaim(id, _sessionId, 5, DateTime.UtcNow);

        Assert.False(claim.Verified);
        Assert.Equal("no-resolution-time", LastLine(id).Facts!.Reason);
    }

    [Fact]
    public void TheTombstoneSweep_KeepsARecordAtLeastAsLongAsTheClaimWindow()
    {
        // Proves the Gateway never retires a record while a claim for it could still be believed.
        Assert.True(CcDirector.Gateway.GatewayHost.DictationTombstoneMaxAge >= DeliveryRetention.ClaimWindow);
    }
}
