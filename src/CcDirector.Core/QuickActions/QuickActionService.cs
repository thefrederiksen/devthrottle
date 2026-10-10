using System.Text;
using CcDirector.Core.Claude;
using CcDirector.Core.Input;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.QuickActions;

/// <summary>
/// Service for executing Quick Action messages via Claude Code CLI.
/// Each user message triggers a single-turn Claude -p call with conversation history injected.
/// </summary>
public sealed class QuickActionService
{
    private readonly QuickActionDatabase _db;
    private readonly string _workingDirectory;
    private ClaudeClient? _claudeClient;

    /// <summary>Default timeout for Quick Action calls (2 minutes).</summary>
    private const int DefaultTimeoutMs = 120_000;

    /// <summary>Maximum characters of conversation history to include.</summary>
    private const int MaxHistoryChars = 50_000;

    public QuickActionService(QuickActionDatabase db)
    {
        FileLog.Write("[QuickActionService] Creating");

        _db = db;
        _workingDirectory = CcStorage.Ensure(CcStorage.ToolOutput("quick-actions"));

        FileLog.Write($"[QuickActionService] workingDir={_workingDirectory}");
    }

    /// <summary>
    /// Execute a user message in the given thread. Stores both user and assistant messages in DB.
    /// Returns the assistant's response text.
    /// </summary>
    public async Task<string> ExecuteAsync(string threadId, string userMessage, CancellationToken ct = default)
    {
        FileLog.Write($"[QuickActionService] ExecuteAsync: threadId={threadId}, msgLen={userMessage.Length}");

        // Store user message
        _db.AddMessage(threadId, "user", userMessage);

        // Build the full prompt with conversation history
        var prompt = BuildPrompt(threadId, userMessage);

        // Get or create Claude client
        var client = GetOrCreateClient();

        var options = new ClaudeOptions
        {
            SkipPermissions = true,
            MaxTurns = 15,
            TimeoutMs = DefaultTimeoutMs,
            Model = "sonnet",
        };

        // Use LargeInputHandler if prompt exceeds threshold
        var effectivePrompt = prompt;
        if (LargeInputHandler.IsLargeInput(prompt))
        {
            var tempFile = LargeInputHandler.CreateTempFile(prompt, _workingDirectory);
            effectivePrompt = $"@{tempFile}";
            FileLog.Write($"[QuickActionService] Large prompt saved to temp file: {tempFile}");
        }

        ClaudeResponse response;
        try
        {
            response = await client.ChatAsync(effectivePrompt, options, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[QuickActionService] ExecuteAsync Claude call FAILED: {ex.GetType().Name}: {ex.Message}");
            FileLog.Write($"[QuickActionService] ExecuteAsync stack FAILED: {ex.StackTrace}");
            throw;
        }

        var result = response.Result;
        FileLog.Write($"[QuickActionService] ExecuteAsync completed: resultLen={result.Length}, cost=${response.TotalCostUsd}, turns={response.NumTurns}, subtype={response.Subtype}, isError={response.IsError}, exitCode={response.ExitCode}");

        if (response.IsError)
            FileLog.Write($"[QuickActionService] ExecuteAsync response was error: subtype={response.Subtype}, result={result[..Math.Min(500, result.Length)]}");

        // Store assistant response
        _db.AddMessage(threadId, "assistant", result);

        return result;
    }

    /// <summary>
    /// Auto-generate a title for a thread based on the first user message.
    /// </summary>
    public async Task<string> GenerateTitleAsync(string firstMessage, CancellationToken ct = default)
    {
        FileLog.Write($"[QuickActionService] GenerateTitleAsync: msgLen={firstMessage.Length}");

        // Simple truncation for title - use first line, max 50 chars
        var firstLine = firstMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? firstMessage;
        if (firstLine.Length > 50)
            firstLine = firstLine[..47] + "...";

        // For short messages, just use the message itself
        if (firstMessage.Length <= 60)
            return firstLine;

        // For longer messages, ask Claude to title it
        var client = GetOrCreateClient();
        var titlePrompt = $"Generate a very short title (max 6 words, no quotes) for this conversation request:\n\n{firstMessage[..Math.Min(500, firstMessage.Length)]}";

        var options = new ClaudeOptions
        {
            SkipPermissions = true,
            MaxTurns = 1,
            TimeoutMs = 15_000,
            Model = "haiku",
        };

        try
        {
            var response = await client.ChatAsync(titlePrompt, options, ct);
            var title = response.Result.Trim().Trim('"');
            if (title.Length > 60)
                title = title[..57] + "...";
            FileLog.Write($"[QuickActionService] GenerateTitleAsync: title={title}");
            return title;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[QuickActionService] GenerateTitleAsync FAILED: {ex.Message}");
            return firstLine;
        }
    }

    private string BuildPrompt(string threadId, string currentMessage)
    {
        var messages = _db.GetMessages(threadId);

        // If this is the first message (only the one we just added), no history needed
        if (messages.Count <= 1)
            return currentMessage;

        var sb = new StringBuilder();
        sb.AppendLine("[Previous conversation]");

        var historyChars = 0;
        // Exclude the last message (the one we just added) from history
        var historyMessages = messages.Take(messages.Count - 1).ToList();

        // Trim from the beginning if history is too long
        var startIndex = 0;
        for (var i = historyMessages.Count - 1; i >= 0; i--)
        {
            historyChars += historyMessages[i].Content.Length + 20; // overhead for role prefix
            if (historyChars > MaxHistoryChars)
            {
                startIndex = i + 1;
                break;
            }
        }

        if (startIndex > 0)
            sb.AppendLine("(earlier messages omitted)");

        for (var i = startIndex; i < historyMessages.Count; i++)
        {
            var msg = historyMessages[i];
            var roleLabel = msg.Role == "user" ? "User" : "Assistant";
            sb.AppendLine($"{roleLabel}: {msg.Content}");
        }

        sb.AppendLine();
        sb.AppendLine("[Current request]");
        sb.Append(currentMessage);

        return sb.ToString();
    }

    private ClaudeClient GetOrCreateClient()
    {
        if (_claudeClient != null)
            return _claudeClient;

        var claudePath = ClaudeClient.FindClaudePath();
        if (claudePath == null)
            throw new InvalidOperationException("Claude Code CLI not found. Install it with: npm install -g @anthropic-ai/claude-code");

        _claudeClient = new ClaudeClient(claudePath, _workingDirectory, DefaultTimeoutMs);
        return _claudeClient;
    }
}
