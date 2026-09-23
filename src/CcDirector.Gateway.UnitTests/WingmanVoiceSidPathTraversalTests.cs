using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// VOICE V1 (audit B1): the tenant partition validates the TENANT id, but the SESSION id becomes a FILE
/// NAME inside that partition - <c>&lt;sid&gt;.mp3</c> / <c>&lt;sid&gt;.json</c> under the tenant's
/// voice-audio directory - and it was concatenated RAW, with no validation. A session id is
/// caller-controlled on the persisting path: a hostile Director advertises any non-empty string as a
/// pushed session id, and the /sessions/voice-mode/all fan-out plus the turn-end narration sweep persist
/// whatever id they are handed with no GUID gate (the request endpoints that DO gate on
/// <c>Guid.TryParse</c> are a different door). So a session id of the shape
/// "../../&lt;other-tenant&gt;/voice-audio/&lt;victim&gt;" walks the write straight out of the caller's
/// own partition and into another tenant's - overwriting or, on the delete path, deleting that tenant's
/// clip.
///
/// These tests reproduce the escape on the SAVE and the DELETE sinks independently, and pin the two
/// guards independently: the save test fails if the save guard is removed (a planted file appears in the
/// other tenant's directory); the delete test fails if the delete guard is removed (the other tenant's
/// clip is deleted). The save guard alone is not enough to keep the delete test green, and vice versa,
/// so neither guard can be dropped without a red.
/// </summary>
public sealed class WingmanVoiceSidPathTraversalTests : IDisposable
{
    private readonly GatewayDbTestHarness _settingsData = new();
    private TenantSettingsResolver? _settings;

