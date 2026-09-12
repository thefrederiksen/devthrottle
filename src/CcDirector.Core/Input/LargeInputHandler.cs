using CcDirector.Core.Utilities;

namespace CcDirector.Core.Input;

/// <summary>
/// Stores input in a repository-local temporary file when direct terminal submission is not
/// reliable. The active agent driver then sends its supported file reference or instruction.
/// </summary>
public static class LargeInputHandler
{
    /// <summary>
    /// Character threshold above which input is written to a temp file.
    /// Conservative value to ensure reliability across all terminal configurations.
    /// </summary>
    public const int LargeInputThreshold = 1000;

    private const string TempDirName = ".temp";
    internal const string InputFileInstructionPrefix = "Read and respond to the complete incoming message in ";

    /// <summary>
    /// Check if the given text should be sent via temp-file reference rather than
    /// typed directly into the agent TUI. True when the text is large (over
    /// <see cref="LargeInputThreshold"/> characters) OR contains line breaks --
    /// multi-line input pasted directly into an interactive input box can get stuck (the
    /// embedded newlines don't submit reliably; only the temp-file path works).
    /// </summary>
    public static bool IsLargeInput(string text) =>
        text.Length > LargeInputThreshold
        || text.Contains('\n')
        || text.Contains('\r');

    /// <summary>
    /// Builds the notification shown when large input is redirected to a temp file.
    /// The agent name is passed in so the message reflects the active session's
    /// actual agent instead of a hardcoded "Claude Code".
    /// </summary>
    /// <param name="agentName">Display name of the active session's agent.</param>
    /// <param name="textLength">Length of the original text, in characters.</param>
    public static string FormatRedirectNotice(string agentName, int textLength) =>
        $"Text over {LargeInputThreshold:N0} characters -- saved to a temporary file and sent to {agentName} ({textLength:N0} characters)";

    internal static string FormatInputFileInstruction(string relativePath) =>
        $"{InputFileInstructionPrefix}{relativePath}.";

    /// <summary>
    /// Creates a temp file in {workingDir}/.temp/ and returns the full path.
    /// The file contains the original text and can be referenced by the active agent driver.
    /// </summary>
    /// <param name="text">The text content to write.</param>
    /// <param name="workingDir">The working directory (repository root) where .temp will be created.</param>
    /// <returns>Full path to the created temp file.</returns>
    public static string CreateTempFile(string text, string workingDir)
    {
        FileLog.Write($"[LargeInputHandler] CreateTempFile: workingDir={workingDir}, textLength={text.Length}");

        var tempDir = Path.Combine(workingDir, TempDirName);
        Directory.CreateDirectory(tempDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var random = Path.GetRandomFileName()[..6];
        var filename = $"input_{timestamp}_{random}.txt";
        var filepath = Path.Combine(tempDir, filename);

        File.WriteAllText(filepath, text);
        FileLog.Write($"[LargeInputHandler] Created temp file: {filepath} ({text.Length} chars)");

        return filepath;
    }

    /// <summary>
    /// Build an agent-friendly @-reference target. Uses a relative path when the temp file is under
    /// the working directory, and always forces forward slashes so Windows backslashes are not
    /// interpreted as escapes by agent TUIs.
    /// </summary>
    public static string MakeAtReference(string absoluteTempPath, string workingDir)
    {
        var path = absoluteTempPath;
        if (!string.IsNullOrEmpty(workingDir))
        {
            try
            {
                var relative = Path.GetRelativePath(workingDir, absoluteTempPath);
                if (!relative.StartsWith("..", StringComparison.Ordinal))
                    path = relative;
            }
            catch
            {
                // Fall through to the absolute path.
            }
        }

        return path.Replace('\\', '/');
    }
}
