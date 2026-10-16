using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CcDirector.DeliveryQualification;

/// <summary>One line of the ledger: one attempt of one scenario, with what the product and the agent were.</summary>
public sealed record LedgerEntry(
    string RunId, DateTime AtUtc, string ProductCommit, string Machine, string Os, string Agent, string AgentVersion,
    string Scenario, int Length, string Director, string Truth, bool Pass, double Seconds, string Detail, string? Screen);

/// <summary>
/// THE DELIVERY LEDGER. Every scenario attempt of every run is appended to one file, so a delivery success rate can be
/// measured over many runs and compared before and after a change: the rate for a product commit is the passes over the
/// attempts made on it. A setup failure - a scenario that could not create its precondition - is kept in the ledger but
/// counted in no rate, because it says nothing about delivery.
/// </summary>
public static class Ledger
{
    public static void Append(string path, LedgerEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + "\n");
    }

    public static List<LedgerEntry> Read(string path) =>
        !File.Exists(path)
            ? []
            : File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonSerializer.Deserialize<LedgerEntry>(l)!).ToList();

    /// <summary>
    /// The success rate per product commit, agent and scenario, each with its 95 per cent interval (Wilson), and a line
    /// per agent over every scenario. A rate over few attempts has a wide interval and the table says so; two rates
    /// whose intervals overlap have not been shown to differ.
    /// </summary>
    public static string Rates(IReadOnlyList<LedgerEntry> entries)
    {
        var sb = new StringBuilder();
        var counted = Counted(entries);
        sb.AppendLine($"{counted.Count} attempts counted ({entries.Count - counted.Count} setup failures not counted).");
        foreach (var commit in counted.GroupBy(e => e.ProductCommit).OrderBy(g => g.Min(e => e.AtUtc)))
        {
            sb.AppendLine();
            sb.AppendLine($"product {commit.Key}  ({commit.Min(e => e.AtUtc):yyyy-MM-dd HH:mm} to {commit.Max(e => e.AtUtc):HH:mm} UTC)");
            sb.AppendLine($"  {"agent",-8} {"scenario",-18} {"pass",5} {"of",4} {"rate",6}  {"95% interval",-13}  failures (director/truth)");
            foreach (var g in commit.GroupBy(e => (e.Agent, e.Scenario)).OrderBy(g => g.Key.Agent).ThenBy(g => Order(g.Key.Scenario)))
                sb.AppendLine(Line(g.Key.Agent, g.Key.Scenario, g.ToList()));
            foreach (var g in commit.GroupBy(e => e.Agent).OrderBy(g => g.Key))
                sb.AppendLine(Line(g.Key, "ALL SCENARIOS", g.ToList()));
        }
        return sb.ToString();
    }

    /// <summary>The attempts a rate counts: every one but a setup failure, which ended before any prompt was sent.</summary>
    public static List<LedgerEntry> Counted(IEnumerable<LedgerEntry> entries) => entries.Where(e => e.Director != "SETUP-FAILED").ToList();

    private static int Order(string scenario)
    {
        var i = Scenarios.All.Select(s => s.Name).ToList().IndexOf(scenario);
        return i < 0 ? int.MaxValue : i;
    }

    private static string Line(string agent, string scenario, List<LedgerEntry> list)
    {
        var pass = list.Count(e => e.Pass);
        var (lo, hi) = Wilson(pass, list.Count);
        var failures = string.Join(", ", list.Where(e => !e.Pass).GroupBy(e => $"{e.Director}/{e.Truth}").Select(f => $"{f.Count()}x {f.Key}"));
        return $"  {agent,-8} {scenario,-18} {pass,5} {list.Count,4} {100.0 * pass / list.Count,5:F0}%  {100 * lo,4:F0}% - {100 * hi,3:F0}%   {failures}";
    }

    /// <summary>The Wilson score interval at 95 per cent for <paramref name="pass"/> of <paramref name="n"/>.</summary>
    internal static (double Low, double High) Wilson(int pass, int n)
    {
        if (n == 0) return (0, 1);
        const double z = 1.96;
        var p = (double)pass / n;
        var denominator = 1 + z * z / n;
        var centre = (p + z * z / (2 * n)) / denominator;
        var half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / denominator;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>
    /// The commit the rig was BUILT from, with "+local-changes" when the product's send path (Core or ControlApi) had
    /// uncommitted changes at build time - stamped by the project file, so checking out another branch after the build
    /// cannot change what the ledger says was measured (review of pull request 4069, finding 1).
    /// </summary>
    public static string ProductCommit() => BuildCommit.Value.Length > 0 ? BuildCommit.Value : "unknown";

    /// <summary>The agent's own version line, or "unknown".</summary>
    public static string AgentVersion(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            if (!p.WaitForExit(15000)) { p.Kill(); return "unknown"; }
            return line.Length > 0 ? line : "unknown";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }
}
