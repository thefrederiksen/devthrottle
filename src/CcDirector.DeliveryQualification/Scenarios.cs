using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;

namespace CcDirector.DeliveryQualification;

/// <summary>
/// One attempt of one scenario: what the Director said, what the agent's own records say, and whether that is a pass.
/// </summary>
/// <param name="Director">OK (the send returned delivered), REFUSED (nothing typed, said so), DELIVERING (the send
/// returned still delivering), FAILED (the send threw), HUNG (did not return in time), SETUP-FAILED (the scenario
/// could not create its precondition - not counted in a rate).</param>
/// <param name="Truth">ARRIVED (the text is in exactly one user turn, alone), LOST (in no record), PARTIAL (its start
/// only), DOUBLED (in two turns), WELDED (in one turn together with other text).</param>
public sealed record ScenarioResult(
    string Scenario, string Agent, int Length, string Director, string Truth, bool Pass, double Seconds,
    string Detail, string? Screen);

/// <summary>
/// THE DELIVERY SCENARIOS (Prompt Delivery mission, 16 October 2026). Each scenario puts a real agent in a real
/// terminal - through the Director's own <see cref="SessionManager"/>, pseudo-terminal, screen parser and composer
/// reader - into one of the states in which prompts were lost in the field, then sends through the Director's own send
/// path (<see cref="Session.SendTextAsync"/> or <see cref="Session.DeliverPrePromptAsync"/>). A scenario PASSES only when
/// the agent's own conversation records hold the text exactly once, alone, in a user turn. The screen is never the
/// proof, and neither is the Director's verdict: both are recorded beside the truth so a send the Director called
/// delivered that never arrived (or arrived welded to other text) is visible as its own failure.
///
/// Every text asks for a one-word reply and no tools, so an attempt costs a few thousand tokens.
/// </summary>
public static class Scenarios
{
    private static readonly SubmissionProvenance RigTyped = SubmissionProvenance.Typed("delivery-qa-rig", "rig");

    /// <summary>Every scenario, in the order a run takes them, and the agents each applies to. "*" is every agent.</summary>
    public static readonly IReadOnlyList<(string Name, string Agents, string What)> All =
    [
        ("first", "*", "a new session's first prompt, gated, as a spawn sends it"),
        ("idle-short", "*", "a one-line prompt to a waiting session (a phone dictation)"),
        ("idle-long-line", "*", "a long single line (over the typing limit, so pasted)"),
        ("idle-multiline-crlf", "*", "several paragraphs with Windows line endings (a schedule seed)"),
        ("idle-special", "*", "backslashes, a literal backslash-t, quotes, shell characters (#2981)"),
        ("idle-unicode", "*", "accents, dashes and curly quotes (dictation output)"),
        ("idle-huge", "*", "a ten-thousand-character brief (the file route)"),
        ("busy", "claude,pi", "a prompt sent while the agent runs a shell command"),
        ("on-screen-repeat", "claude", "a prompt whose exact words are already visible above the composer"),
        ("shell-bang", "claude", "a '! command' (Claude Code shell mode) whose words are already on screen"),
        ("shell-bang-fresh", "claude", "a '! command' whose words are not on screen (the control for shell-bang)"),
        ("menu-open", "claude", "a prompt sent while the agent shows a question menu (the session is red)"),
        ("owner-draft", "claude,pi", "a prompt sent while the owner has unsent text in the composer (#2845)"),
        ("leftover-line", "claude,pi", "a prompt sent while an unsubmitted framework line sits in the composer"),
        ("resume", "claude", "the first prompt of a session resumed on an existing conversation (#3360)"),
        ("untrusted-folder", "claude", "the first prompt of a session in a folder Claude Code has never trusted (#2898)"),
    ];

    public static bool AppliesTo(string scenario, string agent)
    {
        var entry = All.FirstOrDefault(s => s.Name == scenario);
        if (entry.Name is null) throw new ArgumentException($"No scenario named '{scenario}'. Known: {string.Join(", ", All.Select(s => s.Name))}");
        return entry.Agents == "*" || entry.Agents.Split(',').Contains(agent);
    }

