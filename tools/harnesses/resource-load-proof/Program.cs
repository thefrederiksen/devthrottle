// The live load harness for the Resource Monitor (issue #2811, mission section 8). See README.md.
//
//   dotnet run --project tools/harnesses/resource-load-proof -- --idle --cpu --memory --report out/
//
// Each step writes a screenshot and a reading into the report folder, and summary.json / summary.md at the end.
// The exit code is 0 only when every step that ran passed.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CcDirector.DeliveryQualification;
using ResourceLoadProof;

return Harness.Run(args);

internal static class Harness
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        Directory.CreateDirectory(options.ReportDirectory);
        Console.WriteLine($"[run] {Environment.MachineName}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors");
        Console.WriteLine($"[run] comparing against {OsMonitor.MonitorName}; report in {Path.GetFullPath(options.ReportDirectory)}");

        var run = new Steps(options);
        var results = new List<StepResult>();
        foreach (var (enabled, name, step) in new (bool, string, Func<StepResult>)[]
                 {
                     (options.Idle, "1-idle-agreement", run.IdleAgreement),
                     (options.Cpu, "2-processor-red-and-clear", run.ProcessorRedAndClear),
                     (options.Memory, "3-memory-amber-and-clear", run.MemoryAmberAndClear),
                     (options.Gateway, "4-gateway-no-gap", run.GatewayNoGap),
                     (options.Cost, "5-sampler-cost", run.SamplerCost),
                 })
        {
            if (!enabled) continue;
            Console.WriteLine();
            Console.WriteLine($"==== {name}");
            StepResult result;
            try
            {
                result = step() with { Name = name };
            }
            catch (Exception ex)
            {
                // The one entry point for a step: whatever went wrong is the step's result, with its words.
                result = new StepResult(name, Passed: false, $"ERROR: {ex.GetType().Name}: {ex.Message}", new(), new());
            }
            Console.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {name}: {result.Summary}");
            File.WriteAllText(Path.Combine(options.ReportDirectory, $"{name}.json"), JsonSerializer.Serialize(result, Json));
            results.Add(result);
        }

        WriteSummary(options.ReportDirectory, results);
        var passed = results.Count > 0 && results.All(result => result.Passed);
        Console.WriteLine();
        Console.WriteLine(passed ? $"RESULT: PASS - {results.Count} step(s)" : $"RESULT: FAIL - {results.Count(r => !r.Passed)} of {results.Count} step(s) failed");
        return passed ? 0 : 1;
    }

    private static void WriteSummary(string directory, IReadOnlyList<StepResult> results)
    {
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(results, Json));

        var text = new StringBuilder();
        text.AppendLine($"# Resource load proof - {Environment.MachineName}");
        text.AppendLine();
        text.AppendLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors, compared against {OsMonitor.MonitorName}, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.");
        text.AppendLine();
        foreach (var result in results)
        {
            text.AppendLine($"## {result.Name}: {(result.Passed ? "PASS" : "FAIL")}");
            text.AppendLine();
            text.AppendLine(result.Summary);
            text.AppendLine();
            foreach (var (key, value) in result.Readings) text.AppendLine($"- {key}: {value}");
            foreach (var shot in result.Screenshots) text.AppendLine($"- screenshot: {shot}");
            text.AppendLine();
        }
        File.WriteAllText(Path.Combine(directory, "summary.md"), text.ToString());
    }

    private sealed class Steps(Options options)
    {
        private readonly ScreenCapture _screen = new(options.ScreenshotCommand);
        private readonly OsMonitor _os = new(options.Interactive);

        private MeterObservation Observe() =>
            ProofChecks.ParseDiagnoseJson(Shell.Run(options.DiagnoseCommand, TimeSpan.FromSeconds(60)));

        private IReadOnlyList<MeterObservation> ObserveWindow() =>
            ProofChecks.ParseDiagnoseSamples(Shell.Run(options.DiagnoseCommand, TimeSpan.FromSeconds(60)));

        private string Shot(string name)
        {
            var path = Path.Combine(options.ReportDirectory, $"{name}.png");
            _screen.Take(path);
            return Path.GetFileName(path);
        }

        // Polls the Director every five seconds until it has RECORDED a sample, taken after `sinceUtc`, that meets the
        // condition - or the poll deadline passes. The recording is written once a minute, so the newest line can be a
        // minute old: an observation counts only by the time its sample was taken, never by when the harness read it,
        // so a stopped Director whose last line happens to be red can never pass "red". The verdict is then judged on
        // the sample's own time against the 70-second limit.
        // Every poll reads the whole window of recorded samples and judges on the EARLIEST one taken after the change
        // that meets the condition, because the recorder appends a minute of lines at once.
        private WaitResult WaitFor(Func<MeterObservation, bool> condition, DateTime sinceUtc, string what)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var window = ObserveWindow();
                var newest = window[^1];
                var first = ProofChecks.FirstMeeting(window, sinceUtc, condition);
                Console.WriteLine($"[wait] {clock.Elapsed.TotalSeconds,5:F0} s  newest sample {newest.TakenAtUtc:HH:mm:ss}{(newest.TakenAtUtc >= sinceUtc ? "" : " (before the change)")}  CPU {Describe(newest.Cpu)}  MEM {Describe(newest.Memory)}  waiting for {what}");
                if (first is not null)
                    return new WaitResult(ProofChecks.WithinWarningDeadline(sinceUtc, first.TakenAtUtc), first,
                        first.TakenAtUtc - sinceUtc, Seen: true);
                if (clock.Elapsed >= ProofChecks.PollDeadline)
                    return new WaitResult(false, newest, newest.TakenAtUtc - sinceUtc, Seen: false);
                Thread.Sleep(Poll);
            }
        }

        private static string Verdict(WaitResult result, string reached, string since) =>
            result.Met
                ? $"{reached} {result.Elapsed.TotalSeconds:F0} s after {since}, by the recorded sample's own time"
                : result.Seen
                    ? $"{reached} only {result.Elapsed.TotalSeconds:F0} s after {since} - OVER the {ProofChecks.WarningDeadline.TotalSeconds:F0} s limit"
                    : $"NOT {reached}: no recorded sample met it within {ProofChecks.PollDeadline.TotalSeconds:F0} s of polling (newest sample {result.Last.TakenAtUtc:HH:mm:ss} UTC, CPU {Describe(result.Last.Cpu)}, MEM {Describe(result.Last.Memory)})";

        public StepResult IdleAgreement()
        {
            var ours = Observe();
            var age = DateTime.UtcNow - ours.TakenAtUtc;
            var shot = Shot("1-idle-meter");
            var cpu = ProofChecks.Agreement("CPU", ours.Cpu.Percent, _os.Cpu(ours.Cpu.Percent, options.OsCpu));
            var memory = ProofChecks.Agreement("MEM", ours.Memory.Percent, _os.Memory(ours.Memory.Percent, options.OsMemory));
            var fresh = age <= ProofChecks.FreshSampleAge;
            var freshness = fresh
                ? $"the sample was {age.TotalSeconds:F0} s old"
                : $"the newest sample was {age.TotalSeconds:F0} s old, past {ProofChecks.FreshSampleAge.TotalSeconds:F0} s - the Director is not recording, so nothing was compared";
            return new StepResult("", fresh && cpu.Passed && memory.Passed, $"{cpu.Summary}; {memory.Summary}; {freshness}",
                new()
                {
                    ["cpu"] = cpu.Summary,
                    ["memory"] = memory.Summary,
                    ["sample"] = $"taken {ours.TakenAtUtc:O}, {age.TotalSeconds:F0} s before the operating system's figures were read",
                    ["diagnose output"] = ours.Raw,
                },
                [shot]);
        }

        public StepResult ProcessorRedAndClear()
        {
            var readings = new Dictionary<string, string>();
            var shots = new List<string>();
            WaitResult red;
            bool namedCpu;
            var loadStartedUtc = DateTime.UtcNow;
            using (new CpuLoad(Environment.ProcessorCount))
            {
                red = WaitFor(o => o.Cpu.Level == MeterLevel.Critical, loadStartedUtc, "CPU red");
                readings["red"] = Verdict(red, "red", "the load started");
                shots.Add(Shot("2-processor-red"));
                namedCpu = ProofChecks.DiagnosisNames(red.Last, "Cpu");
                readings["diagnosis under load"] = $"couldDiagnose={red.Last.CouldDiagnose}, isSlow={red.Last.IsSlow}, cause={red.Last.Cause}";
            }

            var clear = WaitFor(o => o.Cpu.Level == MeterLevel.Normal, DateTime.UtcNow, "CPU back to normal");
            readings["clear"] = Verdict(clear, "cleared", "the load stopped");
            shots.Add(Shot("2-processor-cleared"));

            var passed = red.Met && clear.Met && namedCpu;
            return new StepResult("", passed, $"{readings["red"]}; {readings["clear"]}; diagnosis {(namedCpu ? "named CPU" : $"did NOT name CPU ({red.Last.Cause})")}", readings, shots);
        }

        public StepResult MemoryAmberAndClear()
        {
            var readings = new Dictionary<string, string>();
            var shots = new List<string>();
            WaitResult amber;
            bool namedMemory;
            using (var load = new MemoryLoad())
            {
                load.RequireRoomToRaise();
                load.TopUp();
                // The 70 seconds count from the machine BEING short of memory, not from the start of filling it: on a
                // machine with 20 GB free the filling alone takes most of a minute.
                var loadStartedUtc = DateTime.UtcNow;
                readings["held"] = $"{load.HeldBytes / (1024 * 1024)} MB; machine {load.Level()}";
                amber = WaitFor(o =>
                {
                    load.TopUp();
                    if (load.Level() == CcDirector.Core.Machine.MemoryPressureLevel.Critical)
                        throw new InvalidOperationException("The machine reached Critical while loaded; the load was released at once.");
                    return o.Memory.Level == MeterLevel.Tight;
                }, loadStartedUtc, "MEM amber");
                readings["amber"] = Verdict(amber, "amber", "the load started");
                shots.Add(Shot("3-memory-amber"));
                namedMemory = ProofChecks.DiagnosisNames(amber.Last, "Memory");
                readings["diagnosis under load"] = $"couldDiagnose={amber.Last.CouldDiagnose}, isSlow={amber.Last.IsSlow}, cause={amber.Last.Cause}";
            }

            var clear = WaitFor(o => o.Memory.Level == MeterLevel.Normal, DateTime.UtcNow, "MEM back to normal");
            readings["clear"] = Verdict(clear, "cleared", "the memory was freed");
            shots.Add(Shot("3-memory-cleared"));

            var passed = amber.Met && clear.Met && namedMemory;
            return new StepResult("", passed, $"{readings["amber"]}; {readings["clear"]}; diagnosis {(namedMemory ? "named memory" : $"did NOT name memory ({amber.Last.Cause})")}", readings, shots);
        }

        public StepResult GatewayNoGap()
        {
            var block = options.GatewayBlock ?? throw new InvalidOperationException("Step 4 needs --gateway-block.");
            var unblock = options.GatewayUnblock ?? throw new InvalidOperationException("Step 4 needs --gateway-unblock.");
            var rows = options.GatewayRows ?? throw new InvalidOperationException("Step 4 needs --gateway-rows.");

            var from = ProofChecks.AlignToWindow(DateTime.UtcNow) - ProofChecks.SummaryWindow;
            Shell.Run(block, TimeSpan.FromMinutes(1));
            var blockedAt = DateTime.UtcNow;
            Console.WriteLine($"[gateway] blocked at {blockedAt:HH:mm:ss} UTC for {options.GatewayMinutes} minutes");
            try
            {
                var until = Stopwatch.StartNew();
                while (until.Elapsed < TimeSpan.FromMinutes(options.GatewayMinutes))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(30));
                    Console.WriteLine($"[gateway] blocked {until.Elapsed.TotalMinutes:F1} of {options.GatewayMinutes} minutes");
                }
            }
            finally
            {
                Shell.Run(unblock, TimeSpan.FromMinutes(1));
                Console.WriteLine($"[gateway] unblocked at {DateTime.UtcNow:HH:mm:ss} UTC");
            }

            // The Director sends the missing windows on reconnect, and the window open at reconnect closes up to
            // five minutes later; one full window plus a minute covers both.
            Console.WriteLine("[gateway] waiting six minutes for the catch-up and the next send");
            Thread.Sleep(TimeSpan.FromMinutes(6));
            var to = ProofChecks.AlignToWindow(DateTime.UtcNow);
            var stored = ProofChecks.ParseWindowStarts(Shell.Run(rows, TimeSpan.FromMinutes(1)));
            var gaps = ProofChecks.Gaps(stored, from, to);
            return new StepResult("", gaps.Passed, gaps.Summary,
                new()
                {
                    ["blocked from"] = blockedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["checked span"] = $"{from:O} to {to:O}",
                    ["rows returned"] = stored.Count.ToString(CultureInfo.InvariantCulture),
                    ["missing"] = string.Join(", ", gaps.MissingWindowStartsUtc.Select(t => t.ToString("O", CultureInfo.InvariantCulture))),
                },
                []);
        }

        public StepResult SamplerCost()
        {
            var pid = options.CostPid ?? throw new InvalidOperationException("Step 5 needs --cost-pid, the process running the sampler.");
            using var process = Process.GetProcessById(pid);
            var minutes = options.CostMinutes;
            Console.WriteLine($"[cost] measuring {process.ProcessName} (process {pid}) for {minutes} minutes");
            var startCpu = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(minutes))
            {
                Thread.Sleep(TimeSpan.FromSeconds(30));
                process.Refresh();
                Console.WriteLine($"[cost] {clock.Elapsed.TotalMinutes:F1} min, {(process.TotalProcessorTime - startCpu).TotalSeconds:F2} s of processor so far");
            }
            process.Refresh();
            var cost = ProofChecks.SamplerCost(process.TotalProcessorTime - startCpu, clock.Elapsed);
            return new StepResult("", cost.Passed, cost.Summary,
                new()
                {
                    ["process"] = $"{process.ProcessName} ({pid})",
                    ["what this measures"] = "the WHOLE process, so it is an upper bound on the sampler's own cost: under the limit here proves the sampler is under it",
                },
                []);
        }

        private static string Describe(MeterBar bar) =>
            bar.CouldRead ? $"{bar.Percent:F1}% {bar.Level}" : $"cannot read ({bar.Level})";
    }
}

