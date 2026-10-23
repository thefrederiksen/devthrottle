using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// THE INPUT ROUTES THE PARITY MATRIX MEASURES (issue #4378) - each a way text reaches an agent's composer:
///  - desktop-type: typed into the desktop terminal one character at a time, then Enter;
///  - desktop-newline: a multi-line text typed with Shift+Enter between the lines, then Enter;
///  - desktop-paste: pasted into the desktop terminal (Ctrl+V's own code), then Enter;
///  - prompt: the Director's send, <see cref="Session.SendTextAsync"/> - what the Gateway's /prompt, the phone and
///    dictation reach.
/// </summary>
public static class Routes
{
    public static readonly IReadOnlyList<string> All = ["desktop-type", "desktop-newline", "desktop-paste", "prompt"];

    /// <summary>The longest text the desktop routes TYPE: past this a person pastes rather than types.</summary>
    public const int TypedLimit = 3200;

    /// <summary>Whether <paramref name="route"/> applies to <paramref name="text"/>: a person types a single line, types a
    /// multi-line text with Shift+Enter, and pastes or sends anything.</summary>
    public static bool AppliesTo(string route, string text)
    {
        var multi = text.Contains('\n') || text.Contains('\r');
        return route switch
        {
            // A person cannot TYPE a tab into a composer - the Tab key completes or switches a mode - so a text with a
            // tab is pasted or sent, never typed.
            "desktop-type" => !multi && !text.Contains('\t') && text.Length <= TypedLimit,
            "desktop-newline" => multi && text.Length <= TypedLimit,
            "desktop-paste" or "prompt" => true,
            _ => throw new ArgumentException($"Unknown route '{route}'. Routes: {string.Join(", ", All)}"),
        };
    }

    /// <summary>Type, paste or send <paramref name="text"/> through a desktop route, ending with the owner's Enter.</summary>
    public static async Task DesktopAsync(DesktopHost desktop, string route, string text)
    {
        switch (route)
        {
            case "desktop-type":
            case "desktop-newline":
                await desktop.TypeAsync(text);
                break;
            case "desktop-paste":
                await desktop.PasteAsync(text);
                break;
            default:
                throw new ArgumentException($"'{route}' is not a desktop route.");
        }
        // A beat between the last character and Enter, as a person leaves.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await desktop.EnterAsync();
    }
}

/// <summary>One cell of the echo layer: what the echo agent received for one text on one route.</summary>
public sealed record EchoResult(string Route, string As, string Text, int Length, string Director, string DirectorDetail, EchoVerdict Verdict, int Run = 1);

/// <summary>
/// THE ECHO LAYER (issue #4378): every route and every text, sent to the byte-echo agent instead of a model. Free, and
/// it answers what each route actually WRITES - the question a real agent's records can only answer indirectly. The
/// desktop routes run through the shipped terminal control on a real Director session; the prompt route runs the
/// shared send (<c>TerminalSubmit.SharedSubmitAsync</c>) once as each agent, because its route choice depends on the agent.
/// </summary>
public static class EchoLayer
{
    private static readonly (string As, string Tag)[] PromptAs = [("claude", "ClaudeCode"), ("codex", "Codex"), ("pi", "Pi")];

