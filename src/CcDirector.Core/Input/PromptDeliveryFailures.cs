using System.Collections.Concurrent;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Input;

/// <summary>
/// One prompt that did not reach an agent, or one composer echo that had to be retyped. The record
/// carries no prompt TEXT - only its length - because the ledger is read by surfaces the operator may
/// show to somebody else, and "how long were the words" is enough to recognise which send this was.
/// </summary>
/// <param name="AtUtc">When the attempt failed.</param>
/// <param name="SessionId">The session the words were meant for.</param>
/// <param name="Kind">
/// <c>"failed-delivery"</c> - the send threw and the user's words did NOT go - or
/// <c>"composer-echo-miss"</c> - the composer did not echo the typed text, so it was cleared and
/// retyped. A miss is a warning, not a loss: the retype usually succeeds. Both are counted because
/// the misses are the early signal that the failures are coming.
/// </param>
/// <param name="Source">Who was sending: UserInput, Delivery (a dictation), Agent, Framework.</param>
/// <param name="Reason">Plain-English reason, already trimmed to something a person can read.</param>
/// <param name="TextLength">How many characters were being delivered.</param>
public sealed record PromptDeliveryFailure(
    DateTime AtUtc,
    Guid SessionId,
    string Kind,
    string Source,
    string Reason,
    int TextLength);

/// <summary>What the ledger knows about one session, for the row that renders it.</summary>
/// <param name="FailedDeliveries">How many sends to this session have failed outright.</param>
/// <param name="ComposerEchoMisses">How many composer echoes had to be cleared and retyped.</param>
/// <param name="LastFailureAtUtc">When the most recent outright failure happened; null if none has.</param>
/// <param name="LastFailureReason">Why it failed, in plain English; null if none has.</param>
/// <param name="Unresolved">
/// True when the LAST thing that happened to this session was a failed delivery - nothing has been
/// delivered successfully since. This is the difference between "your words are gone right now" (say so,
/// loudly) and "a send failed earlier and the retry got through" (a count, not an alarm).
/// </param>
public sealed record PromptDeliveryTally(
    int FailedDeliveries,
    int ComposerEchoMisses,
    DateTime? LastFailureAtUtc,
    string? LastFailureReason,
    bool Unresolved)
{
    /// <summary>A session nothing has ever gone wrong on.</summary>
    public static readonly PromptDeliveryTally Empty = new(0, 0, null, null, false);
}

/// <summary>
/// THE LEDGER OF PROMPTS THAT DID NOT GO (issue internal#811).
///
/// WHY THIS EXISTS. On 2026-07-15 the owner spoke into his phone twice and both times the delivery
/// failed - <c>Command FAILED: the composer never echoed the typed text</c>. That sentence was written
/// to a Director log file and nowhere else, so it sat unread for two days while the bug kept losing his
/// words. A failed delivery is not a diagnostic detail: it is the user's sentence, deleted. It has to be
/// visible without anyone grepping a log.
///
/// So every failure is now COUNTED here as well as logged, and the counts ride the session row all the
/// way to the Cockpit and the phone. The log line remains the durable record - this ledger is in memory
/// and process-lifetime, exactly like the browser error ring - but the ledger is the thing that gets
/// SEEN.
///
/// The ledger holds facts, not verdicts: counts, timestamps, and whether the last attempt failed. The
/// Gateway turns those into the words a client renders (CLAUDE.md rule 7 - the client is dumb).
/// </summary>
public static class PromptDeliveryFailures
{
    /// <summary>How many recent failures the fleet-wide ring keeps. Process-lifetime, newest win.</summary>
    internal const int RingCapacity = 200;

    /// <summary>Longest reason we keep. A stack-trace-length message is not a thing to render.</summary>
    internal const int MaxReasonChars = 300;

    /// <summary>What the user was doing when a failed delivery is reported, in plain words (issue #3675).</summary>
    public const string SendPromptAction = "send a prompt to the session";

    /// <summary>Where a failed delivery is shown when the sender's own screen is not known: the alarm on the session's
    /// row, which every surface draws (issue #3675).</summary>
    public const string SessionRowSurface = "session row";

    private sealed class SessionLedger
    {
        public int FailedDeliveries;
        public int ComposerEchoMisses;
        public DateTime? LastFailureAtUtc;
        public string? LastFailureReason;
        public bool Unresolved;
    }

    private static readonly ConcurrentDictionary<Guid, SessionLedger> Ledgers = new();
    private static readonly object RingLock = new();
    private static readonly Queue<PromptDeliveryFailure> Ring = new();

    /// <summary>
    /// The composer did not echo the text on this attempt, so it is being cleared and retyped. Counted
    /// but never surfaced as an alarm on its own: the retype usually works, and the delivery that
    /// follows decides whether the words were lost. Rising misses are the warning that the losses are
    /// coming, which is exactly what nobody could see before this was counted.
    /// </summary>
    public static void RecordComposerEchoMiss(Guid sessionId, string driverTag, int attempt, int textLength)
    {
        if (sessionId == Guid.Empty) return;

        var ledger = Ledgers.GetOrAdd(sessionId, _ => new SessionLedger());
        lock (ledger)
            ledger.ComposerEchoMisses++;

        Push(new PromptDeliveryFailure(
            DateTime.UtcNow, sessionId, "composer-echo-miss", driverTag,
            $"the composer did not echo the typed text on attempt {attempt} - clearing and retyping",
            textLength));

        FileLog.Write($"[PromptDeliveryFailures] composer echo miss: session={sessionId}, " +
                      $"driver={driverTag}, attempt={attempt}, len={textLength}");
    }

