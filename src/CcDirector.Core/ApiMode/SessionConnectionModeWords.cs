using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// The one translation between the Director's <see cref="SessionConnectionMode"/> and the words the Gateway contracts use
/// for it (<see cref="ConnectionModes"/>: <c>terminal</c>, <c>api</c>), and the wheel a session reports
/// (<see cref="WheelHolders"/>). Issue #4070.
/// </summary>
public static class SessionConnectionModeWords
{
    /// <summary>The contract word for <paramref name="mode"/>.</summary>
    public static string ToWord(SessionConnectionMode mode) => mode switch
    {
        SessionConnectionMode.Terminal => ConnectionModes.Terminal,
        SessionConnectionMode.Api => ConnectionModes.Api,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a connection mode."),
    };

    /// <summary>
    /// The mode a contract word names. Absent (null or empty) is terminal, as from a caller that predates the field. Any
    /// other word is refused, naming it: a mode nobody asked for is never guessed.
    /// </summary>
    public static SessionConnectionMode FromWord(string? word)
    {
        if (string.IsNullOrEmpty(word) || word == ConnectionModes.Terminal)
            return SessionConnectionMode.Terminal;
        if (word == ConnectionModes.Api)
            return SessionConnectionMode.Api;
        FileLog.Write($"[SessionConnectionModeWords] FromWord FAILED: unknown connection mode \"{word}\"");
        throw new ArgumentException(
            $"\"{word}\" is not a connection mode: it is \"{ConnectionModes.Terminal}\" or \"{ConnectionModes.Api}\".", nameof(word));
    }

    /// <summary>
    /// Who holds the conversation, for a session's row. A terminal session has no wheel (<c>none</c>). An API-mode
    /// session's protocol process holds it while the session runs (<c>protocol</c>), and nobody once it has ended
    /// (<c>none</c>). The user taking the wheel (<c>user</c>) is phase 4.
    /// </summary>
    public static string WheelHolderOf(SessionConnectionMode mode, bool processEnded) =>
        mode == SessionConnectionMode.Api && !processEnded ? WheelHolders.Protocol : WheelHolders.None;
}
