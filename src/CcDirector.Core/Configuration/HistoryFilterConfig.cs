using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// What the History tab shows: the user can hide the machinery (tool calls, tool results,
/// the model's thinking) to read just the conversation. Persisted in config.json under the
/// top-level object "history_filter" with these boolean keys, all defaulting to true (show
/// everything, so the tab behaves exactly as before for anyone who never touches the toggles):
///   - "show_tool_calls"   (bool) - the "[tool] ..." lines inside an Assistant bubble.
///   - "show_tool_results" (bool) - the gold "Tool result" bubbles (command output, file lists).
///   - "show_thinking"     (bool) - the "(thinking) ..." reasoning lines inside an Assistant bubble.
///
/// Read once when the History tab attaches and re-read when a toggle is flipped; the toggle
/// writes the new value straight back so the choice sticks between sessions.
///
/// No-fallback rule: a present-but-wrong-typed key THROWS with the fix named, rather than
/// silently picking a default (matching <see cref="AutoResumeConfig"/>).
/// </summary>
public sealed record HistoryFilterConfig(
    bool ShowToolCalls,
    bool ShowToolResults,
    bool ShowThinking)
{
    /// <summary>The default posture: show everything.</summary>
    public static readonly HistoryFilterConfig Default = new(
        ShowToolCalls: true,
        ShowToolResults: true,
        ShowThinking: true);

    /// <summary>Read the effective config from config.json's "history_filter" object; missing keys
    /// fall back to <see cref="Default"/> per key.</summary>
    public static HistoryFilterConfig Get()
    {
        var node = CcDirectorConfigService.ReadRaw()["history_filter"];
        if (node is null)
            return Default;

        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                "config.json key 'history_filter' must be an object. " +
                "Fix the value or remove the key to use the defaults (show everything).");

        return new HistoryFilterConfig(
            ShowToolCalls: ReadBool(obj, "show_tool_calls", Default.ShowToolCalls),
            ShowToolResults: ReadBool(obj, "show_tool_results", Default.ShowToolResults),
            ShowThinking: ReadBool(obj, "show_thinking", Default.ShowThinking));
    }

    /// <summary>Persist this filter to config.json's "history_filter" object, preserving every
    /// other section of the file (deep-merge via <see cref="CcDirectorConfigService.MergePatch"/>).</summary>
    public void Save()
    {
        var patch = new JsonObject
        {
            ["history_filter"] = new JsonObject
            {
                ["show_tool_calls"] = ShowToolCalls,
                ["show_tool_results"] = ShowToolResults,
                ["show_thinking"] = ShowThinking,
            },
        };
        CcDirectorConfigService.MergePatch(patch);
    }

    private static bool ReadBool(JsonObject obj, string key, bool fallback)
    {
        var node = obj[key];
        if (node is null)
            return fallback;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.True) return true;
        if (node is JsonValue v2 && v2.GetValueKind() == JsonValueKind.False) return false;

        throw new InvalidOperationException(
            $"config.json key 'history_filter.{key}' must be true or false. " +
            "Fix the value or remove the key to use the default (true = shown).");
    }
}
