using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Status of session verification against the actual .jsonl file.
/// </summary>
public enum SessionVerificationStatus
{
    /// <summary>The .jsonl file exists and content matches expected prompt.</summary>
    Verified,
    /// <summary>No .jsonl file found for the session ID.</summary>
    FileNotFound,
    /// <summary>No ClaudeSessionId is set on the session.</summary>
    NotLinked,
    /// <summary>An error occurred while reading the .jsonl file.</summary>
    Error,
    /// <summary>The .jsonl file exists but first prompt doesn't match expected content.</summary>
    ContentMismatch
}

/// <summary>
/// Result of verifying a Claude session against its .jsonl file.
/// </summary>
public sealed class SessionVerificationResult
{
    public SessionVerificationStatus Status { get; init; }
    public string? FirstPromptSnippet { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Reads Claude Code session metadata from the ~/.claude/projects folder.
/// </summary>
public static class ClaudeSessionReader
{
    private static readonly string ClaudeProjectsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    /// <summary>
    /// Convert a repo path to the Claude project folder name.
    /// E.g., "D:\Repos\my_project" -> "D--Repos-my-project"
    /// </summary>
    public static string GetProjectFolder(string repoPath)
    {
        // Claude Code sanitizes paths by replacing EVERY non-alphanumeric character
        // with - (so : \ / _ . and spaces all become dashes; hyphens map to
        // themselves). The previous char-list version missed dots, which made every
        // transcript under a path like "...\.temp\brain-sandbox" invisible to us
        // (found live in the issue #184 brain verification).
        var normalized = Path.GetFullPath(repoPath);
        return System.Text.RegularExpressions.Regex.Replace(normalized, "[^a-zA-Z0-9]", "-");
    }

    /// <summary>
    /// Get the full path to the Claude projects folder for a repo.
    /// </summary>
    public static string GetProjectFolderPath(string repoPath)
    {
        return Path.Combine(ClaudeProjectsPath, GetProjectFolder(repoPath));
    }

    /// <summary>
    /// List the repo's transcript FILES (newest first) straight from the projects
    /// directory. This is the authoritative source for "which claude session ids exist
    /// right now": sessions-index.json (which /claude-sessions is built from) is written
    /// lazily by claude.exe and lags behind reality - in particular it does NOT yet
    /// contain the transcript /clear just created, which external brain drivers need to
    /// relink to (issue #172).
    /// </summary>
    public static List<(string ClaudeSessionId, DateTime LastWriteUtc)> ListTranscripts(string repoPath)
    {
        FileLog.Write($"[ClaudeSessionReader] ListTranscripts: repo={repoPath}");
        var projectFolder = GetProjectFolderPath(repoPath);
        if (!Directory.Exists(projectFolder))
            return new List<(string, DateTime)>();

        return Directory.GetFiles(projectFolder, "*.jsonl")
            .Select(f => (Path.GetFileNameWithoutExtension(f), File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(t => t.Item2)
            .ToList();
    }

    /// <summary>
    /// Read session metadata from sessions-index.json for a specific session ID.
    /// </summary>
    public static ClaudeSessionMetadata? ReadSessionMetadata(string claudeSessionId, string repoPath)
    {
        var projectFolder = GetProjectFolderPath(repoPath);
        var indexPath = Path.Combine(projectFolder, "sessions-index.json");

        if (!File.Exists(indexPath))
            return null;

        try
        {
            var json = File.ReadAllText(indexPath);
            var index = JsonSerializer.Deserialize<SessionsIndex>(json);

            if (index?.Entries == null)
                return null;

            var entry = index.Entries.FirstOrDefault(e =>
                string.Equals(e.SessionId, claudeSessionId, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
                return null;

            return new ClaudeSessionMetadata
            {
                SessionId = entry.SessionId ?? string.Empty,
                Summary = entry.Summary,
                FirstPrompt = entry.FirstPrompt,
                MessageCount = entry.MessageCount,
                Created = ParseIsoDate(entry.Created),
                Modified = ParseIsoDate(entry.Modified),
                GitBranch = entry.GitBranch,
                ProjectPath = entry.ProjectPath,
                FullPath = entry.FullPath,
                IsSidechain = entry.IsSidechain
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeSessionReader] Error reading sessions-index.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read all session metadata for a repo from sessions-index.json.
    /// </summary>
    public static List<ClaudeSessionMetadata> ReadAllSessionMetadata(string repoPath)
    {
        var result = new List<ClaudeSessionMetadata>();
        var projectFolder = GetProjectFolderPath(repoPath);
        var indexPath = Path.Combine(projectFolder, "sessions-index.json");

        if (!File.Exists(indexPath))
            return result;

        try
        {
            var json = File.ReadAllText(indexPath);
            var index = JsonSerializer.Deserialize<SessionsIndex>(json);

            if (index?.Entries == null)
                return result;

            foreach (var entry in index.Entries)
            {
                result.Add(new ClaudeSessionMetadata
                {
                    SessionId = entry.SessionId ?? string.Empty,
                    Summary = entry.Summary,
                    FirstPrompt = entry.FirstPrompt,
                    MessageCount = entry.MessageCount,
                    Created = ParseIsoDate(entry.Created),
                    Modified = ParseIsoDate(entry.Modified),
                    GitBranch = entry.GitBranch,
                    ProjectPath = entry.ProjectPath,
                    FullPath = entry.FullPath,
                    IsSidechain = entry.IsSidechain
                });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeSessionReader] Error reading sessions-index.json: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Check if a sessions-index.json exists for the given repo.
    /// </summary>
    public static bool HasSessionIndex(string repoPath)
    {
        var projectFolder = GetProjectFolderPath(repoPath);
        var indexPath = Path.Combine(projectFolder, "sessions-index.json");
        return File.Exists(indexPath);
    }

    /// <summary>
    /// Check if a specific Claude session ID exists in storage for the given repo.
    /// Returns false if the session cannot be found (cannot be resumed).
    /// </summary>
    public static bool SessionExists(string claudeSessionId, string repoPath)
    {
        if (string.IsNullOrEmpty(claudeSessionId))
            return false;

        // Check for the .jsonl file directly — sessions-index.json may not exist
        // or may not be up-to-date. The .jsonl file IS the session data and is
        // the authoritative indicator that a session can be resumed with --resume.
        var jsonlPath = GetJsonlPath(claudeSessionId, repoPath);
        return File.Exists(jsonlPath);
    }

    /// <summary>
    /// Get the path to the .jsonl file for a Claude session.
    /// </summary>
    public static string GetJsonlPath(string claudeSessionId, string repoPath)
    {
        var projectFolder = GetProjectFolderPath(repoPath);
        return Path.Combine(projectFolder, $"{claudeSessionId}.jsonl");
    }

    /// <summary>
    /// Extract user prompt text from a .jsonl file.
    /// Skips meta messages (isMeta=true) and short prompts.
    /// Returns list of prompt strings for matching against terminal content.
    /// </summary>
    public static List<string> ExtractUserPrompts(string jsonlPath)
    {
        var prompts = new List<string>();

        if (!File.Exists(jsonlPath))
            return prompts;

        try
        {
            // Use FileShare.ReadWrite to allow reading while Claude writes
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Skip if not a user message
                    if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "user")
                        continue;

                    // Skip meta messages
                    if (root.TryGetProperty("isMeta", out var metaEl) && metaEl.GetBoolean())
                        continue;

                    // Extract message content
                    if (root.TryGetProperty("message", out var msgEl))
                    {
                        string? content = null;

                        // Message can be a simple string or an object with content array
                        if (msgEl.ValueKind == JsonValueKind.String)
                        {
                            content = msgEl.GetString();
                        }
                        else if (msgEl.TryGetProperty("content", out var contentEl))
                        {
                            if (contentEl.ValueKind == JsonValueKind.String)
                            {
                                content = contentEl.GetString();
                            }
                            else if (contentEl.ValueKind == JsonValueKind.Array)
                            {
                                // Find first text content
                                foreach (var item in contentEl.EnumerateArray())
                                {
                                    if (item.TryGetProperty("type", out var itemType) &&
                                        itemType.GetString() == "text" &&
                                        item.TryGetProperty("text", out var textProp))
                                    {
                                        content = textProp.GetString();
                                        break;
                                    }
                                }
                            }
                        }

                        // Only include actual user-typed prompts
                        if (!string.IsNullOrEmpty(content))
                        {
                            content = content.Trim();

                            if (IsSystemInjectedContent(content))
                                continue;

                            // Skip very short prompts (unreliable for matching)
                            if (content.Length > 10)
                            {
                                prompts.Add(content);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeSessionReader] ExtractUserPrompts error: {ex.Message}");
        }

        return prompts;
    }

    /// <summary>
    /// Check if content is system-injected (not typed by the user) and should be excluded from matching.
    /// </summary>
    internal static bool IsSystemInjectedContent(string content)
    {
        // Command invocations: <command-message>..., <command-name>...
        if (content.StartsWith("<command-message>", StringComparison.Ordinal) ||
            content.StartsWith("<command-name>", StringComparison.Ordinal))
            return true;

        // Skill expansions injected by the CLI
        if (content.StartsWith("Base directory for this skill:", StringComparison.Ordinal))
            return true;

        // Tool results and system notifications injected by Claude CLI
        if (content.StartsWith("<local-command-stdout>", StringComparison.Ordinal) ||
            content.StartsWith("<task-notification>", StringComparison.Ordinal) ||
            content.StartsWith("<system-reminder>", StringComparison.Ordinal) ||
            content.StartsWith("<tool-result>", StringComparison.Ordinal))
            return true;

        // Context continuation messages from session compacting
        if (content.StartsWith("This session is being continued from a previous conversation", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Normalize text for terminal matching: collapse all whitespace into single spaces.
    /// This handles word-wrapped prompts in the terminal where newlines are inserted.
    /// </summary>
    public static string NormalizeForMatching(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Verify that a Claude session's .jsonl file exists and is readable.
    /// This reads the file directly, which is more reliable than sessions-index.json
    /// (which Claude updates asynchronously).
    /// </summary>
    /// <param name="claudeSessionId">The Claude session ID to verify.</param>
    /// <param name="repoPath">The repo path to locate the .jsonl file.</param>
    /// <param name="expectedFirstPrompt">Optional expected first prompt to match against. If null, only file existence is checked.</param>
    public static SessionVerificationResult VerifySessionFile(string? claudeSessionId, string repoPath, string? expectedFirstPrompt = null)
    {
        if (string.IsNullOrEmpty(claudeSessionId))
        {
            return new SessionVerificationResult { Status = SessionVerificationStatus.NotLinked };
        }

        var jsonlPath = GetJsonlPath(claudeSessionId, repoPath);

        if (!File.Exists(jsonlPath))
        {
            return new SessionVerificationResult
            {
                Status = SessionVerificationStatus.FileNotFound,
                ErrorMessage = $"File not found: {jsonlPath}"
            };
        }

        try
        {
            var fileInfo = new FileInfo(jsonlPath);
            var firstPrompt = ReadFirstPromptFromJsonl(jsonlPath);

            // If expected prompt provided, verify it matches
            if (!string.IsNullOrEmpty(expectedFirstPrompt) && !string.IsNullOrEmpty(firstPrompt))
            {
                // Compare normalized versions (trimmed, first 100 chars)
                var normalizedExpected = NormalizeForComparison(expectedFirstPrompt);
                var normalizedActual = NormalizeForComparison(firstPrompt);

                if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
                {
                    return new SessionVerificationResult
                    {
                        Status = SessionVerificationStatus.ContentMismatch,
                        FileSizeBytes = fileInfo.Length,
                        FirstPromptSnippet = firstPrompt,
                        ErrorMessage = $"Expected: \"{expectedFirstPrompt[..Math.Min(50, expectedFirstPrompt.Length)]}...\", Got: \"{firstPrompt[..Math.Min(50, firstPrompt.Length)]}...\""
                    };
                }
            }

            return new SessionVerificationResult
            {
                Status = SessionVerificationStatus.Verified,
                FileSizeBytes = fileInfo.Length,
                FirstPromptSnippet = firstPrompt
            };
        }
        catch (Exception ex)
        {
            return new SessionVerificationResult
            {
                Status = SessionVerificationStatus.Error,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Normalize text for comparison: trim, collapse whitespace, take first 100 chars.
    /// </summary>
    private static string NormalizeForComparison(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Collapse whitespace and newlines
        text = text.ReplaceLineEndings(" ").Trim();
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        // Remove trailing "..." if present (from truncation)
        if (text.EndsWith("..."))
            text = text[..^3].TrimEnd();

        // Take first 100 chars for comparison
        if (text.Length > 100)
            text = text[..100];

        return text;
    }

    /// <summary>
    /// Read the first user prompt from a .jsonl file.
    /// Returns the first 100 characters of the prompt, or null if not found.
    /// </summary>
    public static string? ReadFirstPromptFromJsonl(string jsonlPath)
    {
        if (!File.Exists(jsonlPath))
            return null;

        try
        {
            // Read first few KB to find the first user message
            // (don't load entire file for large sessions)
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            // Read up to 100 lines looking for a user message
            for (int i = 0; i < 100 && !reader.EndOfStream; i++)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Look for user messages
                    if (root.TryGetProperty("type", out var typeProp) &&
                        typeProp.GetString() == "user")
                    {
                        // Get the message content
                        if (root.TryGetProperty("message", out var messageProp))
                        {
                            // Message can be a simple string or an object with content array
                            if (messageProp.ValueKind == JsonValueKind.String)
                            {
                                var text = messageProp.GetString();
                                return TruncatePrompt(text);
                            }
                            else if (messageProp.TryGetProperty("content", out var contentProp) &&
                                     contentProp.ValueKind == JsonValueKind.Array)
                            {
                                // Find first text content
                                foreach (var item in contentProp.EnumerateArray())
                                {
                                    if (item.TryGetProperty("type", out var itemType) &&
                                        itemType.GetString() == "text" &&
                                        item.TryGetProperty("text", out var textProp))
                                    {
                                        return TruncatePrompt(textProp.GetString());
                                    }
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }

            return null;
        }
        catch (IOException ex)
        {
            FileLog.Write($"[ClaudeSessionReader] ReadFirstPromptFromJsonl IO error: {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLog.Write($"[ClaudeSessionReader] ReadFirstPromptFromJsonl access denied: {ex.Message}");
            return null;
        }
    }

    private static string? TruncatePrompt(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        // Clean up whitespace
        text = text.ReplaceLineEndings(" ").Trim();

        // Truncate to 100 chars
        if (text.Length > 100)
            text = text[..100] + "...";

        return text;
    }

    /// <summary>
    /// Scan all projects in ~/.claude/projects and return all sessions.
    /// Filters out sidechain (subagent) sessions.
    /// Returns sessions sorted by Modified date (most recent first).
    /// </summary>
    public static List<ClaudeSessionMetadata> ScanAllProjects()
    {
        var result = new List<ClaudeSessionMetadata>();

        if (!Directory.Exists(ClaudeProjectsPath))
            return result;

        try
        {
            var projectDirs = Directory.GetDirectories(ClaudeProjectsPath);

            foreach (var projectDir in projectDirs)
            {
                var indexPath = Path.Combine(projectDir, "sessions-index.json");
                if (!File.Exists(indexPath))
                    continue;

                try
                {
                    var json = File.ReadAllText(indexPath);
                    var index = JsonSerializer.Deserialize<SessionsIndex>(json);

                    if (index?.Entries == null)
                        continue;

                    // Get original path from index or derive from folder name
                    var originalPath = index.OriginalPath ?? DerivePathFromFolder(Path.GetFileName(projectDir));

                    foreach (var entry in index.Entries)
                    {
                        // Skip sidechains (subagent sessions)
                        if (entry.IsSidechain)
                            continue;

                        // Skip sessions with no messages
                        if (entry.MessageCount <= 0)
                            continue;

                        result.Add(new ClaudeSessionMetadata
                        {
                            SessionId = entry.SessionId ?? string.Empty,
                            Summary = entry.Summary,
                            FirstPrompt = entry.FirstPrompt,
                            MessageCount = entry.MessageCount,
                            Created = ParseIsoDate(entry.Created),
                            Modified = ParseIsoDate(entry.Modified),
                            GitBranch = entry.GitBranch,
                            ProjectPath = entry.ProjectPath ?? originalPath,
                            FullPath = entry.FullPath,
                            IsSidechain = entry.IsSidechain
                        });
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ClaudeSessionReader] Error reading {indexPath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeSessionReader] Error scanning projects: {ex.Message}");
        }

        // Sort by Modified date, most recent first
        return result.OrderByDescending(s => s.Modified).ToList();
    }

    /// <summary>
    /// Derive the original repo path from the sanitized folder name.
    /// E.g., "D--Repos-my-project" -> "D:\Repos\my-project"
    /// This is a best-effort reverse of GetProjectFolder.
    /// </summary>
    private static string DerivePathFromFolder(string folderName)
    {
        if (string.IsNullOrEmpty(folderName))
            return string.Empty;

        // Pattern: First segment is drive letter (e.g., "D-" -> "D:")
        // Remaining dashes are path separators
        var parts = folderName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return folderName;

        // First part is drive letter
        var drive = parts[0] + ":";

        if (parts.Length == 1)
            return drive + "\\";

        // Join remaining parts with backslash
        var path = string.Join("\\", parts.Skip(1));
        return drive + "\\" + path;
    }

    private static DateTime ParseIsoDate(string? isoDate)
    {
        if (string.IsNullOrEmpty(isoDate))
            return DateTime.MinValue;

        if (DateTime.TryParse(isoDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt;

        return DateTime.MinValue;
    }

    /// <summary>
    /// Search all .jsonl files in a repo's Claude project folder for a GUID marker string.
    /// Returns the Claude session ID (filename without .jsonl) of the matching file, or null.
    /// Used to link a CC Director session to its Claude session after marker injection.
    /// </summary>
    public static string? FindSessionByMarker(string repoPath, string marker)
    {
        var projectFolder = GetProjectFolderPath(repoPath);
        return FindSessionByMarkerInFolder(projectFolder, marker);
    }

    /// <summary>
    /// Search all .jsonl files in a folder for a GUID marker string.
    /// Returns the session ID (filename without .jsonl) of the matching file, or null.
    /// </summary>
    internal static string? FindSessionByMarkerInFolder(string folderPath, string marker)
    {
        FileLog.Write($"[ClaudeSessionReader] FindSessionByMarker: folder={folderPath}, marker={marker}");

        if (string.IsNullOrEmpty(marker))
            return null;

        if (!Directory.Exists(folderPath))
        {
            FileLog.Write($"[ClaudeSessionReader] FindSessionByMarker: folder not found: {folderPath}");
            return null;
        }

        // Scan newest files first - our file is the most recently created
        var files = Directory.GetFiles(folderPath, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        FileLog.Write($"[ClaudeSessionReader] FindSessionByMarker: scanning {files.Count} .jsonl files");

        foreach (var file in files)
        {
            var content = ReadFileContentOrNull(file.FullName);
            if (content == null) continue;

            if (content.Contains(marker, StringComparison.Ordinal))
            {
                var sessionId = Path.GetFileNameWithoutExtension(file.Name);
                FileLog.Write($"[ClaudeSessionReader] FindSessionByMarker: FOUND marker in {sessionId}");
                return sessionId;
            }
        }

        FileLog.Write("[ClaudeSessionReader] FindSessionByMarker: marker not found in any file");
        return null;
    }

    /// <summary>
    /// Read file content with ReadWrite sharing. Returns null if the file is locked or unreadable.
    /// Isolated per-file error handling is necessary when scanning multiple external files —
    /// a single locked file must not prevent scanning the rest.
    /// </summary>
    private static string? ReadFileContentOrNull(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            FileLog.Write($"[ClaudeSessionReader] ReadFileContentOrNull: skipping {Path.GetFileName(filePath)}: {ex.Message}");
            return null;
        }
    }

    // Internal JSON models for sessions-index.json
    private sealed class SessionsIndex
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("entries")]
        public List<SessionEntry>? Entries { get; set; }

        [JsonPropertyName("originalPath")]
        public string? OriginalPath { get; set; }
    }

    private sealed class SessionEntry
    {
        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("fullPath")]
        public string? FullPath { get; set; }

        [JsonPropertyName("fileMtime")]
        public long FileMtime { get; set; }

        [JsonPropertyName("firstPrompt")]
        public string? FirstPrompt { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("messageCount")]
        public int MessageCount { get; set; }

        [JsonPropertyName("created")]
        public string? Created { get; set; }

        [JsonPropertyName("modified")]
        public string? Modified { get; set; }

        [JsonPropertyName("gitBranch")]
        public string? GitBranch { get; set; }

        [JsonPropertyName("projectPath")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("isSidechain")]
        public bool IsSidechain { get; set; }
    }
}
