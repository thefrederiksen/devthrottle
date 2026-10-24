using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;

namespace CcDirector.DeliveryQualification;

public sealed record ConformanceExpected(
    IReadOnlyList<string> Composer,
    bool? Picker,
    bool? ModelPicker,
    bool? WorkingMarker,
    IReadOnlyList<string> Activity,
    string? RequiredPickerSign = null,
    string? ComposerText = null,
    IReadOnlyList<string>? RequiredScreenText = null);

public sealed record ConformanceAnswer(
    string Composer,
    string ComposerText,
    bool Picker,
    bool ModelPicker,
    bool WorkingMarker,
    string Activity,
    IReadOnlyList<string> PickerSigns);

public sealed record ConformanceFixture(
    string Agent,
    string Version,
    string State,
    DateTime CapturedAtUtc,
    int CursorRow,
    int CursorCol,
    bool CursorVisible,
    IReadOnlyList<string> Rows,
    IReadOnlyList<string>? RowsWithoutFaint,
    ConformanceAnswer Answer);

public sealed record ConformanceVerdict(bool Pass, IReadOnlyList<string> Differences);

/// <summary>The fixed answers each shipped screen reader must give for every command-line interface state.</summary>
public static class ConformanceExpectations
{
    public static readonly IReadOnlyList<string> States =
        ["start-up", "idle", "working", "permission-prompt", "question-menu", "model-picker", "trust-dialog", "multi-line-composer"];

    public static bool Applies(string agent, string state) => (agent, state) switch
    {
        ("pi", "permission-prompt" or "question-menu" or "trust-dialog") => false,
        ("codex", "question-menu") => false,
        _ => true,
    };

    public static ConformanceExpected For(string agent, string state)
    {
        if (!Applies(agent, state)) throw new ArgumentException($"State '{state}' does not apply to {agent}.");
        return (agent, state) switch
        {
            ("claude", "start-up") => new(["NotFound", "Empty"], false, false, false, ["Starting", "Working", "WaitingForInput"], RequiredScreenText: ["Claude Code v"]),
            ("codex", "start-up") => new(["NotFound", "Empty"], false, false, false, ["Starting", "Working", "WaitingForInput"], RequiredScreenText: ["OpenAI Codex"]),
            ("pi", "start-up") => new(["NotFound", "Empty"], false, false, false, ["Starting", "Working", "WaitingForInput"], RequiredScreenText: ["Pi can explain its own features"]),
            ("pi", "idle") => new(["NotFound"], false, false, false, ["WaitingForInput", "Idle"], RequiredScreenText: ["Pi can explain its own features", "thinking off"]),
            (_, "idle") => new(["Empty"], false, false, false, ["WaitingForInput", "Idle"]),
            ("claude", "working") => new(["NotFound", "Empty"], false, false, true, ["Working"]),
            (_, "working") => new(["NotFound", "Empty"], false, false, false, ["Working"]),
            (_, "permission-prompt") => new(["MenuOpen", "NotFound"], true, false, false, ["WaitingForPerm"]),
            (_, "question-menu") => new(["MenuOpen", "NotFound"], true, false, false, ["MenuOpen", "WaitingForPerm"]),
            ("claude", "model-picker") => new(["MenuOpen"], true, true, false, ["MenuOpen"], "enter-to-set-default"),
            ("pi", "model-picker") => new(["NotFound"], true, true, false, ["MenuOpen"], "pi-model-select"),
            ("codex", "model-picker") => new(["MenuOpen", "NotFound"], true, false, false, ["MenuOpen"]),
            (_, "trust-dialog") => new(["MenuOpen", "NotFound"], true, false, false, ["MenuOpen", "WaitingForPerm", "WaitingForInput"]),
            ("claude", "multi-line-composer") => new(["HoldsText"], false, false, false, ["Working"], ComposerText: "first line\n  second line"),
            ("pi", "multi-line-composer") => new(["NotFound"], false, false, false, ["Working"], RequiredScreenText: ["first line", "second line", "thinking off"]),
            (_, "multi-line-composer") => new(["HoldsText"], false, false, false, ["Working"], ComposerText: "first line\nsecond line"),
            _ => throw new ArgumentException($"No expected answer for {agent} {state}."),
        };
    }
}

