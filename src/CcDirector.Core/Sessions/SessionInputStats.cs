using System.Collections.Concurrent;
using CcDirector.Gateway.Contracts;

using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Per-session tally of how the operator drove this session: submitted TURNS and CHARACTER volume, split
/// by (<see cref="InputModality"/>, <see cref="InputSurface"/>). This is the honest instrumentation heart
/// of the DevThrottle Stats mission: the count is taken at the Session choke point
/// (<see cref="Session.SendInput(byte[], InputOrigin?, SubmissionProvenance)"/> / <see cref="Session.SendTextAsync(string, SubmissionProvenance, SendSource, InputOrigin?)"/>),
/// the one place that sees desktop-local input too, so a published phone/voice share is never silently
/// inflated by a surface the Gateway cannot see.
///
/// Counting rules (mission "unit of how much"):
/// - A submitted turn (a whole message that ends with Enter, or a dictation/voice-turn delivery) is one
///   turn via <see cref="RecordTurn"/>. One spoken utterance and one typed message each count as one turn,
///   so neither modality is inflated by its mechanics.
/// - A bare keystroke is the user COMPOSING and is not a turn and not counted. It is the write carrying
///   the Enter that is the turn, and it is recorded exactly like any other submission, with the character
///   volume of the whole line.
/// - There is ONE writer: <see cref="Session"/>.StampSubmission, which stamps the submission event and
///   counts the turn together. There is deliberately no method here for recording characters without a
///   turn. One existed, terminal typing was the only caller of it, and the result was that 594 of the
///   owner's 771 typed submissions in 2026-W35 reached the character total and never the turn total -
///   28.3 points of a published spoken share (see the mission reconciliation of 2026-09-05).
///
/// Thread-safe: several PTY/stream callers can record concurrently.
/// </summary>
public sealed class SessionInputStats
{
    private sealed class Counters
    {
        public long Turns;
        public long Characters;
    }

    private readonly ConcurrentDictionary<(InputModality Modality, InputSurface Surface), Counters> _buckets = new();

    // Turns this session was driven by ANOTHER AGENT (issue #1636), on their own lane - never in _buckets.
    // See InputStatsDto.AgentDrivenTurns for why they are kept apart rather than given a modality.
    private readonly Counters _agentDriven = new();

    /// <summary>Raised after any recorded change, so the host can persist and push a delta (debounced by
    /// the host - this fires per keystroke). Carries no data; readers call <see cref="Snapshot"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// Fan out <see cref="Changed"/> with a subscriber's fault CONTAINED (inspection finding I2-04 of the "Clean up
    /// Your Throttle" mission, 2026-09-05). The counters are advanced before this is raised, and the caller -
    /// <c>Session.StampSubmission</c> - stamps the submission ledger event AFTER it returns. A subscriber that
    /// threw here therefore left the tally advanced, the backend already holding the text, and the ledger
    /// without the submission: the one invariant this class exists to keep, split by an observer. The fault
    /// is logged and swallowed, exactly as the ledger's own fan-out already does.
    /// </summary>
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { FileLog.Write($"[SessionInputStats] Changed subscriber FAILED (contained; the tally and the submission ledger both stand): {ex.Message}"); }
    }

    /// <summary>
    /// Record one submitted turn from <paramref name="origin"/>, plus its <paramref name="characters"/> of
    /// text volume. The ONE caller is <see cref="Session"/>.StampSubmission, which stamps the submission
    /// event in the same breath - both the text path
    /// (<see cref="Session.SendTextAsync(string, SubmissionProvenance, SendSource, InputOrigin?)"/>, a dictation or voice-turn
    /// delivery) and the raw-byte path (<see cref="Session.SendInput(byte[], InputOrigin?, SubmissionProvenance)"/>, terminal
    /// typing) reach it there. A submission with no new characters (a line recalled from history) is still
    /// one turn: the count must match the submission ledger exactly.
    /// </summary>
    public void RecordTurn(InputOrigin origin, int characters)
    {
        var c = _buckets.GetOrAdd((origin.Modality, origin.Surface), static _ => new Counters());
        Interlocked.Increment(ref c.Turns);
        if (characters > 0)
            Interlocked.Add(ref c.Characters, characters);
        RaiseChanged();
    }

    /// <summary>
    /// Record one turn that ANOTHER AGENT drove into this session (issue #1636) - a fleet message, ask, or
    /// broadcast. A real turn: the sending agent decided to send it. Counted on its own lane, never into
    /// the human buckets, so the voice-versus-typed and phone-versus-desktop numbers stay about the human.
    ///
    /// Framework-authored text (handover, queue drain, pre-prompt) is NOT this: it carries nobody's
    /// decision and is not counted at all, by anything.
    /// </summary>
    public void RecordAgentTurn(int characters)
    {
        Interlocked.Increment(ref _agentDriven.Turns);
        if (characters > 0)
            Interlocked.Add(ref _agentDriven.Characters, characters);
        RaiseChanged();
    }

    /// <summary>An immutable snapshot of the tally as the shared wire DTO, buckets in a stable order.</summary>
    public InputStatsDto Snapshot()
    {
        var dto = new InputStatsDto
        {
            AgentDrivenTurns = Interlocked.Read(ref _agentDriven.Turns),
            AgentDrivenCharacters = Interlocked.Read(ref _agentDriven.Characters),
        };
        foreach (var kvp in _buckets
                     .OrderBy(b => b.Key.Modality)
                     .ThenBy(b => b.Key.Surface))
        {
            var origin = new InputOrigin(kvp.Key.Modality, kvp.Key.Surface);
            dto.Buckets.Add(new InputStatBucketDto
            {
                Modality = origin.ModalityToken,
                Surface = origin.SurfaceToken,
                Turns = Interlocked.Read(ref kvp.Value.Turns),
                Characters = Interlocked.Read(ref kvp.Value.Characters),
            });
        }
        return dto;
    }

    /// <summary>True when nothing has been counted yet (used to skip pushing an empty tally). A session
    /// driven ONLY by other agents has no buckets but is not empty - it must still reach the wire, or the
    /// agent-to-agent tally would silently miss exactly the sessions it is about.</summary>
    public bool IsEmpty => _buckets.IsEmpty && Interlocked.Read(ref _agentDriven.Turns) == 0;

    /// <summary>
    /// Replace the tally with a persisted snapshot (Director restart restore). Buckets not present in
    /// <paramref name="dto"/> are cleared; a null or empty dto leaves the tally empty. Does NOT raise
    /// <see cref="Changed"/> - seeding is a load, not new user activity.
    /// </summary>
    public void Seed(InputStatsDto? dto)
    {
        _buckets.Clear();
        Interlocked.Exchange(ref _agentDriven.Turns, dto?.AgentDrivenTurns ?? 0);
        Interlocked.Exchange(ref _agentDriven.Characters, dto?.AgentDrivenCharacters ?? 0);
        if (dto?.Buckets is null) return;
        foreach (var b in dto.Buckets)
        {
            var modality = string.Equals(b.Modality, "voice", StringComparison.OrdinalIgnoreCase)
                ? InputModality.Voice
                : InputModality.Typed;
            var surface = (b.Surface ?? "").Trim().ToLowerInvariant() switch
            {
                "desktop" => InputSurface.Desktop,
                "cockpit" => InputSurface.Cockpit,
                "phone" => InputSurface.Phone,
                _ => InputSurface.Unknown,
            };
            _buckets[(modality, surface)] = new Counters { Turns = b.Turns, Characters = b.Characters };
        }
    }
}
