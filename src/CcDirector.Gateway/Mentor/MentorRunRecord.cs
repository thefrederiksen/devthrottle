using System.Globalization;
using System.Text;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The RUN RECORD of one mentor run (Phase B ruling R6, decision 6): the folder
/// <c>&lt;root&gt;/mentor-runs/tenants/&lt;tenant id&gt;/&lt;week&gt;/&lt;run id&gt;/</c> under the Gateway's own storage,
/// kept per tenant per week, never emailed, never copied off. It holds:
///
///   run.json          tenant, week, model id, started, finished, outcome, the exploration counts, per-slot attempts
///                     and refusals, the failure text when failed
///   calls.json        every proxy call: seq, purpose, status, response id, usage (prompt, completion, cached), ms
///   cost.json         tokens in / cached / out summed over the run and dollars at the SERVED rates read at run
///                     start from GET /api/v1/models - the model id, the three rates and the arithmetic
///   tool-log.jsonl    the agent's own tool calls (the run's tool log, copied from the scratch folder)
///   host-checks.jsonl the HOST's validation calls (cite and verify_quote run by the slot validators), kept in
///                     their own log so the run's tool log holds only what the agent fetched - see the loop
///   slots.json, report.md, prompts-human.md, metrics.json, log-bound.json, notes.md   the assembler's files
///
/// The SCRATCH folder is the assembler's working folder, a temp folder the loop deletes at the end of the run
/// whatever happened; the record is written before it goes. The record is not the scratch folder. Prompt text
/// reaches the record (the slots and the report quote the developer's prompts), so the record is never committed
/// and never leaves the Gateway's storage.
/// </summary>
public sealed class MentorRunRecord
{
    public const string RootFolder = "mentor-runs";
    public const string RunFile = "run.json";
    public const string CallsFile = "calls.json";
    public const string CostFile = "cost.json";
    public const string HostChecksFile = "host-checks.jsonl";
    public const string LedgerNote = "reconciled in the mission record, not readable by the Gateway";

    /// <summary>The scratch files copied into the record when they exist. report.md is copied only when the run
    /// assembled (a failed run renders nothing - ruling 9).</summary>
    public static readonly string[] ScratchFiles =
    {
        ToolLog.FileName, HostChecksFile, Mentor.Slots.SlotsFile, PromptsFile.FileName, Assembler.MetricsFile, LogCheck.BoundFile, ToolSurface.NotesFile,
    };

    public const string OutcomeRunning = "running";
    public const string OutcomeAssembled = "assembled";
    public const string OutcomeFailed = "failed";

    /// <summary>One slot's history: its path, how many times it was asked, every refusal's reason, and the answer
    /// that was accepted (null until then).</summary>
    public sealed class SlotRecord
    {
        public required string Slot { get; init; }
        public int Attempts { get; set; }
        public List<string> Refusals { get; } = new();
        public object? Final { get; set; }
        public string Status { get; set; } = "pending";
        public bool Accepted => Status == "accepted";
    }

    /// <summary>One proxy call as the record keeps it.</summary>
    public sealed record ProxyCall(int Seq, string Purpose, int Status, string? Id, long PromptTokens, long CompletionTokens, long CachedTokens, long Ms);

    /// <summary>The served rates the proxy publishes for the model, dollars per million tokens.</summary>
    public sealed record RateCard(string ModelId, double InputPerMillion, double CachedInputPerMillion, double OutputPerMillion, string? AsOf, string Source);

    public string Tenant { get; }
    public string Week { get; }
    public string ModelId { get; }
    public string RunId { get; }
    public string Dir { get; }
    public string ScratchDir { get; }
    public DateTime StartedUtc { get; }
    public DateTime? FinishedUtc { get; private set; }
    public string Outcome { get; private set; } = OutcomeRunning;
    public string? Failure { get; private set; }
    public int ExplorationProxyCalls { get; set; }
    public int ExplorationToolCalls { get; set; }
    public string? ExplorationNote { get; set; }
    public int AssemblyRounds { get; set; }
    public List<string> AssemblyRefusals { get; } = new();
    public RateCard? Rates { get; set; }
    public List<SlotRecord> Slots { get; } = new();
    public List<ProxyCall> Calls { get; } = new();

