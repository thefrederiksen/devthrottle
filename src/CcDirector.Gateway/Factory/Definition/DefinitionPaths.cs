using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>What a path in a definition is.</summary>
public enum DefinitionPlace
{
    Factory,
    Seat,
    Grants,
    RuleSet,
    File,
    Check,
    SkillMain,
    SkillFile,
}

/// <summary>A path that is a place a definition holds, and the name it carries (seat, rule set, check or skill).</summary>
public sealed record DefinitionPathInfo(DefinitionPlace Place, string? Name);

/// <summary>
/// The places a definition may hold and the rules a path keeps (design section 1), and the size caps. Pure.
/// </summary>
public static partial class DefinitionPaths
{
    /// <summary>The most one file may hold, in UTF-8 bytes (256 KB).</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>The most one version may hold, all files' UTF-8 bytes together (4 MB).</summary>
    public const int MaxVersionBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The most files one version may hold. The byte caps count only the files' text, so without this an empty file
    /// would cost nothing and a version could hold files without end. The definition store reads this cap, and the
    /// other caps and path rules here, rather than holding its own.
    /// </summary>
    public const int MaxFiles = 1000;

    /// <summary>The longest a path may be, in characters.</summary>
    public const int MaxPathLength = 200;

    /// <summary>The places, as a person reads them in a refusal.</summary>
    public const string PlacesText =
        "factory.yaml, seats/<seat>.yaml, grants.yaml, rules/<name>.json, files/<any path>, checks/<name>.py, " +
        "skills/<name>/SKILL.md and skills/<name>/<any path>";

    /// <summary>The file a factory skill's folder must hold.</summary>
    public const string SkillMainFile = "SKILL.md";

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    // A check is a Python file, so its name may use underscores as well as hyphens (new_mail.py).
    [GeneratedRegex("^[a-z0-9]+([-_][a-z0-9]+)*$")]
    private static partial Regex CheckNamePattern();

    /// <summary>The characters Windows does not allow in a file name, beyond the control characters.</summary>
    public const string CharactersWindowsRefuses = "<>:\"|?*";

