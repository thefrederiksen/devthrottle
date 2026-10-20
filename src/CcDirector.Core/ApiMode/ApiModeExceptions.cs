namespace CcDirector.Core.ApiMode;

/// <summary>The API-mode agent process could not be started. The message names the executable and the reason.</summary>
public sealed class ApiModeStartException : Exception
{
    public ApiModeStartException(string executable, string message)
        : base(message)
    {
        Executable = executable;
    }

    public ApiModeStartException(string executable, string message, Exception innerException)
        : base(message, innerException)
    {
        Executable = executable;
    }

    /// <summary>The executable that was asked for, as the caller gave it.</summary>
    public string Executable { get; }
}

/// <summary>
/// A request was waiting for its response when the agent process ended. The message states the request, the
/// exit code and any transport failure, so the caller can show why the answer never came.
/// </summary>
public sealed class ApiModeConnectionEndedException : Exception
{
    public ApiModeConnectionEndedException(string message, ApiModeProcessEnded ended, Exception? innerException = null)
        : base(message, innerException)
    {
        Ended = ended;
    }

    /// <summary>How the process ended.</summary>
    public ApiModeProcessEnded Ended { get; }
}

/// <summary>
/// Input meant for the user's terminal reached an API-mode session that has none open: the protocol holds the
/// conversation, or nothing does. Nothing was typed. The message is the Director's sentence for the person who typed,
/// so the prompt verb answers it as a refusal the Cockpit and the phone show, never as a failure (phase 4 review,
/// finding 2).
/// </summary>
public sealed class ApiModeNoTerminalException : InvalidOperationException
{
    public ApiModeNoTerminalException(string message)
        : base(message)
    {
    }
}
