using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Gemini;

/// <summary>
/// Reads Gemini's own prompt log (issue #1551): the user's prompts, with real timestamps.
///
/// This is NOT the same source as <see cref="GeminiTerminalHistory"/>, and the difference matters.
/// The History TAB wants the whole conversation as a human reads it, so it scrapes the terminal
/// scrollback - the only place Gemini's REPLIES exist. The durable record wants structured, stably
/// identified messages, and the scrollback is neither: it is one ever-growing unstructured blob with
/// no timestamps, so copying it on each turn would append the entire conversation again every time.
///
/// Gemini does persist the user's half, at
/// <c>~/.gemini/tmp/&lt;sha256 of the repo path&gt;/logs.json</c> - a JSON array of
/// <c>{ sessionId, messageId, type, message, timestamp }</c>. Real prompt text, real timestamps, so
/// the origin join works exactly as it does for the other agents.
///
/// The honest limit: Gemini records the user's prompts ONLY, never the model's responses. So a Gemini
/// session yields prompts and no replies. That is a real gap in what Gemini persists, not something
/// we can parse our way out of - and it is recorded as absent rather than filled in from the
/// scrollback, which cannot be attributed to a turn.
/// </summary>
public static class GeminiPromptLogReader
{
    /// <summary>
    /// The logs.json path for a repo, or null when Gemini has never run there.
    /// </summary>
    /// <param name="repoPath">The session's repo path - what Gemini hashes to key its temp directory.</param>
    /// <param name="homeDirectory">Override the user profile directory. Tests only; null resolves the
    /// real one. (Note the real lookup is the Windows shell API, which the USERPROFILE environment
    /// variable does NOT influence - hence an explicit parameter rather than an env override.)</param>
    public static string? ResolvePath(string repoPath, string? homeDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return null;
        try
        {
            var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".gemini", "tmp", HashRepoPath(repoPath), "logs.json");
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GeminiPromptLogReader] ResolvePath failed for {repoPath} FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gemini keys its per-project temp directory by the SHA-256 of the project path, lowercase hex.
    /// Verified against a live ~/.gemini/tmp on Windows: the input is the path exactly as the OS gives
    /// it, backslashes included.
    /// </summary>
    internal static string HashRepoPath(string repoPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(repoPath));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// The user prompts Gemini recorded for this repo, oldest first. Returns an empty history when
    /// Gemini has not run there or its log cannot be read. Never throws.
    /// </summary>
    public static ConversationHistory Read(string repoPath, string? homeDirectory = null)
    {
        var path = ResolvePath(repoPath, homeDirectory);
        if (path is null) return ConversationHistory.Empty;

        try
        {
            // Gemini rewrites this file as it appends; read shared so a live session cannot lock us out.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ConversationHistory.Empty;

            var messages = new List<ConversationMessage>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                // Gemini writes only user entries today; anything else is not ours to interpret.
                if (!entry.TryGetProperty("type", out var type) || type.GetString() != "user") continue;
                if (!entry.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.String) continue;

                var text = msg.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                DateTimeOffset? ts = null;
                if (entry.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed))
                    ts = parsed;

                // This file is per REPO and accumulates every Gemini context ever run there, so the
                // per-entry sessionId is the only thing separating one context from the next.
                string? contextId = null;
                if (entry.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String)
                    contextId = sidEl.GetString();

                messages.Add(new ConversationMessage(
                    ConversationRole.User,
                    new[] { new ConversationPart(ConversationPartKind.Text, text) },
                    ts,
                    contextId));
            }

            return new ConversationHistory(messages);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GeminiPromptLogReader] Read failed for {path} FAILED: {ex.Message}");
            return ConversationHistory.Empty;
        }
    }
}
