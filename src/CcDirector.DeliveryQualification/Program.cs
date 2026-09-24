using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE TEXT-DELIVERY QUALIFICATION RIG (issue #3290).
///
/// Starts real agents in real terminals through the Director's own <see cref="SessionManager"/>, with the same
/// pointer watcher and terminal-state detector the Director's host wires, and sends every text shape in
/// <see cref="TextCatalogue"/> through the Director's own send paths:
///  - FIRST: a new session's first prompt, <see cref="Session.DeliverPrePromptAsync"/> - what a spawn, a schedule
///    and a restore call;
///  - IDLE: a later prompt to a waiting session, <see cref="Session.SendTextAsync"/> - what typing, the phone and
///    dictation reach.
/// Each send is then judged by the agent's OWN records (<see cref="AgentRecords"/>), and the Director's verdict is
/// set beside the truth, so a send the Director called delivered that never arrived - the silent loss - is its own
/// column.
///
/// Usage:
///   CcDirector.DeliveryQualification --agents claude,codex,pi [--sessions 2] [--parallel 3] [--shapes all]
///                                    [--out DIR] [--first-wait 30] [--args-claude "..."]
/// </summary>
public static class Program
{
    private static readonly Dictionary<string, (AgentKind Kind, string Args)> Agents = new()
    {
        ["claude"] = (AgentKind.ClaudeCode, "--dangerously-skip-permissions --model claude-haiku-4-5-20251001"),
        ["codex"] = (AgentKind.Codex, "--dangerously-bypass-approvals-and-sandbox -m gpt-5.6-luna -c model_reasoning_effort=low"),
        ["pi"] = (AgentKind.Pi, "--model deepinfra-mindzie/zai-org/GLM-5.3-Flash --thinking off"),
        ["gemini"] = (AgentKind.Gemini, "--yolo"),
        ["grok"] = (AgentKind.Grok, ""),
        ["opencode"] = (AgentKind.OpenCode, ""),
        ["copilot"] = (AgentKind.Copilot, ""),
    };

    public static int Main(string[] args)
    {
        // --check <agent> <since-utc-iso> <text-file>: run the records witness alone, for diagnosing the witness.
        if (args.Length == 4 && args[0] == "--check")
        {
            var v = AgentRecords.Check(args[1], DateTime.Parse(args[2]).ToUniversalTime(), File.ReadAllText(args[3]),
                Enumerable.Range(1, 4).Select(i => $@"D:\ReposFred\_delivery-qa\repo-{i}").ToList());
            Console.WriteLine(v);
            return v.Found ? 0 : 1;
        }

        var opts = Options.Parse(args);
        AgentRecords.RigStartedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(opts.Out);

        // A PRIVATE DIRECTOR HOME, set before any Director type is touched: the rig's logs, its session-pointer box
        // and its state live here and never in the owner's live Director. The fleet identity of whatever session
        // launched the rig is removed, so the agents it starts are not taken for that session.
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", Path.Combine(opts.Out, "director-root"));
        foreach (var v in new[] { "CC_SESSION_ID", "CC_DIRECTOR_ID", "CC_GATEWAY_URL", "CC_GATEWAY_SESSION_KEY", "CLAUDECODE" })
            Environment.SetEnvironmentVariable(v, null);

        CcDirector.Core.Utilities.FileLog.Start();
        try
        {
            return RunAsync(opts).GetAwaiter().GetResult();
        }
        finally
        {
            CcDirector.Core.Utilities.FileLog.Stop();
        }
    }

    private static async Task<int> RunAsync(Options opts)
    {
        var agentOptions = DirectorAgentPaths.Load();
        using var manager = new SessionManager(agentOptions) { DirectorId = "delivery-qa-rig" };
        using var watcher = new SessionPointerWatcher(manager);
        watcher.Start();
        manager.OnSessionRemoved += s => watcher.Forget(s.Id);
        using var detector = new TerminalStateDetector(manager, driveState: true);
        detector.Start();

        if (opts.CharsAgent is { } charsAgent)
        {
            var (ck, ca) = Agents[charsAgent];
            await CharsExperiment.RunAsync(manager, charsAgent, ck, opts.ArgsOverride.TryGetValue(charsAgent, out var cca) ? cca : ca,
                Rig.EnsureRepo(Path.Combine(opts.RepoRoot, "repo-1")));
            return 0;
        }

        if (opts.KeysAgent is { } keysAgent)
        {
            var (k, a) = Agents[keysAgent];
            await ClearKeyExperiment.RunAsync(manager, keysAgent, k, opts.ArgsOverride.TryGetValue(keysAgent, out var ka) ? ka : a,
                Rig.EnsureRepo(Path.Combine(opts.RepoRoot, "repo-1")));
            return 0;
        }

        var repos = Enumerable.Range(1, opts.Parallel).Select(i => Rig.EnsureRepo(Path.Combine(opts.RepoRoot, $"repo-{i}"))).ToList();
        var freeRepos = new System.Collections.Concurrent.ConcurrentQueue<string>(repos);
        var slots = new SemaphoreSlim(opts.Parallel);
        var results = new List<SendResult>();
        var resultsPath = Path.Combine(opts.Out, "results.jsonl");

        var plans = new List<(string Agent, int Index)>();
        foreach (var agent in opts.Agents)
            for (var i = 0; i < opts.Sessions; i++)
                plans.Add((agent, i));

        Console.WriteLine($"[rig] {plans.Count} sessions, {opts.Shapes.Count} texts each, {opts.Parallel} at once. Results: {resultsPath}");
        var tasks = plans.Select(async plan =>
        {
            await slots.WaitAsync();
            freeRepos.TryDequeue(out var repo);
            try
            {
                var order = opts.Shapes.Skip(plan.Index % opts.Shapes.Count).Concat(opts.Shapes.Take(plan.Index % opts.Shapes.Count)).ToList();
                var sessionResults = await RunSessionAsync(manager, plan.Agent, repo!, order, opts, repos);
                lock (results)
                {
                    results.AddRange(sessionResults);
                    File.AppendAllLines(resultsPath, sessionResults.Select(r => JsonSerializer.Serialize(r)));
                }
            }
            finally
            {
                freeRepos.Enqueue(repo!);
                slots.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);

        Report.Print(results);
        File.WriteAllText(Path.Combine(opts.Out, "summary.txt"), Report.Render(results));
        return results.All(r => r.Truth == "ARRIVED" && r.Director == "OK") ? 0 : 1;
    }

    private static async Task<List<SendResult>> RunSessionAsync(
        SessionManager manager, string agent, string repo, List<TextShape> order, Options opts, IReadOnlyList<string> repos)
    {
        var results = new List<SendResult>();
        var (kind, defaultArgs) = Agents[agent];
        var userArgs = opts.ArgsOverride.TryGetValue(agent, out var a) ? a : defaultArgs;
        Session session;
        try
        {
            session = manager.CreateSession(repo, kind, string.IsNullOrWhiteSpace(userArgs) ? null : userArgs, SessionBackendType.ConPty, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[rig] {agent}: could not start the agent: {ex.Message}");
            results.Add(new SendResult(agent, "start", "-", 0, "START-FAILED", ex.Message, "NOT-SENT", false, false, 0, repo, null));
            return results;
        }

        try
        {
            for (var i = 0; i < order.Count; i++)
            {
                var path = i == 0 ? "first" : "idle";
                if (path == "idle" && !await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                    Console.WriteLine($"[rig] {agent} {session.Id.ToString()[..8]}: still busy after 120s; sending anyway");

                var token = $"Q{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";
                var text = order[i].Build(token);
                var sentAt = DateTime.UtcNow;
                var sw = Stopwatch.StartNew();
                string director = "OK", detail = "";
                try
                {
                    var send = path == "first"
                        ? session.DeliverPrePromptAsync(text, TimeSpan.FromSeconds(opts.FirstWaitSeconds))
                        : session.SendTextAsync(text, SubmissionProvenance.Typed("delivery-qa-rig", "rig"), SendSource.UserInput);
                    using var progress = new CancellationTokenSource();
                    _ = Task.Run(async () =>
                    {
                        while (!progress.IsCancelled())
                        {
                            await Task.Delay(TimeSpan.FromSeconds(15));
                            if (progress.IsCancelled()) break;
                            var rows = session.SnapshotScreenRows().Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(4);
                            Console.WriteLine($"[rig]   ...{agent} {session.Id.ToString()[..8]} still sending after {sw.Elapsed.TotalSeconds:F0}s: " +
                                              $"state={session.ActivityState} bytes={session.Buffer?.TotalBytesWritten ?? -1} screen: {string.Join(" | ", rows)}");
                        }
                    });
                    var done = await Task.WhenAny(send, Task.Delay(TimeSpan.FromMinutes(12)));
                    progress.Cancel();
                    if (done != send) { director = "HUNG"; detail = "the send did not return within 12 minutes"; }
                    else await send;
                }
                catch (Exception ex)
                {
                    director = "FAILED";
                    detail = $"{ex.GetType().Name}: {ex.Message}";
                }
                var directorSeconds = sw.Elapsed.TotalSeconds;

                // THE TRUTH: the agent's own records. Given time to be written, since a turn can start slowly.
                RecordsVerdict verdict = new(false, false, false, false, null);
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    verdict = AgentRecords.Check(agent, sentAt, text, repos);
                    if (verdict.Found) break;
                    await Task.Delay(1000);
                }
                var truth = verdict.Found ? (verdict.Doubled ? "DOUBLED" : "ARRIVED") : verdict.Partial ? "PARTIAL" : "LOST";
                string? screen = null;
                if (truth != "ARRIVED" || director != "OK")
                {
                    screen = string.Join("\n", session.SnapshotScreenRows().Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(14));
                }
                var r = new SendResult(agent, path, order[i].Name, text.Length, director, detail, truth, verdict.ViaFile,
                    verdict.Doubled, Math.Round(directorSeconds, 1), repo, screen);
                results.Add(r);
                Console.WriteLine($"[rig] {agent,-8} {session.Id.ToString()[..8]} {path,-5} {order[i].Name,-15} len={text.Length,-6} director={director,-6} truth={truth}{(verdict.ViaFile ? " (via file)" : "")} {directorSeconds:F1}s {detail}");
                if (screen is not null) Console.WriteLine("        screen: " + screen.Replace("\n", "\n                "));
            }
        }
        finally
        {
            try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[rig] kill {session.Id}: {ex.Message}"); }
            manager.RemoveSession(session.Id);
        }
        return results;
    }
}

public sealed record SendResult(
    string Agent, string Path, string Shape, int Length, string Director, string Detail, string Truth,
    bool ViaFile, bool Doubled, double DirectorSeconds, string Repo, string? Screen);

public sealed class Options
{
    public List<string> Agents { get; private set; } = ["claude", "codex", "pi"];
    public List<TextShape> Shapes { get; private set; } = TextCatalogue.All.ToList();
    public int Sessions { get; private set; } = 1;
    public int Parallel { get; private set; } = 1;
    public int FirstWaitSeconds { get; private set; } = 30;
    public string RepoRoot { get; private set; } = @"D:\ReposFred\_delivery-qa";
    public string Out { get; private set; } = Path.Combine(@"D:\ReposFred\_delivery-qa\runs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    public Dictionary<string, string> ArgsOverride { get; } = new();
    public string? KeysAgent { get; private set; }
    public string? CharsAgent { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--agents": o.Agents = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--shapes":
                    var s = Next();
                    o.Shapes = s == "all" ? TextCatalogue.All.ToList() : s.Split(',').Select(TextCatalogue.Named).ToList();
                    break;
                case "--sessions": o.Sessions = int.Parse(Next()); break;
                case "--parallel": o.Parallel = int.Parse(Next()); break;
                case "--first-wait": o.FirstWaitSeconds = int.Parse(Next()); break;
                case "--repo-root": o.RepoRoot = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--keys": o.KeysAgent = Next(); break;
                case "--chars": o.CharsAgent = Next(); break;
                default:
                    if (args[i].StartsWith("--args-", StringComparison.Ordinal)) { o.ArgsOverride[args[i]["--args-".Length..]] = Next(); break; }
                    throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return o;
    }
}
