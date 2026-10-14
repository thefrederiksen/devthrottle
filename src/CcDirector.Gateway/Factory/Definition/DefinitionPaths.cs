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

    /// <summary>
    /// Why <paramref name="path"/> breaks the path rules, or null when it keeps them: forward slashes only, no leading
    /// slash, no control character, no <c>.</c>, <c>..</c> or empty part, at most <see cref="MaxPathLength"/> characters.
    /// </summary>
    public static string? ShapeProblem(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? reason = null;
        if (path.Length == 0) reason = "it is empty";
        else if (path.Length > MaxPathLength) reason = $"it is {path.Length} characters long";
        else if (path.Contains('\\')) reason = "it contains a backslash";
        else if (path.Any(char.IsControl)) reason = "it holds a control character, such as a line break";
        else if (path.StartsWith('/')) reason = "it begins with a slash";
        else
        {
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0) { reason = "it has an empty part"; break; }
                if (part is "." or "..") { reason = $"it has a '{part}' part"; break; }
            }
        }
        return reason is null
            ? null
            : $"'{Shown(path.ReplaceLineEndings(" "))}' is not a path a definition can hold: {reason}. A path uses forward " +
              $"slashes, with no leading slash, no control character and no '.', '..' or empty part, and is at most {MaxPathLength} characters.";
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
