using System.Text;
using CcDirector.Gateway.Voice;
using Xunit;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="VoiceTurnArchive"/> - the durable, disk-backed store that makes a
/// completed voice-turn reply "sit in the session" past the in-memory job TTL and across a
/// Gateway restart. Each test archives under an isolated temp root.
/// </summary>
public sealed class VoiceTurnArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-archive-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceTurnArchive _archive;

    public VoiceTurnArchiveTests() => _archive = new VoiceTurnArchive(false, _root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
    }

    private static VoiceTurnArchiveRecord Record(string turnId, string sid, string uploadId = "") => new()
    {
        TurnId = turnId,
        SessionId = sid,
        UploadId = uploadId,
        Stage = "reply",
        Transcript = "what is two plus two",
        Summary = "It is four.",
        HasAudio = true,
        CreatedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public void SaveThenGet_RoundTripsRecordAndAudio()
    {
        var turnId = Guid.NewGuid().ToString();
        var audio = Encoding.UTF8.GetBytes("ID3-fake-mp3-bytes");
        _archive.Save(TenantId.Local, Record(turnId, "sid-1"), audio);

        var got = _archive.Get(TenantId.Local, turnId);
        Assert.NotNull(got);
        Assert.Equal("It is four.", got!.Summary);
        Assert.Equal("what is two plus two", got.Transcript);

        var gotAudio = _archive.GetAudio(TenantId.Local, turnId);
        Assert.NotNull(gotAudio);
        Assert.Equal(audio, gotAudio);
    }

    [Fact]
    public void Save_NoAudio_GetAudioReturnsNull()
    {
        var turnId = Guid.NewGuid().ToString();
        var rec = Record(turnId, "sid-1");
        rec.HasAudio = false;
        _archive.Save(TenantId.Local, rec, replyAudio: null);

        Assert.NotNull(_archive.Get(TenantId.Local, turnId));
        Assert.Null(_archive.GetAudio(TenantId.Local, turnId));
    }

    [Fact]
    public void Get_UnknownTurn_ReturnsNull()
    {
        Assert.Null(_archive.Get(TenantId.Local, Guid.NewGuid().ToString()));
        Assert.Null(_archive.Get(TenantId.Local, "not-a-guid"));
    }

    [Fact]
    public void FindByUpload_MatchesTheOriginatingUpload()
    {
        var turnId = Guid.NewGuid().ToString();
        var uploadId = Guid.NewGuid().ToString("N");
        _archive.Save(TenantId.Local, Record(turnId, "sid-1", uploadId), null);

        var found = _archive.FindByUpload(TenantId.Local, uploadId);
        Assert.NotNull(found);
        Assert.Equal(turnId, found!.TurnId);

        Assert.Null(_archive.FindByUpload(TenantId.Local, Guid.NewGuid().ToString("N")));
        Assert.Null(_archive.FindByUpload(TenantId.Local, ""));
    }

    [Fact]
    public void ListForSession_ReturnsOnlyThatSession_NewestFirst()
    {
        var early = Record(Guid.NewGuid().ToString(), "sid-A");
        early.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
        early.Summary = "older";
        var late = Record(Guid.NewGuid().ToString(), "sid-A");
        late.Summary = "newer";
        var other = Record(Guid.NewGuid().ToString(), "sid-B");

        _archive.Save(TenantId.Local, early, null);
        _archive.Save(TenantId.Local, late, null);
        _archive.Save(TenantId.Local, other, null);

        var list = _archive.ListForSession(TenantId.Local, "sid-A");
        Assert.Equal(2, list.Count);
        Assert.Equal("newer", list[0].Summary);   // newest first
        Assert.Equal("older", list[1].Summary);
        Assert.DoesNotContain(list, r => r.SessionId == "sid-B");
    }

    [Fact]
    public void ListForSession_SinceFilter_ExcludesOlder()
    {
        var old = Record(Guid.NewGuid().ToString(), "sid-A");
        old.CreatedAtUtc = DateTime.UtcNow.AddHours(-2);
        var fresh = Record(Guid.NewGuid().ToString(), "sid-A");
        _archive.Save(TenantId.Local, old, null);
        _archive.Save(TenantId.Local, fresh, null);

        var list = _archive.ListForSession(TenantId.Local, "sid-A", DateTime.UtcNow.AddHours(-1));
        Assert.Single(list);
        Assert.Equal(fresh.TurnId, list[0].TurnId);
    }
}
