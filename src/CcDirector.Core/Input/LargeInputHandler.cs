using CcDirector.Core.Utilities;

namespace CcDirector.Core.Input;

/// <summary>
/// Handles large text input by creating temporary files for Claude Code's @filepath reference.
/// When text exceeds the threshold, it's written to a temp file and the @filepath is returned
/// instead, allowing Claude Code to read the file directly.
/// </summary>
public static class LargeInputHandler
{
    /// <summary>
    /// Character threshold above which input is written to a temp file.
    /// Conservative value to ensure reliability across all terminal configurations.
    /// </summary>
    public const int LargeInputThreshold = 1000;

    private const string TempDirName = ".temp";

    /// <summary>
    /// Check if the given text should be sent via temp-file reference rather than
    /// typed directly into the agent TUI. True when the text is large (over
    /// <see cref="LargeInputThreshold"/> characters) OR contains line breaks --
    /// multi-line input pasted directly into Claude's input box gets stuck (the
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
        $"Text over {LargeInputThreshold:N0} chars -- saved to temp file and @filepath sent to {agentName} ({textLength:N0} chars)";

    /// <summary>
    /// Creates a temp file and returns the full path. The file contains the original text and can be referenced via
    /// @filepath in Claude Code.
    ///
    /// WHERE IT GOES. In <paramref name="promptDirectory"/> when the session names one - a seat of a defined factory,
    /// whose kit has a prompts folder (Factory Definition mission, phase 5, P7) - so a seat's prompt, brief included, is
    /// never written into its checkout and is deleted with its kit. Otherwise in {workingDir}/.temp/, exactly as before.
    /// </summary>
    /// <param name="text">The text content to write.</param>
    /// <param name="workingDir">The working directory (repository root) where .temp will be created.</param>
    /// <param name="promptDirectory">The session's own folder for prompt files, or empty for {workingDir}/.temp/.</param>
    /// <returns>Full path to the created temp file.</returns>
    public static string CreateTempFile(string text, string workingDir, string? promptDirectory = null)
    {
        FileLog.Write($"[LargeInputHandler] CreateTempFile: workingDir={workingDir}, promptDirectory={promptDirectory}, textLength={text.Length}");

        var tempDir = string.IsNullOrWhiteSpace(promptDirectory) ? Path.Combine(workingDir, TempDirName) : promptDirectory;
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
    /// The @-reference Claude Code is typed for a prompt file: <c>@path</c>, or <c>@"path"</c> when the path has a space,
    /// which Claude Code reads as one path (proven on Claude Code 2.1.301:
    /// docs/qa/factory-definition/phase-5/trust-proof/prompt-file-proof-output.txt). A file in .temp/ is referenced
    /// relative to the working folder and never has a space; a seat's file is in its kit, outside the working folder,
    /// so its full path is sent, and that path can run through a user folder whose name has a space.
    /// </summary>
    public static string AtReference(string reference) =>
        reference.Contains(' ') ? $"@\"{reference}\"" : $"@{reference}";

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
