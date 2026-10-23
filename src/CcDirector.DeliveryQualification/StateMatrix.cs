using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// One case of the working-or-waiting matrix (issue #4378). <paramref name="Kind"/> says what the truth is:
///  - <c>turn</c>: the agent works from the send until its answer is recorded, then waits;
///  - <c>pending</c>: the agent stops to ask (an approval, a question menu) and waits on the owner from the moment its
///    record shows the ask;
///  - <c>menu</c>: a picker opened by the owner's own slash command - nothing works, the agent waits from the start.
/// </summary>
public sealed record StateCase(string Name, string Kind, Func<string, string> Prompt, string[] Agents, bool PermissionsOn = false);

/// <summary>What the Director showed during one case, against the truth from the agent's records.</summary>
/// <param name="ToWorking">Seconds from the send until the Director first showed Working; null when it never did.</param>
/// <param name="FalseWaiting">Seconds the Director showed waiting while the agent was truly working.</param>
/// <param name="ToWaiting">Seconds from the true moment the agent started waiting until the Director showed it; null when
/// it never did within the watch.</param>
/// <param name="ShowedApproval">The Director showed WaitingForPerm at any point while an approval was pending.</param>
/// <param name="Timeline">Every state change, as state@seconds-since-send.</param>
public sealed record StateResult(
    string Agent, string Case, string Kind, string Truth, double? TrueWaitAt, double? ToWorking, double FalseWaiting, double? ToWaiting,
    bool ShowedApproval, string Timeline, bool Pass, string Detail);

/// <summary>
/// THE WORKING-OR-WAITING MATRIX (issue #4378; the owner, 23 October 2026: "a VERY robust way of knowing if the terminal
/// is actually working"). For each agent it drives turns whose truth is known - a quick answer, a long streamed answer, a
/// forty-second tool, an approval prompt, a question menu, the model picker - samples the session's
/// <see cref="ActivityState"/> every 250 ms, and sets it beside the truth from the agent's OWN records.
///
/// The pass rules are the mission's targets: Working within 2 seconds of the send; never waiting while the agent truly
/// works; waiting within 2 seconds of the true end; an approval shown as needing approval. The baseline is expected to
/// fail the last one everywhere - no production code sets WaitingForPerm today.
/// </summary>
public static class StateMatrix
{
    public static readonly TimeSpan Sample = TimeSpan.FromMilliseconds(250);
    public const double Tolerance = 2.0;

    /// <summary>How long the case is watched past the true moment: long enough to see the silence rule (10 seconds)
    /// flip it, so a late state is measured rather than reported as never.</summary>
    public static readonly TimeSpan ObserveAfter = TimeSpan.FromSeconds(30);

    private static string Ask(string t) => $"Reply with exactly: ACK {t}";

    public static readonly IReadOnlyList<StateCase> All =
    [
        new("quick", "turn", t => $"Token {t}. Do not run any tools or read any files. {Ask(t)}", ["claude", "codex", "pi"]),
        new("long-answer", "turn", t => $"Token {t}. Without using any tools, write about 300 words on the history of the bicycle, then end with the line: ACK {t}", ["claude", "codex", "pi"]),
        new("tool-40s", "turn", t => $"Token {t}. Run exactly this shell command and wait for it to finish: sleep 40 . Then {Ask(t)}", ["claude", "codex", "pi"]),
        new("approval", "pending", t => $"Token {t}. Use your shell tool to run exactly: touch approval-{t}.txt . Then {Ask(t)}", ["claude", "codex"], PermissionsOn: true),
        new("question-menu", "pending", t => $"Token {t}. Use the AskUserQuestion tool to ask me one question: red or blue. Do nothing else.", ["claude"]),
        new("model-picker", "menu", _ => "/model", ["claude", "codex", "pi"]),
    ];

