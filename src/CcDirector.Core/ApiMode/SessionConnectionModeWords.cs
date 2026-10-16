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
    /// The contract word for who holds the conversation, for a session's row (<see cref="WheelHolders"/>): <c>protocol</c>
    /// while Pi's API-mode process holds it, <c>user</c> while the user holds the wheel in Pi's terminal, and <c>none</c>
    /// when no process does - a terminal session (which has no wheel), an API-mode session that has ended, or one whose
    /// user's terminal exited while they held the wheel.
    /// </summary>
    public static string WheelHolderWord(SessionWheelHolder holder) => holder switch
    {
        SessionWheelHolder.None => WheelHolders.None,
        SessionWheelHolder.Protocol => WheelHolders.Protocol,
        SessionWheelHolder.User => WheelHolders.User,
        _ => throw new ArgumentOutOfRangeException(nameof(holder), holder, "Not a wheel holder."),
    };
}
