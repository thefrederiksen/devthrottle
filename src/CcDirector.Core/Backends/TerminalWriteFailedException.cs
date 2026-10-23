using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// Bytes meant for a session's terminal never left the Director (issue #4380): the terminal was disposed or never
/// started, its process had exited, or the write itself failed. Until then the backends returned quietly in every one of
/// these cases, so a sender was told its input went when it had not. Every throw is logged where it is raised, with the
/// number of bytes and never their content.
/// </summary>
public sealed class TerminalWriteFailedException : IOException
{
    private TerminalWriteFailedException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Log the failure where it happened and make the exception that carries it to the caller.</summary>
    /// <param name="where">The class that failed to write, for the log line.</param>
    /// <param name="what">What was not done: "write 12 byte(s)", "resize to 120x40".</param>
    /// <param name="why">Why, in words.</param>
    public static TerminalWriteFailedException Logged(string where, string what, string why, Exception? inner = null)
    {
        var message = $"[{where}] could not {what}: {why} - nothing reached the terminal";
        FileLog.Write($"{message}{(inner is null ? "" : $" ({inner.GetType().Name}: {inner.Message})")}");
        return new TerminalWriteFailedException(message, inner);
    }
}
