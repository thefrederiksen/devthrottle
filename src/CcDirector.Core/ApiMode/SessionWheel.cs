using CcDirector.Core.Utilities;

namespace CcDirector.Core.ApiMode;

/// <summary>Who holds an API-mode session's conversation (issue #4070, phase 4). The contract's words are
/// <see cref="Gateway.Contracts.WheelHolders"/>.</summary>
public enum SessionWheelHolder
{
    /// <summary>No process holds it: the session has ended, or the user's terminal exited while the user held the wheel.</summary>
    None,

    /// <summary>Pi's API-mode process holds it.</summary>
    Protocol,

    /// <summary>The user holds it in Pi's real terminal: they took the wheel.</summary>
    User,
}

/// <summary>How a take or a hand back ended: done, or refused with the Director's own sentence and nothing changed.</summary>
public sealed record WheelSwitchResult(bool Done, string? Refusal)
{
    public static WheelSwitchResult Switched { get; } = new(true, null);

    public static WheelSwitchResult Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new WheelSwitchResult(false, reason);
    }
}

/// <summary>
/// THE CONVERSATION IS HELD BY ONE SESSION (issue #4070, phase 4): a process-wide claim on a Pi conversation - its
/// session id in its working folder, which is how Pi names it - so two API-mode sessions of this Director can never
/// hold, and so never run a process on, the same conversation (a second session resuming a conversation another one
/// holds is refused). Inside one session, the session's own ownership lock keeps the protocol process and the user's
/// terminal from overlapping. Terminal-mode sessions do not take a claim: their path is unchanged.
/// </summary>
public static class ApiModeConversationClaims
{
    private static readonly Dictionary<string, string> Holders = new(StringComparer.Ordinal);

    /// <summary>The claim's key: the folder (case-insensitive on Windows) and the conversation id.</summary>
    public static string KeyOf(string workingDirectory, string agentSessionId)
    {
        var folder = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            folder = folder.ToUpperInvariant();
        return $"{folder}|{agentSessionId}";
    }

    /// <summary>Claim <paramref name="key"/> for <paramref name="sessionId"/>. Throws, naming the holder, when another
    /// session holds it.</summary>
    public static void Claim(string key, string sessionId)
    {
        lock (Holders)
        {
            if (Holders.TryGetValue(key, out var holder) && holder != sessionId)
            {
                FileLog.Write($"[ApiModeConversationClaims] Claim REFUSED: {key} is held by session {holder}; asked by {sessionId}");
                throw new InvalidOperationException(
                    $"Pi's conversation {key[(key.LastIndexOf('|') + 1)..]} in this folder is already held by session {holder}; two processes may never run on one conversation.");
            }
            Holders[key] = sessionId;
        }
        FileLog.Write($"[ApiModeConversationClaims] Claim: {key} -> {sessionId}");
    }

    /// <summary>Release <paramref name="key"/> if <paramref name="sessionId"/> holds it.</summary>
    public static void Release(string key, string sessionId)
    {
        lock (Holders)
        {
            if (Holders.TryGetValue(key, out var holder) && holder == sessionId)
                Holders.Remove(key);
        }
        FileLog.Write($"[ApiModeConversationClaims] Release: {key} by {sessionId}");
    }

    /// <summary>The session holding <paramref name="key"/>, or null.</summary>
    public static string? HolderOf(string key)
    {
        lock (Holders)
            return Holders.TryGetValue(key, out var holder) ? holder : null;
    }
}
