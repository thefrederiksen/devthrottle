using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// One condition of the condition matrix (issue #4378, the mission's 11.5): a state the agent is in when a text arrives.
/// The baseline only ever sent to an idle agent; a person also types while it works, onto their own draft, with a menu
/// open, and with Claude Code's usage-limit dialog on the screen.
/// </summary>
/// <param name="Agents">The agents it runs on; "echo" for a condition the byte-echo agent stands in for.</param>
/// <param name="Proves">What a pass shows.</param>
/// <param name="Cannot">What it cannot show, and why - said in the results as well as the README.</param>
public sealed record ConditionCase(string Name, string[] Agents, string Proves, string Cannot);

public static class Conditions
{
    public static readonly IReadOnlyList<ConditionCase> All =
    [
        new("mid-turn", ["claude", "codex", "pi"],
            "a text sent while the agent runs a 25-second tool reaches it once, alone, character for character - queued or steered into the turn, as the agent does for a person",
            "when the agent records a queued prompt only at the end of the turn (Codex, Pi) it cannot show the prompt was taken at once, only that it arrived"),
        new("owner-draft", ["claude", "codex", "pi"],
            "a text sent while the owner's own unsent draft sits in the composer arrives in one turn after the draft, both character for character, with only spaces between - the owner's ruling of 21 October 2026, and what a person typing more gets",
            "how many spaces join the two is not judged: the prompt route adds one, a person types their own"),
        new("menu-open", ["claude"],
            "with Claude Code's question menu open, the prompt route refuses in words or delivers (never reports a lost text delivered), and on every route the next text after the menu closes arrives alone",
            "on the desktop routes the keys typed into the menu are the person's own - where they go is recorded, not judged. Codex and Pi have no question menu the rig can raise: Codex asks questions only in plan mode, Pi has none, and their model pickers are not used because Enter in one changes the agent's model (Codex also writes the choice to the owner's own configuration file)"),
        new("usage-limit", ["echo"],
            "with Claude Code's usage-limit dialog on the screen, a desktop paste - a person at the terminal - reaches the agent; desktop typing is the control",
            "Claude Code shows the dialog only when the owner's allowance has run out, which no run can cause on demand, so the byte-echo agent draws the dialog's real words and the Director's own guard reads them. It proves what the Director lets through, not what Claude Code does with it. The prompt route is left out: whether phone and Cockpit prompts pass the dialog is the owner's open question (mission 11.6)"),
    ];

    public static ConditionCase Named(string name) =>
        All.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"No condition named '{name}'. Known: {string.Join(", ", All.Select(c => c.Name))}");

    /// <summary>The text a route sends when no --texts are given: one line, or for Shift+Enter typing, four lines.</summary>
    public static string DefaultTextFor(string route) => route == "desktop-newline" ? "multiline-lf" : "ascii";
}

/// <summary>One cell of the condition matrix.</summary>
/// <param name="Truth">What the records say. Besides the text matrix's words: ARRIVED-AFTER-DRAFT (one turn: the draft,
/// then the text), DRAFT-LOST (the text alone, the draft gone), DRAFT-LEFT (the text alone, the draft still in the
/// composer), ANSWERED-MENU (the text went into the menu's answer), and for the echo agent its verdict word (EXACT,
/// DROPPED, ...). The menu cell adds the next text's truth after a slash.</param>
/// <param name="Setup">What the rig confirmed about the condition before and after the send, from the records or the
/// screen.</param>
public sealed record ConditionResult(
    string Condition, string Agent, string Route, string Text, int Run, int Length, string Director, string Detail, string Truth,
    bool Exact, string? Accepted, bool Pass, double Seconds, string? Difference, string Setup, string? ScreenFile);

