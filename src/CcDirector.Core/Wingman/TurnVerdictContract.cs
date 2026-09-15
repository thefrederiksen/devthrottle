using System.Text;
using System.Text.Json;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// THE turn-verdict contract: the one question the Wingman is asked about a stop, and the mechanical
/// validation of its JSON answer. One model call per stop, and the spoken version is a section of the
/// same answer rather than a second call.
///
/// Everything here is pure - no model calls, no input or output beyond reading the embedded prompt and
/// writing a log line. Validation is MECHANICAL and never interpretive: closed word lists, length
/// caps, structural invariants, and a receipt check that looks for the quoted sentence in the source
/// text. Nothing here reads the answer for sense. That is the whole point: a judge that could talk its
/// way past its own validation is not validated.
///
/// THE DIRECTION OF EVERY DOUBT. A calm verdict takes a session out of the owner's queue, so a wrong
/// calm verdict is the one that goes unnoticed - the session sits there and nobody comes. Every rule
/// here therefore fails AWAY from calm: a refused answer leaves the row exactly as the detector left
/// it, which is red. Silence is never a decision, and a broken answer never moves a row toward quiet.
///
/// The prompt lives in Prompts/turn-verdict-v1.txt as an embedded resource rather than in this file,
/// because the grading tool in the internal repository renders the SAME prompt from the SAME bytes. A
/// prompt that existed twice would be graded in one version and shipped in another.
/// </summary>
public static class TurnVerdictContract
{
    /// <summary>Stamped on every verdict record so a stored answer can say which contract produced it.
    /// Bump on every change to the prompt or to validation.</summary>
    public const string Version = "v1";

    /// <summary>The embedded name of the prompt template. The grading tool reads the same file off
    /// disk at src/CcDirector.Core/Wingman/Prompts/turn-verdict-v1.txt; a test pins the two to be
    /// byte for byte the same.</summary>
    public const string PromptResourceName = "CcDirector.Core.Wingman.Prompts.turn-verdict-v1.txt";

    /// <summary>The repository-relative path of the same file, for the tool that reads it off disk and
    /// for the test that pins the embedded copy to it.</summary>
    public const string PromptResourcePath = "src/CcDirector.Core/Wingman/Prompts/turn-verdict-v1.txt";

    // ==================================================================== caps
    //
    // BOUNDS THIS CONTRACT ADDS, AND WHY. The frozen contract names caps on label, summary and spoken.
    // The rest below are this file's own, and each exists because an unbounded field reaches a screen
    // or a database: they are stated here in one block rather than scattered, so a reader can see
    // exactly what was added beyond the specification and argue with it in one place.

    /// <summary>The one line a row shows. From the frozen contract.</summary>
    public const int MaxLabelChars = 80;

    /// <summary>One or two sentences for a cold reader. From the frozen contract.</summary>
    public const int MaxSummaryChars = 400;

    /// <summary>About thirty seconds out loud. From the frozen contract.</summary>
    public const int MaxSpokenChars = 900;

    /// <summary>The agent's own recommendation. This contract's own cap - same size as the summary,
    /// because it is the same kind of sentence and lands in the same panel.</summary>
    public const int MaxAgentRecommendsChars = 400;

    /// <summary>The receipt. This contract's own cap: a receipt is one decisive sentence, and a model
    /// that pastes a whole reply into it has not picked one. Truncating would break the receipt check,
    /// so an over-long receipt is REFUSED rather than cut.</summary>
    public const int MaxEvidenceChars = 600;

    /// <summary>A menu question. This contract's own cap.</summary>
    public const int MaxMenuQuestionChars = 200;

    /// <summary>An option's label. This contract's own cap, matching the existing brief contract.</summary>
    public const int MaxOptionKeyChars = 60;

    /// <summary>An option's note: at most eighteen words in the frozen contract, which this holds as a
    /// character bound because words are not mechanically countable without inventing a word rule.</summary>
    public const int MaxOptionNoteChars = 140;

    /// <summary>A sanity bound on how many ways of answering one stop may carry. This contract's own:
    /// an unbounded array reaches a screen and a database. Beyond it the answer is REFUSED rather than
    /// trimmed, because dropping an option changes what the reader can choose.</summary>
    public const int MaxOptions = 12;

    // ==================================================================== the prompt

