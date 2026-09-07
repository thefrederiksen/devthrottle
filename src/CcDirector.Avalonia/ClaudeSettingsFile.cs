using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// How a read of a Claude configuration file ended. THREE answers, not two.
///
/// The settings dialog edits a handful of fields inside a file it does not own: the same file also
/// carries <c>hooks</c>, <c>mcpServers</c>, <c>statusLine</c> and anything else Claude Code puts
/// there. Saving is therefore a MERGE, and a merge is only safe when the existing content was
/// actually seen. "The file is not there" and "the file is there and I could not read it" are
/// opposite facts about the world that happen to produce the same empty object, and folding them
/// together is what let a save destroy the file it was merging into.
/// </summary>
public enum ConfigReadKind
{
    /// <summary>No file at that path. There is nothing to preserve, so a save may create one.</summary>
    Absent,

    /// <summary>The file was read and parsed. Its untouched fields must survive the save.</summary>
    Loaded,

    /// <summary>
    /// A file IS at that path and its content could not be obtained - locked by another process,
    /// a permissions failure, a half-written file, or invalid JSON from a hand edit. NOTHING may be
    /// written over it, because what would be lost cannot even be enumerated.
    /// </summary>
    Unreadable,
}

/// <summary>The outcome of reading a configuration file, and what was in it when there was something.</summary>
/// <param name="Kind">Which of the three answers this is.</param>
/// <param name="Root">The parsed object - only ever set when <paramref name="Kind"/> is Loaded.</param>
/// <param name="Problem">Why it could not be read, in words fit to show a person. Only set when Unreadable.</param>
public sealed record ConfigRead(ConfigReadKind Kind, JsonObject? Root, string? Problem);

/// <summary>How a save of the settings file ended.</summary>
public enum SettingsSaveKind
{
    /// <summary>The file did not exist and was created.</summary>
    Created,

    /// <summary>The file existed, was read, and the edits were merged into it.</summary>
    Merged,

    /// <summary>
    /// The file existed and could not be read, so NOTHING was written. This is the state that used
    /// to have no name.
    /// </summary>
    RefusedUnreadable,

    /// <summary>
    /// The read said it was safe to write and the WRITE itself failed - the disk, the permissions,
    /// the path. Distinct from a refusal: a refusal means we chose not to write, this means we tried
    /// and could not, and the file may be in either state. Named because the alternative is an
    /// exception out of a button click, and this dialog's click handler is where those disappear.
    /// </summary>
    WriteFailed,
}

/// <summary>The outcome of a save, and why when it refused.</summary>
public sealed record SettingsSaveResult(SettingsSaveKind Kind, string? Problem)
{
    /// <summary>True when the file on disk was left exactly as it was found.</summary>
    public bool Refused => Kind == SettingsSaveKind.RefusedUnreadable;

    /// <summary>
    /// True only when the settings are on disk. Written as a POSITIVE test of the two kinds that
    /// actually wrote, not as "not refused" - a later kind added to this enum must not silently
    /// become a success, which is how the defect this class fixes came about in the first place.
    /// </summary>
    public bool Saved => Kind is SettingsSaveKind.Created or SettingsSaveKind.Merged;
}

/// <summary>
/// The edits the settings dialog is able to make. Everything else in the file belongs to somebody
/// else and is carried through untouched.
/// </summary>
/// <param name="PermissionMode">The default permission mode, or null to remove it.</param>
/// <param name="Allow">The allow rules, replacing whatever is there.</param>
/// <param name="Deny">The deny rules, replacing whatever is there.</param>
/// <param name="Model">ANTHROPIC_MODEL, or null/blank to remove it.</param>
/// <param name="EffortLevel">CLAUDE_CODE_EFFORT_LEVEL, or null/blank to remove it.</param>
/// <param name="MaxOutputTokens">CLAUDE_CODE_MAX_OUTPUT_TOKENS, or null/blank to remove it.</param>
/// <param name="BashTimeoutMs">BASH_DEFAULT_TIMEOUT_MS, or null/blank to remove it.</param>
/// <param name="EnabledPlugins">The plugin enablement map, replacing whatever is there.</param>
public sealed record ClaudeSettingsEdits(
    string? PermissionMode,
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny,
    string? Model,
    string? EffortLevel,
    string? MaxOutputTokens,
    string? BashTimeoutMs,
    IReadOnlyDictionary<string, bool> EnabledPlugins);

