namespace CcCleanupStorage;

/// <summary>What one command answers with.</summary>
/// <param name="ExitCode">
/// Nought when the command succeeded, one when it failed or the scan behind it is a broken
/// instrument, two when the command line itself was wrong.
/// </param>
/// <param name="TextLines">The answer as a person or an agent reads it.</param>
/// <param name="JsonPayload">The same answer as a machine reads it.</param>
public sealed record Answer(int ExitCode, IReadOnlyList<string> TextLines, object JsonPayload);

/// <summary>The exit codes this tool uses, and the only ones it uses.</summary>
public static class ExitCodes
{
    /// <summary>The command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>The command failed, or the scan behind it is a broken instrument.</summary>
    public const int Failed = 1;

    /// <summary>The command line was wrong: an unknown command, an unknown flag, or a bad value.</summary>
    public const int Usage = 2;
}