    /// <summary>Every placeholder the template may contain. The template is filled in ONE pass, so a
    /// value that happens to contain a placeholder is never rescanned - a screen row reading
    /// "{{SCREEN_ROWS}}" is inert text, not a way into the prompt.</summary>
    public static readonly IReadOnlyList<string> PromptPlaceholders = new[]
    {
        "PACKAGE_KIND",
        "CONVERSATION_AVAILABLE",
        "SESSION_TITLE",
        "AGENT_KIND",
        "FIRST_USER_PROMPT",
        "PREVIOUS_VERDICT_LABEL",
        "TURN_END_CAUSE",
        "TURN_END_CONFIDENCE",
        "PENDING_WAKE_UPS",
        "NEXT_SCHEDULED_WAKE",
        "RECENT_TURNS",
        "REPLY_OR_FAILURE",
        "CURSOR_ROW",
        "ALTERNATE_SCREEN",
        "SCREEN_ROWS",
    };

    /// <summary>What an absent fact is written as. One string for all of them, so the judge never has
    /// to tell "we did not look" apart from "there was nothing" by the shape of a blank line.</summary>
    public const string AbsentValue = "(none)";

    private static string? _promptTemplate;

    /// <summary>The prompt template exactly as embedded. Throws when the resource is missing, because a
    /// build that shipped without its prompt cannot judge anything and must say so loudly rather than
    /// send an empty question to a model.</summary>
    public static string PromptTemplate
    {
        get
        {
            if (_promptTemplate is not null) return _promptTemplate;
            var assembly = typeof(TurnVerdictContract).Assembly;
            using var stream = assembly.GetManifestResourceStream(PromptResourceName)
                ?? throw new InvalidOperationException(
                    $"The turn-verdict prompt resource '{PromptResourceName}' is not embedded in "
                    + $"{assembly.GetName().Name}. It is declared as an EmbeddedResource in "
                    + "CcDirector.Core.csproj and its source is " + PromptResourcePath + ".");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _promptTemplate = reader.ReadToEnd();
            return _promptTemplate;
        }
    }

    /// <summary>The whole question asked of the judge for one stop.</summary>
    public static string BuildPrompt(TurnVerdictPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PACKAGE_KIND"] = TurnVerdictPackage.WireName(package.Kind),
            ["CONVERSATION_AVAILABLE"] = package.ConversationAvailable ? "yes" : "no",
            ["SESSION_TITLE"] = Present(package.SessionTitle),
            ["AGENT_KIND"] = Present(package.AgentKind),
            ["FIRST_USER_PROMPT"] = Present(package.FirstUserPrompt),
            ["PREVIOUS_VERDICT_LABEL"] = Present(package.PreviousVerdictLabel),
            // Null on every agent in this build - no producer stamps a turn-end cause yet. The judge is
            // told so in the same words as any other absent fact, which is the honest thing to say: the
            // boundary was a timer guess and nothing measured why.
            ["TURN_END_CAUSE"] = Present(package.TurnEndCause),
            ["TURN_END_CONFIDENCE"] = Present(package.TurnEndConfidence),
            ["PENDING_WAKE_UPS"] = package.PendingWakeUps?.ToString() ?? AbsentValue,
            ["NEXT_SCHEDULED_WAKE"] = package.NextScheduledWakeUtc?.ToString("u") ?? AbsentValue,
            ["RECENT_TURNS"] = Present(package.RecentTurns),
            ["REPLY_OR_FAILURE"] = Present(package.SourceText),
            ["CURSOR_ROW"] = package.CursorRow >= 0 ? package.CursorRow.ToString() : AbsentValue,
            ["ALTERNATE_SCREEN"] = package.IsAlternateScreen ? "yes" : "no",
            ["SCREEN_ROWS"] = package.ScreenRows.Count == 0
                ? AbsentValue
                : string.Join("\n", package.ScreenRows),
        };