    public static async Task<ScenarioResult> RunAsync(
        string scenario, SessionManager manager, string agent, AgentKind kind, string? args, string repo, string repoRoot,
        IReadOnlyList<string> repos)
    {
        Console.WriteLine($"[scenario] {scenario} on {agent}: {All.First(s => s.Name == scenario).What}");
        var sendBegan = new StrongBox<bool>(false);
        SendBegan.Value = sendBegan;
        try
        {
            return scenario switch
            {
                "first" => await FirstAsync(manager, agent, kind, args, repo, repos),
                "busy" => await BusyAsync(manager, agent, kind, args, repo, repos),
                "on-screen-repeat" => await OnScreenRepeatAsync(manager, agent, kind, args, repo, repos),
                "shell-bang" => await ShellBangAsync(manager, agent, kind, args, repo, onScreen: true),
                "shell-bang-fresh" => await ShellBangAsync(manager, agent, kind, args, repo, onScreen: false),
                "menu-open" => await MenuOpenAsync(manager, agent, kind, args, repo, repos),
                "owner-draft" => await LeftInComposerAsync(manager, agent, kind, args, repo, repos, ownerTyped: true),
                "leftover-line" => await LeftInComposerAsync(manager, agent, kind, args, repo, repos, ownerTyped: false),
                "resume" => await ResumeAsync(manager, agent, kind, args, repo, repos),
                "untrusted-folder" => await UntrustedAsync(manager, agent, kind, args, repoRoot),
                _ when scenario.StartsWith("idle-", StringComparison.Ordinal) =>
                    await IdleAsync(manager, agent, kind, args, repo, repos, TextCatalogue.Named(scenario["idle-".Length..])),
                _ => throw new ArgumentException($"No scenario named '{scenario}'."),
            };
        }
        catch (Exception ex)
        {
            // THE SETUP BOUNDARY IS THE FIRST SEND, NOT THE POSITION OF THIS CATCH (review of pull request 4069, finding 4).
            // Before any prompt was sent, a failure says nothing about delivery and is left out of every rate. After one
            // was sent - it may have been lost - the attempt is a counted failure.
            if (sendBegan.Value)
            {
                Console.WriteLine($"[scenario] {scenario} on {agent}: RIG ERROR after the send: {ex}");
                return new ScenarioResult(scenario, agent, 0, "RIG-ERROR", "UNKNOWN", false, 0, $"{ex.GetType().Name}: {ex.Message}", null);
            }
            Console.WriteLine($"[scenario] {scenario} on {agent}: SETUP FAILED: {ex}");
            return new ScenarioResult(scenario, agent, 0, "SETUP-FAILED", "-", false, 0, $"{ex.GetType().Name}: {ex.Message}", null);
        }
        finally
        {
            SendBegan.Value = null;
        }
    }

    /// <summary>Set by <see cref="TimeSendAsync"/> when the scenario's own prompt is sent; the box is shared with the
    /// scenario's catch, which an async local's own value would not be.</summary>
    private static readonly AsyncLocal<StrongBox<bool>?> SendBegan = new();

    // ===== The scenarios =====