    public static async Task<int> RunAsync(SessionManager manager, Options opts)
    {
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var commit = Ledger.ProductCommit();
        var repo = Rig.EnsureRepo(Path.Combine(opts.RepoRoot, "repo-1"));
        var texts = opts.MatrixTextNames is { } names ? names.Select(MatrixTexts.Named).ToList() : MatrixTexts.All.ToList();
        var routes = opts.RouteNames ?? Routes.All;
        var results = new List<EchoResult>();
        var resultsPath = Path.Combine(opts.Out, "echo-results.jsonl");
        var runs = opts.MatrixRepeat;
        Console.WriteLine($"[echo] run {runId} on product {commit}: {texts.Count} texts x {routes.Count} routes x {runs} runs. Results: {resultsPath}");

        void Record(EchoResult r)
        {
            results.Add(r);
            File.AppendAllText(resultsPath, JsonSerializer.Serialize(r) + "\n");
            var v = r.Verdict;
            Ledger.Append(opts.LedgerPath, new LedgerEntry(runId, DateTime.UtcNow, commit, Environment.MachineName,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription, "echo", $"as {r.As}", $"{r.Route}:{r.Text}", r.Length,
                r.Director, v.Verdict, v.Pass && r.Director == "OK", Math.Round(v.Ms / 1000, 1), $"{r.DirectorDetail} {v.Detail}".Trim(), null,
                Route: r.Route, Exact: v.Verdict == "EXACT", ViaFile: v.Verdict == "VIA-FILE"));
            Console.WriteLine($"[echo] {r.Route,-15} as {r.As,-7} {r.Text,-17} run {r.Run} len={r.Length,-6} {v.Verdict,-13} framed={(v.Framed ? "yes" : "no "),-3} " +
                              $"enters={v.Enters} reads={v.Reads} {v.Ms / 1000:F1}s director={r.Director} {Short(v.Detail)} {Short(r.DirectorDetail)}");
        }

        // THE DESKTOP ROUTES: one echo agent in a real Director session, the shipped terminal control attached to it.
        var desktopRoutes = routes.Where(r => r.StartsWith("desktop-", StringComparison.Ordinal)).ToList();
        if (desktopRoutes.Count > 0)
        {
            var log = Path.Combine(opts.Out, "echo-desktop.jsonl");
            var (exe, args) = EchoCommand(log);
            var session = manager.CreateSession(repo, new RawCliAgent(exe, args), null, SessionBackendType.ConPty, null);
            using var desktop = new DesktopHost();
            try
            {
                await WaitForScreenAsync(() => session.SnapshotScreenRows(), "echo-agent ready", TimeSpan.FromSeconds(30));
                await desktop.AttachAsync(session);
                foreach (var text in texts)
                foreach (var route in desktopRoutes)
                for (var run = 1; run <= runs; run++)
                {
                    var token = Token();
                    var body = text.Build(token);
                    if (!Routes.AppliesTo(route, body)) continue;
                    var before = EchoAgent.ReadLog(log).Count;
                    string director = "OK", detail = "";
                    try { await Routes.DesktopAsync(desktop, route, body); }
                    catch (Exception ex) { director = "FAILED"; detail = $"{ex.GetType().Name}: {ex.Message}"; }
                    var reads = await QuietReadsAsync(log, before);
                    Record(new EchoResult(route, "desktop", text.Name, body.Length, director, detail, EchoAnalysis.Analyze(reads, body), run));
                }
            }
            finally
            {
                try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[echo] kill: {ex.Message}"); }
                manager.RemoveSession(session.Id);
            }
        }

        // THE PROMPT ROUTE: the shared send, as each agent, into an echo agent on a plain terminal backend.
        if (routes.Contains("prompt"))
        {
            foreach (var (asAgent, tag) in PromptAs.Where(p => opts.Agents.Contains(p.As)))
            {
                var log = Path.Combine(opts.Out, $"echo-prompt-{asAgent}.jsonl");
                var (exe, args) = EchoCommand(log);
                using var backend = PlatformSessionBackend.CreateDefault();
                backend.Start(exe, args, repo, 120, 40);
                try
                {
                    await WaitForScreenAsync(() => [System.Text.Encoding.UTF8.GetString(backend.Buffer?.DumpAll() ?? [])],
                        "echo-agent ready", TimeSpan.FromSeconds(30));
                    foreach (var text in texts)
                    for (var run = 1; run <= runs; run++)
                    {
                        var token = Token();
                        var body = text.Build(token);
                        var before = EchoAgent.ReadLog(log).Count;
                        var (director, detail, _) = await Scenarios.TimeSendAsync(() =>
                            CcDirector.Core.Drivers.TerminalSubmit.SharedSubmitAsync(backend, body, tag, bracketedPasteEnabled: true));
                        var reads = await QuietReadsAsync(log, before);
                        var verdict = EchoAnalysis.Analyze(reads, body);
                        if (verdict.Verdict == "VIA-FILE") verdict = verdict with { Detail = verdict.Detail + "; " + PayloadCheck(verdict.Received, repo, body) };
                        Record(new EchoResult("prompt", asAgent, text.Name, body.Length, director, detail, verdict, run));
                    }
                }
                finally
                {
                    try { await backend.GracefulShutdownAsync(2000); } catch (Exception ex) { Console.WriteLine($"[echo] shutdown: {ex.Message}"); }
                }
            }
        }

        var summary = Summary(results);
        Console.WriteLine();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(opts.Out, "echo-summary.txt"), summary);
        if (results.Count == 0)
        {
            Console.WriteLine("[echo] NOTHING WAS MEASURED: no route applied to these texts.");
            return 2;
        }
        return results.All(r => r.Verdict.Pass && r.Director == "OK") ? 0 : 1;
    }

    /// <summary>The command that starts the echo agent: this rig's own executable with <c>--echo-agent</c>.</summary>
    internal static (string Exe, string Args) EchoCommand(string log, bool usageDialog = false)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("The rig cannot find its own executable.");
        var quoted = $"--echo-agent \"{log}\"{(usageDialog ? " --usage-dialog" : "")}";
        return Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (self, $"\"{typeof(EchoLayer).Assembly.Location}\" {quoted}")
            : (self, quoted);
    }

    internal static string Token() => $"Q{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";

    /// <summary>Wait until a screen row holds <paramref name="marker"/>; throw when it never does.</summary>
    internal static async Task WaitForScreenAsync(Func<string[]> rows, string marker, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (rows().Any(r => r.Contains(marker, StringComparison.Ordinal))) return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"'{marker}' never appeared within {limit.TotalSeconds:F0}s.");
    }

    /// <summary>The reads after <paramref name="before"/>, once a second has passed with no new one (two minutes at most).</summary>
    internal static async Task<List<EchoRead>> QuietReadsAsync(string log, int before)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        var count = -1;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            var now = EchoAgent.ReadLog(log).Count;
            if (now != count) { count = now; stableSince = DateTime.UtcNow; }
            else if (count > before && DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(1)) break;
            else if (count == before && DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(5)) break;
            await Task.Delay(100);
        }
        return EchoAgent.ReadLog(log).Skip(before).ToList();
    }

    /// <summary>For a file route: whether the payload file named in the typed line holds the text exactly.</summary>
    private static string PayloadCheck(string typed, string repo, string body)
    {
        var m = System.Text.RegularExpressions.Regex.Match(typed, @"input_\d{8}_\d{6}_\w+\.txt");
        if (!m.Success) return "payload file not named";
        var path = Directory.EnumerateFiles(repo, m.Value, SearchOption.AllDirectories).FirstOrDefault();
        if (path is null) return $"payload {m.Value} not found";
        return EchoAnalysis.Canonical(File.ReadAllText(path)) == EchoAnalysis.Canonical(body) ? "payload exact" : "payload ALTERED";
    }

    internal static string Summary(IReadOnlyList<EchoResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"route",-15} {"as",-8} {"sends",5} {"exact",5} {"split",5} {"altered",7} {"via-file",8} {"other",5}  framed  unsteady-cells");
        foreach (var g in results.GroupBy(r => (r.Route, r.As)))
        {
            var l = g.ToList();
            int C(string v) => l.Count(r => r.Verdict.Verdict == v);
            sb.AppendLine($"{g.Key.Route,-15} {g.Key.As,-8} {l.Count,5} {C("EXACT"),5} {C("SPLIT"),5} {C("ALTERED"),7} {C("VIA-FILE"),8} " +
                          $"{l.Count - C("EXACT") - C("SPLIT") - C("ALTERED") - C("VIA-FILE"),5}  {l.Count(r => r.Verdict.Framed)}/{l.Count}  " +
                          $"{AgentMatrix.Unsteady(l, r => r.Text, r => r.Verdict.Verdict),14}");
        }
        var bad = results.Where(r => !r.Verdict.Pass || r.Director != "OK").ToList();
        sb.AppendLine(bad.Count == 0 ? "EVERY TEXT ARRIVED EXACTLY, AS TEXT, IN ONE SUBMIT." : $"{bad.Count} of {results.Count} did not:");
        foreach (var r in bad)
            sb.AppendLine($"  {r.Route} as {r.As} {r.Text} run {r.Run} len={r.Length}: {r.Verdict.Verdict} director={r.Director} {r.Verdict.Detail} {r.DirectorDetail}".TrimEnd());
        return sb.ToString();
    }

    internal static string Short(string s) => s.Length <= 140 ? s : s[..140] + "...";
}