/// <summary>
/// THE CONDITION MATRIX (issue #4378, the mission's 11.5): the parity texts on every route, sent while the agent is in a
/// state a person meets - working, holding the owner's draft, showing a menu, showing the usage-limit dialog - and judged,
/// as everywhere in this rig, by the agent's own records. Each condition is set up for real or the cell is a setup
/// failure, counted in no rate; nothing is faked. Where a state cannot be brought about for real, <see cref="ConditionCase.Cannot"/>
/// says so in the results.
/// </summary>
public static class ConditionMatrix
{
    private static readonly SubmissionProvenance RigTyped = SubmissionProvenance.Typed("delivery-qa-rig", "rig");

    /// <summary>How long the mid-turn tool runs: long enough that every route's send lands inside it.</summary>
    public const int MidTurnToolSeconds = 25;

    public static async Task<int> RunAsync(SessionManager manager, AgentOptions agentOptions, Options opts)
    {
        var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var commit = Ledger.ProductCommit();
        var repo = Rig.EnsureRepo(Path.Combine(opts.RepoRoot, "repo-1"));
        var conditions = (opts.ConditionNames ?? Conditions.All.Select(c => c.Name).ToList()).Select(Conditions.Named).ToList();
        var routes = opts.RouteNames ?? Routes.All;
        var runs = opts.MatrixRepeat;
        var results = new List<ConditionResult>();
        var resultsPath = Path.Combine(opts.Out, "condition-results.jsonl");
        Console.WriteLine($"[conditions] run {runId} on product {commit}: {string.Join(", ", conditions.Select(c => c.Name))} x {routes.Count} routes x {runs} runs. Results: {resultsPath}");
        foreach (var c in conditions) Console.WriteLine($"[conditions] {c.Name}: proves {c.Proves}. Cannot: {c.Cannot}.");
        using var desktop = new DesktopHost();

        void Record(ConditionResult r, string version)
        {
            results.Add(r);
            File.AppendAllText(resultsPath, JsonSerializer.Serialize(r) + "\n");
            Ledger.Append(opts.LedgerPath, new LedgerEntry(runId, DateTime.UtcNow, commit, Environment.MachineName,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription, r.Agent, version, $"{r.Condition}:{r.Route}:{r.Text}", r.Length,
                r.Director, r.Truth, r.Pass, r.Seconds, $"{r.Detail} {r.Difference} | {r.Setup}".Trim(), r.ScreenFile, Route: r.Route, Exact: r.Exact));
            Console.WriteLine($"[conditions] {r.Condition,-11} {r.Agent,-6} {r.Route,-15} {r.Text,-12} run {r.Run} director={r.Director,-12} truth={r.Truth,-19} " +
                              $"{(r.Director == "SETUP-FAILED" ? "NOT COUNTED" : r.Pass ? "PASS" : "FAIL")} {r.Seconds:F1}s {EchoLayer.Short(r.Difference ?? r.Detail)} | {EchoLayer.Short(r.Setup)}" +
                              (r.ScreenFile is { } sf ? $" screen: {sf}" : ""));
        }

        foreach (var condition in conditions)
        {
            if (condition.Name == "usage-limit")
            {
                await UsageLimitAsync(manager, desktop, opts, repo, routes, runs, r => Record(r, "echo-agent"));
                continue;
            }
            foreach (var agent in opts.Agents.Where(a => condition.Agents.Contains(a)))
            {
                if (!Program.Agents.TryGetValue(agent, out var a)) throw new ArgumentException($"Unknown agent '{agent}'.");
                var executable = agent switch { "claude" => agentOptions.ClaudePath, "codex" => agentOptions.CodexPath, "pi" => agentOptions.PiPath, _ => null } ?? agent;
                var version = Ledger.AgentVersion(executable);
                var args = opts.ArgsOverride.TryGetValue(agent, out var o) ? o : a.Args;
                Console.WriteLine($"[conditions] {condition.Name} on {agent}: {executable} ({version}), args: {args}");
                Session? session = null;
                try
                {
                    foreach (var route in routes)
                    foreach (var text in TextsFor(route, opts))
                    for (var run = 1; run <= runs; run++)
                    {
                        if (session is null || session.Status is SessionStatus.Exited or SessionStatus.Failed)
                        {
                            if (session is not null) await AgentMatrix.EndAsync(manager, session);
                            session = manager.CreateSession(repo, a.Kind, string.IsNullOrWhiteSpace(args) ? null : args, SessionBackendType.ConPty, null);
                            await desktop.AttachAsync(session);
                        }
                        if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                            Console.WriteLine($"[conditions] {agent}: not waiting for input after 120s; setting up anyway");
                        var cell = new Cell(condition.Name, agent, a.Kind, route, text, run, session, desktop, [repo], opts.Out);
                        ConditionResult r;
                        try
                        {
                            r = condition.Name switch
                            {
                                "mid-turn" => await MidTurnAsync(cell),
                                "owner-draft" => await OwnerDraftAsync(cell),
                                "menu-open" => await MenuOpenAsync(cell),
                                _ => throw new ArgumentException($"No condition named '{condition.Name}'."),
                            };
                        }
                        catch (Exception ex)
                        {
                            r = cell.Result(0, "RIG-ERROR", $"{ex.GetType().Name}: {ex.Message}", "UNKNOWN", false, null, false, 0, null,
                                "the rig failed during the cell", ScreenDump.Save(opts.Out, cell.ScreenName("rig-error"), session));
                        }
                        Record(r, version);
                        // Leave the session as the next cell needs it: the turn finished, any menu closed, the composer empty.
                        await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120));
                        AgentMatrix.EmptyComposer(session, a.Kind, text.Build("Q0000000000") + "\n" + DraftFor("Q0000000000"));
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                }
                finally
                {
                    if (session is not null) await AgentMatrix.EndAsync(manager, session);
                }
            }
        }

