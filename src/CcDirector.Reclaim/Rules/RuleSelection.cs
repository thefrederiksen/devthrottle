using CcDirector.Core.Utilities;

namespace CcDirector.Reclaim.Rules;

/// <summary>A rule that was not run, and where it looks instead.</summary>
/// <param name="RuleId">The rule's identifier.</param>
/// <param name="RuleName">The rule's name as a person reads it.</param>
/// <param name="LooksIn">The folder it looks in.</param>
public sealed record RuleNotRun(string RuleId, string RuleName, string LooksIn);

/// <summary>
/// Which of the machine's rules apply to the folder a caller asked about.
///
/// A rule looks at one known place. Asked about one folder, the tool runs the rules whose place is
/// inside it and leaves the others out - otherwise a question about a small folder would be answered
/// with gigabytes found somewhere else entirely, and every number built from the two together would
/// be meaningless.
///
/// A rule left out is NAMED, with the folder it looks in. A tool that quietly ran fewer rules than
/// it has would report less to remove and look exactly like a cleaner disk, which is the failure
/// this whole mission is built to avoid.
/// </summary>
public static class RuleSelection
{
    /// <summary>What a selection produced: the rules to run, and the ones left out with their reason.</summary>
    /// <param name="ToRun">The rules whose folder is inside the folder asked about.</param>
    /// <param name="NotRun">The rules left out, each with the folder it looks in.</param>
    public sealed record Selection(IReadOnlyList<IReclaimRule> ToRun, IReadOnlyList<RuleNotRun> NotRun);

    /// <summary>
    /// Split the machine's rules into the ones that apply to this folder and the ones that do not.
    /// </summary>
    /// <param name="rules">Every rule the machine has.</param>
    /// <param name="rootPath">The folder the caller asked about, in its canonical full form.</param>
    public static Selection For(IReadOnlyList<IReclaimRule> rules, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        FileLog.Write($"[RuleSelection] For: rules={rules.Count}, root={rootPath}");

        var toRun = new List<IReclaimRule>();
        var notRun = new List<RuleNotRun>();

        foreach (var rule in rules)
        {
            if (IsInside(rule.LooksIn, rootPath))
                toRun.Add(rule);
            else
                notRun.Add(new RuleNotRun(rule.Id, rule.Name, rule.LooksIn));
        }

        FileLog.Write($"[RuleSelection] For done: toRun={toRun.Count}, notRun={notRun.Count}");
        return new Selection(toRun, notRun);
    }

    /// <summary>
    /// True when one folder is the other or sits inside it.
    ///
    /// Compared without regard to letter case, which is how Windows and a default macOS volume
    /// compare two folder names, and with a separator forced onto the end of the root so that
    /// C:\Users\bobby is not read as sitting inside C:\Users\bob.
    /// </summary>
    /// <param name="candidate">The folder being asked about.</param>
    /// <param name="root">The folder it might sit inside.</param>
    public static bool IsInside(string candidate, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var left = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var right = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (left.Equals(right, comparison)) return true;

        // A volume root trims down to nothing on a system whose separator is the whole name of the
        // root, and everything is inside that.
        if (right.Length == 0) return true;

        return left.StartsWith(right + Path.DirectorySeparatorChar, comparison);
    }
}