    private MentorRunRecord(string tenant, string week, string modelId, string runId, string dir, string scratchDir, DateTime startedUtc)
    {
        Tenant = tenant;
        Week = week;
        ModelId = modelId;
        RunId = runId;
        Dir = dir;
        ScratchDir = scratchDir;
        StartedUtc = startedUtc;
    }

    /// <summary>Open a new record under <paramref name="root"/> (<see cref="CcStorage.Root"/> when null) and its scratch
    /// folder; both folders exist when this returns and run.json says the run is running.</summary>
    public static MentorRunRecord Start(string tenant, string week, string modelId, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(tenant)) throw new ArgumentException("tenant is required", nameof(tenant));
        MentorReaders.ParseIsoWeek(week);
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId is required", nameof(modelId));
        var started = DateTime.UtcNow;
        var runId = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var dir = Path.Combine(root ?? CcStorage.Root(), RootFolder, "tenants", tenant, week, runId);
        var scratch = Path.Combine(Path.GetTempPath(), "mentor-scratch-" + runId);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(scratch);
        var record = new MentorRunRecord(tenant, week, modelId, runId, dir, scratch, started);
        FileLog.Write($"[MentorRunRecord] Start: week={week} model={modelId} run={runId}");
        record.Write();
        return record;
    }

    public SlotRecord Slot(string slot)
    {
        var found = Slots.FirstOrDefault(s => s.Slot == slot);
        if (found is null)
        {
            found = new SlotRecord { Slot = slot };
            Slots.Add(found);
        }
        return found;
    }

    public ProxyCall AddCall(string purpose, int status, string? id, long promptTokens, long completionTokens, long cachedTokens, long ms)
    {
        var call = new ProxyCall(Calls.Count + 1, purpose, status, id, promptTokens, completionTokens, cachedTokens, ms);
        Calls.Add(call);
        return call;
    }

    public void Finish(string outcome, string? failure)
    {
        FinishedUtc = DateTime.UtcNow;
        Outcome = outcome;
        Failure = failure;
        FileLog.Write($"[MentorRunRecord] Finish: run={RunId} outcome={outcome}" + (failure is null ? "" : " failure=" + failure));
    }

    /// <summary>Summed usage over the calls that answered 200: (prompt, cached, completion).</summary>
    public (long Input, long Cached, long Output) Totals()
    {
        long input = 0, cached = 0, output = 0;
        foreach (var call in Calls.Where(c => c.Status == 200))
        {
            input += call.PromptTokens;
            cached += Math.Min(Math.Max(call.CachedTokens, 0), call.PromptTokens);
            output += call.CompletionTokens;
        }
        return (input, cached, output);
    }

    /// <summary>Dollars at the rate card over the totals, or null with the reason when no rate card was read.</summary>
    public (double InputUncached, double Cached, double Output, double Total)? Dollars()
    {
        if (Rates is null) return null;
        var (input, cached, output) = Totals();
        var uncachedDollars = (input - cached) * Rates.InputPerMillion / 1_000_000d;
        var cachedDollars = cached * Rates.CachedInputPerMillion / 1_000_000d;
        var outputDollars = output * Rates.OutputPerMillion / 1_000_000d;
        return (Round(uncachedDollars), Round(cachedDollars), Round(outputDollars), Round(uncachedDollars + cachedDollars + outputDollars));
    }

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    /// <summary>Write run.json, calls.json and cost.json as they stand.</summary>
    public void Write()
    {
        Directory.CreateDirectory(Dir);
        WriteJson(RunFile, RunDocument());
        WriteJson(CallsFile, Calls.Select(c => (object?)new Dictionary<string, object?>
        {
            ["seq"] = c.Seq,
            ["purpose"] = c.Purpose,
            ["status"] = c.Status,
            ["id"] = c.Id,
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = c.PromptTokens,
                ["completion_tokens"] = c.CompletionTokens,
                ["cached_tokens"] = c.CachedTokens,
            },
            ["ms"] = c.Ms,
        }).ToList());
        WriteJson(CostFile, CostDocument());
    }

    private Dictionary<string, object?> RunDocument() => new()
    {
        ["tenant"] = Tenant,
        ["week"] = Week,
        ["model"] = ModelId,
        ["run_id"] = RunId,
        ["started_utc"] = Stamp(StartedUtc),
        ["finished_utc"] = FinishedUtc is null ? null : Stamp(FinishedUtc.Value),
        ["outcome"] = Outcome,
        ["failure"] = Failure,
        ["exploration"] = new Dictionary<string, object?>
        {
            ["proxy_calls"] = ExplorationProxyCalls,
            ["tool_calls"] = ExplorationToolCalls,
            ["note"] = ExplorationNote,
        },
        ["assembly"] = new Dictionary<string, object?>
        {
            ["rounds"] = AssemblyRounds,
            ["refusals"] = AssemblyRefusals.Select(r => (object?)r).ToList(),
        },
        ["slots"] = Slots.Select(s => (object?)new Dictionary<string, object?>
        {
            ["slot"] = s.Slot,
            ["status"] = s.Status,
            ["attempts"] = s.Attempts,
            ["refusals"] = s.Refusals.Select(r => (object?)r).ToList(),
            ["final"] = s.Final,
        }).ToList(),
        ["proxy_calls"] = Calls.Count,
    };

    private Dictionary<string, object?> CostDocument()
    {
        var (input, cached, output) = Totals();
        var dollars = Dollars();
        return new Dictionary<string, object?>
        {
            ["model"] = ModelId,
            ["rates"] = Rates is null ? null : new Dictionary<string, object?>
            {
                ["model"] = Rates.ModelId,
                ["input_per_million"] = Rates.InputPerMillion,
                ["cached_input_per_million"] = Rates.CachedInputPerMillion,
                ["output_per_million"] = Rates.OutputPerMillion,
                ["as_of"] = Rates.AsOf,
                ["source"] = Rates.Source,
            },
            ["calls"] = Calls.Count(c => c.Status == 200),
            ["tokens"] = new Dictionary<string, object?>
            {
                ["input"] = input,
                ["cached"] = cached,
                ["input_uncached"] = input - cached,
                ["output"] = output,
            },
            ["dollars"] = dollars is null ? null : new Dictionary<string, object?>
            {
                ["input_uncached"] = dollars.Value.InputUncached,
                ["cached"] = dollars.Value.Cached,
                ["output"] = dollars.Value.Output,
                ["total"] = dollars.Value.Total,
            },
            ["arithmetic"] = "(input - cached) * input_per_million / 1000000 + cached * cached_input_per_million / 1000000 + output * output_per_million / 1000000, each rounded to six decimals",
            ["ledger"] = LedgerNote,
        };
    }

    private void WriteJson(string name, object? value)
        => File.WriteAllText(Path.Combine(Dir, name), ParityJson.Pretty(value) + "\n", new UTF8Encoding(false));

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Copy the scratch files that exist into the record; report.md only when the run assembled.</summary>
    public void CaptureScratch()
    {
        Directory.CreateDirectory(Dir);
        var names = Outcome == OutcomeAssembled ? ScratchFiles.Append(Assembler.ReportFile) : ScratchFiles;
        var copied = 0;
        foreach (var name in names)
        {
            var source = Path.Combine(ScratchDir, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(Dir, name), overwrite: true);
            copied++;
        }
        FileLog.Write($"[MentorRunRecord] CaptureScratch: run={RunId} files={copied}");
    }

    /// <summary>Delete the scratch folder. The record has been written by then.</summary>
    public void DeleteScratch()
    {
        if (Directory.Exists(ScratchDir)) Directory.Delete(ScratchDir, recursive: true);
        FileLog.Write($"[MentorRunRecord] DeleteScratch: run={RunId}");
    }
}
