using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// What a read of a session's transcript concluded about whether the agent is holding a structured
/// ask. Three answers, not two, because "I could not look" is not the same fact as "I looked and
/// there is nothing" - and writing the second when you mean the first is how a question box that is
/// genuinely open gets erased.
/// </summary>
public enum PendingInteractionReading
{
    /// <summary>The transcript was read and no ask is outstanding. The session's
    /// <see cref="Session.PendingInteraction"/> is cleared.</summary>
    NothingPending,

    /// <summary>The transcript was read and the agent is waiting on the carried interaction.</summary>
    Pending,

    /// <summary>
    /// No answer at all: the session is not running Claude Code, or its transcript could not be
    /// resolved, read, or parsed. The session's <see cref="Session.PendingInteraction"/> is left
    /// exactly as it was. This is not a guess and never becomes one.
    /// </summary>
    NoAnswer,
}

/// <summary>One reading, with the interaction it found when there is one.</summary>
/// <param name="Reading">What the read concluded.</param>
/// <param name="Interaction">The outstanding ask; null for every reading but
/// <see cref="PendingInteractionReading.Pending"/>.</param>
public readonly record struct PendingInteractionResult(
    PendingInteractionReading Reading,
    PendingInteraction? Interaction);

/// <summary>
/// Decides whether a session has a question box open, by reading the agent's OWN TRANSCRIPT - never by
/// guessing at what is drawn on the terminal.
///
/// The rule is one sentence: a Claude Code tool call to <c>AskUserQuestion</c> or <c>ExitPlanMode</c>
/// that has no matching tool result yet IS a box on the user's screen, because Claude Code does not
/// write the result until the user answers it. That pairing - a tool use answered by the later tool
/// result carrying the same tool use id - is the product's own, already in
/// <see cref="WidgetBuilder.BuildFromMessages"/>, and this reads the same parse
/// (<see cref="StreamMessageParser.ParseFile"/>) rather than inventing a second one.
///
/// WHAT THIS DELIBERATELY DOES NOT DO, said plainly so nobody has to go looking:
///
/// - It answers for CLAUDE CODE ONLY. <c>AskUserQuestion</c> and <c>ExitPlanMode</c> are Claude Code's
///   tools; no other agent writes them. A session of any other agent gets
///   <see cref="PendingInteractionReading.NoAnswer"/> and its transcript is not even opened. Guessing
///   at another agent's screen is exactly the thing this replaces.
/// - It cannot produce <see cref="PendingInteractionKind.Permission"/>, and it does not fake one. A
///   permission prompt was reported by a hook event (a PermissionRequest, or a Notification with
///   notification_type=permission_prompt) and that event is not written into the transcript at all.
///   Question and Plan are what a transcript can honestly say.
/// - It never throws at its caller. This runs at the end of every turn of every session, and a
///   transcript that has vanished, is half-written, or is not JSON at all is an ordinary Tuesday. Those
///   are logged and answered <see cref="PendingInteractionReading.NoAnswer"/>.
/// </summary>
public static class PendingInteractionDetector
{
    /// <summary>Claude Code's question tool. An unanswered call to it is a question box on screen.</summary>
    internal const string AskUserQuestionTool = "AskUserQuestion";

    /// <summary>Claude Code's plan-approval tool. An unanswered call to it is a plan awaiting approval.</summary>
    internal const string ExitPlanModeTool = "ExitPlanMode";

    /// <summary>
    /// What is said when the tool call is plainly unanswered but its wording cannot be read - the input
    /// was not the shape expected, or the parser truncated an oversized value and left the JSON
    /// unclosed. The DETECTION is still certain, so the honest report is "there is a box, and I cannot
    /// quote it", never "there is no box". These are the same words the Agent view already shows for a
    /// question (<see cref="WidgetBuilder"/>).
    /// </summary>
    internal const string QuestionTextNotReadable = "Claude needs your input";

    /// <summary>The headline for a plan awaiting approval, as PendingInteraction.Prompt documents it.</summary>
    internal const string PlanHeadline = "Plan ready - approve?";