        var summary = Summary(results, conditions);
        Console.WriteLine();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(opts.Out, "condition-summary.txt"), summary);
        var counted = results.Where(r => r.Director != "SETUP-FAILED").ToList();
        if (counted.Count == 0)
        {
            Console.WriteLine("[conditions] NOTHING WAS MEASURED: no condition applied to these agents and routes, or every cell failed its setup.");
            return 2;
        }
        return counted.All(r => r.Pass) ? 0 : 1;
    }

    /// <summary>The texts a route sends: the --texts that apply to it, or its default.</summary>
    private static IEnumerable<MatrixText> TextsFor(string route, Options opts)
    {
        if (opts.MatrixTextNames is not { } names) return [MatrixTexts.Named(Conditions.DefaultTextFor(route))];
        return names.Select(MatrixTexts.Named).Where(t => t.SafeForAgents && Routes.AppliesTo(route, t.Build("Q0000000000")));
    }

    /// <summary>Everything one cell needs, and the result it makes.</summary>
    private sealed record Cell(string Condition, string Agent, AgentKind Kind, string Route, MatrixText Text, int Run, Session Session,
        DesktopHost Desktop, IReadOnlyList<string> Repos, string Out)
    {
        public string ScreenName(string what) => $"{Condition}-{Agent}-{Route}-{Text.Name}-run{Run}-{what}";

        public ConditionResult Result(int length, string director, string detail, string truth, bool exact, string? accepted, bool pass,
            double seconds, string? difference, string setup, string? screenFile) =>
            new(Condition, Agent, Route, Text.Name, Run, length, director, detail, truth, exact, accepted, pass, Math.Round(seconds, 1), difference, setup, screenFile);

        /// <summary>Send <paramref name="body"/> by this cell's route, saving the screen at once when it is refused.</summary>
        public async Task<(string Director, string Detail, double Seconds, string? ScreenFile, DateTime SentAt)> SendAsync(string body, string what = "refused")
        {
            var sentAt = DateTime.UtcNow;
            var (director, detail, seconds) = Route == "prompt"
                ? await Scenarios.TimeSendAsync(() => Session.SendTextAsync(body, RigTyped, SendSource.UserInput))
                : await Scenarios.TimeSendAsync(() => Routes.DesktopAsync(Desktop, Route, body));
            var screen = ScreenDump.IsRefusal(director) ? ScreenDump.Save(Out, ScreenName(what), Session, $"{Condition} {Agent} {Route} {Text.Name} run {Run}: {director} {detail}") : null;
            return (director, detail, seconds, screen, sentAt);
        }
    }

    // ===== mid-turn =====

    /// <summary>
    /// The agent is asked, through the desktop as the owner would, to run a 25-second tool; once its records show the tool
    /// call, the cell's text is sent by the route. The text must reach the agent once, alone, character for character.
    /// The cell counts only when the tool was truly running at the send: its call recorded, and the turn's answer not yet.
    /// </summary>
    private static async Task<ConditionResult> MidTurnAsync(Cell c)
    {
        var busyToken = EchoLayer.Token();
        var tool = StateMatrix.LongTool(MidTurnToolSeconds);
        var busyAt = DateTime.UtcNow;
        await c.Desktop.TypeAsync($"Token {busyToken}. Run exactly this shell command and wait for it to finish: {tool} . Then reply with exactly: ACK {busyToken}");
        await c.Desktop.EnterAsync();
        DateTime? toolAt = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (toolAt is null && DateTime.UtcNow < deadline)
        {
            toolAt = StateMatrix.ToolCallRecordedAt(c.Agent, busyAt, $"time.sleep({MidTurnToolSeconds})");
            if (toolAt is null) await Task.Delay(500);
        }
        if (toolAt is null)
            return c.Result(0, "SETUP-FAILED", "", "-", false, null, false, 0, null, "the agent's records never showed the 25-second tool call",
                ScreenDump.Save(c.Out, c.ScreenName("setup"), c.Session));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var token = EchoLayer.Token();
        var body = c.Text.Build(token);
        var (director, detail, seconds, screen, sentAt) = await c.SendAsync(body);
        var into = (sentAt - toolAt.Value).TotalSeconds;
        var answeredBefore = StateMatrix.AnswerRecordedAt(c.Agent, busyAt, busyToken) is { } done && done < sentAt;
        var setup = $"the tool call was recorded {into:F1}s before the send";
        if (answeredBefore || into >= MidTurnToolSeconds)
            return c.Result(body.Length, "SETUP-FAILED", detail, "-", false, null, false, seconds, null, $"NOT MID-TURN: {setup}, and the turn had ended", screen);

        // A queued prompt can be recorded only when the turn ends (Codex, Pi), so the judge waits past the tool's end.
        var (truth, exact, accepted, difference) = await JudgeAloneAsync(c, body, token, sentAt, director, TimeSpan.FromSeconds(MidTurnToolSeconds + 90));
        var pass = director is "OK" or "DELIVERING" && truth == "ARRIVED" && (exact || accepted is not null);
        return c.Result(body.Length, director, detail, truth, exact, accepted, pass, seconds, difference, setup + "; the turn's answer was not yet recorded", screen);
    }

    // ===== owner-draft =====

    /// <summary>The owner's unsent draft: words and a token, no trailing space - a person or the send adds the space.</summary>
    internal static string DraftFor(string token) => $"My unfinished draft {token}";

    /// <summary>
    /// The owner types a draft into the composer through the desktop terminal and leaves it unsent; the cell's text then
    /// comes by the route. Parity is one turn holding the draft, then the text (<see cref="DraftVerdict"/>).
    /// </summary>
    private static async Task<ConditionResult> OwnerDraftAsync(Cell c)
    {
        var draftToken = EchoLayer.Token();
        var draft = DraftFor(draftToken);
        await c.Desktop.TypeAsync(draft);
        if (!await WaitForAsync(() => ScreenHolds(c.Session, draftToken), TimeSpan.FromSeconds(20)))
            return c.Result(0, "SETUP-FAILED", "", "-", false, null, false, 0, null, "the owner's draft never appeared on the screen",
                ScreenDump.Save(c.Out, c.ScreenName("setup"), c.Session));
        await Task.Delay(TimeSpan.FromSeconds(1));

        var token = EchoLayer.Token();
        var body = c.Text.Build(token);
        var (director, detail, seconds, screen, sentAt) = await c.SendAsync(body);
        string? turn = null;
        var deadline = DateTime.UtcNow.AddSeconds(director is "REFUSED" or "FAILED" ? 20 : 90);
        while (DateTime.UtcNow < deadline && (turn = AgentRecords.UserTurnText(c.Agent, sentAt, token)) is null)
            await Task.Delay(1000);
        var turns = AgentRecords.UserTurnsHolding(c.Agent, sentAt, token);
        var draftOnScreen = ScreenHolds(c.Session, draftToken);
        var (truth, exact, accepted, difference) = DraftVerdict(c.Agent, draft, body, turn, turns, draftOnScreen);
        var pass = director is "OK" or "DELIVERING" && truth == "ARRIVED-AFTER-DRAFT";
        return c.Result(body.Length, director, detail, truth, exact, accepted, pass, seconds, difference,
            $"draft typed through the desktop and on the screen before the send; still on the screen after: {draftOnScreen}", screen);
    }

    /// <summary>
    /// The truth of an owner-draft cell. ARRIVED-AFTER-DRAFT when one user turn is the draft, then any number of spaces,
    /// then the text, character for character (the agent's own edge trim accepted); ALTERED when that turn holds both but
    /// differs; DRAFT-LOST or DRAFT-LEFT when the text arrived without the draft; DOUBLED, LOST otherwise. Pure.
    /// </summary>
    internal static (string Truth, bool Exact, string? Accepted, string? Difference) DraftVerdict(
        string agent, string draft, string body, string? turn, int turns, bool draftOnScreen)
    {
        if (turn is null) return ("LOST", false, null, draftOnScreen ? "the draft is still in the composer" : "the draft is gone too");
        if (turns > 1) return ("DOUBLED", false, null, $"{turns} user turns hold the text");
        var got = EchoAnalysis.Canonical(turn);
        var draftToken = draft[(draft.LastIndexOf(' ') + 1)..];
        if (!got.Contains(draftToken, StringComparison.Ordinal))
            return (draftOnScreen ? "DRAFT-LEFT" : "DRAFT-LOST", false, null,
                draftOnScreen ? "the text arrived alone and the draft was left in the composer" : "the text arrived alone and the owner's draft is gone");
        var want = EchoAnalysis.Canonical(body);
        var m = Regex.Match(got, "^" + Regex.Escape(draft) + "( *)(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (m.Success && m.Groups[2].Value == want) return ("ARRIVED-AFTER-DRAFT", true, null, null);
        if (m.Success && AcceptedDifference.AgentTrim(agent, body, m.Groups[2].Value) is { } why) return ("ARRIVED-AFTER-DRAFT", false, why, null);
        return ("ALTERED", false, null, EchoAnalysis.FirstDifference(draft + " " + want, got));
    }

    // ===== menu-open =====

    /// <summary>
    /// The agent opens its question menu (asked through the desktop); the cell's text is sent by the route while it shows;
    /// then the menu is closed with Escape, as an owner dismissing it would, and a NEXT text is sent by the same route.
    /// The token has letters only: a digit typed into the menu would pick an option and say nothing about delivery.
    /// </summary>
    private static async Task<ConditionResult> MenuOpenAsync(Cell c)
    {
        var askToken = EchoLayer.Token();
        var askAt = DateTime.UtcNow;
        await c.Desktop.TypeAsync($"Token {askToken}. Use the AskUserQuestion tool right now to ask me one question: \"Which colour?\" with the two options Red and Blue. Do nothing else.");
        await c.Desktop.EnterAsync();
        var shown = await WaitForAsync(() => StateMatrix.ToolCallRecordedAt(c.Agent, askAt, "AskUserQuestion") is not null
                                             && DoorbellSafety.ReadComposer(c.Kind, c.Session.SnapshotLiveFrame()) is ComposerReading.MenuOpen or ComposerReading.NotFound
                                             && !DoorbellSafety.ShowsWorking(c.Session.SnapshotScreenRows()), TimeSpan.FromSeconds(90));
        if (!shown)
            return c.Result(0, "SETUP-FAILED", "", "-", false, null, false, 0, null, "the agent never showed its question menu",
                ScreenDump.Save(c.Out, c.ScreenName("setup"), c.Session));
        await Task.Delay(TimeSpan.FromSeconds(2));
        var before = DoorbellSafety.ReadComposer(c.Kind, c.Session.SnapshotLiveFrame());
        var menuScreen = ScreenDump.Save(c.Out, c.ScreenName("menu"), c.Session, $"menu-open {c.Agent} {c.Route} run {c.Run}: the menu before the send ({before})");

        var token = LetterToken();
        var body = c.Text.Build(token);
        var (director, detail, seconds, screen, sentAt) = await c.SendAsync(body);
        await Task.Delay(TimeSpan.FromSeconds(8));
        var turn = AgentRecords.UserTurnText(c.Agent, sentAt, token);
        var firstTruth = turn is not null ? AgentMatrix.Verdict(c.Agent, body, AgentRecords.Check(c.Agent, sentAt, body, c.Repos), turn).Truth
            : AgentRecords.RecentLines(c.Agent, sentAt).Any(l => l.Contains(token, StringComparison.Ordinal) && !IsCopy(l)) ? "ANSWERED-MENU" : "LOST";

        // Close the menu if it is still open, as the owner would, and wait for the agent to settle.
        if (DoorbellSafety.ReadComposer(c.Kind, c.Session.SnapshotLiveFrame()) is ComposerReading.MenuOpen or ComposerReading.NotFound)
            c.Session.SendInput([0x1B], null, SubmissionProvenance.Typed("desktop-terminal", "owner"));
        if (!await WaitForAsync(() => DoorbellSafety.ReadComposer(c.Kind, c.Session.SnapshotLiveFrame()) is ComposerReading.Empty or ComposerReading.HoldsText
                                      && c.Session.ActivityState is ActivityState.WaitingForInput or ActivityState.Idle, TimeSpan.FromSeconds(60)))
            return c.Result(body.Length, director, detail, $"{firstTruth}/MENU-STUCK", false, null, false, seconds, "the menu never closed after Escape",
                $"menu shown before the send ({before}), screen {menuScreen}", screen ?? ScreenDump.Save(c.Out, c.ScreenName("stuck"), c.Session));
        // Whatever the menu-time keys left in the composer is cleared, as a person would before typing on.
        AgentMatrix.EmptyComposer(c.Session, c.Kind, body);
        await Task.Delay(TimeSpan.FromSeconds(2));

        var nextToken = LetterToken();
        var nextBody = c.Text.Build(nextToken);
        var (nextDirector, nextDetail, _, nextScreen, nextAt) = await c.SendAsync(nextBody, "next-refused");
        var (nextTruth, nextExact, nextAccepted, nextDifference) = await JudgeAloneAsync(c, nextBody, nextToken, nextAt, nextDirector, TimeSpan.FromSeconds(90));
        var nextPass = nextDirector is "OK" or "DELIVERING" && nextTruth == "ARRIVED" && (nextExact || nextAccepted is not null);
        var firstArrivedLate = firstTruth is not ("ARRIVED" or "ALTERED") && AgentRecords.UserTurnText(c.Agent, sentAt, token) is not null;
        var (pass, truth) = MenuCellPass(c.Route, firstTruth, director, firstArrivedLate, nextTruth, nextPass);
        return c.Result(body.Length, director, detail, truth, nextExact, nextAccepted, pass, seconds, nextDifference,
            $"menu shown before the send ({before}), screen {menuScreen}; menu-time text: {firstTruth}{(firstArrivedLate ? ", arrived late" : "")}; " +
            $"next text after Escape: director={nextDirector} truth={nextTruth} {nextDetail}".TrimEnd(), screen ?? nextScreen);
    }

    /// <summary>
    /// The menu cell's pass rule. The prompt route is the Director's own send, so it is held to the delivery rule
    /// (<see cref="PassRules.MenuOpen"/>): refused in words or delivered, never reported delivered and lost, never arriving
    /// late after a refusal, and the next text arrives. On a desktop route the keys are the person's own, typed into the
    /// menu as a person's would be, so only the next text is judged: it must arrive alone, with nothing of the menu-time
    /// keys welded onto it. Pure.
    /// </summary>
    internal static (bool Pass, string Truth) MenuCellPass(string route, string firstTruth, string firstDirector, bool firstArrivedLate, string nextTruth, bool nextPass)
    {
        if (route != "prompt") return (nextPass, $"{firstTruth}/{(nextPass ? "NEXT-ARRIVED" : "NEXT-" + nextTruth)}");
        var firstPass = firstTruth == "ARRIVED" && firstDirector is "OK" or "DELIVERING";
        var (pass, truth) = PassRules.MenuOpen(firstTruth, firstDirector, firstPass, firstArrivedLate, nextTruth, nextPass);
        return (pass, $"{firstTruth}/{truth}");
    }

    /// <summary>A token of letters only (Q and ten of A to P), for text typed into a menu, where a digit picks an option.</summary>
    internal static string LetterToken() => "Q" + string.Concat(Guid.NewGuid().ToString("N")[..10].Select(ch => (char)('A' + Convert.ToInt32(ch.ToString(), 16))));

    // ===== usage-limit =====

    /// <summary>
    /// The byte-echo agent draws Claude Code's usage-limit dialog (<see cref="EchoAgent.UsageDialogRows"/>); the Director's
    /// guard reads it from the screen as it would Claude Code's. A desktop paste - a person at the terminal - must reach the
    /// agent (EXACT); desktop typing is the control. Each cell checks first that the Director reads the dialog as showing.
    /// </summary>
    private static async Task UsageLimitAsync(SessionManager manager, DesktopHost desktop, Options opts, string repo, IReadOnlyList<string> routes, int runs,
        Action<ConditionResult> record)
    {
        var used = routes.Where(r => r is "desktop-paste" or "desktop-type").ToList();
        if (used.Count == 0) { Console.WriteLine("[conditions] usage-limit: runs on desktop-paste and desktop-type only; neither was asked for."); return; }
        var log = Path.Combine(opts.Out, "echo-usage-limit.jsonl");
        var (exe, args) = EchoLayer.EchoCommand(log, usageDialog: true);
        var session = manager.CreateSession(repo, new RawCliAgent(exe, args), null, SessionBackendType.ConPty, null);
        try
        {
            await EchoLayer.WaitForScreenAsync(() => session.SnapshotScreenRows(), "Esc to cancel", TimeSpan.FromSeconds(30));
            await desktop.AttachAsync(session);
            foreach (var route in used)
            foreach (var text in TextsFor(route, opts))
            for (var run = 1; run <= runs; run++)
            {
                var c = new Cell("usage-limit", "echo", AgentKind.ClaudeCode, route, text, run, session, desktop, [repo], opts.Out);
                await Task.Delay(TimeSpan.FromSeconds(1));
                var rows = session.SnapshotScreenRows();
                var dialogScreen = ScreenDump.Save(opts.Out, c.ScreenName("dialog"), session, $"usage-limit {route} run {run}: the screen before the send");
                if (!CcDirector.Core.Sessions.UsageLimitDialog.IsShowing(rows))
                {
                    record(c.Result(0, "SETUP-FAILED", "", "-", false, null, false, 0, null, "the Director does not read the usage-limit dialog as showing", dialogScreen));
                    continue;
                }
                var body = text.Build(EchoLayer.Token());
                var before = EchoAgent.ReadLog(log).Count;
                var (director, detail, seconds, screen, _) = await c.SendAsync(body);
                var reads = await EchoLayer.QuietReadsAsync(log, before);
                var v = EchoAnalysis.Analyze(reads, body);
                record(c.Result(body.Length, director, detail, v.Verdict, v.Verdict == "EXACT", null, v.Pass && director == "OK", seconds,
                    v.Pass ? null : v.Detail, $"the Director read the dialog as showing before the send (screen {dialogScreen}); framed={(v.Framed ? "yes" : "no")} enters={v.Enters}",
                    screen ?? (v.Pass ? null : ScreenDump.Save(opts.Out, c.ScreenName("after"), session))));
            }
        }
        finally
        {
            await AgentMatrix.EndAsync(manager, session);
        }
    }

    // ===== shared =====

    /// <summary>Wait up to <paramref name="limit"/> for a user turn holding the token, then judge it as the text matrix does.</summary>
    private static async Task<(string Truth, bool Exact, string? Accepted, string? Difference)> JudgeAloneAsync(
        Cell c, string body, string token, DateTime sentAt, string director, TimeSpan limit)
    {
        RecordsVerdict verdict = new(false, false, false, false, null);
        string? turn = null;
        var deadline = DateTime.UtcNow + (director is "REFUSED" or "FAILED" ? TimeSpan.FromSeconds(20) : limit);
        while (DateTime.UtcNow < deadline)
        {
            verdict = AgentRecords.Check(c.Agent, sentAt, body, c.Repos);
            turn = AgentRecords.UserTurnText(c.Agent, sentAt, token);
            if (verdict.Found || turn is not null) break;
            await Task.Delay(1000);
        }
        return AgentMatrix.Verdict(c.Agent, body, verdict, turn);
    }

    private static bool IsCopy(string line)
    {
        try { using var doc = JsonDocument.Parse(line); return AgentRecords.IsCopyOfPrompt(doc.RootElement); }
        catch (JsonException) { return true; }
    }

    private static bool ScreenHolds(Session s, string token) => s.SnapshotScreenRows().Any(r => r.Contains(token, StringComparison.Ordinal));

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(500);
        }
        return condition();
    }

    internal static string Summary(IReadOnlyList<ConditionResult> results, IReadOnlyList<ConditionCase> conditions)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"condition",-12} {"agent",-7} {"route",-15} {"sends",5} {"pass",4} {"fail",4} {"not-counted",11} {"unsteady-cells",14}  truths");
        foreach (var g in results.GroupBy(r => (r.Condition, r.Agent, r.Route)))
        {
            var l = g.ToList();
            var counted = l.Where(r => r.Director != "SETUP-FAILED").ToList();
            sb.AppendLine($"{g.Key.Condition,-12} {g.Key.Agent,-7} {g.Key.Route,-15} {counted.Count,5} {counted.Count(r => r.Pass),4} {counted.Count(r => !r.Pass),4} " +
                          $"{l.Count - counted.Count,11} {AgentMatrix.Unsteady(counted, r => r.Text, r => r.Pass),14}  {string.Join(" ", counted.Select(r => r.Truth))}");
        }
        var bad = results.Where(r => r.Director != "SETUP-FAILED" && !r.Pass).ToList();
        var notCounted = results.Count(r => r.Director == "SETUP-FAILED");
        sb.AppendLine(bad.Count == 0 ? $"EVERY COUNTED CELL PASSED ({notCounted} not counted: the condition could not be set up)." : $"{bad.Count} of {results.Count - notCounted} counted cells did not pass ({notCounted} not counted):");
        foreach (var r in bad)
            sb.AppendLine($"  {r.Condition} {r.Agent} {r.Route} {r.Text} run {r.Run}: director={r.Director} truth={r.Truth} {r.Difference ?? r.Detail}{(r.ScreenFile is { } sf ? $" (screen: {sf})" : "")}".TrimEnd());
        foreach (var r in results.Where(r => r.Director == "SETUP-FAILED"))
            sb.AppendLine($"  not counted: {r.Condition} {r.Agent} {r.Route} run {r.Run}: {r.Setup}");
        sb.AppendLine("What each condition cannot show:");
        foreach (var c in conditions) sb.AppendLine($"  {c.Name}: {c.Cannot}.");
        return sb.ToString();
    }
}