/// <summary>
/// Reading and writing the user's Claude <c>settings.json</c> without destroying the parts of it
/// this application does not understand.
///
/// Split out of <see cref="ClaudeConfigDialog"/> so the merge decision can be exercised against a
/// real file on a real path. The dialog resolves its paths from the user profile, so testing the
/// save through the dialog would mean pointing a test at the developer's own settings - which is
/// precisely the file this class exists to stop being overwritten.
/// </summary>
public static class ClaudeSettingsFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Read a configuration file and say which of the three things happened. Never throws: the
    /// failure is the RETURN VALUE, because a caller that cannot see the difference between absent
    /// and unreadable is the whole defect this replaces.
    /// </summary>
    public static ConfigRead Read(string path)
    {
        // Something is at that path and it is NOT a file. File.Exists answers false for a directory,
        // which would fold this into Absent - the permissive branch, the one that says "go ahead and
        // create". Creating is then an unhandled write failure rather than an answer. A directory in
        // the way is a state of the world, so it gets its own answer like every other one.
        if (Directory.Exists(path))
        {
            FileLog.Write($"[ClaudeSettingsFile] Read: {path} is a directory, not a settings file");
            return new ConfigRead(ConfigReadKind.Unreadable, null, "is a directory, not a file");
        }

        if (!File.Exists(path))
            return new ConfigRead(ConfigReadKind.Absent, null, null);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeSettingsFile] Read: {path} exists but could not be opened: {ex.Message}");
            return new ConfigRead(ConfigReadKind.Unreadable, null, $"could not be opened ({ex.Message})");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[ClaudeSettingsFile] Read: {path} is not valid JSON: {ex.Message}");
            return new ConfigRead(ConfigReadKind.Unreadable, null, $"is not valid JSON ({ex.Message})");
        }

        // Parsed, but not into an object - a file holding "null", a bare array or a number. There is
        // content there and it is not something edits can be merged into, so it is not safe to write
        // over either. Deliberately NOT folded into Absent.
        if (node is not JsonObject obj)
        {
            FileLog.Write($"[ClaudeSettingsFile] Read: {path} parsed but does not hold a JSON object");
            return new ConfigRead(ConfigReadKind.Unreadable, null, "does not hold a JSON object");
        }

        return new ConfigRead(ConfigReadKind.Loaded, obj, null);
    }

    /// <summary>
    /// Merge <paramref name="edits"/> into the settings file at <paramref name="path"/> and write it
    /// back, preserving every field the dialog does not edit.
    ///
    /// REFUSES, writing nothing, when the file is there and cannot be read. That refusal is the fix:
    /// the previous behaviour treated an unreadable file as an empty one and wrote a fresh object
    /// over it, which silently discarded the user's hooks - the very fields the read exists to carry
    /// through, and, for this fleet, the ones that inject the preamble every session runs under.
    /// </summary>
    public static SettingsSaveResult Save(string path, ClaudeSettingsEdits edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        var read = Read(path);
        if (read.Kind == ConfigReadKind.Unreadable)
        {
            FileLog.Write($"[ClaudeSettingsFile] Save REFUSED for {path}: the file {read.Problem}. "
                          + "Nothing was written - merging into a file that cannot be read would discard "
                          + "every field this dialog does not edit, hooks included.");
            return new SettingsSaveResult(SettingsSaveKind.RefusedUnreadable, read.Problem);
        }

        var creating = read.Kind == ConfigReadKind.Absent;
        var obj = read.Root ?? new JsonObject();

        Apply(obj, edits);

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = obj.ToJsonString(WriteOptions);

            // Write a sibling temp file and MOVE it over the target, the same way config.json and the
            // key store in this repository already do it. An in-place WriteAllText truncates first, so
            // a crash or a full disk part-way through leaves the settings file destroyed or
            // unparsable - this fix would then have MANUFACTURED the very state it refuses to write
            // over. The move is the commit point: readers see the old file or the new one.
            var temp = Path.Combine(dir ?? ".", $".settings.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);

            FileLog.Write($"[ClaudeSettingsFile] Save: {path} ({json.Length} bytes, "
                          + $"{(creating ? "created" : "merged into existing")})");
        }
        catch (Exception ex)
        {
            // NOT swallowed and NOT a fallback: turned into an answer the caller must render. Letting
            // it throw sends it to the dialog's click handler, which has no catch, and from there to
            // the application's unhandled-exception handler, which marks it Handled - so the person
            // presses Save, nothing happens, and nothing is said.
            FileLog.Write($"[ClaudeSettingsFile] Save FAILED writing {path}: {ex.Message}");
            return new SettingsSaveResult(SettingsSaveKind.WriteFailed, $"could not be written ({ex.Message})");
        }

        return new SettingsSaveResult(
            creating ? SettingsSaveKind.Created : SettingsSaveKind.Merged, null);
    }

    /// <summary>Apply the dialog's edits to an object, touching only the fields it owns.</summary>
    private static void Apply(JsonObject obj, ClaudeSettingsEdits edits)
    {
        if (obj["$schema"] is null)
            obj["$schema"] = "https://json.schemastore.org/claude-code-settings.json";

        var perms = obj["permissions"] as JsonObject ?? new JsonObject();
        obj["permissions"] = perms;

        if (!string.IsNullOrEmpty(edits.PermissionMode))
            perms["defaultMode"] = edits.PermissionMode;
        else
            perms.Remove("defaultMode");

        perms["allow"] = new JsonArray(edits.Allow.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        perms["deny"] = new JsonArray(edits.Deny.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());

        var env = obj["env"] as JsonObject ?? new JsonObject();
        obj["env"] = env;

        SetOrRemove(env, "ANTHROPIC_MODEL", edits.Model);
        SetOrRemove(env, "CLAUDE_CODE_EFFORT_LEVEL", edits.EffortLevel);
        SetOrRemove(env, "CLAUDE_CODE_MAX_OUTPUT_TOKENS", edits.MaxOutputTokens);
        SetOrRemove(env, "BASH_DEFAULT_TIMEOUT_MS", edits.BashTimeoutMs);

        if (env.Count == 0)
            obj.Remove("env");

        var pluginsObj = new JsonObject();
        foreach (var (key, enabled) in edits.EnabledPlugins)
            pluginsObj[key] = enabled;
        obj["enabledPlugins"] = pluginsObj;
    }

    private static void SetOrRemove(JsonObject env, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            env[key] = value.Trim();
        else
            env.Remove(key);
    }
}