/// <summary>One cell of the agent layer: one text on one route to one real agent, judged by its own records.</summary>
/// <param name="Run">Which run of the cell this is (each cell runs twice by default).</param>
/// <param name="Accepted">Why a record that is not character for character still passes - only the agent's own trim of
/// edge spaces (<see cref="AcceptedDifference"/>); null otherwise.</param>
/// <param name="ScreenFile">The saved screen of a refused send, relative to the run folder (<see cref="ScreenDump"/>).</param>
public sealed record AgentMatrixResult(
    string Agent, string Route, string Text, int Length, string Director, string Detail, string Truth, bool Exact, bool ViaFile,
    double Seconds, string? Difference, string? Screen, int Run = 1, string? Accepted = null, string? ScreenFile = null)
{
    /// <summary>Parity: the Director did not fail it, it arrived once, alone, as text, character for character - or with
    /// only the agent's own trim of edge spaces.</summary>
    public bool Pass => Director is "OK" or "DELIVERING" && Truth == "ARRIVED" && (Exact || Accepted is not null) && !ViaFile;
}

/// <summary>
/// THE AGENT LAYER (issue #4378): the parity texts on every route to real Claude Code, Codex and Pi, each judged by the
/// agent's OWN records twice - the folding witness (<see cref="AgentRecords.Check"/>: arrived, lost, partial, doubled,
/// welded) and the strict one (<see cref="AgentRecords.UserTurnText"/>: character for character). Each turn is stopped
/// with Escape as soon as the records show the prompt arrived, so the agent spends almost nothing answering it.
/// </summary>
public static class AgentMatrix
{
    public static async Task<int> RunAsync(SessionManager manager, AgentOptions agentOptions, Options opts)
    {
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var commit = Ledger.ProductCommit();
        var repo = Rig.EnsureRepo(Path.Combine(opts.RepoRoot, "repo-1"));
        var repos = new List<string> { repo };
        var texts = opts.MatrixTextNames is { } names ? names.Select(MatrixTexts.Named).ToList() : MatrixTexts.AgentDefault.ToList();
        var unsafeTexts = texts.Where(t => !t.SafeForAgents).Select(t => t.Name).ToList();
        if (unsafeTexts.Count > 0)
            throw new ArgumentException($"These texts are for the echo agent only and are never sent to a real agent: {string.Join(", ", unsafeTexts)}");
        var routes = opts.RouteNames ?? Routes.All;
        var results = new List<AgentMatrixResult>();
        var resultsPath = Path.Combine(opts.Out, "agent-results.jsonl");
        var cap = opts.MaxTurns;
        var runs = opts.MatrixRepeat;
        Console.WriteLine($"[agents] run {runId} on product {commit}: {texts.Count} texts x {routes.Count} routes x {opts.Agents.Count} agents x {runs} runs, at most {cap} turns. Results: {resultsPath}");
        using var load = opts.CpuLoadThreads > 0 ? new CpuLoad(opts.CpuLoadThreads) : null;
        using var desktop = new DesktopHost();
        var turns = 0;

        foreach (var agent in opts.Agents)
        {
            if (!Program.Agents.TryGetValue(agent, out var a)) throw new ArgumentException($"Unknown agent '{agent}'.");
            var executable = agent switch { "claude" => agentOptions.ClaudePath, "codex" => agentOptions.CodexPath, "pi" => agentOptions.PiPath, _ => null } ?? agent;
            var version = Ledger.AgentVersion(executable);
            var args = opts.ArgsOverride.TryGetValue(agent, out var o) ? o : a.Args;
            Console.WriteLine($"[agents] {agent}: {executable} ({version}), args: {args}");
            Session? session = null;
            try
            {
                foreach (var text in texts)
                foreach (var route in routes)
                for (var run = 1; run <= runs; run++)
                {
                    var token = EchoLayer.Token();
                    var body = text.Build(token);
                    if (!Routes.AppliesTo(route, body)) continue;
                    if (turns >= cap) { Console.WriteLine($"[agents] turn cap of {cap} reached; stopping."); goto done; }
                    if (session is null || session.Status is SessionStatus.Exited or SessionStatus.Failed)
                    {
                        if (session is not null) await EndAsync(manager, session);
                        session = manager.CreateSession(repo, a.Kind, string.IsNullOrWhiteSpace(args) ? null : args, SessionBackendType.ConPty, null);
                        await desktop.AttachAsync(session);
                    }
                    if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                        Console.WriteLine($"[agents] {agent}: not waiting for input after 120s; sending anyway");

                    turns++;
                    var sentAt = DateTime.UtcNow;
                    var current = session;
                    var (director, detail, seconds) = route == "prompt"
                        ? await Scenarios.TimeSendAsync(() => current.SendTextAsync(body, SubmissionProvenance.Typed("delivery-qa-rig", "rig"), SendSource.UserInput))
                        : await Scenarios.TimeSendAsync(() => Routes.DesktopAsync(desktop, route, body));

                    // The screen the send was refused on, saved at once - before the judge waits - so it is the screen the
                    // Director read, not the one after (the mission's 11.5).
                    var screenFile = ScreenDump.IsRefusal(director) ? ScreenDump.Save(opts.Out, $"text-{agent}-{route}-{text.Name}-run{run}", current, $"{agent} {route} {text.Name} run {run}: {director} {detail}") : null;
                    var r = await JudgeAsync(agent, route, text.Name, body, token, sentAt, director, detail, seconds, current, repos) with { Run = run, ScreenFile = screenFile };
                    // The one-word answer is left to finish rather than stopped with Escape: Claude Code puts an
                    // interrupted prompt back into its composer, and the next cell's text then welds onto it. Then the
                    // composer is emptied, so a split cell's leftover lines cannot reach the next cell either.
                    await Rig.WaitUntilIdleAsync(current, TimeSpan.FromSeconds(120));
                    EmptyComposer(current, a.Kind, body);
                    results.Add(r);
                    File.AppendAllText(resultsPath, JsonSerializer.Serialize(r) + "\n");
                    Ledger.Append(opts.LedgerPath, new LedgerEntry(runId, DateTime.UtcNow, commit, Environment.MachineName,
                        System.Runtime.InteropServices.RuntimeInformation.OSDescription, agent, version, $"{route}:{text.Name}", body.Length,
                        r.Director, r.Truth, r.Pass, r.Seconds, $"{r.Detail} {r.Difference}".Trim(), r.Screen, Route: route, Exact: r.Exact, ViaFile: r.ViaFile));
                    Console.WriteLine($"[agents] {agent,-7} {route,-15} {text.Name,-17} run {run} len={body.Length,-6} director={r.Director,-10} truth={r.Truth,-8} " +
                                      $"exact={(r.Exact ? "yes" : "NO "),-3}{(r.ViaFile ? " via-file" : "")}{(r.Accepted is { } acc ? $" accepted: {acc}" : "")} {(r.Pass ? "PASS" : "FAIL")} {r.Seconds:F1}s " +
                                      $"{EchoLayer.Short(r.Difference ?? r.Detail)}{(r.ScreenFile is { } sf ? $" screen: {sf}" : "")}");
                }
            }
            finally
            {
                if (session is not null) await EndAsync(manager, session);
            }
        }
        done:
        var summary = Summary(results);
        Console.WriteLine();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(opts.Out, "agent-summary.txt"), summary);
        if (results.Count == 0) return 2;
        return results.All(r => r.Pass) ? 0 : 1;
    }

    internal static async Task<AgentMatrixResult> JudgeAsync(string agent, string route, string textName, string body, string token, DateTime sentAt,
        string director, string detail, double seconds, Session session, IReadOnlyList<string> repos)
    {
        RecordsVerdict verdict = new(false, false, false, false, null);
        string? turn = null;
        var deadline = DateTime.UtcNow.AddSeconds(director is "REFUSED" or "FAILED" ? 20 : 90);
        while (DateTime.UtcNow < deadline)
        {
            verdict = AgentRecords.Check(agent, sentAt, body, repos);
            turn = AgentRecords.UserTurnText(agent, sentAt, token);
            if (verdict.Found || turn is not null) break;
            await Task.Delay(1000);
        }
        var (truth, exact, accepted, difference) = Verdict(agent, body, verdict, turn);
        string? screen = null;
        if (!(truth == "ARRIVED" && (exact || accepted is not null)) || director is not ("OK" or "DELIVERING"))
            screen = string.Join("\n", session.SnapshotScreenRows().Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(14));
        return new AgentMatrixResult(agent, route, textName, body.Length, director, detail, truth, exact, verdict.ViaFile, Math.Round(seconds, 1), difference, screen,
            Accepted: accepted);
    }

    /// <summary>
    /// The truth of one cell from the two witnesses: the folding one (<paramref name="verdict"/>) and the user turn that
    /// holds the token (<paramref name="turn"/>, null when none does). ARRIVED when the turn is the text character for
    /// character - or differs only by the agent's own trim of edge spaces, which is then named in Accepted - or when it
    /// came by the file route; ALTERED when it arrived in one turn but changed; DOUBLED, PARTIAL or LOST otherwise. Pure,
    /// so it is tested without an agent.
    /// </summary>
    internal static (string Truth, bool Exact, string? Accepted, string? Difference) Verdict(string agent, string body, RecordsVerdict verdict, string? turn)
    {
        var exact = !verdict.ViaFile && turn is not null && EchoAnalysis.Canonical(turn) == EchoAnalysis.Canonical(body);
        var accepted = !verdict.ViaFile && !exact && turn is not null ? AcceptedDifference.AgentTrim(agent, body, EchoAnalysis.Canonical(turn)) : null;
        string truth;
        if (!verdict.Found && turn is null) truth = verdict.Partial ? "PARTIAL" : "LOST";
        else if (verdict.Found && verdict.Doubled) truth = "DOUBLED";
        else truth = verdict.ViaFile || exact || accepted is not null ? "ARRIVED" : "ALTERED";
        string? difference = null;
        if (truth == "ALTERED")
            difference = turn is null
                ? "the folding witness found the text but no user turn holds its token"
                : EchoAnalysis.FirstDifference(EchoAnalysis.Canonical(body), EchoAnalysis.Canonical(turn));
        return (truth, exact, accepted, difference);
    }

    /// <summary>
    /// Empty the agent's composer with the keys measured for it (<see cref="CcDirector.Core.Drivers.ComposerClearKeys"/>),
    /// pressed once per line the last text could have left behind - for Codex and Pi, Ctrl+U clears a line and Backspace
    /// joins the one above. Harmless on an empty composer.
    /// </summary>
    internal static void EmptyComposer(Session session, AgentKind kind, string lastText)
    {
        var lines = lastText.Count(c => c == '\n') + 2;
        var keys = kind == AgentKind.ClaudeCode
            ? CcDirector.Core.Drivers.ComposerClearKeys.For(kind, lastText.Length)
            : Enumerable.Range(0, lines).SelectMany(_ => (CcDirector.Core.Drivers.ComposerClearKeys.For(kind) ?? []).Append((byte)0x7F)).ToArray();
        if (keys is null || keys.Length == 0) return;
        session.SendInput(keys, null, SubmissionProvenance.Typed("delivery-qa-rig", "rig"));
    }

    internal static async Task EndAsync(SessionManager manager, Session session)
    {
        try { await manager.KillSessionAsync(session.Id); } catch (Exception ex) { Console.WriteLine($"[agents] kill {session.Id}: {ex.Message}"); }
        manager.RemoveSession(session.Id);
    }

    internal static string Summary(IReadOnlyList<AgentMatrixResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"agent",-8} {"route",-15} {"sends",5} {"pass",4} {"altered",7} {"lost",4} {"partial",7} {"doubled",7} {"via-file",8} {"director-failed",15} {"trim-accepted",13} {"unsteady-cells",14}");
        foreach (var g in results.GroupBy(r => (r.Agent, r.Route)))
        {
            var l = g.ToList();
            sb.AppendLine($"{g.Key.Agent,-8} {g.Key.Route,-15} {l.Count,5} {l.Count(r => r.Pass),4} {l.Count(r => r.Truth == "ALTERED"),7} " +
                          $"{l.Count(r => r.Truth == "LOST"),4} {l.Count(r => r.Truth == "PARTIAL"),7} {l.Count(r => r.Truth == "DOUBLED"),7} " +
                          $"{l.Count(r => r.ViaFile),8} {l.Count(r => r.Director is not ("OK" or "DELIVERING")),15} {l.Count(r => r.Accepted is not null),13} " +
                          $"{Unsteady(l, r => r.Text, r => r.Pass),14}");
        }
        var bad = results.Where(r => !r.Pass).ToList();
        sb.AppendLine(bad.Count == 0 ? "EVERY TEXT ARRIVED EXACTLY, AS TEXT, ON EVERY ROUTE." : $"{bad.Count} of {results.Count} did not:");
        foreach (var r in bad)
            sb.AppendLine($"  {r.Agent} {r.Route} {r.Text} run {r.Run} len={r.Length}: director={r.Director} truth={r.Truth}{(r.ViaFile ? " via-file" : "")} {r.Difference ?? r.Detail}{(r.ScreenFile is { } sf ? $" (screen: {sf})" : "")}".TrimEnd());
        return sb.ToString();
    }

    /// <summary>How many cells - the results sharing a <paramref name="cell"/> key - had one <paramref name="outcome"/> on
    /// one run and another on the next (passed once and failed once, or two different verdicts). Each cell runs twice (the
    /// mission's 11.5) so that this number exists: an unsteady cell is a race, not a rule.</summary>
    internal static int Unsteady<T, TOutcome>(IEnumerable<T> results, Func<T, string> cell, Func<T, TOutcome> outcome) =>
        results.GroupBy(cell).Count(g => g.Select(outcome).Distinct().Count() > 1);
}