    private static Task<ScenarioResult> FirstAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo, IReadOnlyList<string> repos) =>
        WithSessionAsync(m, kind, args, repo, async s =>
        {
            var text = TextCatalogue.Named("short").Build(Token());
            return await SendAndJudgeAsync("first", agent, s, text, repos, () => s.DeliverPrePromptAsync(text, TimeSpan.FromSeconds(30)));
        });

    private static Task<ScenarioResult> IdleAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo, IReadOnlyList<string> repos, TextShape shape) =>
        WithReadySessionAsync(m, agent, kind, args, repo, repos, async s =>
        {
            var text = shape.Build(Token());
            return await SendAndJudgeAsync("idle-" + shape.Name, agent, s, text, repos, () => s.SendTextAsync(text, RigTyped));
        });

    private static Task<ScenarioResult> BusyAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo, IReadOnlyList<string> repos) =>
        WithReadySessionAsync(m, agent, kind, args, repo, repos, async s =>
        {
            await s.SendTextAsync("Run exactly this shell command and nothing else, then reply with the word FINISHED: sleep 40", RigTyped);
            if (!await WaitForAsync(() => DoorbellSafety.ShowsWorking(s.SnapshotScreenRows()) || s.ActivityState == ActivityState.Working, TimeSpan.FromSeconds(60)))
                throw new InvalidOperationException("the agent never showed it was working on the shell command");
            await Task.Delay(TimeSpan.FromSeconds(4));
            var text = TextCatalogue.Named("short").Build(Token());
            return await SendAndJudgeAsync("busy", agent, s, text, repos, () => s.SendTextAsync(text, RigTyped), recordsWait: TimeSpan.FromSeconds(150));
        });

    /// <summary>The agent prints the exact words first, so they are visible above the composer when they are sent.</summary>
    private static Task<ScenarioResult> OnScreenRepeatAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo, IReadOnlyList<string> repos) =>
        WithReadySessionAsync(m, agent, kind, args, repo, repos, async s =>
        {
            var token = Token();
            var text = $"Please reply with exactly ACK {token} and nothing else";
            await s.SendTextAsync($"Do not run any tools. Reply with exactly this line and nothing else: {text}", RigTyped);
            if (!await WaitForAsync(() => ScreenHolds(s, token) && Idle(s), TimeSpan.FromSeconds(90)))
                throw new InvalidOperationException("the agent did not print the words to repeat");
            await Task.Delay(TimeSpan.FromSeconds(3));
            return await SendAndJudgeAsync("on-screen-repeat", agent, s, text, repos, () => s.SendTextAsync(text, RigTyped), copiesExpected: 2);
        });

    /// <summary>
    /// A '! command' typed into Claude Code runs in its shell mode; Claude Code records it as a user line
    /// "&lt;bash-input&gt; command&lt;/bash-input&gt;". With <paramref name="onScreen"/> the agent first prints the
    /// command - exactly what happens in the field, where the agent tells the owner "type ! sudo ..." and they do.
    /// </summary>
    private static Task<ScenarioResult> ShellBangAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo, bool onScreen) =>
        WithReadySessionAsync(m, agent, kind, args, repo, [repo], async s =>
        {
            var token = Token();
            var text = $"! echo BANG{token}";
            var name = onScreen ? "shell-bang" : "shell-bang-fresh";
            if (onScreen)
            {
                await s.SendTextAsync($"Do not run any tools. To run it, I will type this exact line: {text}  - reply with only the word READY.", RigTyped);
                if (!await WaitForAsync(() => ScreenHolds(s, token) && Idle(s), TimeSpan.FromSeconds(90)))
                    throw new InvalidOperationException("the agent did not finish its reply");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            var sentAt = DateTime.UtcNow;
            var (director, detail, seconds) = await TimeSendAsync(() => s.SendTextAsync(text, RigTyped));
            var copies = 0;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline && (copies = ClaudeShellInputs(repo, sentAt, "BANG" + token)) == 0)
                await Task.Delay(1000);
            var truth = copies == 1 ? "ARRIVED" : copies == 0 ? "LOST" : "DOUBLED";
            return Result(name, agent, s, text, director, truth, seconds, detail);
        });

    /// <summary>The agent opens its question menu (AskUserQuestion) and the prompt arrives while it is on screen: the
    /// moment the session is red and the owner most wants to answer.</summary>
    private static Task<ScenarioResult> MenuOpenAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo, IReadOnlyList<string> repos) =>
        WithReadySessionAsync(m, agent, kind, args, repo, repos, async s =>
        {
            await s.SendTextAsync("Use the AskUserQuestion tool right now to ask me one question: \"Which colour?\" with the two options Red and Blue. Do nothing else.", RigTyped);
            if (!await WaitForAsync(() => DoorbellSafety.ReadComposer(kind, s.SnapshotLiveFrame()) is ComposerReading.MenuOpen or ComposerReading.NotFound
                                          && !DoorbellSafety.ShowsWorking(s.SnapshotScreenRows()), TimeSpan.FromSeconds(90)))
                throw new InvalidOperationException("the agent never showed its question menu");
            await Task.Delay(TimeSpan.FromSeconds(2));
            var before = DoorbellSafety.ReadComposer(kind, s.SnapshotLiveFrame());
            var text = TextCatalogue.Named("short").Build(Token());
            var firstSentAt = DateTime.UtcNow;
            var first = await SendAndJudgeAsync("menu-open", agent, s, text, repos, () => s.SendTextAsync(text, RigTyped));

            // A prompt cannot arrive through a menu. What the owner needs is one plain refusal, and then the NEXT prompt,
            // once the menu is answered, to go through: on 15 October every later send was refused instead. So the menu
            // is closed (Escape, as an owner dismissing the question would) and a second prompt is sent; the scenario
            // passes when the first was refused or arrived, nothing was lost silently, and the second ARRIVED.
            s.SendInput([0x1B], null, SubmissionProvenance.Typed("desktop-terminal", "owner"));
            if (!await WaitForAsync(() => DoorbellSafety.ReadComposer(kind, s.SnapshotLiveFrame()) is ComposerReading.Empty or ComposerReading.HoldsText
                                          && Idle(s), TimeSpan.FromSeconds(60)))
                return first with { Pass = false, Detail = $"composer before the send: {before}. The menu never closed after Escape. {first.Detail}" };
            await Task.Delay(TimeSpan.FromSeconds(2));
            var nextText = TextCatalogue.Named("short").Build(Token());
            var next = await SendAndJudgeAsync("menu-open (next prompt)", agent, s, nextText, repos, () => s.SendTextAsync(nextText, RigTyped));
            // The first prompt is looked for again (review finding 3): one reported failed that reached the agent once the
            // menu closed is a false alarm.
            var firstArrivedLate = first.Truth != "ARRIVED" && AgentRecords.Check(agent, firstSentAt, text, repos).Found;
            var (pass, truth) = PassRules.MenuOpen(first.Truth, first.Director, first.Pass, firstArrivedLate, next.Truth, next.Pass);
            return first with
            {
                Truth = truth,
                Pass = pass,
                Detail = $"composer before the send: {before}. First: director={first.Director} truth={first.Truth}, arrived late={firstArrivedLate}. " +
                         $"Next prompt after the menu closed: director={next.Director} truth={next.Truth} {next.Detail}. {first.Detail}",
            };
        });

    /// <summary>
    /// Text sits unsent in the composer - the owner's own draft (<paramref name="ownerTyped"/>, typed through the same
    /// door as the owner's keyboard) or a framework line that was typed and never submitted (the doorbell line left by
    /// a submit that could not be verified, seen welded to an owner prompt on 15 October 2026) - and a prompt arrives.
    /// Right answers: the prompt arrives ALONE, or the send is refused and the unsent text is left as it was. Wrong:
    /// the two submitted as one turn (WELDED), or the unsent text destroyed.
    /// </summary>
    private static Task<ScenarioResult> LeftInComposerAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo,
        IReadOnlyList<string> repos, bool ownerTyped) =>
        WithReadySessionAsync(m, agent, kind, args, repo, repos, async s =>
        {
            var name = ownerTyped ? "owner-draft" : "leftover-line";
            var leftToken = Token();
            var left = ownerTyped ? $"my unfinished draft {leftToken}" : $"[DevThrottle doorbell] {leftToken} fleet message is waiting. To read, run: cc-devthrottle message inbox";
            s.SendInput(Encoding.UTF8.GetBytes(left), null, ownerTyped ? SubmissionProvenance.Typed("desktop-terminal", "owner") : SubmissionProvenance.FrameworkText());
            if (!await WaitForAsync(() => ScreenHolds(s, leftToken), TimeSpan.FromSeconds(20)))
                throw new InvalidOperationException("the unsent text never appeared in the composer");
            await Task.Delay(TimeSpan.FromSeconds(1));
            var text = TextCatalogue.Named("short").Build(Token());
            var sentAt = DateTime.UtcNow;
            var r = await SendAndJudgeAsync(name, agent, s, text, repos, () => s.SendTextAsync(text, RigTyped));
            var leftSubmitted = AgentRecords.Check(agent, sentAt, leftToken, repos).Found || AgentRecords.Check(agent, sentAt, left, repos).Found;
            var leftOnScreen = ScreenHolds(s, leftToken);
            var (pass, truth) = PassRules.LeftInComposer(ownerTyped, r.Truth, r.Director, leftSubmitted, leftOnScreen);
            return r with
            {
                Truth = truth, Pass = pass,
                Detail = $"unsent text submitted={leftSubmitted}, still on screen after={leftOnScreen}. {r.Detail}",
            };
        });

    /// <summary>A conversation is made, its session ended, and a new session resumes it and is sent its first prompt.</summary>
    private static async Task<ScenarioResult> ResumeAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo, IReadOnlyList<string> repos)
    {
        var since = DateTime.UtcNow;
        var seedToken = Token();
        string? conversation = null;
        await WithReadySessionAsync(m, agent, kind, args, repo, repos, async s =>
        {
            await s.SendTextAsync(TextCatalogue.Named("short").Build(seedToken), RigTyped);
            await WaitForAsync(() => (conversation = ClaudeConversationHolding(repo, since, seedToken)) is not null && Idle(s), TimeSpan.FromSeconds(90));
            return null!;
        });
        if (conversation is null) throw new InvalidOperationException("the conversation to resume was never written");
        Console.WriteLine($"[scenario] resume: resuming conversation {conversation}");
        var session = m.CreateSession(repo, kind, args, SessionBackendType.ConPty, conversation);
        try
        {
            var text = TextCatalogue.Named("short").Build(Token());
            return await SendAndJudgeAsync("resume", agent, session, text, repos, () => session.DeliverPrePromptAsync(text, TimeSpan.FromSeconds(30)));
        }
        finally { await EndAsync(m, session); }
    }

    /// <summary>A brand-new folder no one has opened Claude Code in: the agent stops on its folder-trust question.</summary>
    private static async Task<ScenarioResult> UntrustedAsync(SessionManager m, string agent, AgentKind kind, string? args, string repoRoot)
    {
        var folder = Rig.EnsureRepo(Path.Combine(repoRoot, "untrusted", "u" + Guid.NewGuid().ToString("N")[..8]), trust: false);
        var session = m.CreateSession(folder, kind, args, SessionBackendType.ConPty, null);
        try
        {
            var text = TextCatalogue.Named("short").Build(Token());
            // The gate's own limit is ten minutes; the rig gives it ninety seconds, which is plenty for an agent that is
            // going to start at all on this machine, and reports the gate's own words.
            return await SendAndJudgeAsync("untrusted-folder", agent, session, text, [folder],
                () => session.DeliverFirstPromptAsync(text, SubmissionProvenance.FrameworkText(), TimeSpan.FromSeconds(90)));
        }
        finally { await EndAsync(m, session); }
    }

    // ===== The judge =====

    /// <summary>Send, then read the agent's own records until the text is there (or the wait ends).</summary>
    private static async Task<ScenarioResult> SendAndJudgeAsync(
        string scenario, string agent, Session s, string text, IReadOnlyList<string> repos, Func<Task> send,
        TimeSpan? recordsWait = null, int copiesExpected = 1)
    {
        var sentAt = DateTime.UtcNow;
        var (director, detail, seconds) = await TimeSendAsync(send);
        RecordsVerdict verdict = new(false, false, false, false, null);
        var deadline = DateTime.UtcNow + (recordsWait ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < deadline)
        {
            verdict = AgentRecords.Check(agent, sentAt, text, repos);
            if (verdict.Found) break;
            await Task.Delay(1000);
        }
        // The on-screen-repeat text is in the records twice by design: once inside the instruction that made the agent
        // print it, once as the prompt itself; only the prompt counts, so the check is "a turn holding it alone".
        var truth = verdict.Found ? (verdict.Doubled && copiesExpected == 1 ? "DOUBLED" : "ARRIVED") : verdict.Partial ? "PARTIAL" : "LOST";
        if (copiesExpected > 1 && agent == "claude")
        {
            // The instruction that made the agent print the words was sent BEFORE this send, so only turns since it count;
            // they are waited for, since the instruction's own copy ends the records wait at once (review note).
            var (holding, exactly) = (0, 0);
            while ((holding = ClaudeTurnsHolding(repos[0], sentAt, text)) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(1000);
            exactly = ClaudeTurnsExactly(repos[0], sentAt, text);
            truth = holding == 0 ? "LOST" : holding > 1 ? "DOUBLED" : exactly == 1 ? "ARRIVED" : "WELDED";
        }
        return Result(scenario, agent, s, text, director, truth, seconds, detail);
    }

    private static ScenarioResult Result(string scenario, string agent, Session s, string text, string director, string truth, double seconds, string detail)
    {
        var screen = truth == "ARRIVED" && director == "OK"
            ? null
            : string.Join("\n", s.SnapshotScreenRows().Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(12));
        // A text that arrived while the Director said it failed is a FALSE ALARM, and not a pass: the owner is told to
        // send again, and the agent then gets it twice.
        var r = new ScenarioResult(scenario, agent, text.Length, director, truth, truth == "ARRIVED" && director is "OK" or "DELIVERING",
            Math.Round(seconds, 1), detail, screen);
        Console.WriteLine($"[scenario] {scenario,-18} {agent,-7} len={text.Length,-6} director={director,-10} truth={truth,-8} {(r.Pass ? "PASS" : "FAIL")} {seconds:F1}s {Short(detail)}");
        if (screen is not null) Console.WriteLine("           screen: " + screen.Replace("\n", "\n                   "));
        return r;
    }

    private static async Task<(string Director, string Detail, double Seconds)> TimeSendAsync(Func<Task> send)
    {
        if (SendBegan.Value is { } began) began.Value = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var task = send();
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(12))) != task)
                return ("HUNG", "the send did not return within 12 minutes", sw.Elapsed.TotalSeconds);
            await task;
            if (task is Task<TextSendOutcome> withOutcome)
            {
                var o = withOutcome.Result;
                if (o.NothingTyped) return ("REFUSED", o.Reason ?? "", sw.Elapsed.TotalSeconds);
                if (!o.Confirmed) return ("DELIVERING", o.Reason ?? "", sw.Elapsed.TotalSeconds);
            }
            return ("OK", "", sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            return ("FAILED", $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed.TotalSeconds);
        }
    }

    // ===== Sessions =====

    private static async Task<ScenarioResult> WithSessionAsync(SessionManager m, AgentKind kind, string? args, string repo, Func<Session, Task<ScenarioResult>> body)
    {
        var session = m.CreateSession(repo, kind, args, SessionBackendType.ConPty, null);
        try { return await body(session); }
        finally { await EndAsync(m, session); }
    }

    /// <summary>A session that has taken a first prompt and is waiting again - the state every "later prompt"
    /// scenario starts from. A first prompt that does not arrive is a setup failure, not a result of this scenario.</summary>
    private static Task<ScenarioResult> WithReadySessionAsync(SessionManager m, string agent, AgentKind kind, string? args, string repo,
        IReadOnlyList<string> repos, Func<Session, Task<ScenarioResult>> body) =>
        WithSessionAsync(m, kind, args, repo, async s =>
        {
            var warm = $"Do not run any tools or read any files. Reply with exactly: WARM {Token()}";
            var since = DateTime.UtcNow;
            await s.DeliverPrePromptAsync(warm, TimeSpan.FromSeconds(30));
            if (!await WaitForAsync(() => AgentRecords.Check(agent, since, warm, repos).Found, TimeSpan.FromSeconds(90)))
                throw new InvalidOperationException("the warm-up prompt never reached the agent's records");
            if (!await Rig.WaitUntilIdleAsync(s, TimeSpan.FromSeconds(120)))
                throw new InvalidOperationException("the agent never went idle after the warm-up prompt");
            await Task.Delay(TimeSpan.FromSeconds(2));
            return await body(s);
        });

    private static async Task EndAsync(SessionManager m, Session s)
    {
        try { await m.KillSessionAsync(s.Id); } catch (Exception ex) { Console.WriteLine($"[scenario] kill {s.Id}: {ex.Message}"); }
        m.RemoveSession(s.Id);
    }

    // ===== Small readers =====

    private static string Token() => $"Q{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";

    private static bool Idle(Session s) => s.ActivityState is ActivityState.WaitingForInput or ActivityState.Idle
                                           && !DoorbellSafety.ShowsWorking(s.SnapshotScreenRows());

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

    private static string Short(string s) => s.Length > 240 ? s[..240] + "..." : s;

    private static IEnumerable<string> ClaudeFilesFor(string repo, DateTime sinceUtc)
    {
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var key = Path.GetFileName(repo.TrimEnd('\\', '/'));
        if (!Directory.Exists(projects)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(projects).Where(d => Path.GetFileName(d).Contains("delivery-qa", StringComparison.OrdinalIgnoreCase)
                                                                                 && Path.GetFileName(d).EndsWith(key, StringComparison.OrdinalIgnoreCase)))
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").Where(f => File.GetLastWriteTimeUtc(f) >= sinceUtc.AddSeconds(-5)))
            yield return file;
    }

    private static IEnumerable<JsonElement> ClaudeUserTurns(string repo, DateTime sinceUtc)
    {
        foreach (var file in ClaudeFilesFor(repo, sinceUtc))
        {
            string[] lines;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                lines = reader.ReadToEnd().Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement root;
                try { root = JsonDocument.Parse(line).RootElement.Clone(); } catch (JsonException) { continue; }
                if (root.TryGetProperty("type", out var t) && t.GetString() == "user"
                    && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
                    && root.TryGetProperty("timestamp", out var ts) && DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
                    && at >= sinceUtc.AddSeconds(-2))
                    yield return root;
            }
        }
    }

    private static string UserContent(JsonElement turn)
    {
        if (!turn.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" && part.TryGetProperty("text", out var tx))
                sb.Append(tx.GetString());
        return sb.ToString();
    }

    /// <summary>User turns since <paramref name="sinceUtc"/> whose whole text is <paramref name="text"/>.</summary>
    private static int ClaudeTurnsExactly(string repo, DateTime sinceUtc, string text)
    {
        var needle = AgentRecords.Normalize(text);
        return ClaudeUserTurns(repo, sinceUtc).Count(t => AgentRecords.Normalize(UserContent(t)) == needle);
    }

    /// <summary>User turns since <paramref name="sinceUtc"/> whose text holds <paramref name="text"/>.</summary>
    private static int ClaudeTurnsHolding(string repo, DateTime sinceUtc, string text)
    {
        var needle = AgentRecords.Normalize(text);
        return ClaudeUserTurns(repo, sinceUtc).Count(t => AgentRecords.Normalize(UserContent(t)).Contains(needle, StringComparison.Ordinal));
    }

    /// <summary>Shell-mode inputs since <paramref name="sinceUtc"/> holding <paramref name="token"/>.</summary>
    private static int ClaudeShellInputs(string repo, DateTime sinceUtc, string token) =>
        ClaudeUserTurns(repo, sinceUtc).Count(t => UserContent(t) is var c && c.Contains("<bash-input>", StringComparison.Ordinal)
                                                   && c.Contains(token, StringComparison.Ordinal));

    /// <summary>The id of the conversation that holds <paramref name="token"/> - the file name Claude Code resumes by.</summary>
    private static string? ClaudeConversationHolding(string repo, DateTime sinceUtc, string token) =>
        ClaudeFilesFor(repo, sinceUtc)
            .FirstOrDefault(f => File.ReadAllText(f).Contains(token, StringComparison.Ordinal)) is { } file
            ? Path.GetFileNameWithoutExtension(file)
            : null;
}

