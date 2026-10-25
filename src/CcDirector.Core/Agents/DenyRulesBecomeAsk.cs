using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Agents;

/// <summary>
/// THE OWNER'S WORD IS FINAL, EVERYWHERE (the owner, 24 and 25 October 2026: "If I say so, directly, it can ask me for
/// approval. But if I approve, it has to do it." - "Yes, I want to be God everywhere."). A deny rule refuses with no
/// prompt, so the owner has no way to approve what it blocks, and those files are often written by an agent. Before a
/// Director starts ANY session it turns every outright refusal in the settings files that session's agent will read into
/// the same agent's "ask the person" form, and says so: the launch log names each file and rule, and the session carries
/// the list to the Gateway. This is the owner's standing approval; it does not ask per file.
///
/// Per agent, what refuses outright and what it becomes (each read from the installed tool's own documentation and,
/// where it has a checker, from that checker's answer):
/// <list type="bullet">
/// <item>Claude Code: <c>permissions.deny</c> in the folder's <c>.claude/settings.json</c> and <c>settings.local.json</c>
/// and the machine's <c>settings.json</c>; each rule moves to <c>permissions.ask</c>.</item>
/// <item>Grok: the same Claude Code files (it reads them when it has no permission section of its own), and the
/// <c>[permission]</c> section of <c>~/.grok/config.toml</c> and the folder's <c>.grok/config.toml</c>: a <c>deny</c>
/// list becomes the <c>ask</c> list, a rule's <c>action = "deny"</c> becomes <c>"ask"</c>.</item>
/// <item>Codex: <c>decision="forbidden"</c> in <c>~/.codex/rules/*.rules</c> and the folder's <c>.codex/rules/*.rules</c>
/// becomes <c>decision="prompt"</c> (<c>codex execpolicy check</c> answers "forbidden" and "prompt" for the two).</item>
/// <item>Gemini: <c>decision = "deny"</c> in the user's <c>~/.gemini/policies/*.toml</c> becomes <c>"ask_user"</c>. A
/// folder's <c>.gemini/policies</c> is not read by Gemini (its documentation marks that tier disabled), so it is left.</item>
/// <item>OpenCode: every <c>"deny"</c> under <c>permission</c> in <c>~/.config/opencode/opencode.json</c> and the folder's
/// <c>opencode.json</c> becomes <c>"ask"</c>.</item>
/// <item>Pi, Copilot, Cursor: nothing is rewritten. Pi has no approval prompt at all; Copilot refuses only through
/// command-line flags the Director does not pass and a URL list with no ask form; Cursor is not installed anywhere it
/// could be read.</item>
/// </list>
/// Managed settings an organisation deploys are read, never written: a deny rule there is reported as one the Director
/// cannot change. A file this cannot rewrite without losing something - comments, a key written twice, a deny list
/// beside an ask list it cannot merge - is reported with the reason and left as it is, never half-changed.
/// </summary>
public static class DenyRulesBecomeAsk
{
    /// <summary>The kinds of settings file an agent refuses from, each with its own rewrite.</summary>
    internal enum Format { ClaudeJson, OpenCodeJson, CodexRules, GeminiPolicy, GrokToml }

    /// <summary>One file a session's agent reads. A managed one is reported, never written.</summary>
    internal sealed record Target(string Path, Format Format, bool Managed = false);

    /// <summary>What a rewrite of one file's text found: the new text (null when nothing changes), the rules it turned
    /// into ask rules, and why it could not when it could not.</summary>
    internal sealed record Rewrite(string? Text, List<string> Rules, string? Problem)
    {
        public static readonly Rewrite Nothing = new(null, new List<string>(), null);
        public static Rewrite Cannot(string problem, List<string>? rules = null) => new(null, rules ?? new List<string>(), problem);
    }

    // Two sessions starting at once on one Director must not both read, change and write the machine's settings.
    private static readonly object WriteLock = new();

