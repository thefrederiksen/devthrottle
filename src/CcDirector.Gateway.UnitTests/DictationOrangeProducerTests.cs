using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Transcription;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// DEFECT 19's PRODUCER - the half that had no test at all, found by phase 5 while auditing what phase 2's
/// fix was actually protected by.
///
/// WHY THIS FILE EXISTS. <see cref="DictationOrangeBoundTests"/> pins the RULE
/// (<see cref="DictationPhase.For"/>) and pins it well. It does not, and cannot, pin the WIRING that
/// supplies the rule's three facts: the roster callback appeared in ZERO test files. So you could wire
/// <c>progressing: true</c> as a constant and the entire Gateway suite stayed green while defect 19 returned
/// in full - an undelivered record would paint orange forever again, which is precisely the ninety-minute
/// lie the mission was convened to end. The rule was guarded; the thing that calls it was not.
///
/// That is this repository's signature failure and the mission has now found it FOUR times: a live consumer
/// whose producer is unguarded or absent (the Director's colour computation, the ask-and-wait verb waiting
/// on a state nothing writes, the auto-drain green for fourteen months on an injected state - and this).
/// A green suite that guards nothing is the same species as a specification that reads like a finding while
/// being invented: it is not WRONG, it is PLAUSIBLE, and plausible ships.
///
/// SO NOTHING HERE IS HAND-SET. The collaborators are the REAL <see cref="TranscribingSessions"/> and the
/// REAL <see cref="VoiceUploadStore"/> (on a temporary directory - never the machine's own dictation store),
/// driven through their real public verbs, and the method under test is the SAME
/// <c>GatewayHost.DictationStatusFor</c> the live roster calls. The one thing injected is the CLOCK, which
/// is a legitimate clock advance and not a fabricated fact: without it the idle case takes ninety seconds of
/// real time to reach.
///
/// PROVED TO FAIL, not assumed to: with <c>progressing:</c> hard-wired to <c>true</c> in
/// <c>GatewayHost.DictationStatusFor</c>, <see cref="AnUndeliveredRecordThatIsNotProgressing_PaintsNothing"/>
/// and <see cref="AProgressMarkThatGoesIdle_StopsPainting_TheNinetySecondBound"/> both go RED with the
/// defect's own symptom - an undelivered record painting "Uploading from phone" about an upload that is not
/// happening - while every other test in the suite stays green. That is the check that makes this file
/// evidence rather than decoration.
///
/// Design: docs/new_architecture/sessions.html, defect 19.
/// </summary>
public sealed class DictationOrangeProducerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dictation-producer-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 7, 14, 6, 11, 0, DateTimeKind.Utc); // the hour f13cb4b6d9d0 wedged
    private readonly TranscribingSessions _marks;
    private readonly VoiceUploadStore _uploads;

    public DictationOrangeProducerTests()
    {
        _marks = new TranscribingSessions(() => _now);
        _uploads = new VoiceUploadStore(_root, TenantId.Local);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a test failure */ }
    }

    /// <summary>The production seam itself - the exact method the live roster's dictationStatusFor calls.
    /// This is a self-host (Local) store, so the tenant is Local throughout - the same partition the live
    /// self-host roster reads. Cross-tenant isolation of the mark is proven in
    /// <see cref="TranscribingSessionsTenantIsolationTests"/>.</summary>
    private string? PhaseFor(string sessionId) => GatewayHost.DictationStatusFor(TenantId.Local, sessionId, _marks, _uploads);

    /// <summary>
    /// A real durable PENDING record, written by the real store's real verb, and read back through the real
    /// disk projection before the test proceeds.
    ///
    /// The id is a real GUID because the real store demands one: MarkPending runs NormalizeId and throws
    /// "invalid upload id" on anything Guid.TryParse rejects. The first version of this file used the literal
    /// "f13cb4b6d9d0" from the wedge investigation and the store threw - that string is the LOG's truncated
    /// display of a full upload id, not the id. Worth keeping in the record: a value copied out of a log is
    /// not the value production handles, and the real component is what caught it.
    /// </summary>
    private string ARealUndeliveredDictation(string sessionId)
    {
        var uploadId = Guid.NewGuid().ToString("N");
        _uploads.MarkPending(uploadId, sessionId);
        Assert.True(_uploads.IsSessionLocked(sessionId), "the durable record must actually stand, or this test proves nothing");
        return uploadId;
    }

    // ===================== THE DEFECT =====================

    /// <summary>
    /// THE DEFECT ITSELF, at the producer. An undelivered record stands - durable, correct, and it must
    /// never expire - and NOTHING is progressing. Upload f13cb4b6d9d0's audio had arrived intact and its
    /// transcription was failing; nothing was uploading. The old rule painted "Uploading from phone" anyway,
    /// for an hour and a half, across four Gateway restarts.
    ///
    /// This is the test that goes red if anyone hard-wires the progress fact.
    /// </summary>
    [Fact]
    public void AnUndeliveredRecordThatIsNotProgressing_PaintsNothing()
    {
        ARealUndeliveredDictation("wedged");

        Assert.Null(PhaseFor("wedged"));
    }

    /// <summary>
    /// THE BOUND, driven through the real idle rule rather than asserted about it. The phone uploads, then
    /// goes quiet - out of signal, battery dead, whatever. The colour must fall away on its own, and the
    /// words must NOT: both halves are checked here, because the ruling was "bound the colour, KEEP the
    /// record", and a fix that dropped the record would pass a colour-only test while losing the user's words.
    /// </summary>
    [Fact]
    public void AProgressMarkThatGoesIdle_StopsPainting_TheNinetySecondBound()
    {
        ARealUndeliveredDictation("quiet-phone");
        _marks.Begin(TenantId.Local, "quiet-phone");

        Assert.Equal(DictationPhase.Uploading, PhaseFor("quiet-phone"));

        _now = _now.AddSeconds(91); // the phone stops sending - the idle window lapses

        Assert.Null(PhaseFor("quiet-phone"));
        // ...and the words are still there. The record is durable BY DESIGN and this is the half that
        // vindicated it: f13cb4b6d9d0 delivered 362 real characters at 07:40. Bounding the colour must
        // never be implemented by throwing the audio away.
        Assert.True(_uploads.IsSessionLocked("quiet-phone"),
            "the durable record must survive the colour falling away - an age cut here would make a phone " +
            "out of signal lose the user's words, which is the one thing the ruling forbids");
    }

    // ===================== THE POSITIVE CASES =====================

    /// <summary>
    /// A genuinely slow upload keeps its label and is never cut short - the case that stops the bound from
    /// being implemented as a crude age cut. Progress refreshes the mark, so the label holds across a window
    /// far longer than the idle one.
    /// </summary>
    [Fact]
    public void AnUploadThatKeepsMakingProgress_KeepsPainting_HoweverLongItTakes()
    {
        ARealUndeliveredDictation("slow-phone");
        _marks.Begin(TenantId.Local, "slow-phone");

        for (var chunk = 0; chunk < 10; chunk++)
        {
            _now = _now.AddSeconds(60);      // slow, but alive
            _marks.Refresh(TenantId.Local, "slow-phone");    // the real verb the chunk-store path calls
            Assert.Equal(DictationPhase.Uploading, PhaseFor("slow-phone"));
        }
    }

    /// <summary>The server has the audio and is turning it into text: the most specific true statement wins,
    /// and it is the honest label - "Transcribing", not "Uploading from phone".</summary>
    [Fact]
    public void WhileTheServerIsActuallyTranscribing_TheLabelSaysSo()
    {
        ARealUndeliveredDictation("transcribing");
        _marks.MarkActivelyTranscribing(TenantId.Local, "transcribing");

        Assert.Equal(DictationPhase.Transcribing, PhaseFor("transcribing"));
    }

    /// <summary>
    /// THE CONTROL. A session with no dictation at all paints nothing - so the tests above are not passing
    /// because the producer is simply silent about everything.
    /// </summary>
    [Fact]
    public void ASessionWithNoDictation_PaintsNothing()
    {
        Assert.Null(PhaseFor("innocent"));
    }

    /// <summary>
    /// THE OTHER CONTROL, and the one that matters most: progress WITHOUT an undelivered record must not
    /// paint either. If it did, the producer would be reading the bounded fact as though it were the durable
    /// one - the two questions conflated again, in the opposite direction.
    /// </summary>
    [Fact]
    public void AProgressMarkWithNoUndeliveredRecord_PaintsNothing()
    {
        _marks.Begin(TenantId.Local, "no-record");

        Assert.False(_uploads.IsSessionLocked("no-record"));
        Assert.Null(PhaseFor("no-record"));
    }

    /// <summary>
    /// THE DELIVERY, end to end through the real store: once the words land, the record is terminal and the
    /// colour is gone. This is the normal path, and it is why the defect was so hard to believe - the common
    /// case never wedges, so the two comments claiming "it never wedges" were half-true, and that half was
    /// load-bearing.
    /// </summary>
    [Fact]
    public void OnceTheWordsAreDelivered_NothingPaints()
    {
        var uploadId = ARealUndeliveredDictation("delivering");
        _marks.Begin(TenantId.Local, "delivering");
        Assert.Equal(DictationPhase.Uploading, PhaseFor("delivering"));

        _uploads.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "the 362 characters");

        Assert.False(_uploads.IsSessionLocked("delivering"));
        Assert.Null(PhaseFor("delivering"));
    }
}
