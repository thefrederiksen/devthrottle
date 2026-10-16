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
