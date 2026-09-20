using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Core.Storage;

/// <summary>
/// WHO AUTHORED each prompt that went into a session - the owner, or the product and the fleet - held in
/// memory so the conversation history can say which turns are the owner's own.
///
/// This exists for the same reason <see cref="InputOriginBuffer"/> does, and it is deliberately its
/// sibling rather than a field on it. By the time a prompt reaches the agent's transcript, a line the
/// owner typed and a line the fleet doorbell typed are IDENTICAL - both are a user message with no mark
/// on them. The distinction is knowable only here, at the Session submit choke point, where the
/// <see cref="Sessions.SendSource"/> and the <see cref="Sessions.InputOrigin"/> are still in hand.
///
/// The two buffers differ in what they are allowed to miss. InputOriginBuffer records only submissions
/// that carry human keystrokes or speech, because it feeds the work counts and framework plumbing is not
/// work. This one must record the plumbing TOO - a doorbell is precisely what the reader wants filtered
/// out - so it records every submission that was delivered, whoever drove it.
///
/// NO TEXT IS KEPT. A submission is remembered as a hash of its normalized text, which is enough to
/// recognise the same words coming back out of the transcript and useless for anything else. That
/// follows the house rule the delivery ledger already states: a record read by other surfaces carries no
/// prompt text.
///
/// In memory, not on disk, exactly like its sibling: the Director keeps no log of its own. A Director
/// that restarts has forgotten who authored the turns before it, and every one of them then reads as
/// <see cref="Unknown"/> - which the reader is SHOWN, never hidden. An unknown author means the question
/// could not be answered, not that the answer was "the product", and the two must never share a path:
/// hiding a turn the owner really did type is the one failure this feature cannot afford.
/// </summary>
public static class PromptAuthorBuffer
{
    /// <summary>
    /// The author could not be established - this Director never saw the submission, or has forgotten it.
    /// Distinct from both real answers, and always treated as the owner's by anything that hides turns.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// How many recent submissions to remember per session. Unlike its sibling - which only has to
    /// survive the seconds between a submission and the end of its turn - this is read against a whole
    /// conversation, so it has to cover one. A thousand turns is far past any real session; beyond it the
    /// oldest simply age out to <see cref="Unknown"/> and are shown.
    /// </summary>
    private const int MaxPerSession = 1000;

    private static readonly ConcurrentDictionary<string, LinkedList<PromptAuthorEvent>> BySession = new();

    /// <summary>Note who authored a submission that was just delivered.</summary>
    public static void Record(string sessionId, string origin, string text)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var key = KeyFor(text);
        if (key.Length == 0) return;

        var list = BySession.GetOrAdd(sessionId, _ => new LinkedList<PromptAuthorEvent>());
        lock (list)
        {
            list.AddLast(new PromptAuthorEvent(DateTime.UtcNow, origin, key));
            while (list.Count > MaxPerSession) list.RemoveFirst();
        }
    }

    /// <summary>
    /// Who authored the prompt whose text this is, or <see cref="Unknown"/> when this session's
    /// submissions do not include it. Matched on the text rather than on a timestamp because the
    /// transcript's clock and the Director's are not the same clock, and a near-miss on time would
    /// attribute a turn to the wrong author - which is worse than admitting we do not know.
    ///
    /// The NEWEST matching submission wins: the same words sent twice are two turns by whoever sent
    /// them last, and the reader is looking at the latest conversation either way.
    /// </summary>
    public static string AuthorOf(string sessionId, string text)
    {
        if (string.IsNullOrEmpty(sessionId)) return Unknown;
        var key = KeyFor(text);
        if (key.Length == 0) return Unknown;
        if (!BySession.TryGetValue(sessionId, out var list)) return Unknown;

        lock (list)
        {
            for (var node = list.Last; node is not null; node = node.Previous)
                if (string.Equals(node.Value.TextKey, key, StringComparison.Ordinal))
                    return node.Value.Origin;
        }
        return Unknown;
    }

    /// <summary>Drop a session's authors (it ended).</summary>
    public static void Forget(string sessionId) => BySession.TryRemove(sessionId, out _);

    /// <summary>Drop everything (tests).</summary>
    public static void Clear() => BySession.Clear();

    /// <summary>
    /// The lookup key for a piece of prompt text: every run of whitespace collapsed to one space and the
    /// ends trimmed, then hashed. The collapse is what makes the two sides meet - a prompt is submitted
    /// as one line and can come back out of a transcript wrapped, re-indented, or with its newlines
    /// normalized, and none of those change a word of it. Returns "" for text that is only whitespace,
    /// which is never recorded and never matched.
    /// </summary>
    internal static string KeyFor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new StringBuilder(text.Length);
        var inRun = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { inRun = true; continue; }
            if (inRun && sb.Length > 0) sb.Append(' ');
            inRun = false;
            sb.Append(ch);
        }
        if (sb.Length == 0) return "";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>
/// One delivered submission, remembered by who drove it. Carries no prompt text - only a hash of it, by
/// which the same words are recognised coming back out of the agent's transcript.
/// </summary>
/// <param name="TsUtc">When the submission was delivered.</param>
/// <param name="Origin">
/// <c>WorkingOrigins.Owner</c> or <c>WorkingOrigins.Agent</c> - the same two words, decided by the same
/// test, that the session already reports for what started its current work.
/// </param>
/// <param name="TextKey">The hash of the normalized submitted text.</param>
public readonly record struct PromptAuthorEvent(DateTime TsUtc, string Origin, string TextKey);
