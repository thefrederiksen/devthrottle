using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Voice;

/// <summary>
/// Pure, UI-free, audio-free wake-word state machine for the "wingman" hands-free
/// grammar:
///
///   "wingman ..."          -> wake; start capturing a prompt
///   "... wingman send"     -> commit the captured prompt
///   "... wingman cancel"   -> discard the captured prompt
///
/// The engine consumes the CUMULATIVE transcript the way
/// <c>OpenAiRealtimeProvider.OnPartial</c> delivers it (one growing string per
/// listen session), classifies the control phrases as delimiters, and raises
/// <see cref="OnEvent"/>. It knows nothing about microphones or Avalonia, so it
/// is unit-tested with scripted strings and reused unchanged by the desktop test
/// dialog (and later the phone / session view).
///
/// STREAMING DISAMBIGUATION
/// ------------------------
/// The control words live inside the same transcript as the prompt body ("wingman
/// fix the bug wingman send" in one breath), so the engine works on a rolling,
/// normalized token buffer and treats the phrases as boundaries. The load-bearing
/// rule is that it NEVER acts on the final settled token until <see cref="Finalize"/>
/// is called: a trailing "wingman" could still become "wingman send" when the next
/// delta arrives. The caller debounces speech silence and calls Finalize() so a
/// phrase ending in "wingman send" still commits.
///
/// NO FALLBACKS: every control phrase that cannot act (e.g. "wingman send" with no
/// captured body) is surfaced as <see cref="WakeWordEventKind.ControlIgnored"/> with
/// a reason, never silently dropped.
/// </summary>
public sealed class WakeWordEngine
{
    private readonly string _wake;
    private readonly string _send;
    private readonly string _cancel;

    // Residual raw text for the current scanning window: the transcript since the
    // last consumed control boundary, with already-processed leading tokens trimmed
    // off. Kept raw (original case/punctuation) so the emitted body reads naturally.
    private string _pending = "";

    // Last cumulative snapshot seen, used to compute the appended delta and to detect
    // a (defensive) non-monotonic reset.
    private string _lastCumulative = "";

    // While Capturing: raw char offset into _pending where the prompt body begins
    // (just after the wake word). -1 while Idle.
    private int _bodyStart = -1;

    /// <summary>Current state of the machine.</summary>
    public WakeWordState State { get; private set; } = WakeWordState.Idle;

    /// <summary>The prompt body captured so far (empty while Idle).</summary>
    public string CurrentBody { get; private set; } = "";

    /// <summary>Raised for every classification event. Synchronous, on the caller's thread.</summary>
    public event Action<WakeWordEvent>? OnEvent;

    /// <param name="wakeWord">The wake word, default "wingman".</param>
    /// <param name="sendVerb">The commit verb following the wake word, default "send".</param>
    /// <param name="cancelVerb">The discard verb following the wake word, default "cancel".</param>
    public WakeWordEngine(string wakeWord = "wingman", string sendVerb = "send", string cancelVerb = "cancel")
    {
        _wake = NormalizeToken(wakeWord);
        _send = NormalizeToken(sendVerb);
        _cancel = NormalizeToken(cancelVerb);
        if (_wake.Length == 0) throw new ArgumentException("Wake word cannot be empty after normalization.", nameof(wakeWord));
        if (_send.Length == 0) throw new ArgumentException("Send verb cannot be empty after normalization.", nameof(sendVerb));
        if (_cancel.Length == 0) throw new ArgumentException("Cancel verb cannot be empty after normalization.", nameof(cancelVerb));
    }

    /// <summary>
    /// Feed the latest CUMULATIVE transcript snapshot (as delivered by the realtime
    /// provider's OnPartial). The engine processes only the newly appended text.
    /// </summary>
    public void Feed(string cumulativeTranscript)
    {
        cumulativeTranscript ??= "";

        string delta;
        if (cumulativeTranscript.StartsWith(_lastCumulative, StringComparison.Ordinal))
        {
            delta = cumulativeTranscript.Substring(_lastCumulative.Length);
        }
        else
        {
            // Non-monotonic snapshot (provider reset, or out-of-order test input).
            // Treat the whole new snapshot as freshly appended speech rather than
            // losing the in-flight body.
            FileLog.Write("[WakeWordEngine] non-monotonic snapshot; appending whole snapshot to residual");
            delta = cumulativeTranscript;
        }

        _lastCumulative = cumulativeTranscript;
        if (delta.Length == 0) return;

        _pending += delta;
        Process(forceFinal: false);
    }

