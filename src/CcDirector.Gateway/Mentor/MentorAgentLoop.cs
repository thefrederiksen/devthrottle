using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Mentor;

/// <summary>A proxy call did not answer 200: the status and the message the run records.</summary>
public sealed class MentorProxyException : Exception
{
    public int Status { get; }
    public TimeSpan? RetryAfter { get; }

    public MentorProxyException(int status, string message, TimeSpan? retryAfter = null) : base(message)
    {
        Status = status;
        RetryAfter = retryAfter;
    }
}

/// <summary>What one run answered: the outcome the record carries, and the record.</summary>
public sealed class MentorRunOutcome
{
    public required MentorRunRecord Record { get; init; }
    public bool Assembled => Record.Outcome == MentorRunRecord.OutcomeAssembled;
    public string Outcome => Record.Outcome;
    public string? Failure => Record.Failure;
    public string? AssembledLine { get; init; }
}

/// <summary>
/// THE MENTOR AGENT LOOP - the host of the mentor inside the Gateway (Mentor on the Gateway, Phase B ruling R3):
/// chat completions with native TOOL CALLING against the DevThrottle inference proxy, the tools being the C# surface
/// bound to one tenant (<see cref="MentorTools"/>), the skill (SKILL.md + rubric.md, verbatim) as the system prompt
/// behind a short host preamble, and the written slots as the output contract, one bounded ask per slot (R5).
///
/// THE RUN, in three phases (the design's section 4):
///
///   EXPLORATION. The model calls tools freely; the loop feeds every result back as a tool message; bounded by
///   <see cref="ExplorationToolCallCeiling"/> tool calls (on reaching it the loop moves to the slots and the record
///   says so). No answer text is needed from the model here; a model that stops calling tools before it has run
///   week_overview, session_index and at least one dimension_candidates is nudged once with a host message, then
///   moved on.
///
///   THE SLOT ASKS. Ten asks, one per written slot, in the report's order (recommendations[0..2], went_well, the
///   six dimensions in rubric order), each a host message stating the slot, its shape and its limits (from
///   <see cref="Slots"/>' constants) and asking for ONE JSON object and nothing else. The model may still call tools
///   inside an ask (cite and verify_quote before it writes, as the skill says). The answer is parsed and validated on
///   arrival by the ported validators (<see cref="Slots.ValidateRecommendation"/>, <see cref="Slots.ValidateWentWell"/>,
///   <see cref="Slots.ValidateDimension"/>); a refusal is sent back naming the slot and the reason, up to
///   <see cref="SlotAttempts"/> attempts for that slot alone, the conversation otherwise untouched; after the third
///   refusal the slot is FAILED in the record and the run FAILS. No check is loosened.
///
///   ASSEMBLY. The accepted slots are written as slots.json in the scratch folder and <see cref="Assembler.Assemble"/>
///   runs on them - the same code path as a slots.json the reference's agent writes. A <c>REFUSED slot</c> goes back
///   as ONE more retry of that slot (counted in the record), then the assembly runs again; a <c>REFUSED report:</c>
///   naming no slot FAILS the run: recorded with the refusal text, no retry, nothing rendered (ruling 9).
///
/// WHY THE HOST'S VALIDATION CALLS HAVE THEIR OWN LOG. The validators resolve every citation through cite and every
/// fragment through verify_quote. If those calls landed in the RUN'S tool log, the log check that assembly runs
/// (every citation in the written slots backed by a cite call BEFORE the assembly's bound) would be satisfied by the
/// host's own checking: a citation the model never fetched would read as fetched. So the host validates through a
/// SECOND surface over the same store whose log is <c>host-checks.jsonl</c> in the scratch folder, kept in the
/// record beside the tool log; the run's tool log holds only what the model itself called, and the assembler's log
/// check keeps the guarantee the reference gives it. (The check cannot be answered without the model's own calls;
/// a check the host could satisfy for it would fail open.)
///
/// EVERY PROXY CALL presents the Bearer credential, is bounded by a per-call deadline (<see cref="DefaultCallTimeout"/>,
/// the wingman's 60 s) inside the run deadline (<see cref="DefaultRunDeadline"/>), maps 402 and 429 as
/// <c>HostedInferenceBrain</c> maps them, and makes any non-success a failure of the run with the status in the
/// record. Every call's usage (prompt, completion, cached) and response id go on the record's calls list; the
/// served rates are read once at run start from GET /api/v1/models into cost.json.
///
/// The credential is NEVER logged (this class logs statuses, counts and seconds; never the key, never a prompt's
/// text, never a reply's text). The model is the minted type ONLY: the credential is the deployment key, and a
/// catalog id can never reach the wire (<see cref="IncludedModelId"/> is the guard, as the wingman says).
/// </summary>
public sealed class MentorAgentLoop
{
    public const int ExplorationToolCallCeiling = 120;
    public const int SlotToolCallCeiling = 40;
    public const int SlotAttempts = 3;
    public const int AssemblerRetriesPerSlot = 1;
    public const int AssemblyRounds = 12;
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultRunDeadline = TimeSpan.FromMinutes(40);
    public const string RatesSource = "GET /api/v1/models pricing block (inputPerMTokUsd, cachedInputPerMTokUsd, outputPerMTokUsd), dollars per million tokens";