    /// <summary>
    /// A send threw: the user's words did not go. Stamps the session UNRESOLVED so every surface can say
    /// so until something actually lands.
    /// </summary>
    public static void RecordFailedDelivery(Guid sessionId, string source, string reason, int textLength)
    {
        if (sessionId == Guid.Empty) return;

        var trimmed = Trim(reason);
        var at = DateTime.UtcNow;

        var ledger = Ledgers.GetOrAdd(sessionId, _ => new SessionLedger());
        lock (ledger)
        {
            ledger.FailedDeliveries++;
            ledger.LastFailureAtUtc = at;
            ledger.LastFailureReason = trimmed;
            ledger.Unresolved = true;
        }

        Push(new PromptDeliveryFailure(at, sessionId, "failed-delivery", source, trimmed, textLength));

        // THE ROW THE USER SAW (the Error Logging mission, issue #3675): this failure is what puts the "not delivered"
        // alarm on the session's row, on the desktop, the Cockpit and the phone - so of every error line one failed send
        // writes, this one alone is marked user-visible. The others carry the same correlation id from the context the
        // prompt path opened, so the incident reads as one.
        var current = ErrorContext.Current;
        using var shown = ErrorContext.Begin(
            surface: current?.Surface is null ? SessionRowSurface : null,
            action: current?.Action is null ? SendPromptAction : null,
            userVisible: true);
        // The FAILED line is an error line, so it is REPORTED to the Gateway: it carries the session, the source, the
        // length and the previous send, never the reason. The reason is a refusal's message, and a real one holds the
        // prompt's first characters, the composer's text and the terminal's tail - no prompt words in a report. The
        // reason is still written WHOLE, on the next line, which is not an error line (quoted, no marker in its head)
        // and so stays in this machine's log, where the Prompt Delivery mission wanted it.
        FileLog.Write($"[PromptDeliveryFailures] FAILED DELIVERY: session={sessionId}, source={source}, len={textLength}, {PreviousSend.Describe(sessionId)}");
        FileLog.Write($"[PromptDeliveryFailures] why that delivery to session {sessionId} did not go, kept in this log only: \"{reason}\"");
    }

    /// <summary>
    /// Something was delivered to this session. Clears the unresolved flag - the words are going through
    /// again, so the alarm has nothing left to warn about - while KEEPING the counts, which are the
    /// history of how often this happens and must not be erased by a lucky retry.
    /// </summary>
    /// <returns>
    /// True only when this actually CLEARED a live alarm. Callers use it to repaint exactly once instead
    /// of on every successful prompt, which is most of them.
    /// </returns>
    public static bool RecordDeliverySucceeded(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return false;
        if (!Ledgers.TryGetValue(sessionId, out var ledger)) return false;

        lock (ledger)
        {
            if (!ledger.Unresolved) return false;
            ledger.Unresolved = false;
        }

        FileLog.Write($"[PromptDeliveryFailures] delivery recovered: session={sessionId} - " +
                      "a later prompt landed, so the unresolved failure is cleared");
        return true;
    }

    /// <summary>What to report on this session's row. Never null - a clean session reports zeros.</summary>
    public static PromptDeliveryTally Tally(Guid sessionId)
    {
        if (sessionId == Guid.Empty || !Ledgers.TryGetValue(sessionId, out var ledger))
            return PromptDeliveryTally.Empty;

        lock (ledger)
            return new PromptDeliveryTally(
                ledger.FailedDeliveries,
                ledger.ComposerEchoMisses,
                ledger.LastFailureAtUtc,
                ledger.LastFailureReason,
                ledger.Unresolved);
    }

    /// <summary>The fleet-wide recent list, newest first, so a diagnostic read needs no log file.</summary>
    public static IReadOnlyList<PromptDeliveryFailure> Recent(int max = RingCapacity)
    {
        if (max <= 0) return Array.Empty<PromptDeliveryFailure>();
        lock (RingLock)
            return Ring.Reverse().Take(max).ToList();
    }

    /// <summary>Drop a dead session's ledger so a long-lived Director does not accumulate them.</summary>
    public static void Forget(Guid sessionId) => Ledgers.TryRemove(sessionId, out _);

    /// <summary>Wipe everything. Tests only - a shared static needs a way back to a known state.</summary>
    internal static void ResetForTests()
    {
        Ledgers.Clear();
        lock (RingLock)
            Ring.Clear();
    }

    private static void Push(PromptDeliveryFailure record)
    {
        lock (RingLock)
        {
            Ring.Enqueue(record);
            while (Ring.Count > RingCapacity)
                Ring.Dequeue();
        }
    }

    private static string Trim(string reason)
    {
        var oneLine = (reason ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length == 0) return "the send failed without saying why";
        return oneLine.Length <= MaxReasonChars ? oneLine : oneLine[..MaxReasonChars] + "...";
    }
}
