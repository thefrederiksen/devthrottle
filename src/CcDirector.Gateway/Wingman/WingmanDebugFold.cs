using System.Text.Json;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE WINGMAN DEBUG VIEW'S ANSWER: for each stop, what was fed in, the exact prompt, and the raw answer -
/// FOR BOTH MODEL CALLS (the owner's ruling of 2026-09-18).
///
/// WHY IT EXISTS. A reading takes two model calls, and until this was built only the first was recorded at
/// all, with nothing able to read even that back. So when a reading came out wrong there was no way to ask
/// the one question that settles it: what was the model actually given, and what did it actually say? The
/// contract was shrunk from twelve fields to five on the strength of a measurement nobody could check
/// against the model's own words. This is that check, and it is why the owner wanted it BEFORE the shrink.
///
/// IT IS A DUMP, NOT A FOLD, AND THAT IS DELIBERATE. Every other Gateway fold in this product decides what
/// a screen means and hands the client finished words (CLAUDE.md rule 7). This one hands over the stored
/// bytes: the package as it was serialized, the prompt as it was sent, the answer as it came back. A debug
/// view that showed a rendering of the evidence rather than the evidence would answer a different question
/// from the one it is opened to answer. The only things decided here are the headings and the sentence
/// shown where a call was not made.
///
/// WHAT IT NEVER CARRIES: anything from another account. The route resolves the caller's tenant and reads
/// that tenant's traces, exactly as every other session read does - see <c>GatewayEndpoints.ReadWingmanDebug</c>.
/// </summary>
public static class WingmanDebugFold
{
    /// <summary>The heading over the judge's half of a reading.</summary>
    public const string JudgeCallTitle = "Call one - what happened";

    /// <summary>The heading over the narration's half.</summary>
    public const string NarrationCallTitle = "Call two - the words";

    /// <summary>What is shown where the judge was never asked - a stop that stood down, or one that reused
    /// an answer it already had.</summary>
    public const string JudgeNotAsked = "The judge was not asked about this stop.";

    /// <summary>What is shown where the model was not asked: which code step decided instead, when one did (contract
    /// v4), so a reader of a stop can see at once that code, not the model, made the call.</summary>
    internal static string JudgeNotAskedFor(TurnVerdictDto? verdict)
        => verdict is { Failed: false, DecidedBy: { Length: > 0 } step } && step != CallACodeSteps.ModelStep
            ? $"The model was not asked: code decided this stop at the '{step}' step - {verdict.DecisionReason}"
            : JudgeNotAsked;

    /// <summary>What is shown where the narration call was not made.</summary>
    public const string NarrationNotMade =
        "The narration call was not made for this stop. It is not made for a judgement the contract refused, "
        + "for a session another live session owns, or for an account whose plan does not include the Wingman.";

    /// <summary>What is shown where a package was built but was over the store's ceiling and not kept.</summary>
    public const string PackageOmitted =
        "What the Wingman was fed was larger than the record keeps, so it was not stored for this stop.";

    /// <summary>What is shown where no package was built at all.</summary>
    public const string PackageAbsent = "Nothing was fed to a model for this stop: no package was built.";

    /// <summary>Both calls of every stop in the page, newest first.</summary>
    public static WingmanDebugResponse Fold(string sessionId, IReadOnlyList<TurnVerdictTrace> traces)
    {
        ArgumentNullException.ThrowIfNull(traces);
        var answer = new WingmanDebugResponse { SessionId = sessionId };
        foreach (var trace in traces) answer.Stops.Add(One(trace));
        return answer;
    }