/// <summary>Runs and replays the on-demand terminal classifier conformance check.</summary>
public static class Conformance
{
    private static readonly Regex UnsafeFileName = new("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FixtureName(string agent, string version, string state)
    {
        static string Safe(string value) => UnsafeFileName.Replace(value.ToLowerInvariant(), "-").Trim('-');
        return $"{Safe(agent)}-{Safe(version)}-{Safe(state)}.json";
    }

    public static ConformanceAnswer Classify(string agent, AgentKind kind, ScreenFrame frame, ActivityState activity)
    {
        var (composer, text) = DoorbellSafety.ReadComposerText(kind, frame);
        var picker = PickerOnScreen.Read(frame.Rows, kind.ToString());
        return new ConformanceAnswer(composer.ToString(), text, picker.Drawn, picker.ModelPickerDrawn,
            DoorbellSafety.ShowsWorking(frame.Rows), activity.ToString(), picker.Signs);
    }

    public static ConformanceVerdict Judge(ConformanceFixture fixture)
    {
        var kind = Kind(fixture.Agent);
        var frame = new ScreenFrame(fixture.Rows, fixture.CursorRow, fixture.CursorCol, fixture.CursorVisible, fixture.RowsWithoutFaint);
        if (!Enum.TryParse<ActivityState>(fixture.Answer.Activity, out var activity))
            throw new InvalidDataException($"Fixture activity '{fixture.Answer.Activity}' is not an ActivityState.");
        var actual = Classify(fixture.Agent, kind, frame, activity);
        var expected = ConformanceExpectations.For(fixture.Agent, fixture.State);
        var differences = new List<string>();
        if (!expected.Composer.Contains(actual.Composer, StringComparer.Ordinal)) differences.Add($"composer expected [{string.Join(",", expected.Composer)}], got {actual.Composer}");
        if (expected.Picker is { } picker && picker != actual.Picker) differences.Add($"picker expected {picker}, got {actual.Picker}");
        if (expected.ModelPicker is { } model && model != actual.ModelPicker) differences.Add($"model picker expected {model}, got {actual.ModelPicker}");
        if (expected.WorkingMarker is { } working && working != actual.WorkingMarker) differences.Add($"working marker expected {working}, got {actual.WorkingMarker}");
        if (!expected.Activity.Contains(actual.Activity, StringComparer.Ordinal)) differences.Add($"activity expected [{string.Join(",", expected.Activity)}], got {actual.Activity}");
        if (expected.RequiredPickerSign is { } sign && !actual.PickerSigns.Contains(sign, StringComparer.Ordinal)) differences.Add($"picker sign '{sign}' was absent; got [{string.Join(",", actual.PickerSigns)}]");
        if (expected.ComposerText is { } text && !string.Equals(text, actual.ComposerText, StringComparison.Ordinal)) differences.Add($"composer text expected '{text.Replace("\n", "\\n")}', got '{actual.ComposerText.Replace("\n", "\\n")}'");
        var screen = string.Join("\n", fixture.Rows);
        foreach (var required in expected.RequiredScreenText ?? [])
            if (!screen.Contains(required, StringComparison.Ordinal)) differences.Add($"screen text '{required}' was absent");
        return new ConformanceVerdict(differences.Count == 0, differences);
    }

    public static int Replay(string path)
    {
        var fixture = JsonSerializer.Deserialize<ConformanceFixture>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Fixture {path} was empty.");
        return PrintVerdict(fixture, Judge(fixture));
    }

    public static async Task<int> RunAsync(SessionManager manager, CcDirector.Core.Configuration.AgentOptions paths, Options options)
    {
        var fixtures = new List<ConformanceFixture>();
        using var desktop = new DesktopHost();
        foreach (var agent in options.Agents)
        {
            if (!Program.Agents.TryGetValue(agent, out var configured)) throw new ArgumentException($"Unknown agent '{agent}'.");
            var executable = agent switch { "claude" => paths.ClaudePath, "codex" => paths.CodexPath, "pi" => paths.PiPath, _ => agent };
            var version = Ledger.AgentVersion(executable);
            var args = options.ArgsOverride.TryGetValue(agent, out var custom) ? custom : configured.Args;
            var repo = Rig.EnsureRepo(Path.Combine(options.RepoRoot, $"conformance-{agent}"));
            var session = manager.CreateSession(repo, configured.Kind, args, SessionBackendType.ConPty, null);
            try
            {
                await desktop.AttachAsync(session);
                await RequireAsync(() => session.SnapshotScreenRows().Any(row => !string.IsNullOrWhiteSpace(row)), "start-up screen", TimeSpan.FromSeconds(30));
                Save(fixtures, options.Out, agent, version, "start-up", session);
                if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                    throw new InvalidOperationException("Conformance state 'idle' did not remain ready for three seconds within 120 seconds.");
                await Task.Delay(1000);
                Save(fixtures, options.Out, agent, version, "idle", session);

                await desktop.TypeAsync($"Run exactly this shell command and wait for it: {StateMatrix.LongTool(8)}");
                await desktop.EnterAsync();
                await RequireAsync(() => session.ActivityState == ActivityState.Working, "working", TimeSpan.FromSeconds(10));
                await Task.Delay(1000);
                Save(fixtures, options.Out, agent, version, "working", session);
                await RequireAsync(() => session.ActivityState is ActivityState.WaitingForInput or ActivityState.Idle, "turn end", TimeSpan.FromSeconds(90));

                await desktop.TypeAsync("/model");
                await desktop.EnterAsync();
                await RequireAsync(() => session.ActivityState == ActivityState.MenuOpen, "model picker", TimeSpan.FromSeconds(30));
                Save(fixtures, options.Out, agent, version, "model-picker", session);
                await session.CancelTurnAsync();
                await Task.Delay(1000);

                await desktop.TypeAsync("first line\nsecond line");
                await Task.Delay(1000);
                Save(fixtures, options.Out, agent, version, "multi-line-composer", session);
            }
            finally
            {
                await manager.KillSessionAsync(session.Id);
                manager.RemoveSession(session.Id);
            }

            if (ConformanceExpectations.Applies(agent, "permission-prompt"))
                await CapturePromptAsync(manager, desktop, fixtures, options.Out, repo, agent, version, configured.Kind,
                    agent == "claude" ? "--permission-mode default --model claude-haiku-4-5-20251001" : "-a on-request -s read-only -m gpt-5.6-luna -c model_reasoning_effort=low",
                    "permission-prompt", $"Use your shell tool to run exactly: touch conformance-{Guid.NewGuid():N}.txt");
            if (ConformanceExpectations.Applies(agent, "question-menu"))
                await CapturePromptAsync(manager, desktop, fixtures, options.Out, repo, agent, version, configured.Kind, args,
                    "question-menu", "Use the AskUserQuestion tool to ask me one question: red or blue. Do nothing else.");
            if (ConformanceExpectations.Applies(agent, "trust-dialog"))
                await CaptureTrustAsync(manager, fixtures, options, agent, version, configured.Kind, args);
        }

        var failed = 0;
        foreach (var fixture in fixtures) failed += PrintVerdict(fixture, Judge(fixture)) == 0 ? 0 : 1;
        File.WriteAllText(Path.Combine(options.Out, "conformance-summary.txt"), $"{fixtures.Count - failed} of {fixtures.Count} fixtures passed; {failed} failed.\n");
        if (fixtures.Count == 0) return 2;
        if (failed != 0) return 1;
        RecordPassingRun(fixtures);
        return 0;
    }

    private static async Task CapturePromptAsync(SessionManager manager, DesktopHost desktop, List<ConformanceFixture> fixtures,
        string output, string repo, string agent, string version, AgentKind kind, string args, string state, string prompt)
    {
        var session = manager.CreateSession(repo, kind, args, SessionBackendType.ConPty, null);
        try
        {
            await desktop.AttachAsync(session);
            if (!await Rig.WaitUntilIdleAsync(session, TimeSpan.FromSeconds(120)))
                throw new InvalidOperationException($"Conformance state '{state} idle' did not remain ready for three seconds within 120 seconds.");
            await desktop.TypeAsync(prompt);
            await desktop.EnterAsync();
            var expectedActivity = ConformanceExpectations.For(agent, state).Activity;
            await RequireAsync(() => expectedActivity.Contains(session.ActivityState.ToString(), StringComparer.Ordinal), state, TimeSpan.FromSeconds(90));
            Save(fixtures, output, agent, version, state, session);
            await session.CancelTurnAsync();
        }
        finally
        {
            await manager.KillSessionAsync(session.Id);
            manager.RemoveSession(session.Id);
        }
    }

    private static async Task CaptureTrustAsync(SessionManager manager, List<ConformanceFixture> fixtures, Options options,
        string agent, string version, AgentKind kind, string args)
    {
        var repo = Rig.EnsureRepo(Path.Combine(options.RepoRoot, "conformance-untrusted", $"{agent}-{Guid.NewGuid():N}"), trust: false);
        var session = manager.CreateSession(repo, kind, args, SessionBackendType.ConPty, null);
        try
        {
            await RequireAsync(() => PickerOnScreen.Read(session.SnapshotScreenRows(), kind.ToString()).Drawn, "trust dialog", TimeSpan.FromSeconds(60));
            Save(fixtures, options.Out, agent, version, "trust-dialog", session);
            await session.CancelTurnAsync();
        }
        finally
        {
            await manager.KillSessionAsync(session.Id);
            manager.RemoveSession(session.Id);
        }
    }

    private static void Save(List<ConformanceFixture> fixtures, string output, string agent, string version, string state, Session session)
    {
        var frame = session.SnapshotLiveFrame();
        var answer = Classify(agent, session.AgentKind, frame, session.ActivityState);
        var fixture = new ConformanceFixture(agent, version, state, DateTime.UtcNow, frame.CursorRow, frame.CursorCol,
            frame.CursorVisible, frame.Rows.ToArray(), frame.RowsWithoutFaint?.ToArray(), answer);
        var directory = Path.Combine(output, "fixtures");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FixtureName(agent, version, state));
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, Json));
        fixtures.Add(fixture);
        Console.WriteLine($"[conformance] saved {agent} {version} {state}: {path}");
    }

    private static async Task RequireAsync(Func<bool> condition, string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(250);
        }
        throw new InvalidOperationException($"Conformance state '{name}' did not appear within {timeout.TotalSeconds:F0} seconds.");
    }

    private static int PrintVerdict(ConformanceFixture fixture, ConformanceVerdict verdict)
    {
        Console.WriteLine($"[conformance] {fixture.Agent} {fixture.Version} {fixture.State}: {(verdict.Pass ? "PASS" : "FAIL")}");
        foreach (var difference in verdict.Differences) Console.WriteLine($"  {difference}");
        return verdict.Pass ? 0 : 1;
    }

    private static void RecordPassingRun(IReadOnlyList<ConformanceFixture> fixtures)
    {
        ValidateComplete(fixtures);
        var root = FindRepositoryRoot();
        var manifestPath = Path.Combine(root, CliConformance.ManifestRelativePath);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException($"Conformance manifest {manifestPath} was empty.");
        foreach (var group in fixtures.GroupBy(fixture => (fixture.Agent, fixture.Version)))
        {
            var label = group.Key.Agent switch { "claude" => "Claude Code", "codex" => "Codex", "pi" => "Pi", _ => throw new ArgumentException($"Unknown agent '{group.Key.Agent}'.") };
            if (!manifest.TryGetValue(label, out var versions)) throw new InvalidDataException($"Conformance manifest has no '{label}' entry.");
            if (!versions.Contains(group.Key.Version, StringComparer.Ordinal)) versions.Add(group.Key.Version);
        }
        foreach (var versions in manifest.Values) versions.Sort(StringComparer.Ordinal);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, Json) + "\n");

        var fixtureDirectory = Path.Combine(root, "src", "CcDirector.DeliveryQualification", "docs", "conformance-4385-2026-10-24", "fixtures", "passing");
        Directory.CreateDirectory(fixtureDirectory);
        foreach (var fixture in fixtures)
            File.WriteAllText(Path.Combine(fixtureDirectory, FixtureName(fixture.Agent, fixture.Version, fixture.State)), JsonSerializer.Serialize(fixture, Json) + "\n");
        Console.WriteLine($"[conformance] recorded {fixtures.Count} passing fixtures and updated {CliConformance.ManifestRelativePath}");
    }

    /// <summary>Refuse to record a passing version unless every applicable fixed state appears exactly once.</summary>
    internal static void ValidateComplete(IReadOnlyList<ConformanceFixture> fixtures)
    {
        foreach (var group in fixtures.GroupBy(fixture => (fixture.Agent, fixture.Version)))
        {
            var expected = ConformanceExpectations.States.Where(state => ConformanceExpectations.Applies(group.Key.Agent, state)).ToHashSet(StringComparer.Ordinal);
            var actual = group.Select(fixture => fixture.State).ToList();
            var actualSet = actual.ToHashSet(StringComparer.Ordinal);
            if (!expected.SetEquals(actualSet) || actual.Count != actualSet.Count)
                throw new InvalidDataException($"Refusing to record {group.Key.Agent} {group.Key.Version}: expected states [{string.Join(",", expected)}], captured [{string.Join(",", actual)}].");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, CliConformance.ManifestRelativePath))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find the repository root containing {CliConformance.ManifestRelativePath}; no passing version was recorded.");
    }

    private static AgentKind Kind(string agent) => agent switch
    {
        "claude" => AgentKind.ClaudeCode,
        "codex" => AgentKind.Codex,
        "pi" => AgentKind.Pi,
        _ => throw new ArgumentException($"Unknown conformance agent '{agent}'."),
    };
}