    /// <summary>The arguments each agent runs with when the case needs permission prompts ON.</summary>
    private static readonly Dictionary<string, string> PermissionArgs = new()
    {
        ["claude"] = "--permission-mode default --model claude-haiku-4-5-20251001",
        ["codex"] = "-a untrusted -s read-only -m gpt-5.6-luna -c model_reasoning_effort=low",
    };

    public static async Task<int> RunAsync(SessionManager manager, AgentOptions agentOptions, Options opts)
    {
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var commit = Ledger.ProductCommit();
        var repo = Rig.EnsureRepo(Path.Combine(opts.RepoRoot, "repo-1"));
        var cases = opts.StateCaseNames is { } names
            ? names.Select(n => All.FirstOrDefault(c => c.Name == n) ?? throw new ArgumentException($"No state case named '{n}'.")).ToList()
            : All.ToList();
        var results = new List<StateResult>();
        var resultsPath = Path.Combine(opts.Out, "state-results.jsonl");
        Console.WriteLine($"[state] run {runId} on product {commit}: {cases.Count} cases x {opts.Agents.Count} agents. Results: {resultsPath}");
        using var desktop = new DesktopHost();

        foreach (var agent in opts.Agents)
        {
            if (!Program.Agents.TryGetValue(agent, out var a)) throw new ArgumentException($"Unknown agent '{agent}'.");
            var executable = agent switch { "claude" => agentOptions.ClaudePath, "codex" => agentOptions.CodexPath, "pi" => agentOptions.PiPath, _ => null } ?? agent;
            var version = Ledger.AgentVersion(executable);
            foreach (var c in cases.Where(c => c.Agents.Contains(agent)))
            {
                var args = c.PermissionsOn ? PermissionArgs[agent] : opts.ArgsOverride.TryGetValue(agent, out var o) ? o : a.Args;
                var session = manager.CreateSession(repo, a.Kind, string.IsNullOrWhiteSpace(args) ? null : args, SessionBackendType.ConPty, null);
                StateResult r;
                try
                {
                    await desktop.AttachAsync(session);
                    if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                        Console.WriteLine($"[state] {agent} {c.Name}: not waiting for input after 120s; starting anyway");
                    r = await RunCaseAsync(agent, c, session, desktop);
                }
                catch (Exception ex)
                {
                    r = new StateResult(agent, c.Name, c.Kind, "-", null, null, 0, null, false, "", false, $"RIG-ERROR {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[state] kill: {ex.Message}"); }
                    manager.RemoveSession(session.Id);
                }
                results.Add(r);
                File.AppendAllText(resultsPath, JsonSerializer.Serialize(r) + "\n");
                Ledger.Append(opts.LedgerPath, new LedgerEntry(runId, DateTime.UtcNow, commit, Environment.MachineName,
                    System.Runtime.InteropServices.RuntimeInformation.OSDescription, agent, version, $"state:{c.Name}", 0,
                    "OK", r.Pass ? "CORRECT" : "WRONG", r.Pass, r.ToWaiting ?? -1, $"{r.Detail} | {r.Timeline}", null, Route: "state"));
                Console.WriteLine($"[state] {agent,-7} {c.Name,-14} {(r.Pass ? "PASS" : "FAIL")} working-after={Fmt(r.ToWorking)} false-waiting={r.FalseWaiting:F1}s " +
                                  $"waiting-after-true-end={Fmt(r.ToWaiting)} approval-shown={(r.ShowedApproval ? "yes" : "no")} {r.Detail}");
                Console.WriteLine($"        {r.Timeline}");
            }
        }

