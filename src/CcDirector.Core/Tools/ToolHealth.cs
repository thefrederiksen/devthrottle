namespace CcDirector.Core.Tools;

/// <summary>
/// One tool's inputs for the home tool-health roll-up. <see cref="Passed"/> is only meaningful when
/// <see cref="IsBuilt"/> is true (a not-built tool ran no tests). <see cref="IsExpected"/> marks a tool
/// this install was meant to provide (shim or built), so a not-built-but-expected tool is a repairable
/// half-install rather than an optional/never-installed one.
/// </summary>
public readonly record struct ToolHealthInput(string Name, bool IsBuilt, bool IsExpected, bool Passed);

/// <summary>
/// Aggregate cc-* tool health for the home readiness: how many built tools pass their checks, how many
/// fail, and how many are not built - plus how many of the not-built ones are "broken" (expected here
/// but missing, i.e. repairable) versus simply optional/never-installed. The home alarms only on real
/// problems (a failing built tool or a broken one); optional not-built tools are shown but stay quiet.
/// </summary>
public sealed record ToolHealthSummary(
    int Pass, int Fail, int NotBuilt, int Broken, IReadOnlyList<string> Failing)
{
    /// <summary>Total tools considered (pass + fail + not-built).</summary>
    public int Total => Pass + Fail + NotBuilt;

    /// <summary>
    /// True when the home should warn: ANY tool that is not passing - a built tool whose test failed, or
    /// a not-built tool (broken half-install OR optional/never-installed). The home shows the true picture
    /// and routes to the Tools page rather than hiding not-built tools behind "all systems go".
    /// </summary>
    public bool HasProblem => Fail > 0 || NotBuilt > 0;

    public static ToolHealthSummary From(IEnumerable<ToolHealthInput> inputs)
    {
        int pass = 0, fail = 0, notBuilt = 0, broken = 0;
        var failing = new List<string>();
        foreach (var t in inputs)
        {
            if (!t.IsBuilt)
            {
                notBuilt++;
                if (t.IsExpected) broken++; // shim present, exe missing = repairable half-install
                continue;
            }
            if (t.Passed) pass++;
            else { fail++; failing.Add(t.Name); }
        }
        return new ToolHealthSummary(pass, fail, notBuilt, broken, failing);
    }
}