    /// <summary>
    /// Settle the held trailing token. Call this on a speech pause / stop so a phrase
    /// ending in "wingman send" (or a bare trailing "wingman") is acted upon rather
    /// than held forever. (Named Flush, not Finalize, to avoid hiding Object.Finalize.)
    /// </summary>
    public void Flush()
    {
        Process(forceFinal: true);
    }

    /// <summary>Clear all state back to Idle for a new listen session.</summary>
    public void Reset()
    {
        _pending = "";
        _lastCumulative = "";
        _bodyStart = -1;
        State = WakeWordState.Idle;
        CurrentBody = "";
    }

    // ===== core scan ========================================================

    private readonly record struct Tok(string Norm, int RawStart, int RawEnd);

    private void Process(bool forceFinal)
    {
        var tokens = Tokenize(_pending);
        if (tokens.Count == 0)
        {
            // Nothing actionable; trim pure-punctuation residual so it cannot grow.
            if (_pending.Trim().Length == 0) { _pending = ""; _bodyStart = State == WakeWordState.Capturing ? 0 : -1; }
            return;
        }

        // Hold the final token unless finalizing, so "wingman" can still become
        // "wingman send". A multi-token control phrase ("wingman send") therefore
        // needs both tokens settled, which forceFinal also provides.
        int limit = forceFinal ? tokens.Count : tokens.Count - 1;

        int i = 0;
        int consumeUntil = 0; // exclusive index of leading tokens safe to drop from _pending
        int bodyEnd = limit;  // exclusive token index where the live body ends (excludes a held trailing wake)

        while (i < limit)
        {
            var tok = tokens[i];

            if (State == WakeWordState.Idle)
            {
                if (tok.Norm != _wake)
                {
                    // Chatter before any wake word: ignore and drop it.
                    i++;
                    consumeUntil = i;
                    continue;
                }

                // tok is the wake word. Disambiguate against the NEXT settled token.
                if (i + 1 < limit)
                {
                    var next = tokens[i + 1].Norm;
                    if (next == _send || next == _cancel)
                    {
                        // "wingman send"/"wingman cancel" with nothing captured.
                        Emit(WakeWordEventKind.ControlIgnored, "",
                            $"\"{_wake} {next}\" while idle - nothing to {(next == _send ? "send" : "cancel")}");
                        i += 2;
                        consumeUntil = i;
                        continue;
                    }

                    // Genuine wake: body begins at the next token.
                    BeginCapture(tokens[i + 1].RawStart);
                    i += 1;
                    consumeUntil = i; // drop chatter + the wake word; retain the body region
                    continue;
                }

                if (forceFinal)
                {
                    // Lone trailing "wingman": a bare wake with an empty body.
                    BeginCapture(tok.RawEnd);
                    i += 1;
                    consumeUntil = i;
                    continue;
                }

                // Wake word is the held trailing token; wait for more text.
                break;
            }

            // State == Capturing
            if (tok.Norm == _wake)
            {
                if (i + 1 < limit)
                {
                    var next = tokens[i + 1].Norm;
                    if (next == _send)
                    {
                        var body = BodySlice(tokens, i); // body = bodyStart .. token before this wake
                        Emit(WakeWordEventKind.Committed, body, null);
                        EndCapture();
                        i += 2;
                        consumeUntil = i;
                        continue;
                    }
                    if (next == _cancel)
                    {
                        Emit(WakeWordEventKind.Cancelled, "", null);
                        EndCapture();
                        i += 2;
                        consumeUntil = i;
                        continue;
                    }

                    // "wingman <non-verb>" mid-capture: the user just said the word.
                    // Keep it as part of the body and move on.
                    i += 1;
                    continue;
                }

                if (forceFinal)
                {
                    // Trailing "wingman" with no verb at finalize: treat as body text.
                    i += 1;
                    continue;
                }

                // Hold the trailing wake; it may become "wingman send/cancel".
                // Exclude it from the live body until it resolves.
                bodyEnd = i;
                break;
            }

            // Ordinary body word: included via the body slice; just advance.
            i++;
        }

        // While capturing, surface the live body (excludes the held trailing token).
        if (State == WakeWordState.Capturing)
        {
            var liveBody = BodySlice(tokens, bodyEnd);
            if (liveBody != CurrentBody)
            {
                CurrentBody = liveBody;
                Emit(WakeWordEventKind.BodyUpdated, liveBody, null);
            }
        }

        TrimConsumed(tokens, consumeUntil);
    }

    private void BeginCapture(int rawBodyStart)
    {
        State = WakeWordState.Capturing;
        _bodyStart = rawBodyStart;
        CurrentBody = "";
        Emit(WakeWordEventKind.WakeDetected, "", null);
    }

