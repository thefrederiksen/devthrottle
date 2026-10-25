using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Claude;

/// <summary>
/// DENY RULES IN THE FOLDER A SESSION STARTS IN (the owner's ruling of 24 October 2026: "if I approve, it has to do
/// it"). A deny rule in <c>.claude/settings.json</c> or <c>.claude/settings.local.json</c> refuses with no prompt, so the
/// owner has no way to approve what it blocks - and those files are often written by an agent, not by the owner. On
/// 24 October 2026 a factory's own Architect had written one that stopped the owner removing a rule from the very same
/// file. This class only READS and REPORTS: the user's files are never changed here. The launch log says what it found,
/// and the session carries it to the Gateway, whose factory page says it in words.
/// </summary>
public static class FolderDenyRules
{
    /// <summary>The two settings files of a folder Claude Code reads permission rules from, in the order they are reported.</summary>
    public static readonly IReadOnlyList<string> SettingsFiles = new[]
    {
        Path.Combine(".claude", "settings.json"),
        Path.Combine(".claude", "settings.local.json"),
    };

    /// <summary>
    /// Every settings file of <paramref name="folder"/> that holds at least one deny rule, with its rules in file order.
    /// A file that is missing or holds none is left out, so an empty list is a folder with no deny rule. A file that
    /// exists but is not a settings file this can read is reported with <see cref="FolderDenyRulesDto.Problem"/> set,
    /// never passed over as though it held nothing.
    /// </summary>
    public static List<FolderDenyRulesDto> Read(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var found = new List<FolderDenyRulesDto>();
        foreach (var relative in SettingsFiles)
        {
            var path = Path.Combine(folder, relative);
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                found.Add(new FolderDenyRulesDto { File = path, Problem = $"it could not be read: {ex.Message}" });
                continue;
            }
            if (Parse(path, text) is { } entry) found.Add(entry);
        }
        FileLog.Write($"[FolderDenyRules] Read: folder={folder}, filesWithDeny={found.Count}, rules={found.Sum(f => f.Rules.Count)}");
        return found;
    }

    /// <summary>The deny rules of one settings file's text, or null when it holds none. Pure.</summary>
    internal static FolderDenyRulesDto? Parse(string path, string text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex)
        {
            return new FolderDenyRulesDto { File = path, Problem = $"it is not JSON, so whether it holds a deny rule could not be read ({ex.Message})" };
        }
        if (root is not JsonObject doc || doc["permissions"] is not JsonObject permissions || permissions["deny"] is not { } deny)
            return null;
        if (deny is not JsonArray list || list.Any(r => r is not JsonValue v || v.GetValueKind() != JsonValueKind.String))
            return new FolderDenyRulesDto { File = path, Problem = "its permissions.deny is not a list of rules" };
        if (list.Count == 0) return null;
        return new FolderDenyRulesDto { File = path, Rules = list.Select(r => r!.GetValue<string>()).ToList() };
    }

    /// <summary>The launch log's line for what <see cref="Read"/> found, or null when it found nothing.</summary>
    public static string? LogLine(IReadOnlyList<FolderDenyRulesDto> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (found.Count == 0) return null;
        var parts = found.Select(f => f.Problem is null
            ? $"{f.File} holds deny rules: {string.Join(", ", f.Rules)}"
            : $"{f.File}: {f.Problem}");
        return "DENY RULES IN THE FOLDER: " + string.Join("; ", parts) +
               ". A deny rule refuses with no prompt, so the owner cannot approve what it blocks; they were left as they are.";
    }
}