        var summary = Summary(results);
        Console.WriteLine();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(opts.Out, "state-summary.txt"), summary);
        if (results.Count == 0) return 2;
        return results.All(r => r.Pass) ? 0 : 1;
    }

    private static string Fmt(double? s) => s is { } v ? $"{v:F1}s" : "never";

    private static async Task<StateResult> RunCaseAsync(string agent, StateCase c, Session session, DesktopHost desktop)
    {
        var token = EchoLayer.Token();
        var prompt = c.Prompt(token);
        var samples = new List<(double At, ActivityState State)>();
        var sentAt = DateTime.UtcNow;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                samples.Add(((DateTime.UtcNow - sentAt).TotalSeconds, session.ActivityState));
                try { await Task.Delay(Sample, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });

        // Typed and submitted as the owner would, through the desktop terminal.
        await desktop.TypeAsync(prompt);
        await desktop.EnterAsync();

        // THE TRUTH, from the agent's records: the moment it began waiting - its answer for a turn, its ask for a pending
        // case - or, for a picker the owner opened, the send itself.
        double? trueWaitAt = c.Kind == "menu" ? 1.0 : null;
        var watchUntil = DateTime.UtcNow.AddSeconds(c.Name == "tool-40s" ? 150 : 90);
        while (trueWaitAt is null && DateTime.UtcNow < watchUntil)
        {
            var at = c.Kind == "turn" ? AnswerRecordedAt(agent, sentAt, token) : AskRecordedAt(agent, sentAt, token, c.Name);
            if (at is { } t) trueWaitAt = Math.Max(0, (t - sentAt).TotalSeconds);
            else await Task.Delay(500);
        }
        // Watch on past the true moment, so a late or a wrong state is seen.
        var observeUntil = sentAt.AddSeconds((trueWaitAt ?? (DateTime.UtcNow - sentAt).TotalSeconds) + ObserveAfter.TotalSeconds);
        while (DateTime.UtcNow < observeUntil) await Task.Delay(250);
        stop.Cancel();
        await sampler;

        // Close whatever the case opened (the approval, the menu, the picker), so the session ends cleanly.
        if (c.Kind != "turn") await session.CancelTurnAsync();

        var timeline = Timeline(samples);
        if (trueWaitAt is null)
            return new StateResult(agent, c.Name, c.Kind, "unknown", null, null, 0, null, false, timeline, false,
                "RIG: the records never showed the answer or the ask, so there is no truth to judge against");
        return Judge(agent, c, trueWaitAt.Value, samples, timeline);
    }

    /// <summary>Judge the samples against the truth. Pure, so it is tested without an agent.</summary>
    internal static StateResult Judge(string agent, StateCase c, double trueWaitAt, IReadOnlyList<(double At, ActivityState State)> samples, string timeline)
    {
        static bool Waiting(ActivityState s) => s is ActivityState.WaitingForInput or ActivityState.WaitingForPerm or ActivityState.Idle;
        double? toWorking = samples.FirstOrDefault(s => s.State == ActivityState.Working) is var w && w.State == ActivityState.Working ? w.At : null;
        var falseWaiting = 0.0;
        if (c.Kind != "menu")
            for (var i = 1; i < samples.Count; i++)
                // Every waiting sample from the end of the start tolerance up to the true moment counts: the mission's
                // target is zero false waiting, so no margin is taken before the truth (review of #4390, finding 2).
                if (samples[i].At > Tolerance && samples[i].At < trueWaitAt && Waiting(samples[i].State))
                    falseWaiting += samples[i].At - samples[i - 1].At;
        var firstWaiting = samples.FirstOrDefault(s => s.At >= trueWaitAt && Waiting(s.State));
        double? toWaiting = Waiting(firstWaiting.State) && firstWaiting.At >= trueWaitAt ? Math.Round(firstWaiting.At - trueWaitAt, 2) : null;
        var showedApproval = samples.Any(s => s.At >= trueWaitAt && s.State == ActivityState.WaitingForPerm);

        var failures = new List<string>();
        if (c.Kind != "menu" && (toWorking is null || toWorking > Tolerance)) failures.Add("not shown Working within 2s of the send");
        if (falseWaiting > 0) failures.Add($"shown waiting for {falseWaiting:F1}s while the agent worked");
        if (toWaiting is null || toWaiting > Tolerance) failures.Add("not shown waiting within 2s of the true end");
        if (c.Name == "approval" && !showedApproval) failures.Add("the approval was never shown as needing approval");
        var truth = c.Kind switch { "turn" => "worked, then waited", "pending" => "asked, then waited on the owner", _ => "a picker the owner opened" };
        return new StateResult(agent, c.Name, c.Kind, truth, Math.Round(trueWaitAt, 2), toWorking is { } tw ? Math.Round(tw, 2) : null,
            Math.Round(falseWaiting, 2), toWaiting, showedApproval, timeline, failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>The state changes only, as state@seconds.</summary>
    internal static string Timeline(IReadOnlyList<(double At, ActivityState State)> samples)
    {
        var sb = new StringBuilder();
        ActivityState? last = null;
        foreach (var (at, state) in samples)
        {
            if (state == last) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(state).Append('@').Append(at.ToString("F1", CultureInfo.InvariantCulture));
            last = state;
        }
        return sb.ToString();
    }

    /// <summary>When the agent's ANSWER was recorded: the latest non-user record holding "ACK token".</summary>
    private static DateTime? AnswerRecordedAt(string agent, DateTime sinceUtc, string token) =>
        RecordTimes(agent, sinceUtc, $"ACK {token}", root => !AgentRecords.IsUserTurn(agent, root)).DefaultIfEmpty().Max() is var t && t != default ? t : null;

    /// <summary>When the agent's ASK was recorded: the earliest non-user record that names the tool and holds the token
    /// (the approval's command names it) or, for the question menu, the AskUserQuestion call.</summary>
    private static DateTime? AskRecordedAt(string agent, DateTime sinceUtc, string token, string caseName)
    {
        var needle = caseName == "question-menu" ? "AskUserQuestion" : $"approval-{token}";
        var times = RecordTimes(agent, sinceUtc, needle, root => !AgentRecords.IsUserTurn(agent, root)).ToList();
        return times.Count == 0 ? null : times.Min();
    }

    /// <summary>The timestamps of the agent's records after <paramref name="sinceUtc"/> that hold <paramref name="needle"/>
    /// and pass <paramref name="keep"/>. Every agent stamps each record with an ISO time or Unix milliseconds.</summary>
    private static IEnumerable<DateTime> RecordTimes(string agent, DateTime sinceUtc, string needle, Func<JsonElement, bool> keep)
    {
        foreach (var line in AgentRecords.RecentLines(agent, sinceUtc))
        {
            if (!line.Contains(needle, StringComparison.Ordinal)) continue;
            DateTime? at = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !keep(root)) continue;
                at = Stamp(root);
            }
            catch (JsonException) { continue; }
            if (at is { } t && t >= sinceUtc.AddSeconds(-1)) yield return t;
        }
    }

    internal static DateTime? Stamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts)) return null;
        return ts.ValueKind switch
        {
            JsonValueKind.String when DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) => d,
            JsonValueKind.Number when ts.TryGetInt64(out var ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            _ => null,
        };
    }

    internal static string Summary(IReadOnlyList<StateResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"agent",-8} {"case",-14} {"pass",-5} {"working-after",13} {"false-waiting",13} {"waiting-after-end",17} approval-shown");
        foreach (var r in results)
            sb.AppendLine($"{r.Agent,-8} {r.Case,-14} {(r.Pass ? "yes" : "NO"),-5} {Fmt(r.ToWorking),13} {r.FalseWaiting,12:F1}s {Fmt(r.ToWaiting),17} {(r.Kind == "pending" ? (r.ShowedApproval ? "yes" : "no") : "-")}");
        var pass = results.Count(r => r.Pass);
        sb.AppendLine($"{pass} of {results.Count} cases correct.");
        foreach (var r in results.Where(r => !r.Pass))
            sb.AppendLine($"  {r.Agent} {r.Case}: {r.Detail}");
        return sb.ToString();
    }
}