    /// <summary>The ten written slots in the report's order.</summary>
    public static readonly string[] SlotOrder =
        new[] { "recommendations[0]", "recommendations[1]", "recommendations[2]", "went_well" }
            .Concat(Slots.DimensionKeys.Select(k => "prompting." + k)).ToArray();

    /// <summary>The host preamble that precedes the skill and the rubric in the system prompt. It says how the
    /// skill's command line maps onto function calling, and that the slots are the answers to the host's asks.</summary>
    public const string MentorHostPreamble =
        "HOST PREAMBLE. You are running inside the Gateway as a function-calling agent. Every tool in the skill's table "
        + "below is a native function of the same name and the same parameter names, called by function calling and never "
        + "by a command line: where the skill shows a command line, call the function of that name with those arguments; a "
        + "--limit, --n or --session option is the parameter of that name. A refused call answers an object with one key, "
        + "error, holding the reason. note writes to the run's notes. You do not write slots.json and you do not run "
        + "assemble: the host does the assembly. The slots are not a file; they are your answers to the host's asks, one "
        + "slot at a time, each ONE JSON object in the shape the skill gives for that slot and nothing else - no prose "
        + "around it. A refusal comes back as the host's next message naming the slot and the reason; fix that slot only. "
        + "During exploration you call tools; when you hold enough cited candidates, answer in text and wait for the "
        + "asks. Address the developer as you. Say the agent; name no provider and no model. ASCII only.";

    private const string ExplorationOpener =
        "Explore this developer's week now. Work through the skill's order of work with the tools: week_overview first, "
        + "session_index second, then dimension_candidates for each of the six rubric dimensions, prompt_search for the "
        + "phrases the rubric names, session_prompts and session_outcomes on the sessions the map or a hit points at, "
        + "turn_record where one moment needs the agent's side, cite for every citation you mean to use and verify_quote "
        + "for every fragment you mean to quote, and note for every candidate. Reply in text only when you are done "
        + "exploring and hold cited candidates for three findings, one thing that went well and all six dimensions; the "
        + "host then asks for the slots one at a time.";

    private const string ExplorationNudge =
        "You have not yet run week_overview, session_index and at least one dimension_candidates. Run them now, then "
        + "continue exploring; reply in text only when you hold enough cited candidates.";

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ToolSurface _surface;
    private readonly ToolLog _log;
    private readonly MentorRunRecord _record;
    private readonly HttpClient _http;
    private readonly Action<string> _out;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _runDeadline;
    private readonly List<Dictionary<string, object?>> _messages = new();
    private readonly List<Dictionary<string, object?>> _tools = MentorTools.FunctionDefinitions();
    private readonly HashSet<string> _toolsRun = new(StringComparer.Ordinal);
    private ToolSurface? _checks;
    private List<Dictionary<string, object?>>? _index;
    private long _humanCount;
    private readonly Dictionary<string, object?> _accepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _assemblerRetries = new(StringComparer.Ordinal);
    private CancellationToken _runToken;