    private TenantSettingsResolver Settings =>
        _settings ??= new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));

    public void Dispose() => _settingsData.Dispose();

    private static readonly TenantId TenantA = new("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly TenantId TenantB = new("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");

    private static byte[] Mp3(string marker) => Encoding.ASCII.GetBytes("ID3" + marker);

    private static string NewBaseDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-voice-traversal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private WingmanVoiceService ServiceAt(string baseDir)
    {
        Func<TenantId, Core.Configuration.WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => Task.FromResult<IAgentBrain>(null!);
        var vault = new KeyVault(Path.Combine(baseDir, "vault.json"));
        return new WingmanVoiceService(brain, vault, Settings, Path.Combine(baseDir, "voice-sessions.json"));
    }

    /// <summary>
    /// The SAVE sink: a traversal session id must NOT write a file into another tenant's voice-audio
    /// directory. Tenant B's directory is made to exist first (a legitimate clip), so that on unpatched
    /// code the unguarded write actually lands rather than failing on a missing directory - the reproduction
    /// has to be a real escape, not a swallowed IOException that only looks like containment.
    /// </summary>
    [Fact]
    public void A_traversal_session_id_cannot_write_into_another_tenants_voice_audio_directory()
    {
        var svc = ServiceAt(NewBaseDir());

        // Tenant B legitimately stores a clip, so B's voice-audio directory exists on disk.
        svc.StoreReadyAudioForTest(TenantB, "existing", "spoken B", "reply B", Mp3("B"));
        var bAudioDir = Path.Combine(svc.PartitionDirectoryFor(TenantB), "voice-audio");
        Assert.True(File.Exists(Path.Combine(bAudioDir, "existing.mp3")));   // control: B's own write works

        // Tenant A stores under a session id crafted to resolve into B's voice-audio directory. From A's
        // own voice-audio dir, "../../<B>/voice-audio/planted" walks up to base/tenants and back down into B.
        var evil = "../../" + TenantB.Value + "/voice-audio/planted";
        svc.StoreReadyAudioForTest(TenantA, evil, "spoken EVIL", "reply EVIL", Mp3("EVIL"));

        // The write must NOT have escaped into B's directory. On unpatched code both of these exist.
        Assert.False(File.Exists(Path.Combine(bAudioDir, "planted.mp3")));
        Assert.False(File.Exists(Path.Combine(bAudioDir, "planted.json")));

        // And B's own clip is untouched (a traversal id aimed at "existing" would have overwritten it).
        Assert.Equal(Mp3("B"), File.ReadAllBytes(Path.Combine(bAudioDir, "existing.mp3")));
    }

    /// <summary>
    /// The same escape aimed at an EXISTING victim clip: the write must not overwrite another tenant's bytes.
    /// </summary>
    [Fact]
    public void A_traversal_session_id_cannot_overwrite_another_tenants_clip()
    {
        var svc = ServiceAt(NewBaseDir());

        svc.StoreReadyAudioForTest(TenantB, "victim", "spoken B", "reply B", Mp3("VICTIM"));
        var victimMp3 = Path.Combine(svc.PartitionDirectoryFor(TenantB), "voice-audio", "victim.mp3");
        Assert.True(File.Exists(victimMp3));   // control

        var evil = "../../" + TenantB.Value + "/voice-audio/victim";
        svc.StoreReadyAudioForTest(TenantA, evil, "spoken EVIL", "reply EVIL", Mp3("EVIL"));

        // B's victim bytes are unchanged - A's write did not reach across the partition.
        Assert.Equal(Mp3("VICTIM"), File.ReadAllBytes(victimMp3));
    }

    /// <summary>
    /// The DELETE sink: a traversal session id must NOT delete another tenant's clip. An in-memory Ready
    /// entry under the traversal id (its own save is refused, so nothing lands on disk) is then removed via
    /// <see cref="WingmanVoiceService.Unmark"/>, which calls the delete sink for that id. Unguarded, that
    /// delete resolves into B's directory and removes B's files.
    ///
    /// This test is sensitive to the DELETE guard specifically: with the save guard in place but the delete
    /// guard removed, the victim is not overwritten (save refused) yet IS deleted (delete escaped), so it
    /// still fails.
    /// </summary>
    [Fact]
    public void A_traversal_session_id_cannot_delete_another_tenants_clip()
    {
        var svc = ServiceAt(NewBaseDir());

        svc.StoreReadyAudioForTest(TenantB, "victim", "spoken B", "reply B", Mp3("VICTIM"));
        var victimMp3 = Path.Combine(svc.PartitionDirectoryFor(TenantB), "voice-audio", "victim.mp3");
        var victimJson = Path.Combine(svc.PartitionDirectoryFor(TenantB), "voice-audio", "victim.json");
        Assert.True(File.Exists(victimMp3));    // control
        Assert.True(File.Exists(victimJson));   // control

        // Seed A's in-memory Ready map under the traversal id, then unmark it. Unmark removes the Ready
        // entry and calls the delete sink for the same id.
        var evil = "../../" + TenantB.Value + "/voice-audio/victim";
        svc.StoreReadyAudioForTest(TenantA, evil, "x", "y", Mp3("EVIL"));
        svc.Unmark(TenantA, evil);

        // B's victim survives, bytes intact - A's delete did not reach across the partition.
        Assert.True(File.Exists(victimMp3));
        Assert.True(File.Exists(victimJson));
        Assert.Equal(Mp3("VICTIM"), File.ReadAllBytes(victimMp3));
    }

    /// <summary>
    /// A separator-bearing session id is refused rather than written as a nested path. This is the plain
    /// "single path segment" property: a legitimate session id (a GUID) has no separators and is unaffected.
    /// </summary>
    [Fact]
    public void A_session_id_with_a_directory_separator_writes_nothing()
    {
        var svc = ServiceAt(NewBaseDir());
        var aAudioDir = Path.Combine(svc.PartitionDirectoryFor(TenantA), "voice-audio");

        svc.StoreReadyAudioForTest(TenantA, "sub/child", "spoken", "reply", Mp3("X"));

        // No "child" file created under a "sub" subdirectory of A's own voice-audio dir either.
        Assert.False(File.Exists(Path.Combine(aAudioDir, "sub", "child.mp3")));
        Assert.False(Directory.Exists(Path.Combine(aAudioDir, "sub")));
    }

    /// <summary>
    /// A PERCENT-ENCODED traversal id must write nothing. This is the shape a separator/invalid-char denylist
    /// lets through: "%2e%2e%2f%2e%2e%2fescape" carries no literal separator and no filesystem-invalid char,
    /// so the earlier "single file-name component" check accepted it and built a file literally named that
    /// inside A's own directory. The strict allow-list refuses it because '%' is not an allowed character, so
    /// nothing is written at all - neither an escape nor a bizarrely-named clip.
    /// </summary>
    [Fact]
    public void A_percent_encoded_traversal_session_id_writes_nothing()
    {
        var svc = ServiceAt(NewBaseDir());
        var aAudioDir = Path.Combine(svc.PartitionDirectoryFor(TenantA), "voice-audio");

        var evil = "%2e%2e%2f%2e%2e%2fescape";
        svc.StoreReadyAudioForTest(TenantA, evil, "spoken", "reply", Mp3("X"));

        // The percent-encoded id is refused: no clip file is created under it on disk.
        Assert.False(File.Exists(Path.Combine(aAudioDir, evil + ".mp3")));
        Assert.False(File.Exists(Path.Combine(aAudioDir, evil + ".json")));
    }

    /// <summary>
    /// An OVER-LONG session id must be refused by the length bound BEFORE any path work happens. A 300-char
    /// id is all allow-list characters yet far past any real session id, so the bound rejects it - closing
    /// the unbounded-segment shape (which could push a file name past a filesystem's component limit).
    ///
    /// The observable is the voice-audio DIRECTORY, not a clip file: a 300-char file name would blow past the
    /// platform path limit and throw on the write regardless of the guard, so a "no .mp3 file" assertion would
    /// pass for the wrong reason (the OS, not our bound) and fail to pin the guard. The guard instead returns
    /// null and the save sink bails out BEFORE it calls Directory.CreateDirectory, so the tenant's voice-audio
    /// directory is never even created. Drop the length bound and the sink runs on to CreateDirectory (then
    /// throws on the over-long write), so the directory appears - reddening this test.
    /// </summary>
    [Fact]
    public void An_over_long_session_id_is_refused_before_any_directory_is_created()
    {
        var svc = ServiceAt(NewBaseDir());
        var aAudioDir = Path.Combine(svc.PartitionDirectoryFor(TenantA), "voice-audio");
        Assert.False(Directory.Exists(aAudioDir));   // precondition: nothing created yet

        var evil = new string('a', 300);
        svc.StoreReadyAudioForTest(TenantA, evil, "spoken", "reply", Mp3("X"));

        // The length bound refused the id before the sink touched the filesystem, so the directory the sink
        // would have created for the write was never created.
        Assert.False(Directory.Exists(aAudioDir));
    }

    /// <summary>
    /// CONTROL: an ordinary GUID session id still round-trips through the save sink unchanged, so the guard
    /// refuses only the unsafe shapes and never a legitimate id.
    /// </summary>
    [Fact]
    public void A_normal_guid_session_id_still_persists()
    {
        var svc = ServiceAt(NewBaseDir());
        var sid = Guid.NewGuid().ToString();
        svc.StoreReadyAudioForTest(TenantA, sid, "spoken", "reply", Mp3("OK"));

        var mp3 = Path.Combine(svc.PartitionDirectoryFor(TenantA), "voice-audio", sid + ".mp3");
        Assert.True(File.Exists(mp3));
        Assert.Equal(Mp3("OK"), File.ReadAllBytes(mp3));
        Assert.True(svc.HasVoice(TenantA, sid));
    }
}