        return Fill(PromptTemplate, values);
    }

    /// <summary>
    /// Fill the template in ONE left-to-right pass. A filled value is never rescanned, so no screen
    /// row, reply or title can introduce a placeholder of its own. An unknown placeholder THROWS: a
    /// template carrying a name the code does not fill is a defect in this pair of files, and sending
    /// the model a question with a literal "{{...}}" in it would hide that defect behind a worse
    /// answer.
    /// </summary>
    private static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length + 8_192);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(template, i, template.Length - i); break; }
            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { sb.Append(template, i, template.Length - i); break; }

            var name = template[(open + 2)..close];
            if (!values.TryGetValue(name, out var value))
                throw new InvalidOperationException(
                    $"The turn-verdict prompt template contains the placeholder '{{{{{name}}}}}', which "
                    + "nothing fills. Add it to TurnVerdictContract.PromptPlaceholders and give it a value "
                    + "in BuildPrompt, or take it out of " + PromptResourcePath + ".");

            sb.Append(template, i, open - i);
            sb.Append(value);
            i = close + 2;
        }
        return sb.ToString();
    }

    private static string Present(string? value)
        => string.IsNullOrWhiteSpace(value) ? AbsentValue : value;

    // ==================================================================== validation

    /// <summary>
    /// Parse and validate one answer. NEVER returns null: an answer that fails any rule comes back as a
    /// record with <see cref="TurnVerdictDto.Failed"/> set and the reason in plain words, so a refusal
    /// is stored and answerable by query exactly as an accepted answer is. A refusal that left only a
    /// log line would be a decision nobody could audit.
    /// </summary>
    /// <param name="raw">The judge's answer, exactly as it came back.</param>
    /// <param name="package">The package the answer was asked about - the source the receipt is checked against.</param>
    /// <param name="model">Which judge answered. Stored, and the grading depends on it, so it is required
    /// rather than defaulted: a record that cannot say who answered cannot be graded.</param>
    /// <param name="turnEndObservedAtUtc">When the detector observed the stop - the join key to the turn log.</param>
    public static TurnVerdictDto ParseAndValidate(
        string? raw,
        TurnVerdictPackage package,
        string model,
        DateTime turnEndObservedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (string.IsNullOrWhiteSpace(raw))
            return Refuse(package, model, turnEndObservedAtUtc, "the judge answered nothing");

        var json = Unwrap(raw);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            return Refuse(package, model, turnEndObservedAtUtc, $"the answer is not valid JSON ({ex.Message})");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refuse(package, model, turnEndObservedAtUtc, "the answer is not a JSON object");

            // ---- verdict: one of the six, and nothing else ----------------------------------
            var verdict = Str(root, "verdict");
            if (!TurnVerdictVocabulary.IsWingmanVerdict(verdict))
                return Refuse(package, model, turnEndObservedAtUtc,
                    $"unknown verdict word '{verdict}'; the six allowed words are "
                    + string.Join(", ", TurnVerdictVocabulary.WingmanVerdicts));

            // ---- a failure with no reply can never be calm -----------------------------------
            if (package.Kind == TurnVerdictPackageKind.TerminalFailure && TurnVerdictVocabulary.IsCalm(verdict))
                return Refuse(package, model, turnEndObservedAtUtc,
                    $"the verdict '{verdict}' is calm, and this stop has no reply at all - its turn ended on "
                    + "a failure shown on screen, which cannot mean the session finished or is carrying on");

            // ---- risk: one of the four, and never defaulted ----------------------------------
            var risk = Str(root, "risk");
            if (!TurnVerdictVocabulary.Risks.Contains(risk, StringComparer.Ordinal))
                return Refuse(package, model, turnEndObservedAtUtc,
                    risk.Length == 0
                        ? "no risk word; there is no safe default, because defaulting to 'none' would tell the "
                          + "owner an irreversible action is free"
                        : $"unknown risk word '{risk}'; the four allowed words are "
                          + string.Join(", ", TurnVerdictVocabulary.Risks));

            // ---- the receipt ------------------------------------------------------------------
            var evidence = Str(root, "evidence");
            if (verdict != TurnVerdictVocabulary.CannotTell)
            {
                if (evidence.Length == 0)
                    return Refuse(package, model, turnEndObservedAtUtc,
                        "no evidence; every verdict except cannot-tell must carry the agent's own decisive "
                        + "sentence as a receipt");
                if (evidence.Length > MaxEvidenceChars)
                    return Refuse(package, model, turnEndObservedAtUtc,
                        $"the evidence is {evidence.Length} characters, over the {MaxEvidenceChars} character "
                        + "bound; a receipt is one decisive sentence, and it cannot be cut without breaking "
                        + "the check that it is verbatim");
                if (!EvidenceIsVerbatim(package, evidence))
                    return Refuse(package, model, turnEndObservedAtUtc,
                        "the evidence is not found verbatim in the reply or on the screen; it was paraphrased, "
                        + "retyped or invented, and an unanchored answer is thrown away whole");
            }

            // ---- label and summary ------------------------------------------------------------
            var label = Str(root, "label");
            if (label.Length == 0)
                return Refuse(package, model, turnEndObservedAtUtc,
                    "no label; the label is the one line every row shows, and a row that reports nothing "
                    + "reads as broken");
            if (label.Length > MaxLabelChars) label = label[..MaxLabelChars];

            var summary = Str(root, "summary");
            if (summary.Length > MaxSummaryChars) summary = summary[..MaxSummaryChars];

            // ---- the spoken section -----------------------------------------------------------
            var spoken = Str(root, "spoken");
            if (spoken.Length == 0)
                return Refuse(package, model, turnEndObservedAtUtc,
                    "no spoken section; it is produced for every owned stop, whether or not anybody is "
                    + "listening, and a stop without one is silent in the car");
            if (spoken.Length > MaxSpokenChars) spoken = spoken[..MaxSpokenChars];

            // ---- how the person answers -------------------------------------------------------
            var optionsResult = ReadOptions(root);
            if (optionsResult.Reason is not null)
                return Refuse(package, model, turnEndObservedAtUtc, optionsResult.Reason);
            var options = optionsResult.Options;

            var hasAnswerVia = root.TryGetProperty("answerVia", out var answerViaElement)
                && answerViaElement.ValueKind == JsonValueKind.String;
            var answerVia = hasAnswerVia ? (answerViaElement.GetString() ?? "").Trim() : "";
            if (hasAnswerVia && !TurnVerdictVocabulary.AnswerVias.Contains(answerVia, StringComparer.Ordinal))
                return Refuse(package, model, turnEndObservedAtUtc,
                    $"unknown answerVia word '{answerVia}'; it decides whether a carriage return is appended "
                    + "to what gets typed into a live session, so it is never guessed");
            if (!hasAnswerVia)
            {
                if (options.Count > 0)
                    return Refuse(package, model, turnEndObservedAtUtc,
                        "options were offered with no answerVia; nothing can say how those bytes reach the "
                        + "session");
                // No options and no answerVia: there is nothing to answer, so the field is inert. "reply"
                // is written rather than an empty string so the stored shape is always one of the two words.
                answerVia = "reply";
            }

            var menuResult = ReadMenu(root);
            if (menuResult.Reason is not null)
                return Refuse(package, model, turnEndObservedAtUtc, menuResult.Reason);
            if (answerVia == "keys" && menuResult.Menu is null)
                return Refuse(package, model, turnEndObservedAtUtc,
                    "the answer is a selection in a picker but carries no menu; without it nothing knows "
                    + "what question the keys answer");

            // ---- confidence -------------------------------------------------------------------
            // A word outside the pair is read as "ambiguous" rather than "high". The judge did not say it
            // was sure, so nothing here may say so on its behalf. Confidence is not a colour and never
            // demotes a red stop, so this cannot fail toward quiet.
            var confidence = Str(root, "confidence");
            if (!TurnVerdictVocabulary.Confidences.Contains(confidence, StringComparer.Ordinal))
                confidence = "ambiguous";

            var agentRecommends = Str(root, "agentRecommends");
            if (agentRecommends.Length > MaxAgentRecommendsChars)
                agentRecommends = agentRecommends[..MaxAgentRecommendsChars];

            return new TurnVerdictDto
            {
                VerdictId = Guid.NewGuid().ToString("N"),
                JudgedAtUtc = DateTime.UtcNow,
                TurnEndObservedAtUtc = turnEndObservedAtUtc,
                ScreenHash = package.ScreenHash,
                Model = model,
                ContractVersion = Version,
                PackageKind = TurnVerdictPackage.WireName(package.Kind),
                Failed = false,
                FailureReason = null,
                Verdict = verdict,
                Confidence = confidence,
                Evidence = evidence,
                Label = label,
                Summary = summary,
                AgentRecommends = agentRecommends.Length == 0 ? null : agentRecommends,
                AnswerVia = answerVia,
                Menu = menuResult.Menu,
                Options = options,
                Risk = risk,
                Spoken = spoken,
            };
        }
    }

    // ==================================================================== the receipt check

    /// <summary>
    /// Is this sentence actually in front of us? The reply (or the failure text) and the screen are the
    /// two places it may come from. Whitespace is tolerated - a model that collapses two spaces into one
    /// has still quoted the sentence - and box-drawing characters are stripped from the EDGES of screen
    /// rows, because a sentence drawn inside a terminal box is the same sentence as the one outside it.
    /// Nothing else is tolerated: one word different is a different sentence.
    /// </summary>
    public static bool EvidenceIsVerbatim(TurnVerdictPackage package, string evidence)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(evidence)) return false;

        var source = package.SourceText;
        if (!string.IsNullOrWhiteSpace(source)
            && BriefBuilder.FindVerbatim(source, evidence) is not null)
            return true;

        var screen = NormalizeScreen(package.ScreenRows);
        return screen.Length > 0 && BriefBuilder.FindVerbatim(screen, evidence) is not null;
    }

    /// <summary>
    /// The screen rows as one block of text with the box drawing taken off each row's edges. The
    /// character ranges are written as code points rather than as the characters themselves so this
    /// file stays plain keyboard text: U+2500 to U+257F is the box-drawing block (the corners, lines and
    /// junctions an agent draws its panels with) and U+2580 to U+259F is the block-element block (the
    /// bars a progress or selection marker is drawn with).
    /// </summary>
    public static string NormalizeScreen(IReadOnlyList<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var text = row ?? "";
            var start = 0;
            var end = text.Length;
            while (start < end && IsEdgeCharacter(text[start])) start++;
            while (end > start && IsEdgeCharacter(text[end - 1])) end--;
            sb.Append(text, start, end - start).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The first character of the box-drawing block: the corners, lines and junctions an agent
    /// draws its panels with. Written as a code point rather than as the character, so this file stays
    /// plain keyboard text.</summary>
    private const char BoxDrawingFirst = (char)0x2500;

    /// <summary>The last character of the block-elements block, which follows box drawing immediately:
    /// the bars a progress or selection marker is drawn with. The two blocks are contiguous, so one
    /// range covers both.</summary>
    private const char BlockElementsLast = (char)0x259F;

    private static bool IsEdgeCharacter(char c)
        => char.IsWhiteSpace(c) || (c >= BoxDrawingFirst && c <= BlockElementsLast);

    // ==================================================================== pieces

    private readonly record struct OptionsResult(List<TurnVerdictOptionDto> Options, string? Reason);

    private static OptionsResult ReadOptions(JsonElement root)
    {
        var options = new List<TurnVerdictOptionDto>();
        if (!root.TryGetProperty("options", out var array) || array.ValueKind != JsonValueKind.Array)
            return new OptionsResult(options, null);

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                return new OptionsResult(options, "an option is not an object");

            var key = Str(element, "key");
            var send = ReadSend(element);
            if (send.Length == 0)
                return new OptionsResult(options,
                    $"the option '{key}' has nothing to send; an option that cannot be sent is not an "
                    + "option, and dropping it would silently turn a choice into a single button");

            options.Add(new TurnVerdictOptionDto
            {
                Key = key.Length > MaxOptionKeyChars ? key[..MaxOptionKeyChars] : key,
                Send = send,
                Recommended = element.TryGetProperty("recommended", out var recommended)
                    && recommended.ValueKind == JsonValueKind.True,
                Note = Cap(Str(element, "note"), MaxOptionNoteChars),
            });
        }

        if (options.Count == 1)
            return new OptionsResult(options,
                "exactly one option was offered; one option is not a choice, and a button that is the only "
                + "thing on offer hides whatever else the person could have done");

        if (options.Count > MaxOptions)
            return new OptionsResult(options,
                $"{options.Count} options were offered, over the bound of {MaxOptions}; they are not trimmed, "
                + "because dropping one changes what the reader can choose");

        // At most one recommended. An extra flag is a presentation defect, not a reason to throw away an
        // otherwise sound answer: the extras are dropped so the stored record satisfies the invariant,
        // and the drop is logged so it is visible rather than silent.
        var seen = false;
        foreach (var option in options)
        {
            if (!option.Recommended) continue;
            if (seen)
            {
                FileLog.Write("[TurnVerdictContract] more than one recommended option; dropping the extra flag");
                option.Recommended = false;
            }
            seen = true;
        }

        return new OptionsResult(options, null);
    }

    /// <summary>
    /// An option's bytes, taken EXACTLY as written and never trimmed.
    ///
    /// Every other string field here is trimmed, and this one must not be. The send is what gets typed
    /// into a live session: a picker is confirmed by a carriage return carried inside the send, and
    /// trimming turns "1\r" into "1", which selects the option and never confirms it - the person taps
    /// the button, the picker sits there, and nothing says why. That is not a hypothetical; the first
    /// run of this contract's own tests caught it, because the trim was inherited from a contract whose
    /// fields are all prose.
    ///
    /// Nothing to send means an empty string, or one made only of ordinary whitespace - spaces type
    /// spaces, which answers nothing. A send that is only carriage returns or line feeds is a real
    /// send: it is how a picker's highlighted default is accepted.
    /// </summary>
    private static string ReadSend(JsonElement option)
    {
        if (!option.TryGetProperty("send", out var element) || element.ValueKind != JsonValueKind.String)
            return "";
        var raw = element.GetString() ?? "";
        if (raw.Length == 0) return "";
        if (raw.Trim().Length == 0 && !raw.Any(c => c is '\r' or '\n')) return "";
        return raw;
    }

    private readonly record struct MenuResult(TurnVerdictMenuDto? Menu, string? Reason);

    private static MenuResult ReadMenu(JsonElement root)
    {
        if (!root.TryGetProperty("menu", out var element) || element.ValueKind != JsonValueKind.Object)
            return new MenuResult(null, null);

        var selectionMode = Str(element, "selectionMode");
        if (selectionMode.Length == 0) selectionMode = "single";
        if (!TurnVerdictVocabulary.SelectionModes.Contains(selectionMode, StringComparer.Ordinal))
            return new MenuResult(null,
                $"unknown selectionMode '{selectionMode}'; it decides whether one key answers the picker or "
                + "several do, and a wrong guess types the wrong thing into a live session");

        var submit = element.TryGetProperty("submit", out var submitElement)
            && submitElement.ValueKind == JsonValueKind.String
                ? submitElement.GetString() ?? ""
                : "";
        if (submit.Length > 0 && submit != "\r")
            return new MenuResult(null,
                "the menu's submit is neither empty nor a carriage return; nothing else completes a picker");

        if (selectionMode == "multiple" && submit.Length == 0)
            return new MenuResult(null,
                "a pick-any-that-apply menu with no way to submit it; the person could toggle the boxes "
                + "for ever and never answer");

        return new MenuResult(new TurnVerdictMenuDto
        {
            Question = Cap(Str(element, "question"), MaxMenuQuestionChars),
            SelectionMode = selectionMode,
            Submit = submit,
        }, null);
    }

    /// <summary>Models wrap JSON in fences and narrate a sentence in front of it despite being told not
    /// to. Both are absorbed mechanically, exactly as the brief contract has absorbed them for a year -
    /// it is a quirk of how models answer, not a fact about the stop. What remains still has to be valid
    /// JSON or the answer is refused.</summary>
    private static string Unwrap(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineBreak >= 0 && lastFence > firstLineBreak)
                json = json[(firstLineBreak + 1)..lastFence].Trim();
        }
        if (!json.StartsWith('{'))
        {
            var open = json.IndexOf('{');
            var close = json.LastIndexOf('}');
            if (open >= 0 && close > open) json = json[open..(close + 1)];
        }
        return json;
    }

    private static TurnVerdictDto Refuse(
        TurnVerdictPackage package,
        string model,
        DateTime turnEndObservedAtUtc,
        string reason)
    {
        FileLog.Write($"[TurnVerdictContract] refused: {reason}");
        return new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = DateTime.UtcNow,
            TurnEndObservedAtUtc = turnEndObservedAtUtc,
            ScreenHash = package.ScreenHash,
            Model = model,
            ContractVersion = Version,
            PackageKind = TurnVerdictPackage.WireName(package.Kind),
            Failed = true,
            FailureReason = reason,
        };
    }

    private static string Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    private static string Cap(string value, int max)
        => value.Length <= max ? value : value[..max];
}
