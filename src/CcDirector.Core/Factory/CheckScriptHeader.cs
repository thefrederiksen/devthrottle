using System.Text.RegularExpressions;

namespace CcDirector.Core.Factory;

/// <summary>
/// READS A CHECK'S INLINE SCRIPT HEADER (PEP 723) for the one thing the Director needs from it: does the check
/// declare packages? A check that does runs under <c>uv run --script</c>, which installs them; one that does not runs
/// under plain Python (Factory Definition, phase 5b, design 2.3).
///
/// The header is the block that opens with a line <c># /// script</c> and closes with a line <c># ///</c>; each line in
/// between is a comment holding one line of TOML. Packages are declared when its <c>dependencies</c> array holds at
/// least one entry. A block that opens and never closes is a broken header, said so, never guessed at.
///
/// It lives in Core because two places read one header and must read it alike: the Director's check runner, which
/// chooses the interpreter, and the Gateway's publish, which asks the seat's machine for Python or for uv by it.
/// </summary>
public static partial class CheckScriptHeader
{
    /// <summary>What the header says.</summary>
    /// <param name="Error">Why the header cannot be read, or null.</param>
    public sealed record Reading(bool DeclaresPackages, string? Error);

    [GeneratedRegex(@"^\s*dependencies\s*=\s*\[", RegexOptions.CultureInvariant)]
    private static partial Regex DependenciesKey();

    [GeneratedRegex(@"[""']", RegexOptions.CultureInvariant)]
    private static partial Regex AnyQuotedEntry();

    public static Reading Read(string scriptText)
    {
        ArgumentNullException.ThrowIfNull(scriptText);
        var lines = scriptText.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "# /// script");
        if (start < 0) return new Reading(false, null);

        var toml = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line == "# ///") return new Reading(DeclaresPackagesIn(toml), null);
            if (line == "#") toml.Add("");
            else if (line.StartsWith("# ", StringComparison.Ordinal)) toml.Add(line[2..]);
            else
                return new Reading(false,
                    $"the check's script header opened with '# /// script' on line {start + 1} and line {i + 1} is not " +
                    "a comment, so the header never closes with '# ///'");
        }
        return new Reading(false,
            $"the check's script header opened with '# /// script' on line {start + 1} and never closes with '# ///'");
    }

    private static bool DeclaresPackagesIn(List<string> toml)
    {
        for (var i = 0; i < toml.Count; i++)
        {
            var match = DependenciesKey().Match(toml[i]);
            if (!match.Success) continue;
            // The array may span lines; read to its closing bracket. Comments in it never hold a quote that matters,
            // so a TOML comment is cut before looking.
            var body = new System.Text.StringBuilder(toml[i][match.Length..]);
            for (var j = i + 1; !body.ToString().Contains(']') && j < toml.Count; j++) body.Append('\n').Append(toml[j]);
            var inside = body.ToString();
            var close = inside.IndexOf(']');
            if (close >= 0) inside = inside[..close];
            var withoutComments = string.Join('\n', inside.Split('\n').Select(l => l.Split('#')[0]));
            return AnyQuotedEntry().IsMatch(withoutComments);
        }
        return false;
    }
}
