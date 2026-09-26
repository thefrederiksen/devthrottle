using System.Runtime.CompilerServices;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Drivers;

/// <summary>
/// REMEMBERS THAT A COMPOSER MAY STILL BE HOLDING TEXT WE COULD NOT ACCOUNT FOR (issue #2818).
///
/// Before this existed, a submit that could not prove its text had arrived pressed Escape and typed
/// again. That destroyed the owner's words whenever the machine was merely slow. The fix is to stop
/// clearing on a guess - but "leave the text alone" creates the second half of a known defect: pull
/// request #1513 records what happens when a send types onto the end of an orphaned prompt, and the
/// two run mashed together as one.
///
/// So a submit that gives up WITHOUT clearing records the text it left behind, and the next submit to
/// the same terminal decides what to do about it ON EVIDENCE rather than blindly - see
/// <see cref="ShouldClearBeforeTyping"/>. The owner's words are then either sent by them (they are on
/// screen, and Enter is theirs to press) or replaced by the next thing they send - never silently
/// welded onto it.
///
/// Keyed on the backend instance through a <see cref="ConditionalWeakTable{TKey,TValue}"/> rather than
/// on a session identifier, because the driver and backend submit routes carry no session to name and
/// need the same protection. The table holds no strong reference, so a closed session's entry goes
/// when the backend does.
/// </summary>
internal static class ComposerRetention
{
    private sealed class Mark
    {
        public string? RetainedText;
    }

    private static readonly ConditionalWeakTable<ISessionBackend, Mark> Marks = new();

    /// <summary>A send guarded against other input writes through a wrapper; the mark belongs to the terminal under it,
    /// so a guarded send and an ordinary one see the same mark.</summary>
    private static ISessionBackend TerminalOf(ISessionBackend backend) =>
        backend is Sessions.InputGuardedBackend guarded ? guarded.Inner : backend;

    /// <summary>
    /// Record that a submit gave up without being able to prove the composer was clear, along with the
    /// text it may have left there so the next send can look for exactly that.
    /// </summary>
    public static void MarkMayHoldText(ISessionBackend backend, string driverTag, string text)
    {
        Marks.GetOrCreateValue(TerminalOf(backend)).RetainedText = text;
        FileLog.Write($"[{driverTag}] ComposerRetention: giving up WITHOUT clearing the composer - it may still hold " +
                      $"{text.Length} characters that were never submitted. The next send to this terminal will look " +
                      "for them before it types, so the two cannot run together.");
    }

    /// <summary>
    /// The text a previous submit may have left in this composer, or null if there is none. Reading it
    /// CLEARS the mark, so exactly one caller acts on it.
    /// </summary>
    public static string? TakeRetainedText(ISessionBackend backend)
    {
        if (!Marks.TryGetValue(TerminalOf(backend), out var mark) || mark.RetainedText is null) return null;

        var text = mark.RetainedText;
        mark.RetainedText = null;
        return text;
    }

    /// <summary>
    /// Whether a previous submit left a mark on this terminal, WITHOUT taking it. The doorbell (the Message Load
    /// mission, slice 2) asks this before it types: a send made while a mark stands begins by pressing Escape,
    /// and a doorbell must never be the send that does that.
    /// </summary>
    public static bool MayHoldText(ISessionBackend backend) =>
        Marks.TryGetValue(backend, out var mark) && mark.RetainedText is not null;

    /// <summary>
    /// Whether to press Escape over a composer that may be holding an earlier, unsent prompt, given what
    /// the screen can be seen to show.
    ///
    /// THE TRADE, STATED PLAINLY, because there is no option here that is safe in every direction:
    ///
    /// - <see cref="ComposerEvidence.Present"/> - the orphan is provably still there. Clear it. It has
    ///   already been reported to the owner as not delivered, and leaving it would weld it to the new
    ///   prompt.
    /// - <see cref="ComposerEvidence.Absent"/> - provably gone; the owner most likely pressed Enter
    ///   themselves, or the interface recovered. Do NOT clear: there is nothing to clear, and an Escape
    ///   would fall on whatever they have typed since. (One exception, made by the caller: when the composer
    ///   also reads EMPTY, the measured clear keys are pressed anyway, because characters of the earlier
    ///   send still unread in the terminal's input are read before them - Voice Delivery mission, phase 6.)
    /// - <see cref="ComposerEvidence.Unknown"/> - we cannot see. Clear, and this is the one branch that
    ///   acts without proof. It is chosen deliberately: the cost of clearing wrongly is one prompt the
    ///   owner has already been told did not arrive, while the cost of NOT clearing is two prompts
    ///   welded into one instruction the owner never gave - which is the failure in pull request #1513
    ///   and is strictly worse. The Escape is logged either way so the choice is visible.
    /// </summary>
    public static bool ShouldClearBeforeTyping(ComposerEvidence evidence) =>
        evidence != ComposerEvidence.Absent;

    /// <summary>
    /// Forget any mark for this terminal - called when a submit completes normally, because a composer
    /// that has just accepted and submitted text is not holding anything stale.
    /// </summary>
    public static void Clear(ISessionBackend backend)
    {
        if (Marks.TryGetValue(TerminalOf(backend), out var mark)) mark.RetainedText = null;
    }
}