    /// <param name="baseUrl">The proxy's <c>/v1</c> base URL.</param>
    /// <param name="apiKey">The deployment credential, presented as the Bearer token; never logged.</param>
    /// <param name="model">The minted included id the run speaks to.</param>
    /// <param name="surface">The tool surface bound to the tenant and the week, whose log is the run's tool log in the
    /// record's scratch folder.</param>
    /// <param name="log">The run's tool log - the surface's own (<see cref="ToolSurface.Log"/>); passed so the caller says
    /// which log the run is bound to, and refused when it is not the surface's).</param>
    /// <param name="record">The run record, started; its scratch folder is the surface's run folder.</param>
    /// <param name="http">The HTTP client (tests inject a stub over a fake handler); a shared client when null.</param>
    /// <param name="log2">The log sink; <see cref="FileLog.Write"/> when null.</param>
    /// <param name="callTimeout">The per-call deadline; <see cref="DefaultCallTimeout"/> when null.</param>
    /// <param name="runDeadline">The whole-run deadline; <see cref="DefaultRunDeadline"/> when null.</param>
    public MentorAgentLoop(string baseUrl, string apiKey, IncludedModelId model, ToolSurface surface, ToolLog log, MentorRunRecord record,
        HttpClient? http = null, Action<string>? log2 = null, TimeSpan? callTimeout = null, TimeSpan? runDeadline = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("[MentorAgentLoop] No DevThrottle deployment key is configured; the mentor cannot reach the model. "
                + "Store it in the key vault as " + TranscriptionEndpointResolver.DevThrottleKeyName + ".");
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(record);
        if (!ReferenceEquals(surface.Log, log))
            throw new ArgumentException("The tool log must be the surface's own log; the run is bound to one log.", nameof(log));
        if (!string.Equals(Path.GetFullPath(surface.RunDir), Path.GetFullPath(record.ScratchDir), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The surface's run folder " + surface.RunDir + " is not the record's scratch folder " + record.ScratchDir + ".", nameof(surface));
        if (!string.Equals(record.ModelId, model.Value, StringComparison.Ordinal))
            throw new ArgumentException("The record names model " + record.ModelId + " but the loop was given " + model.Value + ".", nameof(record));
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model.Value;
        _surface = surface;
        _log = log;
        _record = record;
        _http = http ?? SharedHttp;
        _out = log2 ?? FileLog.Write;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        _runDeadline = runDeadline ?? DefaultRunDeadline;
    }

    /// <summary>The system prompt: the host preamble, then SKILL.md, then rubric.md, verbatim.</summary>
    public static string SystemPrompt()
        => MentorHostPreamble + "\n\n" + MentorSkill.Skill().Body.TrimEnd('\n') + "\n\n" + MentorSkill.Rubric().Body.TrimEnd('\n') + "\n";

    // ------------------------------------------------------------------ the run

    /// <summary>Run the whole thing. The outcome is returned, never thrown, for everything the run itself decides
    /// (a slot failed, a report refusal, a proxy status, the deadline); a defect in the host goes up after the
    /// record is written and the scratch deleted.</summary>
    public async Task<MentorRunOutcome> RunAsync(CancellationToken ct = default)
    {
        _out($"[MentorAgentLoop] RunAsync: week={_record.Week} model={_model} run={_record.RunId}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_runDeadline);
        _runToken = deadline.Token;
        var sw = Stopwatch.StartNew();
        string? assembledLine = null;
        try
        {
            _record.Rates = await ReadRateCardAsync(_runToken);
            _record.Write();
            _messages.Add(Message("system", SystemPrompt()));
            _messages.Add(Message("user", ExplorationOpener));
            await ExploreAsync();
            _record.Write();
            BindChecks();
            foreach (var slot in SlotOrder)
            {
                if (!await AskSlotAsync(slot, null))
                {
                    var failed = _record.Slot(slot);
                    _record.Finish(MentorRunRecord.OutcomeFailed, "slot " + slot + " refused " + failed.Attempts + " times: " + string.Join(" | ", failed.Refusals));
                    return Done(null);
                }
                _record.Write();
            }
            assembledLine = await AssembleAsync();
            if (assembledLine is null) return Done(null);
            _record.Finish(MentorRunRecord.OutcomeAssembled, null);
            return Done(assembledLine);
        }
        catch (MentorProxyException error)
        {
            _record.Finish(MentorRunRecord.OutcomeFailed, "proxy answered " + error.Status + ": " + error.Message);
            return Done(null);
        }
        catch (TimeoutException error)
        {
            _record.Finish(MentorRunRecord.OutcomeFailed, error.Message);
            return Done(null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _record.Finish(MentorRunRecord.OutcomeFailed, "the run did not finish within " + _runDeadline.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture) + " minutes");
            return Done(null);
        }
        catch (Exception error)
        {
            _record.Finish(MentorRunRecord.OutcomeFailed, error.GetType().Name + ": " + error.Message);
            Done(null);
            throw;
        }
        finally
        {
            _out($"[MentorAgentLoop] RunAsync: run={_record.RunId} outcome={_record.Outcome} calls={_record.Calls.Count} tool_calls={_log.Seq} in {sw.Elapsed.TotalSeconds:F0}s");
        }
    }

    private MentorRunOutcome Done(string? assembledLine)
    {
        try
        {
            _record.CaptureScratch();
            _record.Write();
        }
        finally
        {
            _record.DeleteScratch();
        }
        return new MentorRunOutcome { Record = _record, AssembledLine = assembledLine };
    }

    // ------------------------------------------------------------------ exploration

    private async Task ExploreAsync()
    {
        var nudged = false;
        while (true)
        {
            var reply = await CallAsync("exploration", offerTools: true);
            _record.ExplorationProxyCalls++;
            if (reply.ToolCalls.Count > 0)
            {
                RunToolCalls(reply, "exploration");
                _record.ExplorationToolCalls += reply.ToolCalls.Count;
                if (_record.ExplorationToolCalls >= ExplorationToolCallCeiling)
                {
                    _record.ExplorationNote = "the exploration ceiling of " + ExplorationToolCallCeiling + " tool calls was reached after "
                        + _record.ExplorationToolCalls + "; the loop moved to the slots";
                    _out("[MentorAgentLoop] Explore: " + _record.ExplorationNote);
                    return;
                }
                continue;
            }
            var missing = new[] { "week_overview", "session_index", "dimension_candidates" }.Where(t => !_toolsRun.Contains(t)).ToList();
            if (missing.Count > 0 && !nudged)
            {
                nudged = true;
                _record.ExplorationNote = "nudged once: the model stopped before running " + string.Join(", ", missing);
                _out("[MentorAgentLoop] Explore: " + _record.ExplorationNote);
                _messages.Add(Message("user", ExplorationNudge));
                continue;
            }
            if (missing.Count > 0)
                _record.ExplorationNote = "moved on after the nudge with " + string.Join(", ", missing) + " never run";
            _out($"[MentorAgentLoop] Explore: done after {_record.ExplorationProxyCalls} proxy calls and {_record.ExplorationToolCalls} tool calls");
            return;
        }
    }

    /// <summary>Run every tool call of a reply against the run's surface and append the tool messages. Every call
    /// is answered (a request with an unanswered tool call is invalid), so a ceiling is checked after, never inside.</summary>
    private void RunToolCalls(ChatReply reply, string phase)
    {
        foreach (var call in reply.ToolCalls)
        {
            var (text, ok) = MentorTools.Call(_surface, call.Name, call.Arguments);
            if (ok) _toolsRun.Add(call.Name);
            _out($"[MentorAgentLoop] {phase}: tool {call.Name} ok={(ok ? "true" : "false")} answer={text.Length} chars");
            _messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["name"] = call.Name,
                ["content"] = text,
            });
        }
    }

    // ------------------------------------------------------------------ the slot asks

    /// <summary>The host's second surface, whose log is host-checks.jsonl (see the class remarks), and the index and
    /// human count the validators need, read through it.</summary>
    private void BindChecks()
    {
        _checks = new ToolSurface(_surface.Store, new ToolLog(Path.Combine(_record.ScratchDir, MentorRunRecord.HostChecksFile)));
        var overview = _checks.WeekOverview();
        _index = _checks.SessionIndex();
        _humanCount = HumanCount(overview);
        _out($"[MentorAgentLoop] BindChecks: index={_index.Count} rows, human prompts={_humanCount}");
    }

    internal static long HumanCount(Dictionary<string, object?> overview)
    {
        var origin = (Dictionary<string, object?>)overview["origin"]!;
        var byOrigin = (Dictionary<string, object?>)origin["prompts_by_origin"]!;
        var value = (Dictionary<string, object?>)byOrigin["value"]!;
        var human = (Dictionary<string, object?>)value["human"]!;
        return (long)human["count"]!;
    }

    /// <summary>Ask one slot until it is accepted or its attempts are spent. <paramref name="reasonFromAssembler"/> is
    /// the assembler's refusal when this is the one extra retry it earns.</summary>
    private async Task<bool> AskSlotAsync(string slot, string? reasonFromAssembler)
    {
        var record = _record.Slot(slot);
        var ask = reasonFromAssembler is null
            ? AskText(slot)
            : "REFUSED slot " + slot + " at assembly: " + reasonFromAssembler + " Answer again with ONE JSON object for slot " + slot + " and nothing else, fixing only that.";
        var limit = reasonFromAssembler is null ? SlotAttempts : record.Attempts + 1;
        while (record.Attempts < limit)
        {
            record.Attempts++;
            record.Status = "asked";
            _messages.Add(Message("user", ask));
            var content = await AnswerAsync("slot " + slot + " attempt " + record.Attempts);
            string? refusal;
            object? value;
            try
            {
                value = ParseObject(content);
                Validate(slot, value);
                refusal = null;
            }
            catch (SlotError error)
            {
                value = null;
                refusal = "REFUSED slot " + error.Slot + ": " + error.Reason;
            }
            if (refusal is null)
            {
                record.Status = "accepted";
                record.Final = value;
                _accepted[slot] = value;
                _out($"[MentorAgentLoop] slot {slot}: accepted on attempt {record.Attempts}");
                return true;
            }
            record.Refusals.Add(refusal);
            _out($"[MentorAgentLoop] slot {slot}: attempt {record.Attempts} refused ({refusal.Length} chars)");
            ask = refusal + " Answer again with ONE JSON object for slot " + slot + " and nothing else, fixing only that.";
        }
        record.Status = "failed";
        _out($"[MentorAgentLoop] slot {slot}: FAILED after {record.Attempts} attempts");
        return false;
    }

    /// <summary>One ask's answer: the model may call tools first (bounded by <see cref="SlotToolCallCeiling"/>); the
    /// text of the first reply without tool calls is the answer.</summary>
    private async Task<string> AnswerAsync(string purpose)
    {
        var toolCalls = 0;
        while (true)
        {
            var reply = await CallAsync(purpose, offerTools: true);
            if (reply.ToolCalls.Count == 0) return reply.Content ?? "";
            RunToolCalls(reply, purpose);
            toolCalls += reply.ToolCalls.Count;
            if (toolCalls >= SlotToolCallCeiling)
            {
                _messages.Add(Message("user", "You have made " + toolCalls + " tool calls inside this ask; answer now with the ONE JSON object."));
                var last = await CallAsync(purpose + " (after the tool-call ceiling)", offerTools: false);
                return last.Content ?? "";
            }
        }
    }

    /// <summary>The written slot's value, validated by the ported validator for its kind; a <see cref="SlotError"/> names
    /// the field.</summary>
    private void Validate(string slot, object? value)
    {
        var resolver = new Slots.Resolver(_checks!, _index!);
        if (slot.StartsWith("recommendations[", StringComparison.Ordinal))
            Slots.ValidateRecommendation(slot, value, resolver);
        else if (slot == "went_well")
            Slots.ValidateWentWell(value, resolver);
        else if (slot.StartsWith("prompting.", StringComparison.Ordinal))
            Slots.ValidateDimension(slot.Substring("prompting.".Length), value, resolver, _humanCount);
        else
            throw new InvalidOperationException("no validator for slot " + slot);
    }

    /// <summary>The first JSON object in the model's answer (code fences and prose around it are allowed and
    /// ignored); anything else is a refusal of the slot.</summary>
    internal static object? ParseObject(string content)
    {
        var text = content.Replace("\r\n", "\n");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new SlotError("answer", "is not one JSON object; answer with the object and nothing else");
        object? value;
        try
        {
            value = JsonValues.Parse(text.Substring(start, end - start + 1));
        }
        catch (JsonException error)
        {
            throw new SlotError("answer", "is not valid JSON: " + error.Message);
        }
        if (value is not Dictionary<string, object?>)
            throw new SlotError("answer", "is not one JSON object");
        return value;
    }

    /// <summary>The host message for one slot: the slot, its shape and its limits, from the validators' constants.</summary>
    public static string AskText(string slot)
    {
        const string common = " Every value is one string on one line, ASCII only, no #, a double mark only inside a citation's "
            + "fragment, no provider or model name, every sentence ended with a full stop. Every citation is the exact string cite "
            + "returned and every quoted fragment was answered true by verify_quote before you write it; call them now if you have not.";
        if (slot.StartsWith("recommendations[", StringComparison.Ordinal))
        {
            var number = slot[16] - '0' + 1;
            return "SLOT " + slot + ": finding number " + number + " of three, ordered by the benefit you expect, largest first"
                + (number > 1 ? ", different from the findings you already gave" : "") + ". Answer with ONE JSON object and nothing "
                + "else, exactly the keys " + string.Join(", ", Slots.RecommendationKeys) + ". title: " + Slots.TitleMinWords + " to "
                + Slots.TitleMaxWords + " words, words only: no digit, no citation, no double mark. saw: at most " + Slots.SawMaxWords
                + " words, at least " + Slots.SawMinCitations + " citations, at least one of them quoted; the number that goes with what "
                + "you saw, said as a person would say it aloud (no decimal fraction, nothing over four digits). cost: at most "
                + Slots.CostMaxWords + " words, one or " + Slots.CostMaxSentences + " sentences, at least " + Slots.CostMinCitations
                + " citation; what this takes from the developer this week. try: at most " + Slots.TryMaxWords
                + " words, one sentence, one concrete step the developer performs in the coming days." + common;
        }
        if (slot == "went_well")
        {
            return "SLOT went_well: one thing that went well this week. Answer with ONE JSON object and nothing else, exactly the key "
                + "text: at most " + Slots.WentWellMaxWords + " words, with one quoted citation; when nothing qualifies, exactly '"
                + Slots.NothingQualified + "'." + common;
        }
        if (slot.StartsWith("prompting.", StringComparison.Ordinal))
        {
            var key = slot.Substring("prompting.".Length);
            var heading = Slots.Dimensions.First(d => d.Key == key).Heading.Substring(4);
            return "SLOT " + slot + ": the rubric dimension '" + heading + "'. Answer with ONE JSON object and nothing else, exactly the "
                + "keys " + string.Join(", ", Slots.PromptingKeys) + ". level: one word, " + string.Join(", ", Contract.JudgedWords)
                + ", meaning how often the week's prompts show the practice; when week_overview reports fewer than " + Contract.TooFewBelow
                + " human prompts, exactly '" + Contract.TooFew + "'. observation: at most " + Slots.ObservationMaxWords + " words, at least "
                + Slots.ObservationMinCitations + " citations, at least one quoted. step: at most " + Slots.StepMaxWords
                + " words, one sentence, one action." + common;
        }
        throw new ArgumentException("no ask for slot " + slot, nameof(slot));
    }

    // ------------------------------------------------------------------ assembly

    /// <summary>Write slots.json from the accepted slots and assemble; on a slot refusal re-ask that slot once and
    /// assemble again; on a report refusal fail the run. Returns the assembled line, or null when the run failed.</summary>
    private async Task<string?> AssembleAsync()
    {
        while (_record.AssemblyRounds < AssemblyRounds)
        {
            _record.AssemblyRounds++;
            WriteSlotsFile();
            var result = Assembler.Assemble(_record.ScratchDir, _surface);
            if (result.Ok)
            {
                _out("[MentorAgentLoop] Assemble: " + result.Summary);
                return result.Summary;
            }
            var first = result.Refusals[0];
            _record.AssemblyRefusals.Add(first);
            _out($"[MentorAgentLoop] Assemble round {_record.AssemblyRounds}: refused ({result.Refusals.Count} line(s))");
            const string slotPrefix = "REFUSED slot ";
            if (!first.StartsWith(slotPrefix, StringComparison.Ordinal))
            {
                _record.Finish(MentorRunRecord.OutcomeFailed, first);
                return null;
            }
            var colon = first.IndexOf(':', slotPrefix.Length);
            var path = colon < 0 ? first.Substring(slotPrefix.Length) : first.Substring(slotPrefix.Length, colon - slotPrefix.Length);
            var slot = SlotOrder.FirstOrDefault(s => path == s || path.StartsWith(s + ".", StringComparison.Ordinal));
            if (slot is null)
            {
                _record.Finish(MentorRunRecord.OutcomeFailed, first + " (the refusal names no written slot)");
                return null;
            }
            var retries = _assemblerRetries.TryGetValue(slot, out var used) ? used : 0;
            if (retries >= AssemblerRetriesPerSlot)
            {
                _record.Slot(slot).Status = "failed";
                _record.Finish(MentorRunRecord.OutcomeFailed, "slot " + slot + " refused at assembly again: " + first);
                return null;
            }
            _assemblerRetries[slot] = retries + 1;
            var reason = colon < 0 ? "" : first.Substring(colon + 1).Trim();
            if (!await AskSlotAsync(slot, reason))
            {
                var failed = _record.Slot(slot);
                _record.Finish(MentorRunRecord.OutcomeFailed, "slot " + slot + " refused at assembly and again on the retry: " + string.Join(" | ", failed.Refusals));
                return null;
            }
            _record.Write();
        }
        _record.Finish(MentorRunRecord.OutcomeFailed, "the assembly did not succeed within " + AssemblyRounds + " rounds");
        return null;
    }

    private void WriteSlotsFile()
    {
        var recommendations = new List<object?>();
        for (var position = 0; position < Slots.RecommendationCount; position++)
            recommendations.Add(_accepted["recommendations[" + position + "]"]);
        var prompting = new Dictionary<string, object?>();
        foreach (var key in Slots.DimensionKeys) prompting[key] = _accepted["prompting." + key];
        var slots = new Dictionary<string, object?>
        {
            ["recommendations"] = recommendations,
            ["went_well"] = _accepted["went_well"],
            ["prompting"] = prompting,
        };
        File.WriteAllText(Path.Combine(_record.ScratchDir, Slots.SlotsFile), ParityJson.PrettyOrdered(slots) + "\n", new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ the proxy

    /// <summary>One chat-completions reply as the loop reads it.</summary>
    internal sealed class ChatReply
    {
        public string? Id { get; init; }
        public string? Content { get; init; }
        public List<(string Id, string Name, string? Arguments)> ToolCalls { get; } = new();
        public long PromptTokens { get; init; }
        public long CompletionTokens { get; init; }
        public long CachedTokens { get; init; }
    }

    private static Dictionary<string, object?> Message(string role, string content)
        => new() { ["role"] = role, ["content"] = content };

    /// <summary>POST one chat completion over the whole conversation, append the assistant message, record the call.</summary>
    private async Task<ChatReply> CallAsync(string purpose, bool offerTools)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = _messages,
            ["stream"] = false,
        };
        if (offerTools)
        {
            payload["tools"] = _tools;
            payload["tool_choice"] = "auto";
        }
        var (status, body, ms, retryAfter) = await PostAsync(_baseUrl + "/chat/completions", JsonSerializer.Serialize(payload), _runToken);
        if (status != 200)
        {
            _record.AddCall(purpose, status, null, 0, 0, 0, ms);
            _record.Write();
            _out($"[MentorAgentLoop] chat/completions model={_model} -> {status} ({body.Length} bytes) during {purpose}");
            throw new MentorProxyException(status, StatusMessage(status, body, retryAfter), retryAfter);
        }
        ChatReply reply;
        try
        {
            reply = ParseReply(body);
        }
        catch (JsonException error)
        {
            _record.AddCall(purpose, 200, null, 0, 0, 0, ms);
            _record.Write();
            throw new MentorProxyException(200, "the proxy answered 200 with a body that is not JSON: " + error.Message);
        }
        catch (KeyNotFoundException error)
        {
            _record.AddCall(purpose, 200, null, 0, 0, 0, ms);
            _record.Write();
            throw new MentorProxyException(200, "the proxy answered 200 with a reply the loop cannot read: " + error.Message);
        }
        _record.AddCall(purpose, 200, reply.Id, reply.PromptTokens, reply.CompletionTokens, reply.CachedTokens, ms);
        _record.Write();
        _out($"[MentorAgentLoop] chat/completions model={_model} OK: {purpose}, {reply.ToolCalls.Count} tool call(s), {(reply.Content ?? "").Length} chars, usage {reply.PromptTokens}/{reply.CachedTokens}/{reply.CompletionTokens} in {ms} ms");
        var assistant = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = reply.Content };
        if (reply.ToolCalls.Count > 0)
        {
            assistant["tool_calls"] = reply.ToolCalls.Select(c => (object?)new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = c.Name, ["arguments"] = c.Arguments ?? "{}" },
            }).ToList();
        }
        _messages.Add(assistant);
        return reply;
    }

    /// <summary>The wingman's mapping: a 402 by its machine code through the shared copy, a 429 with its Retry-After,
    /// anything else by its status.</summary>
    private static string StatusMessage(int status, string body, TimeSpan? retryAfter)
    {
        if (status == 402)
            return HostedAiMessages.For(HostedAiErrorMapper.Map402(body)).Text + " (code " + HostedAiErrorMapper.ParseErrorCode(body) + ")";
        if (status == 429)
            return "the proxy is rate limiting the mentor" + (retryAfter is null ? "" : "; retry after " + retryAfter.Value.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s");
        return "the mentor model call failed with HTTP " + status + ": " + (body.Length > 300 ? body.Substring(0, 300) : body);
    }

    private async Task<(int Status, string Body, long Ms, TimeSpan? RetryAfter)> PostAsync(string url, string json, CancellationToken runToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(request, runToken);
    }

    private async Task<(int Status, string Body, long Ms, TimeSpan? RetryAfter)> SendAsync(HttpRequestMessage request, CancellationToken runToken)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        timeout.CancelAfter(_callTimeout);
        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!runToken.IsCancellationRequested)
        {
            throw new TimeoutException("the mentor model call did not answer within " + _callTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " seconds");
        }
        using (response)
        {
            return ((int)response.StatusCode, body, sw.ElapsedMilliseconds, RetryAfterHeader.Parse(response.Headers.RetryAfter));
        }
    }

    internal static ChatReply ParseReply(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        long prompt = 0, completion = 0, cached = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number) prompt = p.GetInt64();
            if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number) completion = c.GetInt64();
            if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("cached_tokens", out var cachedEl) && cachedEl.ValueKind == JsonValueKind.Number)
                cached = cachedEl.GetInt64();
        }
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new MentorProxyException(200, "the proxy answered 200 with no choices");
        var message = choices[0].GetProperty("message");
        string? content = null;
        if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String) content = contentEl.GetString();
        var reply = new ChatReply
        {
            Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            Content = content,
            PromptTokens = prompt,
            CompletionTokens = completion,
            CachedTokens = cached,
        };
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var callId = call.TryGetProperty("id", out var callIdEl) && callIdEl.ValueKind == JsonValueKind.String ? callIdEl.GetString()! : "call_" + (reply.ToolCalls.Count + 1);
                var name = function.GetProperty("name").GetString() ?? "";
                var arguments = function.TryGetProperty("arguments", out var argumentsEl) && argumentsEl.ValueKind == JsonValueKind.String ? argumentsEl.GetString() : null;
                reply.ToolCalls.Add((callId, name, arguments));
            }
        }
        return reply;
    }

    // ------------------------------------------------------------------ the rate card

    /// <summary>The served rates for the model from GET /api/v1/models: the pricing block the proxy publishes to this
    /// key. A model without a pricing block, or a model the route does not list, is a failure with the fix named -
    /// cost.json needs the served rates and invents none.</summary>
    public async Task<MentorRunRecord.RateCard> ReadRateCardAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models?type=chat");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var (status, body, ms, _) = await SendAsync(request, ct);
        _out($"[MentorAgentLoop] models -> {status} ({body.Length} bytes) in {ms} ms");
        if (status != 200)
            throw new MentorProxyException(status, "the models route answered " + status + " while reading the rate card");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new MentorProxyException(200, "the models route answered no data list");
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var id) || id.GetString() != _model) continue;
            if (!entry.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("the models route lists " + _model + " with no pricing block for this key; cost.json needs the served "
                    + "rates, and the proxy publishes them only to an administrator key - use the deployment key");
            return new MentorRunRecord.RateCard(_model,
                pricing.GetProperty("inputPerMTokUsd").GetDouble(),
                pricing.GetProperty("cachedInputPerMTokUsd").GetDouble(),
                pricing.GetProperty("outputPerMTokUsd").GetDouble(),
                pricing.TryGetProperty("asOf", out var asOf) && asOf.ValueKind == JsonValueKind.String ? asOf.GetString() : null,
                RatesSource);
        }
        throw new InvalidOperationException("the models route does not list " + _model + "; the alias is not served on this proxy yet");
    }
}
