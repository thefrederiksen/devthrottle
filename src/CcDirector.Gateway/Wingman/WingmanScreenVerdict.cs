using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The model's judgment of what a session's live screen needs from its owner, asked by the send-time menu guard
/// (<c>WaitingScreenReader.ConfirmedMenuAsync</c>) on a menu-shaped screen. A per-turn reading no longer produces
/// it: since contract v4 Call A answers one word and says nothing about a menu. Three answers, one story:
/// <c>menu</c> - an interactive picker owns the screen and typed text cannot answer it; <c>answer</c> -
/// the agent is waiting on words or a decision the owner types or speaks; <c>nothing</c> - the turn is
/// informational, the agent reported and is not waiting on the owner. Null/absent means the model gave
/// no verdict (no live screen was supplied, or the line did not parse) - callers treat that as unknown
/// and fall back to their fail-safe default, never to a block.
/// </summary>
public sealed class WingmanScreenVerdict
{
    /// <summary>"menu" | "answer" | "nothing" - normalized lowercase; anything else never leaves the parser.</summary>
    public string Needs { get; set; } = "";

    /// <summary>For a menu: the choice being asked, in plain words. Empty otherwise.</summary>
    public string Question { get; set; } = "";

    /// <summary>For a menu: the visible option labels as shown on screen. Empty otherwise.</summary>
    public List<string> Options { get; set; } = new();
}

/// <summary>
/// Remembers the model's latest screen verdict per session, keyed by a FINGERPRINT of the grid rows it
/// judged. It is fed only by the send-time guard's own question (the prompt menu guard, a voice reply): the
/// per-turn reading fed it until contract v4, and no longer does, because Call A no longer says whether a menu
/// is drawn. The guard asks again for the same screen only when the fingerprint changed - a match serves the
/// earlier answer instantly; a mismatch means the screen moved and the caller re-judges, failing closed. One
/// entry per session (newest wins).
/// </summary>
public static class WingmanScreenVerdictCache
{
    private static readonly ConcurrentDictionary<string, (string Hash, string Needs)> Entries = new();

    /// <summary>The fingerprint of a live grid: SHA-256 over the rows joined with newlines. Any repaint -
    /// even a spinner glyph - changes it, which is the conservative direction: a changed screen is
    /// re-judged rather than served a stale verdict.</summary>
    public static string HashRows(IReadOnlyList<string> rows)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Record the model's verdict for the screen with this fingerprint.</summary>
    public static void Store(string sessionKey, string hash, string needs)
        => Entries[sessionKey] = (hash, needs);

    /// <summary>The cached verdict, but ONLY when the fingerprint still matches the live screen.</summary>
    public static bool TryGet(string sessionKey, string hash, out string needs)
    {
        needs = "";
        if (!Entries.TryGetValue(sessionKey, out var e) || e.Hash != hash) return false;
        needs = e.Needs;
        return true;
    }

    /// <summary>TEST-ONLY: drop every cached verdict so one test's screens never leak into another's.</summary>
    public static void Clear() => Entries.Clear();
}