/// <summary>The outcome of one wait: whether the limit was met, the last observation, how long after the change its sample was taken, and whether a matching sample was seen at all.</summary>
internal sealed record WaitResult(bool Met, MeterObservation Last, TimeSpan Elapsed, bool Seen);

/// <summary>One step's outcome, as written into the report.</summary>
internal sealed record StepResult(string Name, bool Passed, string Summary, Dictionary<string, string> Readings, List<string> Screenshots);

/// <summary>The command line.</summary>
internal sealed record Options(
    bool Idle, bool Cpu, bool Memory, bool Gateway, bool Cost,
    string ReportDirectory,
    string DiagnoseCommand,
    string? ScreenshotCommand,
    bool Interactive,
    double? OsCpu, double? OsMemory,
    string? GatewayBlock, string? GatewayUnblock, string? GatewayRows, int GatewayMinutes,
    int? CostPid, int CostMinutes)
{
    public const string Usage = """
        resource-load-proof [steps] --report <folder> [options]
          steps (at least one):
            --idle      1. the meter beside the operating system's own figures, at idle
            --cpu       2. pin the processor: red within 70 s, then clear; diagnosis names CPU
            --memory    3. hold memory to Tight: amber within 70 s, then clear; diagnosis names memory
            --gateway   4. block the Gateway, unblock, and expect no gap in the five-minute rows
            --cost      5. the sampler's own processor cost over 10 minutes, under 0.5% of one core
          options:
            --diagnose-command "<cmd>"   prints the Director's diagnose JSON (steps 1-3);
                                         default: cc-devthrottle machine diagnose --json --samples --minutes 5
            --screenshot-command "<cmd>" with {path}; default is the platform's own
            --non-interactive            never prompt; pass --os-cpu and --os-mem instead
            --os-cpu <percent> --os-mem <percent>   the operating system's figures, read by hand
            --gateway-block "<cmd>" --gateway-unblock "<cmd>" --gateway-rows "<cmd>" [--gateway-minutes 10]
            --cost-pid <pid> [--cost-minutes 10]
        """;

    /// <summary>The Director's own diagnose command, which reports the newest sample, verdict and diagnosis it recorded.</summary>
    public const string DefaultDiagnoseCommand = "cc-devthrottle machine diagnose --json --samples --minutes 5";

    private static readonly string[] SwitchFlags = ["--idle", "--cpu", "--memory", "--gateway", "--cost", "--non-interactive"];

    private static readonly string[] ValueFlags =
    [
        "--report", "--diagnose-command", "--screenshot-command", "--os-cpu", "--os-mem",
        "--gateway-block", "--gateway-unblock", "--gateway-rows", "--gateway-minutes", "--cost-pid", "--cost-minutes",
    ];

    public static Options Parse(string[] args)
    {
        // An argument the harness does not know is refused, so a misspelt step ("--memroy") fails the run instead of
        // quietly being skipped while the run reports PASS (docs/axi-standard.md).
        // One walk both validates and reads, so a value is never mistaken for a flag ("--report --idle" is refused).
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (ValueFlags.Contains(args[i]))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{args[i]} needs a value.");
                values[args[i]] = args[++i];
                continue;
            }
            if (!SwitchFlags.Contains(args[i])) throw new ArgumentException($"Unknown argument \"{args[i]}\".");
            switches.Add(args[i]);
        }

        string? Value(string name) => values.TryGetValue(name, out var value) ? value : null;

        double? Percent(string name) =>
            Value(name) is { } text
                ? double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100
                    ? value
                    : throw new ArgumentException($"{name} must be a percentage from 0 to 100, not \"{text}\".")
                : null;

        int? Whole(string name) =>
            Value(name) is { } text
                ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                    ? value
                    : throw new ArgumentException($"{name} must be a positive whole number, not \"{text}\".")
                : null;

        var options = new Options(
            switches.Contains("--idle"), switches.Contains("--cpu"), switches.Contains("--memory"), switches.Contains("--gateway"), switches.Contains("--cost"),
            Value("--report") ?? throw new ArgumentException("--report <folder> is required."),
            Value("--diagnose-command") ?? DefaultDiagnoseCommand,
            Value("--screenshot-command"),
            !switches.Contains("--non-interactive"),
            Percent("--os-cpu"), Percent("--os-mem"),
            Value("--gateway-block"), Value("--gateway-unblock"), Value("--gateway-rows"), Whole("--gateway-minutes") ?? 10,
            Whole("--cost-pid"), Whole("--cost-minutes") ?? 10);

        if (!(options.Idle || options.Cpu || options.Memory || options.Gateway || options.Cost))
            throw new ArgumentException("Name at least one step.");
        return options;
    }
}
