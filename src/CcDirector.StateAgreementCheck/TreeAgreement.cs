using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.StateAgreementCheck;

/// <summary>
/// THE OWNERSHIP TREE, CHECKED ACROSS THE TWO LANGUAGES THAT FOLD IT.
///
/// The tree, the descendants, the crew summary and the attention order exist twice - in TypeScript
/// (packages/client-core/src/sessions/tree.ts, shipped first for the Cockpit and the phone) and in C#
/// (<see cref="SessionTree"/>, ported for the Director rail). Two folds of one question drift, which is
/// the defect this mission exists to stop, so it must not be the shape of its own fix.
///
/// NEITHER IMPLEMENTATION OWNS THE ANSWER. A shared file does:
/// <c>packages/client-core/src/sessions/tree-agreement.json</c>, which states the sessions and the answers
/// once. This class runs the REAL C# fold over those sessions and compares; tree.agreement.test.ts runs
/// the REAL TypeScript fold over the same file and compares to the same answers. Change either fold alone
/// and that language goes red. Change a fold and the file together and the OTHER language goes red. There
/// is no way to move one side quietly, which is the whole point.
///
/// IT ALSO CHECKS THE BRIDGE, not just the answers. The two folds take different input - the browser reads
/// the Gateway's stamped triageBucket, the C# calls <see cref="SessionOrdering.Classify"/> and folds the
/// same answer from raw facts - so a fixture could satisfy both files while saying two different things
/// about one session. Every session is therefore checked: the fold this side computes must equal the stamp
/// the other side reads.
///
/// FAILS LOUDLY RATHER THAN RETURNING AN EMPTY LIST. A missing or unreadable fixture file, or one that
/// parses to zero cases, throws. An agreement check that quietly reports "no disagreements" because it
/// examined nothing is worse than no check at all, and this repository has shipped that exact thing.
/// </summary>
public static class TreeAgreement
{
    /// <summary>The shared statement of the answers, read by both languages.</summary>
    public const string RelativePath = "packages/client-core/src/sessions/tree-agreement.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One way the C# fold disagrees with the shared answers, named by the case and the question.</summary>
    public sealed record Finding(string Case, string Question, string Expected, string Actual)
    {
        public override string ToString() => $"TREE DISAGREEMENT [{Case}] {Question}\n    expected: {Expected}\n    actual:   {Actual}";
    }

    /// <summary>The number of cases the last <see cref="Check(string)"/> examined, so a caller can state
    /// the scope of its verdict instead of printing a bare zero.</summary>
    public sealed record Result(int Cases, int Sessions, IReadOnlyList<Finding> Findings);

    /// <summary>Run the C# fold over the shared fixtures and report every answer that differs.</summary>
    public static Result Check(string repoRoot)
    {
        var path = Path.Combine(repoRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"The shared tree answers were not found at '{path}'. The cross-language tree check cannot " +
                "run without the file both folds are measured against - it must never be assumed or copied.",
                path);

        return Check(JsonDocument.Parse(File.ReadAllText(path)));
    }

    /// <summary>
    /// The comparison itself, over an already-parsed document, so a test can hand it a deliberately broken
    /// fixture and WATCH this report the fault. A check nobody has seen fail is worth nothing.
    /// </summary>
    public static Result Check(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"{RelativePath} has no 'cases' array. Refusing to report agreement from a file this check " +
                "could not read - that is a check that cannot fail.");

        var caseList = cases.EnumerateArray().ToList();
        if (caseList.Count == 0)
            throw new InvalidOperationException(
                $"{RelativePath} parsed to ZERO cases. Refusing to report agreement over nothing.");

        var findings = new List<Finding>();
        var sessionCount = 0;