    /// <summary>
    /// Read this session's transcript and say whether it is holding a question box or a plan. Never
    /// throws.
    /// </summary>
    public static PendingInteractionResult Detect(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            // Claude Code only, and the transcript is not opened for anyone else. See the class remarks.
            if (session.AgentKind != AgentKind.ClaudeCode)
                return new PendingInteractionResult(PendingInteractionReading.NoAnswer, null);

            var path = SessionHistoryReader.ResolveTranscriptPath(session);
            if (string.IsNullOrWhiteSpace(path))
            {
                FileLog.Write($"[PendingInteractionDetector] Detect: sessionId={session.Id} has no transcript " +
                              "path yet, so there is no answer; the pending interaction is left as it was");
                return new PendingInteractionResult(PendingInteractionReading.NoAnswer, null);
            }

            var messages = StreamMessageParser.ParseFile(path);

            // Zero messages is the one shape that covers every way the read can fail: the file is not
            // there, it is empty, or every line of it failed to parse (the parser swallows a bad line
            // and returns what it could). None of those is evidence that nothing is pending, so none of
            // them clears the property. A transcript that genuinely holds nothing pending has messages
            // in it and takes the ordinary path below.
            if (messages.Count == 0)
            {
                FileLog.Write($"[PendingInteractionDetector] Detect: sessionId={session.Id} transcript " +
                              $"'{path}' yielded no parsed messages (missing, empty, or unreadable), so " +
                              "there is no answer; the pending interaction is left as it was");
                return new PendingInteractionResult(PendingInteractionReading.NoAnswer, null);
            }

            return FromMessages(messages);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PendingInteractionDetector] Detect FAILED (swallowed) sessionId={session.Id}: {ex.Message}");
            return new PendingInteractionResult(PendingInteractionReading.NoAnswer, null);
        }
    }

    /// <summary>
    /// The detection itself, over an already-parsed transcript.
    ///
    /// Only the two interaction tools are tracked. An ordinary Bash or Read with no result yet is a
    /// session that is BUSY, not one that is asking - and since every working session has one of those
    /// outstanding most of the time, tracking them would light the question-box flag on the whole fleet.
    ///
    /// The LAST still-unanswered ask wins: when a transcript holds two, the earlier one has been
    /// superseded on screen by the newer one, which is the box the user is actually looking at.
    /// </summary>
    internal static PendingInteractionResult FromMessages(IEnumerable<StreamMessage> messages)
    {
        var unanswered = new List<ContentBlock>();

        foreach (var message in messages)
        {
            // Meta lines are the harness talking to itself, not the agent asking the user - the same
            // lines WidgetBuilder.BuildFromMessages skips.
            if (message.IsMeta) continue;

            foreach (var block in message.ContentBlocks)
            {
                if (string.IsNullOrEmpty(block.ToolUseId)) continue;

                if (block.Type == ContentBlockType.ToolUse &&
                    block.ToolName is AskUserQuestionTool or ExitPlanModeTool)
                {
                    unanswered.Add(block);
                }
                else if (block.Type == ContentBlockType.ToolResult)
                {
                    // The user answered it. Claude Code writes this result only once they have.
                    unanswered.RemoveAll(b => b.ToolUseId == block.ToolUseId);
                }
            }
        }

        if (unanswered.Count == 0)
            return new PendingInteractionResult(PendingInteractionReading.NothingPending, null);

        return new PendingInteractionResult(PendingInteractionReading.Pending, Build(unanswered[^1]));
    }

    private static PendingInteraction Build(ContentBlock block) =>
        block.ToolName == ExitPlanModeTool ? BuildPlan(block) : BuildQuestion(block);

    private static PendingInteraction BuildPlan(ContentBlock block) => new()
    {
        Kind = PendingInteractionKind.Plan,
        CreatedAt = DateTimeOffset.UtcNow,
        Prompt = PlanHeadline,
        // ExitPlanMode carries the markdown plan on its "plan" input. Absent means the agent sent none;
        // the plan is still waiting either way.
        PlanBody = block.ToolInput.GetValueOrDefault("plan"),
    };

    /// <summary>
    /// The question and its options, read off the tool call's own input.
    ///
    /// AskUserQuestion's input is a "questions" ARRAY, each entry carrying the question text, a short
    /// header and a list of options. The parser stores a non-string input value as its raw JSON, so
    /// that array arrives here as a JSON string to be read. The FIRST entry is the one taken: a
    /// PendingInteraction holds one prompt, and the first is the one the box leads with.
    /// </summary>
    private static PendingInteraction BuildQuestion(ContentBlock block)
    {
        var prompt = QuestionTextNotReadable;
        var options = new List<PendingInteractionOption>();

        if (block.ToolInput.TryGetValue("questions", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var question in document.RootElement.EnumerateArray())
                    {
                        if (question.TryGetProperty("question", out var text) &&
                            text.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(text.GetString()))
                        {
                            prompt = text.GetString()!;
                        }

                        if (question.TryGetProperty("options", out var list) &&
                            list.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var option in list.EnumerateArray())
                                options.Add(new PendingInteractionOption
                                {
                                    Label = option.TryGetProperty("label", out var label)
                                        ? label.GetString() ?? "" : "",
                                    Description = option.TryGetProperty("description", out var description)
                                        ? description.GetString() : null,
                                });
                        }

                        break;
                    }
                }
            }
            catch (JsonException ex)
            {
                // The input was not readable JSON. The commonest cause is benign and expected: the
                // transcript parser truncates any input value over 2000 characters and appends a marker,
                // which leaves the array unclosed. That costs the wording, not the detection - the tool
                // call is still plainly unanswered - so the box is reported with its text unread.
                FileLog.Write("[PendingInteractionDetector] BuildQuestion: the question text could not be " +
                              $"read from the tool input, so the box is reported without it: {ex.Message}");
            }
        }

        return new PendingInteraction
        {
            Kind = PendingInteractionKind.Question,
            CreatedAt = DateTimeOffset.UtcNow,
            Prompt = prompt,
            Options = options,
        };
    }
}