    private static WingmanDebugStop One(TurnVerdictTrace trace)
    {
        var verdict = trace.Verdict;
        return new WingmanDebugStop
        {
            TraceId = trace.TraceId,
            ObservedAtUtc = trace.TurnEndObservedAtUtc,
            RecordedAtUtc = trace.RecordedAtUtc,
            Trigger = trace.Trigger,
            Outcome = trace.Outcome,
            Cause = trace.Cause,
            VerdictId = trace.VerdictId,
            Model = verdict?.Model,
            ContractVersion = verdict?.ContractVersion,
            // THE READING AS IT WAS STORED, serialized whole. The debug view shows the five fields of the
            // contract beside the raw answer that produced them, so a reader can see what validation kept and
            // what it dropped without holding two screens side by side.
            State = verdict?.State,
            Label = verdict?.Label,
            Narration = verdict?.Narration,
            Failed = verdict?.Failed ?? false,
            FailureReason = verdict?.FailureReason,
            OptionsDroppedReason = verdict?.OptionsDroppedReason,
            DecidedBy = verdict?.DecidedBy,
            DecisionReason = verdict?.DecisionReason,
            RetriesMade = verdict?.RetriesMade ?? 0,
            NextRetryAtUtc = verdict?.NextRetryAtUtc,
            RowColour = trace.RowColour,
            RowLabel = trace.RowLabel,

            // ---- what both calls were fed. One package; both calls get it.
            Fed = trace.Package is null
                ? null
                : JsonSerializer.Serialize(trace.Package, new JsonSerializerOptions { WriteIndented = true }),
            FedAbsentText = trace.Package is not null ? null : trace.PackageOmitted ? PackageOmitted : PackageAbsent,

            // ---- call one
            JudgePrompt = trace.Prompt,
            JudgePromptCutText = trace.PromptTruncated ? "This prompt was longer than the record keeps and was cut." : null,
            JudgeRawReply = trace.RawReply,
            JudgeRawReplyCutText = trace.RawReplyTruncated ? "This answer was longer than the record keeps and was cut." : null,
            JudgeSeconds = trace.ReplySeconds,
            JudgeNotAskedText = trace.Prompt is null ? JudgeNotAskedFor(verdict) : null,

            // ---- call two
            NarrationPrompt = trace.NarrationPrompt,
            NarrationPromptCutText = trace.NarrationPromptTruncated ? "This prompt was longer than the record keeps and was cut." : null,
            NarrationRawReply = trace.NarrationRawReply,
            NarrationRawReplyCutText = trace.NarrationRawReplyTruncated ? "This answer was longer than the record keeps and was cut." : null,
            NarrationSeconds = trace.NarrationSeconds,
            NarrationFailureDetail = trace.NarrationFailureDetail,
            NarrationNotMadeText = trace.NarrationPrompt is null && trace.NarrationFailureDetail is null ? NarrationNotMade : null,
        };
    }
}

/// <summary>Every stop of one session, with both model calls shown whole. Staff only.</summary>
public sealed class WingmanDebugResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>The name of the first call, for the heading the client draws.</summary>
    public string JudgeCallTitle { get; set; } = WingmanDebugFold.JudgeCallTitle;

    /// <summary>The name of the second call.</summary>
    public string NarrationCallTitle { get; set; } = WingmanDebugFold.NarrationCallTitle;

    public List<WingmanDebugStop> Stops { get; set; } = new();
}

/// <summary>One stop, both calls. Every string here is shown as it is; nothing is re-derived by a client.</summary>
public sealed class WingmanDebugStop
{
    public string TraceId { get; set; } = "";
    public DateTime ObservedAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string Trigger { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Cause { get; set; }
    public string? VerdictId { get; set; }
    public string? Model { get; set; }
    public string? ContractVersion { get; set; }

    public string? State { get; set; }
    public string? Label { get; set; }
    public string? Narration { get; set; }
    public bool Failed { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Why this reading's buttons were dropped while the reading itself was kept, or null.</summary>
    public string? OptionsDroppedReason { get; set; }

    /// <summary>Which step of Call A decided this stop - a code step, or "model" - or null before contract v4.</summary>
    public string? DecidedBy { get; set; }

    /// <summary>Why that step decided, in words a person can read, or null before contract v4.</summary>
    public string? DecisionReason { get; set; }

    /// <summary>On a failed reading: how many scheduled retries the stop had spent when this was stored.</summary>
    public int RetriesMade { get; set; }

    /// <summary>On a failed reading: when the next retry was booked for, or null when nothing was booked.</summary>
    public DateTime? NextRetryAtUtc { get; set; }

    public string? RowColour { get; set; }
    public string? RowLabel { get; set; }

    public string? Fed { get; set; }
    public string? FedAbsentText { get; set; }

    public string? JudgePrompt { get; set; }
    public string? JudgePromptCutText { get; set; }
    public string? JudgeRawReply { get; set; }
    public string? JudgeRawReplyCutText { get; set; }
    public double? JudgeSeconds { get; set; }
    public string? JudgeNotAskedText { get; set; }

    public string? NarrationPrompt { get; set; }
    public string? NarrationPromptCutText { get; set; }
    public string? NarrationRawReply { get; set; }
    public string? NarrationRawReplyCutText { get; set; }
    public double? NarrationSeconds { get; set; }
    public string? NarrationFailureDetail { get; set; }
    public string? NarrationNotMadeText { get; set; }
}