    /// <summary>
    /// Why <paramref name="path"/> breaks the path rules, or null when it keeps them: forward slashes only, no leading
    /// slash, no control character, no <c>.</c>, <c>..</c> or empty part, at most <see cref="MaxPathLength"/> characters
    /// - and a path every machine can write as a file (review of the definition store, finding 1): none of the
    /// characters <see cref="CharactersWindowsRefuses"/>, no part ending in a dot or a space (Windows strips them, so
    /// the file would land under another name), and no part that is a name Windows keeps for a device. A published
    /// version's files are downloaded onto the seat's machine (mission 5.5a), so a path one Windows Director cannot
    /// create stops that seat from starting.
    /// </summary>
    public static string? ShapeProblem(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? reason = null;
        if (path.Length == 0) reason = "it is empty";
        else if (path.Length > MaxPathLength) reason = $"it is {path.Length} characters long";
        else if (path.Contains('\\')) reason = "it contains a backslash";
        else if (path.Any(char.IsControl)) reason = "it holds a control character, such as a line break";
        else if (path.IndexOfAny(CharactersWindowsRefuses.ToCharArray()) is var at and >= 0)
            reason = $"it holds the character '{path[at]}', which Windows does not allow in a file name";
        else if (path.StartsWith('/')) reason = "it begins with a slash";
        else
        {
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0) { reason = "it has an empty part"; break; }
                if (part is "." or "..") { reason = $"it has a '{part}' part"; break; }
                if (part.EndsWith('.') || part.EndsWith(' '))
                {
                    reason = $"its part '{part}' ends in a dot or a space, which Windows removes, so the file would get another name";
                    break;
                }
                if (FactoryNames.IsWindowsDeviceName(part))
                {
                    reason = $"its part '{part}' is a name Windows keeps for a device, so it cannot be a file or a folder";
                    break;
                }
            }
        }
        return reason is null
            ? null
            : $"'{Shown(path.ReplaceLineEndings(" "))}' is not a path a definition can hold: {reason}. A path uses forward " +
              $"slashes, with no leading slash, no control character, none of {CharactersWindowsRefuses}, no '.', '..' or " +
              $"empty part, no part ending in a dot or a space and no device name such as nul, and is at most {MaxPathLength} characters.";
    }

    /// <summary>
    /// The paths of one definition that no machine could write side by side (review of the definition store, finding
    /// 1), each with the sentence refusing it: two paths whose names differ only in the case of their letters
    /// (<c>files/Brief.md</c> and <c>files/brief.md</c>), which Windows and macOS hold as ONE file, and a name that is a
    /// file in one path and a folder in another (<c>files/a</c> and <c>files/a/b</c>). Either way an export writes
    /// fewer files than the definition holds, and importing it gives back a different definition. Every folder on the
    /// way is compared, not only the file name. Pure; the paths are compared in ordinal order, so the answer is the
    /// same whatever order they come in, and the later of two clashing paths is the one named.
    /// </summary>
    public static IReadOnlyList<(string Path, string Refusal)> Clashes(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // Every name on the way, folded to one case: how it was spelled, whether it was a file, and the path that
        // brought it first.
        var seen = new Dictionary<string, (string Spelled, bool IsFile, string FirstPath)>(StringComparer.Ordinal);
        var clashes = new List<(string, string)>();
        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                var spelled = string.Join('/', parts, 0, i + 1);
                var isFile = i == parts.Length - 1;
                var folded = spelled.ToLowerInvariant();
                if (!seen.TryGetValue(folded, out var before))
                {
                    seen[folded] = (spelled, isFile, path);
                    continue;
                }
                if (before.Spelled != spelled)
                {
                    clashes.Add((path, $"'{Shown(path)}' spells '{spelled}' where '{Shown(before.FirstPath)}' spells it " +
                        $"'{before.Spelled}'. Windows and macOS hold names that differ only in the case of their letters " +
                        "as one, so the two would become one file. Spell them the same, or rename one."));
                    break;
                }
                if (before.IsFile != isFile)
                {
                    clashes.Add((path, $"'{spelled}' is a file in '{Shown(isFile ? path : before.FirstPath)}' and a folder in " +
                        $"'{Shown(isFile ? before.FirstPath : path)}'; no machine can hold both. Rename one."));
                    break;
                }
            }
        }
        return clashes;
    }

    /// <summary>
    /// What place <paramref name="path"/> is, or the sentence refusing it. Call only on a path that keeps the shape
    /// rules (<see cref="ShapeProblem"/> returned null).
    /// </summary>
    public static (DefinitionPathInfo? Info, string? Refusal) Classify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/');

        if (parts is ["factory.yaml"]) return (new DefinitionPathInfo(DefinitionPlace.Factory, null), null);
        if (parts is ["grants.yaml"]) return (new DefinitionPathInfo(DefinitionPlace.Grants, null), null);

        if (parts is ["seats", var seatFile] && seatFile.EndsWith(".yaml", StringComparison.Ordinal))
            return Named(path, DefinitionPlace.Seat, seatFile[..^".yaml".Length], "seat", IdPattern());
        if (parts is ["rules", var ruleFile] && ruleFile.EndsWith(".json", StringComparison.Ordinal))
            return Named(path, DefinitionPlace.RuleSet, ruleFile[..^".json".Length], "rule set", IdPattern());
        if (parts is ["checks", var checkFile] && checkFile.EndsWith(".py", StringComparison.Ordinal))
            return Named(path, DefinitionPlace.Check, checkFile[..^".py".Length], "check", CheckNamePattern());
        if (parts.Length >= 2 && parts[0] == "files")
            return (new DefinitionPathInfo(DefinitionPlace.File, null), null);
        if (parts.Length >= 3 && parts[0] == "skills")
        {
            var place = parts.Length == 3 && parts[2] == SkillMainFile ? DefinitionPlace.SkillMain : DefinitionPlace.SkillFile;
            return Named(path, place, parts[1], "skill", IdPattern());
        }

        return (null, $"'{Shown(path)}' is not a place a definition holds. The places are: {PlacesText}.");
    }

    private static (DefinitionPathInfo?, string?) Named(string path, DefinitionPlace place, string name, string what, Regex pattern)
    {
        if (name.Length > FactoryNames.MaxFactoryLength || !pattern.IsMatch(name))
        {
            var rule = what == "check"
                ? "lower-case letters and digits joined by single hyphens or underscores"
                : "lower-case letters and digits joined by single hyphens";
            return (null, $"'{path}' names the {what} '{name}', but a {what} name is {rule}, at most " +
                          $"{FactoryNames.MaxFactoryLength} characters. Rename it.");
        }
        return (new DefinitionPathInfo(place, name), null);
    }

    private static string Shown(string path) => path.Length <= 120 ? path : path[..117] + "...";
}