    /// <summary>
    /// Turn every outright refusal the <paramref name="kind"/> agent would read, starting in <paramref name="folder"/>,
    /// into an ask rule. Returns one entry per file that held one: rewritten (its rules listed), or not (with why).
    /// An empty list is a launch where nothing refused outright.
    /// </summary>
    public static List<FolderDenyRulesDto> Apply(AgentKind kind, string folder, string home, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);
        var found = ApplyTo(TargetsFor(kind, folder, home, environment));
        FileLog.Write($"[DenyRulesBecomeAsk] Apply: agent={kind}, folder={folder}, files={found.Count}, " +
                      $"rewritten={found.Count(f => f.Rewritten)}, rules={found.Sum(f => f.Rules.Count)}");
        return found;
    }

    /// <summary>Each target in order: one entry per file that refused something outright.</summary>
    internal static List<FolderDenyRulesDto> ApplyTo(IEnumerable<Target> targets)
    {
        var found = new List<FolderDenyRulesDto>();
        lock (WriteLock)
        {
            foreach (var target in targets)
            {
                if (ApplyTo(target) is { } entry) found.Add(entry);
            }
        }
        return found;
    }

    /// <summary>Every file the agent reads that can refuse outright, folder files first, then the machine's, then managed ones.
    /// Directory patterns are expanded to the files present now. Each path appears once.</summary>
    internal static List<Target> TargetsFor(AgentKind kind, string folder, string home, Func<string, string?> environment)
    {
        var targets = new List<Target>();
        var claudeFiles = new[]
        {
            new Target(Path.Combine(folder, ".claude", "settings.json"), Format.ClaudeJson),
            new Target(Path.Combine(folder, ".claude", "settings.local.json"), Format.ClaudeJson),
            new Target(Path.Combine(ClaudeSettingsFiles.ConfigFolder(environment, home), "settings.json"), Format.ClaudeJson),
        };
        switch (kind)
        {
            case AgentKind.ClaudeCode:
                targets.AddRange(claudeFiles);
                targets.Add(new Target(ClaudeSettingsFiles.ManagedPath(), Format.ClaudeJson, Managed: true));
                break;
            case AgentKind.Grok:
                targets.AddRange(claudeFiles);
                var grokHome = HomeOf(environment, "GROK_HOME", Path.Combine(home, ".grok"));
                targets.Add(new Target(Path.Combine(folder, ".grok", "config.toml"), Format.GrokToml));
                targets.Add(new Target(Path.Combine(grokHome, "config.toml"), Format.GrokToml));
                targets.Add(new Target(Path.Combine(grokHome, "managed_config.toml"), Format.GrokToml, Managed: true));
                targets.Add(new Target(Path.Combine(grokHome, "requirements.toml"), Format.GrokToml, Managed: true));
                if (!OperatingSystem.IsWindows())
                {
                    targets.Add(new Target("/etc/grok/managed_config.toml", Format.GrokToml, Managed: true));
                    targets.Add(new Target("/etc/grok/requirements.toml", Format.GrokToml, Managed: true));
                }
                break;
            case AgentKind.Codex:
                var codexHome = HomeOf(environment, "CODEX_HOME", Path.Combine(home, ".codex"));
                targets.AddRange(FilesIn(Path.Combine(folder, ".codex", "rules"), "*.rules", Format.CodexRules));
                targets.AddRange(FilesIn(Path.Combine(codexHome, "rules"), "*.rules", Format.CodexRules));
                break;
            case AgentKind.Gemini:
                targets.AddRange(FilesIn(Path.Combine(home, ".gemini", "policies"), "*.toml", Format.GeminiPolicy));
                targets.AddRange(FilesIn(GeminiAdminPolicies(), "*.toml", Format.GeminiPolicy, managed: true));
                break;
            case AgentKind.OpenCode:
                var configHome = HomeOf(environment, "XDG_CONFIG_HOME", Path.Combine(home, ".config"));
                targets.Add(new Target(Path.Combine(folder, "opencode.json"), Format.OpenCodeJson));
                targets.Add(new Target(Path.Combine(configHome, "opencode", "opencode.json"), Format.OpenCodeJson));
                break;
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        return targets.Where(t => seen.Add(Path.GetFullPath(t.Path))).ToList();
    }

    private static string HomeOf(Func<string, string?> environment, string variable, string otherwise)
    {
        var value = environment(variable);
        return string.IsNullOrWhiteSpace(value) ? otherwise : value;
    }

    private static string GeminiAdminPolicies() =>
        OperatingSystem.IsWindows() ? @"C:\ProgramData\gemini-cli\policies"
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/GeminiCli/policies"
        : "/etc/gemini-cli/policies";

    private static IEnumerable<Target> FilesIn(string directory, string pattern, Format format, bool managed = false) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal).Select(f => new Target(f, format, managed))
            : Enumerable.Empty<Target>();

    /// <summary>One file: read, rewrite, write. Null when the file is missing or holds no outright refusal.</summary>
    private static FolderDenyRulesDto? ApplyTo(Target target)
    {
        if (!File.Exists(target.Path)) return null;
        string text;
        try { text = File.ReadAllText(target.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderDenyRulesDto { File = target.Path, Problem = $"it could not be read, so whether it refuses anything outright is unknown ({ex.Message})" };
        }

        var rewrite = RewriteText(target.Format, text);
        if (rewrite.Problem is not null)
            return new FolderDenyRulesDto { File = target.Path, Rules = rewrite.Rules, Problem = rewrite.Problem };
        if (rewrite.Text is null) return null;
        if (target.Managed)
            return new FolderDenyRulesDto
            {
                File = target.Path, Rules = rewrite.Rules,
                Problem = "it is managed settings an organisation deployed, which the Director does not change",
            };

        try { WriteReplacing(target.Path, rewrite.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderDenyRulesDto { File = target.Path, Rules = rewrite.Rules, Problem = $"it could not be written ({ex.Message})" };
        }
        FileLog.Write($"[DenyRulesBecomeAsk] ApplyTo: {target.Path}: {rewrite.Rules.Count} rule(s) now ask: {string.Join(", ", rewrite.Rules)}");
        return new FolderDenyRulesDto { File = target.Path, Rules = rewrite.Rules, Rewritten = true };
    }

    // Written beside the file and moved over it, so a reader never sees half a settings file.
    private static void WriteReplacing(string path, string text)
    {
        var temporary = path + ".devthrottle-" + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The rewrite of one file's text in its format. Pure.</summary>
    internal static Rewrite RewriteText(Format format, string text) => format switch
    {
        Format.ClaudeJson => RewriteClaudeJson(text),
        Format.OpenCodeJson => RewriteOpenCodeJson(text),
        Format.CodexRules => RewriteDecisions(text, CodexForbidden, "prompt"),
        Format.GeminiPolicy => RewriteDecisions(text, GeminiDeny, "ask_user"),
        Format.GrokToml => RewriteGrokToml(text),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "no rewrite for this format"),
    };

    // ---------- JSON: Claude Code and OpenCode ----------

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The file as a JSON object, or why it cannot be rewritten without losing something.</summary>
    private static (JsonObject? Root, string? Problem) ParseStrict(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, "it is not a JSON object");
            if (FirstDuplicateKey(document.RootElement) is { } key)
                return (null, $"the key '{key}' is written twice in it, so it cannot be rewritten without changing which one counts");
        }
        catch (JsonException)
        {
            // Comments and trailing commas are read by the agents but would be lost by a rewrite.
            try
            {
                using var lenient = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return (null, "it holds comments or trailing commas, which rewriting it would drop: change each deny to ask by hand");
            }
            catch (JsonException ex)
            {
                return (null, $"it is not JSON, so whether it refuses anything outright could not be read ({ex.Message})");
            }
        }
        return ((JsonObject)JsonNode.Parse(text)!, null);
    }

    private static string? FirstDuplicateKey(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) return property.Name;
                if (FirstDuplicateKey(property.Value) is { } inner) return inner;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FirstDuplicateKey(item) is { } inner) return inner;
        }
        return null;
    }

    private static string Serialize(JsonObject root, string original)
    {
        var newLine = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var json = root.ToJsonString(Indented).ReplaceLineEndings(newLine);
        return original.EndsWith('\n') ? json + newLine : json;
    }

    private static Rewrite RewriteClaudeJson(string text)
    {
        if (!text.Contains("\"deny\"", StringComparison.Ordinal)) return Rewrite.Nothing;
        var (root, problem) = ParseStrict(text);
        if (problem is not null) return Rewrite.Cannot(problem);
        if (root!["permissions"] is not JsonObject permissions || permissions["deny"] is not { } deny) return Rewrite.Nothing;
        if (deny is not JsonArray denyList || denyList.Any(r => r is not JsonValue v || v.GetValueKind() != JsonValueKind.String))
            return Rewrite.Cannot("its permissions.deny is not a list of rules");
        if (denyList.Count == 0) return Rewrite.Nothing;
        if (permissions["ask"] is { } existing && existing is not JsonArray)
            return Rewrite.Cannot("its permissions.ask is not a list, so the deny rules cannot be added to it");

        var rules = denyList.Select(r => r!.GetValue<string>()).ToList();
        var ask = permissions["ask"] as JsonArray;
        if (ask is null)
        {
            ask = new JsonArray();
            permissions["ask"] = ask;
        }
        var have = ask.Select(r => r?.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in rules)
            if (have.Add(rule)) ask.Add(rule);
        permissions.Remove("deny");
        return new Rewrite(Serialize(root, text), rules, null);
    }

    private static Rewrite RewriteOpenCodeJson(string text)
    {
        if (!text.Contains("\"deny\"", StringComparison.Ordinal)) return Rewrite.Nothing;
        var (root, problem) = ParseStrict(text);
        if (problem is not null) return Rewrite.Cannot(problem);
        var rules = new List<string>();
        switch (root!["permission"])
        {
            case JsonValue value when IsDeny(value):
                root["permission"] = "ask";
                rules.Add("permission");
                break;
            case JsonObject permission:
                AskInstead(permission, "permission", rules);
                break;
        }
        return rules.Count == 0 ? Rewrite.Nothing : new Rewrite(Serialize(root, text), rules, null);
    }

    private static bool IsDeny(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "deny";

    private static void AskInstead(JsonObject node, string path, List<string> rules)
    {
        foreach (var (key, value) in node.ToList())
        {
            if (IsDeny(value))
            {
                node[key] = "ask";
                rules.Add($"{path}.{key}");
            }
            else if (value is JsonObject inner)
            {
                AskInstead(inner, $"{path}.{key}", rules);
            }
        }
    }

    // ---------- line rules: Codex and Gemini ----------

    private static readonly Regex CodexForbidden = new(@"(?<lead>\bdecision\s*=\s*)(?<q>[""'])forbidden\k<q>", RegexOptions.CultureInvariant);
    private static readonly Regex GeminiDeny = new(@"(?<lead>^[ \t]*decision[ \t]*=[ \t]*)(?<q>[""'])deny\k<q>", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static Rewrite RewriteDecisions(string text, Regex refusal, string ask)
    {
        var rules = new List<string>();
        var rewritten = refusal.Replace(text, m =>
        {
            rules.Add(LineOf(text, m.Index));
            return m.Groups["lead"].Value + m.Groups["q"].Value + ask + m.Groups["q"].Value;
        });
        return rules.Count == 0 ? Rewrite.Nothing : new Rewrite(rewritten, rules, null);
    }

    /// <summary>"line N: the line", the way a rule is named in the log and on the factory page.</summary>
    private static string LineOf(string text, int index)
    {
        var number = 1 + text.AsSpan(0, index).Count('\n');
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        var line = text[start..(end < 0 ? text.Length : end)].Trim();
        return $"line {number}: {(line.Length > 160 ? line[..160] + "..." : line)}";
    }

    // ---------- Grok's TOML ----------

    private static readonly Regex TomlTable = new(@"^\s*\[\[?\s*(?<name>[A-Za-z0-9_.\-]+)\s*\]\]?\s*(#.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex TomlDenyKey = new(@"^(?<indent>\s*)deny(?<rest>\s*=.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex TomlAskKey = new(@"^\s*ask\s*=", RegexOptions.CultureInvariant);
    private static readonly Regex TomlDenyAction = new(@"(?<lead>\baction\s*=\s*)(?<q>[""'])deny\k<q>", RegexOptions.CultureInvariant);
    private static readonly Regex TomlString = new(@"""((?:[^""\\]|\\.)*)""|'([^']*)'", RegexOptions.CultureInvariant);

    private static Rewrite RewriteGrokToml(string text)
    {
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var rules = new List<string>();
        var table = "";
        int? denyLine = null;
        var askInPermission = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (TomlTable.Match(lines[i]) is { Success: true } header)
            {
                table = header.Groups["name"].Value;
                continue;
            }
            if (table != "permission" && !table.StartsWith("permission.", StringComparison.Ordinal)) continue;
            if (table == "permission" && TomlAskKey.IsMatch(lines[i])) askInPermission = true;
            if (table == "permission" && denyLine is null && TomlDenyKey.IsMatch(lines[i])) denyLine = i;
            var index = i;
            lines[i] = TomlDenyAction.Replace(lines[i], m =>
            {
                rules.Add($"line {index + 1}: {lines[index].Trim()}");
                return m.Groups["lead"].Value + m.Groups["q"].Value + "ask" + m.Groups["q"].Value;
            });
        }

        if (denyLine is { } at)
        {
            var listed = DenyListItems(lines, at);
            if (askInPermission)
                return Rewrite.Cannot("its [permission] section has both a deny list and an ask list, which the Director does not merge in TOML: move the deny rules into the ask list by hand", listed);
            var key = TomlDenyKey.Match(lines[at]);
            lines[at] = key.Groups["indent"].Value + "ask" + key.Groups["rest"].Value;
            rules.InsertRange(0, listed);
        }
        return rules.Count == 0 ? Rewrite.Nothing : new Rewrite(string.Join(newLine, lines), rules, null);
    }

    /// <summary>The strings of the <c>deny = [ ... ]</c> list that starts on <paramref name="at"/>, across lines.</summary>
    private static List<string> DenyListItems(List<string> lines, int at)
    {
        var items = new List<string>();
        var depth = 0;
        for (var i = at; i < lines.Count; i++)
        {
            var line = i == at ? lines[i][(lines[i].IndexOf('=') + 1)..] : lines[i];
            foreach (Match m in TomlString.Matches(line))
                items.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            depth += line.Count(c => c == '[') - line.Count(c => c == ']');
            if (depth <= 0) break;
        }
        return items;
    }

    /// <summary>The launch log's line for what <see cref="Apply"/> did, or null when it found nothing.</summary>
    public static string? LogLine(IReadOnlyList<FolderDenyRulesDto> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (found.Count == 0) return null;
        var parts = new List<string>();
        var done = found.Where(f => f.Rewritten).ToList();
        var left = found.Where(f => !f.Rewritten).ToList();
        if (done.Count > 0)
            parts.Add("DENY RULES TURNED INTO ASK RULES (the owner's standing approval): " +
                      string.Join("; ", done.Select(f => $"{f.File}: {string.Join(", ", f.Rules)}")));
        if (left.Count > 0)
            parts.Add("REFUSALS THE DIRECTOR COULD NOT TURN INTO ASK RULES: " +
                      string.Join("; ", left.Select(f => f.Rules.Count > 0
                          ? $"{f.File} ({string.Join(", ", f.Rules)}): {f.Problem}"
                          : $"{f.File}: {f.Problem}")));
        return string.Join(". ", parts) + ".";
    }
}
