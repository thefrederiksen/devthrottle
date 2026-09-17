using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE ONE PLACE THE DISPLAY FOLD GETS ITS INPUTS (the Wingman inspector, phase 2, inspection round 1).
///
/// Two things fold a roster into the colour and label a row shows: the display push that every screen renders, and the
/// Wingman inspector's trace, which records the colour a stop produced. They used to be two call sites, each naming the
/// fold's inputs for itself - and they disagreed. The trace passed no voice-waiting clock, so a voice-mode session whose
/// wait had given up showed red "Voice did not arrive" on the push and was recorded yellow "Preparing voice". Fixing that
/// one argument would leave the next input free to drift the same way. So neither caller names an input any more: both
/// call here, and every input is chosen in <see cref="Fold"/>, once.
///
/// WHAT THE TWO CALLERS MAY DIFFER IN, and nothing else:
///  - WHICH VERDICTS: the push reads the account's live verdicts; a trace forces its own stop's verdict on its row. That
///    difference is the whole point of the trace.
///  - WHETHER STATE MOVES: the push writes the clocks it folds from (the needs-you clock, the voice-waiting clock) and
///    spends a snooze expiry. A trace READS the same clocks and memory and moves nothing, because recording what the
///    screen showed must not change what the screen shows. Each read answers what the write would have answered (see
///    <see cref="VoiceWaitingClock.Peek"/>, <see cref="NeedsYouClock.Peek"/>, <see cref="SnoozeExpiryReJudge.Peek"/>).
///  - PRUNING: only the push prunes the snooze memory to the account's roster. Pruning is a write.
/// </summary>
internal sealed class DisplayFold
{
    private readonly Func<WingmanVoiceService?> _voice;
    private readonly NeedsYouClock _needsYou;
    private readonly VoiceWaitingClock _voiceWaiting;
    private readonly Snooze.SnoozeRegistry? _snoozes;
    private readonly HandRaiseRegistry _handRaises;
    private readonly Func<SnoozeExpiryReJudge?> _snoozeExpiry;
    private readonly PushedSessionStore _pushed;
    private readonly Func<TenantId, IDisposable> _enterScope;

    /// <param name="voice">The voice service, read at fold time because the host builds it later. Null answers "no voice
    /// state at all".</param>
    /// <param name="snoozeExpiry">The snooze-expiry memory, read at fold time for the same reason.</param>
    /// <param name="enterScope">Enters an account's scope for a fold that runs outside one (a trace, on the writer's
    /// thread). The push already runs inside its account's scope.</param>
    public DisplayFold(
        Func<WingmanVoiceService?> voice,
        NeedsYouClock needsYou,
        VoiceWaitingClock voiceWaiting,
        Snooze.SnoozeRegistry? snoozes,
        HandRaiseRegistry handRaises,
        Func<SnoozeExpiryReJudge?> snoozeExpiry,
        PushedSessionStore pushed,
        Func<TenantId, IDisposable> enterScope)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _needsYou = needsYou ?? throw new ArgumentNullException(nameof(needsYou));
        _voiceWaiting = voiceWaiting ?? throw new ArgumentNullException(nameof(voiceWaiting));
        _snoozes = snoozes;
        _handRaises = handRaises ?? throw new ArgumentNullException(nameof(handRaises));
        _snoozeExpiry = snoozeExpiry ?? throw new ArgumentNullException(nameof(snoozeExpiry));
        _pushed = pushed ?? throw new ArgumentNullException(nameof(pushed));
        _enterScope = enterScope ?? throw new ArgumentNullException(nameof(enterScope));
    }

    /// <summary>The display push: the account in scope, its live verdicts, and every clock moved as it is folded.</summary>
    /// <param name="ambientTenant">The account of the per-tenant pass, or null when none is in scope - then nothing
    /// about voice is read, which is a deny, never a fall back to another account.</param>
    public void Push(TenantId? ambientTenant, List<SessionDto> sessions, ITurnVerdictRowSource? verdicts)
        => Fold(ambientTenant, sessions, verdicts, writes: true);

    /// <summary>A trace's record of what the push shows, with the stop's own verdicts, moving nothing.</summary>
    public void Record(TenantId tenant, List<SessionDto> sessions, ITurnVerdictRowSource verdicts)
    {
        using var scope = _enterScope(tenant);
        Fold(tenant, sessions, verdicts, writes: false);
    }

    private void Fold(TenantId? ambientTenant, List<SessionDto> sessions, ITurnVerdictRowSource? verdicts, bool writes)
    {
        var voice = ambientTenant is { } t && WingmanVoiceService.CanNameVoicePartition(t) ? _voice() : null;
        var tenant = ambientTenant ?? TenantId.Local;
        GatewayHost.EnrichVoiceThenFoldForPush(
            sessions,
            voiceGeneratingFor: sid => voice?.IsGenerating(tenant, sid) == true,
            voiceAudioReadyFor: sid => voice?.HasVoice(tenant, sid) == true,
            tenant: tenant,
            needsYouStampFor: writes
                ? (acct, sid, isRed) => _needsYou.Stamp(acct, sid, isRed)
                : (acct, sid, isRed) => _needsYou.Peek(acct, sid, isRed),
            snoozeRegistry: _snoozes,
            handRaises: _handRaises,
            voiceUnavailableFor: sid => voice?.VoiceUnavailableFor(tenant, sid),
            nothingToNarrateFor: sid => voice?.NothingToNarrateFor(tenant, sid) == true,
            directorCannotSendConversationFor: sid => voice?.DirectorCannotSendConversationFor(tenant, sid) == true,
            narrationAbandonedFor: sid => voice?.NarrationAbandonedFor(tenant, sid) == true,
            voiceWaitingStampFor: ambientTenant is null
                ? null
                : writes
                    ? (sid, waiting) => _voiceWaiting.Stamp(tenant, sid, waiting)
                    : (sid, waiting) => _voiceWaiting.Peek(tenant, sid, waiting),
            turnVerdictRows: verdicts,
            snoozeExpiry: _snoozeExpiry(),
            // KNOWN, not connected: a Director that has gone quiet still has its sessions on the roster, so pruning to
            // the connected ones would read "I cannot see it this second" as "it is gone". Only a writing fold prunes.
            snoozeRosterSessionIds: writes && ambientTenant is { } rosterTenant ? _pushed.KnownSessionIds(rosterTenant) : null,
            writes: writes);
    }
}