    private void EndCapture()
    {
        State = WakeWordState.Idle;
        _bodyStart = -1;
        CurrentBody = "";
    }

    /// <summary>
    /// Raw body text from the capture start up to (but excluding) token index
    /// <paramref name="endTokenExclusive"/>. Returns "" if no body tokens yet.
    /// </summary>
    private string BodySlice(IReadOnlyList<Tok> tokens, int endTokenExclusive)
    {
        if (_bodyStart < 0) return "";
        int end = Math.Min(endTokenExclusive, tokens.Count);
        // Find the last token at-or-after bodyStart and before end.
        int lastEnd = -1;
        for (int k = 0; k < end; k++)
        {
            if (tokens[k].RawStart >= _bodyStart)
                lastEnd = tokens[k].RawEnd;
        }
        if (lastEnd < 0 || lastEnd <= _bodyStart) return "";
        return _pending.Substring(_bodyStart, lastEnd - _bodyStart).Trim();
    }

    /// <summary>
    /// Drop the leading <paramref name="consumeUntil"/> tokens from _pending so the
    /// buffer cannot grow unbounded, keeping the body region (when capturing) and any
    /// held trailing token. Adjusts _bodyStart to the trimmed coordinates.
    /// </summary>
    private void TrimConsumed(IReadOnlyList<Tok> tokens, int consumeUntil)
    {
        if (consumeUntil <= 0) return;

        int cut;
        if (consumeUntil >= tokens.Count)
            cut = _pending.Length;
        else
            cut = tokens[consumeUntil].RawStart;

        if (State == WakeWordState.Capturing)
        {
            // Never cut into the body region.
            if (_bodyStart >= 0 && cut > _bodyStart) cut = _bodyStart;
        }

        if (cut <= 0) return;
        _pending = _pending.Substring(cut);
        if (_bodyStart >= 0) _bodyStart = Math.Max(0, _bodyStart - cut);
    }

    private void Emit(WakeWordEventKind kind, string text, string? reason)
    {
        try { OnEvent?.Invoke(new WakeWordEvent(kind, text, reason)); }
        catch (Exception ex) { FileLog.Write($"[WakeWordEngine] OnEvent handler threw: {ex.Message}"); }
    }

    // ===== tokenization / normalization =====================================

    /// <summary>
    /// Split into whitespace-delimited tokens, recording each token's normalized form
    /// (lowercase, surrounding punctuation stripped) and its raw start/end offsets so
    /// the body can be sliced back out in original case.
    /// </summary>
    private static List<Tok> Tokenize(string s)
    {
        var result = new List<Tok>();
        int i = 0;
        int n = s.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i])) i++;
            if (i >= n) break;
            int start = i;
            while (i < n && !char.IsWhiteSpace(s[i])) i++;
            int end = i;
            var norm = NormalizeToken(s.Substring(start, end - start));
            if (norm.Length > 0)
                result.Add(new Tok(norm, start, end));
        }
        return result;
    }

    /// <summary>Lowercase and keep only letters/digits (strips punctuation like "wingman,").</summary>
    private static string NormalizeToken(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>State of the <see cref="WakeWordEngine"/>.</summary>
public enum WakeWordState
{
    /// <summary>Listening only for the wake word; all other speech ignored.</summary>
    Idle,

    /// <summary>Wake word heard; accumulating the prompt body until send/cancel.</summary>
    Capturing,
}

/// <summary>The kind of event the <see cref="WakeWordEngine"/> raised.</summary>
public enum WakeWordEventKind
{
    /// <summary>Wake word detected; entered Capturing.</summary>
    WakeDetected,

    /// <summary>The captured body grew; <see cref="WakeWordEvent.Text"/> is the body so far.</summary>
    BodyUpdated,

    /// <summary>"wingman send" heard; <see cref="WakeWordEvent.Text"/> is the final prompt.</summary>
    Committed,

    /// <summary>"wingman cancel" heard; the body was discarded.</summary>
    Cancelled,

    /// <summary>A control phrase could not act; <see cref="WakeWordEvent.Reason"/> says why.</summary>
    ControlIgnored,
}

/// <summary>One classification event from the <see cref="WakeWordEngine"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Text">The relevant text (body for BodyUpdated/Committed; empty otherwise).</param>
/// <param name="Reason">Human-readable reason for ControlIgnored; null otherwise.</param>
public sealed record WakeWordEvent(WakeWordEventKind Kind, string Text, string? Reason = null);