/// <summary>
/// The scenario pass rules that are more than "arrived once, alone, and not reported failed", kept apart from the
/// scenarios that drive real agents so they can be tested on their own.
/// </summary>
public static class PassRules
{
    /// <summary>
    /// Text sat unsent in the composer when the prompt arrived. WELDED - the prompt and that text in one turn, or the text
    /// submitted at all - always fails. For the OWNER'S draft the draft must also still be on screen afterwards: a product
    /// that clears the composer before typing destroys it, which is the outcome issue 2845 asks to detect (review of pull
    /// request 4069, finding 2). The PRODUCT'S own leftover line may be cleared: the prompt arriving alone passes.
    /// Refused with the text left exactly where it was passes for both.
    /// </summary>
    public static (bool Pass, string Truth) LeftInComposer(bool ownerTyped, string truth, string director, bool leftSubmitted, bool leftOnScreen)
    {
        var welded = truth == "DOUBLED" || (truth == "ARRIVED" && leftSubmitted);
        if (welded) return (false, "WELDED");
        if (leftSubmitted) return (false, truth);
        var refusedLeavingIt = truth == "LOST" && director is "REFUSED" or "FAILED" && leftOnScreen;
        if (ownerTyped)
        {
            if (!leftOnScreen) return (false, "DRAFT-DESTROYED");
            return (refusedLeavingIt || truth == "ARRIVED", truth);
        }
        return (truth == "ARRIVED" || refusedLeavingIt, truth);
    }

    /// <summary>
    /// A prompt sent while a question menu is open, then the next prompt once the menu is closed. Passes when the first
    /// either arrived (and was not reported failed) or was refused plainly - lost and reported failed, and it did NOT turn
    /// up later - and the next prompt passed. The truth word names what failed: LATE for a first prompt that arrived after
    /// it was reported failed (a false alarm), NEXT-... for a failed next prompt.
    /// </summary>
    public static (bool Pass, string Truth) MenuOpen(string firstTruth, string firstDirector, bool firstPass, bool firstArrivedLate, string nextTruth, bool nextPass)
    {
        if (firstArrivedLate) return (false, "LATE");
        var refusedPlainly = firstTruth == "LOST" && firstDirector is "FAILED" or "REFUSED";
        if (!firstPass && !refusedPlainly) return (false, firstTruth);
        if (!nextPass) return (false, "NEXT-" + nextTruth);
        return (true, firstTruth);
    }
}