        foreach (var one in caseList)
        {
            var name = one.GetProperty("name").GetString() ?? "(unnamed case)";
            var sessions = one.GetProperty("sessions").EnumerateArray()
                .Select(e => e.Deserialize<SessionDto>(Json)
                             ?? throw new InvalidOperationException($"A session in case '{name}' did not deserialize."))
                .ToList();
            sessionCount += sessions.Count;

            CheckTheBridge(name, sessions, one, findings);

            var tree = SessionTree.Build(sessions);
            var byId = sessions.ToDictionary(s => s.SessionId, StringComparer.Ordinal);

            Compare(findings, name, "roots",
                Expected(one, "roots"),
                Ids(tree.Roots));

            foreach (var expected in one.GetProperty("children").EnumerateObject())
            {
                Compare(findings, name, $"children of {expected.Name}",
                    expected.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToList(),
                    Ids(SessionTree.ChildrenOf(tree, byId[expected.Name])));
            }

            // A parent the file does NOT list must have no children: otherwise a fold that invented an
            // extra crew would pass, because nothing would ask about it.
            var listed = one.GetProperty("children").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var actualParents = sessions.Where(s => SessionTree.ChildrenOf(tree, s).Count > 0).Select(s => s.SessionId).ToList();
            Compare(findings, name, "the sessions that have children at all",
                listed.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                actualParents.OrderBy(x => x, StringComparer.Ordinal).ToList());

            foreach (var crew in one.GetProperty("crews").EnumerateArray())
            {
                var rootId = crew.GetProperty("root").GetString() ?? "";
                var root = byId[rootId];
                var descendants = SessionTree.DescendantsOf(tree, root);

                Compare(findings, name, $"descendants of {rootId}",
                    crew.GetProperty("descendants").EnumerateArray().Select(v => v.GetString() ?? "").ToList(),
                    descendants.Select(d => $"{d.Session.SessionId}@{d.Parent.SessionId}@{d.Depth}").ToList());

                var summary = SessionTree.SummarizeCrew(root, descendants.Select(d => d.Session));
                Compare(findings, name, $"crew counts of {rootId}",
                    $"count={crew.GetProperty("count").GetInt32()} needsYou={crew.GetProperty("needsYou").GetInt32()} " +
                    $"working={crew.GetProperty("working").GetInt32()} stopped={crew.GetProperty("stopped").GetInt32()}",
                    $"count={summary.Count} needsYou={summary.NeedsYou} working={summary.Working} stopped={summary.Stopped}");

                Compare(findings, name, $"crew oldest session of {rootId}",
                    crew.GetProperty("since").GetString() ?? "null",
                    summary.Since is { } since ? since.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : "null");

                Compare(findings, name, $"crew line of {rootId}",
                    crew.GetProperty("line").GetString() ?? "",
                    SessionTree.CrewSummaryLine(summary));

                var ageAt = DateTime.Parse(crew.GetProperty("ageAt").GetString() ?? "",
                    null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                Compare(findings, name, $"crew age of {rootId} at {crew.GetProperty("ageAt").GetString()}",
                    crew.GetProperty("age").GetString() ?? "",
                    SessionTree.CrewAge(summary, ageAt));
            }

            if (one.TryGetProperty("onAnotherMachine", out var elsewhere))
            {
                foreach (var pair in elsewhere.EnumerateObject())
                {
                    var ends = pair.Name.Split('>');
                    Compare(findings, name, $"is {ends[1]} on another Director than {ends[0]}",
                        pair.Value.GetBoolean().ToString(),
                        SessionTree.IsOnAnotherMachine(byId[ends[0]], byId[ends[1]]).ToString());
                }
            }

            var sections = SessionTree.AttentionSections(tree.Roots);
            Compare(findings, name, "attention sections",
                one.GetProperty("attention").EnumerateArray().Select(a => a.GetProperty("title").GetString() ?? "").ToList(),
                sections.Select(s => s.Title).ToList());

            foreach (var (expected, index) in one.GetProperty("attention").EnumerateArray().Select((a, i) => (a, i)))
            {
                if (index >= sections.Count) break;
                Compare(findings, name, $"attention section '{expected.GetProperty("title").GetString()}'",
                    expected.GetProperty("roots").EnumerateArray().Select(v => v.GetString() ?? "").ToList(),
                    Ids(sections[index].Roots));
            }
        }

        return new Result(caseList.Count, sessionCount, findings);
    }

    /// <summary>
    /// THE BRIDGE BETWEEN THE TWO LANGUAGES' INPUTS. The browser reads the stamped answer; this side folds
    /// its own from raw facts. If a fixture's stamp and its raw facts disagree, both files can pass while
    /// describing two different sessions - so the stamp is checked against the fold, per session, here.
    /// </summary>
    private static void CheckTheBridge(string name, IReadOnlyList<SessionDto> sessions, JsonElement one, List<Finding> findings)
    {
        var raw = one.GetProperty("sessions").EnumerateArray().ToList();
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var stampedBucket = raw[i].TryGetProperty("triageBucket", out var b) ? b.GetString() ?? "" : "";
            var stampedColor = raw[i].TryGetProperty("effectiveColor", out var c) ? c.GetString() ?? "" : "";

            var folded = SessionOrdering.Classify(s) switch
            {
                SessionOrdering.TriageBucket.NeedsYou => "needsYou",
                SessionOrdering.TriageBucket.OnHold => "onHold",
                _ => "active",
            };
            Compare(findings, name, $"the triage bucket of {s.SessionId} (the stamp the browser reads versus the fold this side computes)",
                stampedBucket, folded);
            Compare(findings, name, $"the effective colour of {s.SessionId} (the stamp the browser reads versus the fold this side computes)",
                stampedColor, SessionOrdering.EffectiveColor(s));
        }
    }

    private static List<string> Expected(JsonElement one, string property) =>
        one.GetProperty(property).EnumerateArray().Select(v => v.GetString() ?? "").ToList();

    private static List<string> Ids(IEnumerable<SessionDto> sessions) => sessions.Select(s => s.SessionId).ToList();

    private static void Compare(List<Finding> findings, string caseName, string question,
        IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        if (expected.SequenceEqual(actual, StringComparer.Ordinal)) return;
        findings.Add(new Finding(caseName, question, $"[{string.Join(", ", expected)}]", $"[{string.Join(", ", actual)}]"));
    }

    private static void Compare(List<Finding> findings, string caseName, string question, string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        findings.Add(new Finding(caseName, question, expected, actual));
    }
}
