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
/// The prompt lives in Prompts/turn-verdict-v3.txt as an embedded resource rather than in this file,
/// because the grading tool in the internal repository renders the SAME prompt from the SAME bytes. A
/// prompt that existed twice would be graded in one version and shipped in another.
/// </summary>
public static class TurnVerdictContract
{
    /// <summary>Stamped on every verdict record so a stored answer can say which contract produced it.
    /// Bump on every change to the prompt or to validation, and rename the prompt file with it.
    ///
    /// v1: the judge contract as slice C shipped it. v2 (slice D): the prompt gained finishedKind and the
    /// owned-sessions facts, and validation requires finishedKind on a finished verdict and refuses it on
    /// every other. Records stored under v1 keep their v1 stamp and stay readable; nothing reads the
    /// version to decide whether a record may be shown.
    ///
    /// v2.1 (2026-09-16): the SPOKEN section only. The judge no longer writes the session title - it is
    /// prepended from the record after the answer - and it is told plainly that no downstream step LOOKS
    /// FOR an identifier it leaves in. (A general Markdown pass does run before synthesis and drops fenced
    /// blocks whole; it is not identifier-specific and is no safety net, which is what the judge is told.) An earlier draft of this note said the opposite, promising a scrub that
    /// was written, reviewed three times and then deleted because no pattern separates an identifier from
    /// a number in prose without deleting real answers. Nothing about the JSON shape or validation changed, which is why the
    /// resource file keeps its v2 name and the grading tool keeps its path: renaming it would move a file
    /// the grader reads, for a revision that cannot change how any stored record is read. The stamp still
    /// moves, so a record can say which wording produced it.
    ///
    /// v2.2 (2026-09-16, slice I): VALIDATION only - the prompt is byte for byte the v2.1 prompt. A REFUSED
    /// answer that was readable JSON keeps its spoken text on the failed record, so a field check no longer
    /// silences voice; the refusal still stands for the row. (A rewrite of the spoken section was graded in the
    /// same slice and withdrawn on the Architect's ruling: on the fast judge it did not reach the old
    /// translator's fidelity and raised refusals.) The JSON shape is unchanged, so the file keeps its v2 name.
    ///
    /// v3 (2026-09-18, the owner's ruling on the Wingman redesign report): TWELVE FIELDS BECOME FIVE, and the prompt
    /// is a new file. The judge is asked for "state", "label", "agentRecommends", "menu" and "options" and for
    /// nothing else. Gone: "spoken", "summary", "evidence", "risk", "confidence", "answerVia" and "finishedKind" -
    /// the last folded into "state", and "answerVia" derived from whether a menu was drawn. Measured on the live
    /// fleet that morning, 40 per cent of readings failed: 48 of 198 timed out at sixty seconds and 30 were refused
    /// by a content rule, most of them the verbatim receipt. Every one of those seven fields was a further way for a
    /// fast model to fail a shape check on a call every session pays for at every stop, and four of them never
    /// reached a screen at all.
    ///
    /// THE RECEIPT IS GONE, AND THAT IS A COST TAKEN KNOWINGLY. It was a machine-checkable anchor against invention,
    /// and nothing replaces it. It could not be satisfied: the agent's reply carries Markdown, so a judge quoting the
    /// sentence the way a person reads it wrote "Three new contacts" where the reply held it in asterisks, and the
    /// comparison is word for word. A check nothing can pass is not a check, it is an outage with a reason attached -
    /// and it threw away the whole answer, label and words and all, so the owner read "The Wingman could not explain
    /// this stop" above a complete and correct briefing.
    ///
    /// WHAT DID NOT CHANGE: every rule that decides what BYTES reach a live session. The options' shape, the menu's
    /// shape, the one-option refusal, the at-most-one-recommended refusal and <see cref="ValidateExecutable"/> are
    /// exactly as they were. Those guard a button that ACTS; the seven removed fields guarded prose.</summary>
    public const string Version = "v3";

    /// <summary>The embedded name of the prompt template. The grading tool reads the same file off
    /// disk at src/CcDirector.Core/Wingman/Prompts/turn-verdict-v3.txt; a test pins the two to be
    /// byte for byte the same.</summary>
    public const string PromptResourceName = "CcDirector.Core.Wingman.Prompts.turn-verdict-v3.txt";

    /// <summary>The repository-relative path of the same file, for the tool that reads it off disk and
    /// for the test that pins the embedded copy to it.</summary>
    public const string PromptResourcePath = "src/CcDirector.Core/Wingman/Prompts/turn-verdict-v3.txt";

    // ==================================================================== caps
    //
    // BOUNDS THIS CONTRACT ADDS, AND WHY. The frozen contract names caps on label, summary and spoken.
    // The rest below are this file's own, and each exists because an unbounded field reaches a screen
    // or a database: they are stated here in one block rather than scattered, so a reader can see
    // exactly what was added beyond the specification and argue with it in one place.

    /// <summary>The one line a row shows. From the frozen contract.</summary>
    public const int MaxLabelChars = 80;

    /// <summary>The agent's own recommendation, one of the three fields that make the screen. This
    /// contract's own cap: it is a sentence in a panel, not a paragraph, and it is CUT at a word rather
    /// than refused, because a recommendation the reader can see most of is worth more than none.</summary>
    public const int MaxAgentRecommendsChars = 400;

    /// <summary>A menu question. This contract's own cap.</summary>
    public const int MaxMenuQuestionChars = 200;

    /// <summary>An option's label - the words on the button. This contract's own bound, matching the
    /// existing brief contract. An option over it is REFUSED rather than cut: see the note below, which
    /// applies to both halves of an option.</summary>
    public const int MaxOptionKeyChars = 60;

    /// <summary>An option's note: at most eighteen words in the frozen contract, which this holds as a
    /// character bound because words are not mechanically countable without inventing a word rule.
    ///
    /// AN OPTION IS NEVER CUT. The key is the action and the note is its consequence, and a shortened
    /// consequence is a different promise: "deletes the rows older than seven days, and the backup"
    /// becomes "deletes the rows older than seven days", which is a button the owner presses believing
    /// something the judge did not say. The prose fields are cut because a reader who wants more can
    /// open the session; an option is pressed, once, and cannot be asked what the rest of it said. Over
    /// the bound the whole answer is refused, exactly as an over-long receipt is.</summary>
    public const int MaxOptionNoteChars = 200;

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
        "OWNED_SESSIONS",
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
            // Owning nothing is a known fact rather than an absent one, so it is said in words.
            ["OWNED_SESSIONS"] = package.OwnedSessions is { } owned
                ? $"{owned.Working} working, {owned.Stopped} stopped, {owned.NeedYou} need a person"
                : "none - this session owns no other session",
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

            // ---- A REFUSAL CARRIES NO WORDS, BECAUSE THERE ARE NO WORDS HERE TO CARRY (contract v3) ------
            // Until v3 a refused answer kept the judge's "spoken" field, so a field check no longer silenced voice.
            // v3 deleted that field: this call produces no prose the owner reads or hears. The words come from the
            // narration call, and a reading is not published until BOTH calls are done - so a refused judgement is a
            // reading that could not be read, and it says so, rather than half of one.
            TurnVerdictDto RefuseReadable(string reason)
                => Refuse(package, model, turnEndObservedAtUtc, reason);

            // ---- THE SHAPE, BEFORE ANY OF IT IS READ FOR MEANING -----------------------------
            // Every rule below this line reads a field already proved to exist and to be the type the
            // shape declares. Before this pass, a missing field and a wrongly typed one both arrived as
            // an empty string or a null, so a malformed answer became a well-formed one on the way in
            // and the record then said the judge had answered something it never wrote.
            var shapeFault = ValidateShape(root);
            if (shapeFault is not null)
                return RefuseReadable(shapeFault);

            // ---- state: one of the seven, and nothing else -----------------------------------
            var state = Str(root, "state");
            if (!TurnVerdictVocabulary.IsWingmanState(state))
                return RefuseReadable(
                    $"unknown state word {Quoted(state)}; the seven allowed words are "
                    + string.Join(", ", TurnVerdictVocabulary.WingmanStates));

            // The state word is the SURFACE; what is stored is the pair every record and the labelling corpus
            // already carry. TurnVerdictVocabulary.SplitState is the one place the two spellings meet.
            var (verdict, finishedKind) = TurnVerdictVocabulary.SplitState(state);

            // ---- a failure with no reply can never be calm -----------------------------------
            if (package.Kind == TurnVerdictPackageKind.TerminalFailure && TurnVerdictVocabulary.IsCalm(verdict))
                return RefuseReadable(
                    $"the state {Quoted(state)} is calm, and this stop has no reply at all - its turn ended on "
                    + "a failure shown on screen, which cannot mean the session finished or is carrying on");

            // ---- the label --------------------------------------------------------------------
            var label = Str(root, "label");
            if (label.Length == 0)
                return RefuseReadable(
                    "no label; the label is the one line every row shows, and a row that reports nothing "
                    + "reads as broken");
            label = CapAtWordBoundary(label, MaxLabelChars);

            // ---- the options and the menu -----------------------------------------------------
            var optionsResult = ReadOptions(root);
            if (optionsResult.Reason is not null)
                return RefuseReadable(optionsResult.Reason);
            var options = optionsResult.Options;

            var menuResult = ReadMenu(root);
            if (menuResult.Reason is not null)
                return RefuseReadable(menuResult.Reason);

            // HOW THE PERSON ANSWERS IS DERIVED, NOT ASKED (contract v3). It was a word of its own, and the judge
            // could contradict itself with it - "keys" with no menu, "reply" with one - and either way the answer
            // was refused. There was never a third possibility: a picker was drawn on the screen or it was not, and
            // the menu is what says so. So the route reads the menu, and one field cannot disagree with another.
            var answerVia = menuResult.Menu is null ? AnswerViaReply : AnswerViaKeys;

            // ---- CAN THE ROUTE ACTUALLY PERFORM THIS, EXACTLY ONCE? --------------------------
            var executableFault = ValidateExecutable(package, answerVia, menuResult.Menu, options);
            if (executableFault is not null)
                return RefuseReadable(executableFault);

            // Null or absent means the agent recommended nothing. The shape pass has already refused
            // every other kind of value, so an object here can no longer become a silence.
            var agentRecommends = CapAtWordBoundary(Str(root, "agentRecommends"), MaxAgentRecommendsChars);

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
                FinishedKind = finishedKind,
                Label = label,
                AgentRecommends = agentRecommends.Length == 0 ? null : agentRecommends,
                AnswerVia = answerVia,
                Menu = menuResult.Menu,
                Options = options,
            };
        }
    }

    /// <summary>The person answers in typed or spoken words: no picker was drawn.</summary>
    public const string AnswerViaReply = "reply";

    /// <summary>The person answers by selecting in a picker that is drawn on the screen.</summary>
    public const string AnswerViaKeys = "keys";

    /// <summary>A word from the judge, in single quotes, for a refusal reason a person reads.</summary>
    private static string Quoted(string word) => SingleQuote + word + SingleQuote;

    private const char SingleQuote = (char)39;

    // ==================================================================== the screen, normalised

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

    // ==================================================================== the shape

    /// <summary>Members the shape declares as strings and always present. A missing one is not an empty
    /// one: "the judge did not answer this" and "the judge answered nothing here" are different facts,
    /// and only the second is the judge's.</summary>
    private static readonly string[] RequiredStringMembers =
    {
        "state", "label",
    };

    /// <summary>
    /// THE DECLARED SHAPE, PROVED BEFORE ANY FIELD IS READ FOR WHAT IT MEANS.
    ///
    /// The prompt asks for an object "in exactly this shape". This proves the answer IS that shape:
    /// every declared string is present and is a string, and each nullable member is null or its own
    /// type and nothing else.
    ///
    /// WHY IT IS A SEPARATE PASS. The readers below are convenient - they answer "" for a field that is
    /// missing, and "" again for a field that is a number, an array or an object. That convenience is
    /// how a malformed answer used to arrive looking well formed: an array where the menu belongs read
    /// as no menu, an object where the recommendation belongs as no recommendation, the string "true"
    /// as recommended-false. Each was a repair this contract made on the judge's behalf and then stored
    /// as though the judge had made it. Proving the shape first means every rule after this point is
    /// reading a value the judge actually wrote.
    /// </summary>
    private static string? ValidateShape(JsonElement root)
    {
        foreach (var name in RequiredStringMembers)
        {
            if (!root.TryGetProperty(name, out var value))
                return $"the answer has no '{name}'; the shape declares it, a missing field is not an "
                    + "empty one, and nothing here answers it on the judge's behalf";
            if (value.ValueKind != JsonValueKind.String)
                return $"'{name}' is {value.ValueKind} where the shape declares a string; a malformed "
                    + "answer is thrown away whole rather than read as though the field were absent";
        }

        // Nullable members: null is an answer, a wrong type is not. Absent reads as null, because the
        // shape's own "null or ..." says there is nothing to carry.
        if (root.TryGetProperty("agentRecommends", out var recommends)
            && recommends.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return $"'agentRecommends' is {recommends.ValueKind} where the shape declares a string or "
                + "null; it is not read as no recommendation, because that stores a silence the judge "
                + "did not answer with";

        // options is REQUIRED and is an array - empty when there are none, exactly as the prompt shows.
        // Round three made an explicit null refuse and left ABSENT meaning "there are none", which is a
        // second way to say one thing: the same answer could arrive with the member missing or with an
        // empty array, and the same list was synthesised from either. The shape declares the member, so
        // the member is there.
        if (!root.TryGetProperty("options", out var options))
            return "the answer has no 'options'; the shape declares it as a list, empty when there is "
                + "nothing to offer, and a missing member is not an empty list - one shape, not two";
        if (options.ValueKind != JsonValueKind.Array)
            return $"options is present but is not a list (it is {options.ValueKind}); the shape declares "
                + "an array and never a null, so a malformed answer is thrown away whole rather than read "
                + "as an answer that offered nothing";

        if (root.TryGetProperty("menu", out var menu)
            && menu.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            return $"'menu' is {menu.ValueKind} where the shape declares an object or null; it is not "
                + "read as no menu, because a picker silently becoming no picker is how a selection the "
                + "owner cannot make gets stored as one he can";

        return null;
    }

    // ==================================================================== can it be executed?

    /// <summary>
    /// ONE RULE, AND EVERY ACCEPTED RECORD OBEYS IT: the owner presses once, and the right bytes reach
    /// the session once.
    ///
    /// A reply option is sent and ONE carriage return is appended by the route, so the send carries none
    /// itself, and a reply is not a picker so it carries no menu. A keys option carries only the bytes
    /// that SELECT it; the selected options go in the order given and then the menu's submit, as one
    /// action under one screen lock, so the confirm lives in the submit and never inside a send. A
    /// pick-any-that-apply menu must submit with a carriage return, because a checklist with nothing to
    /// confirm cannot be finished.
    ///
    /// These are CROSS-FIELD rules: each field can be right on its own while the record as a whole
    /// promises something nothing can perform. A reply send ending in a carriage return gets a second
    /// appended and sends twice. A picker whose confirm sits inside the first option's send confirms
    /// before the person has finished choosing. Keys with a menu and no options is a question with no
    /// buttons - except in the one shape below, where there is nothing to choose and only something to
    /// confirm.
    /// </summary>
    private static string? ValidateExecutable(
        TurnVerdictPackage package,
        string answerVia,
        TurnVerdictMenuDto? menu,
        IReadOnlyList<TurnVerdictOptionDto> options)
    {
        foreach (var option in options)
        {
            if (option.Send.IndexOf(CarriageReturn) < 0 && option.Send.IndexOf(LineFeed) < 0) continue;
            return $"the option '{option.Key}' sends a carriage return or a line feed; an option carries "
                + "only the bytes that choose it - a reply has one Enter appended by the route, and a "
                + "picker is confirmed by the menu's submit - so a line ending inside a send is either "
                + "sent twice or confirms before the person has finished choosing";
        }

        if (answerVia != "keys")
        {
            return menu is null
                ? null
                : "the answer is typed or spoken words and yet carries a picker menu; a reply is not a "
                  + "selection, and a record claiming both cannot say which one the owner is doing";
        }

        if (menu is null)
            return "the answer is a selection in a picker but carries no menu; without it nothing knows "
                + "what question the keys answer, or what confirms it";

        if (menu.SelectionMode == "multiple" && menu.Submit != CarriageReturnText)
            return "a pick-any-that-apply menu whose submit is not a carriage return; the toggles choose "
                + "and only the submit finishes, so without one the person could toggle for ever and "
                + "never answer";

        if (options.Count > 0) return null;

        // THE ONE SHAPE IN WHICH A KEYS ANSWER MAY CARRY NO OPTIONS: the person has already typed their
        // reply into the composer and the only action left is to send it. There is nothing to toggle and
        // something to confirm, so the submit is the whole action and the route sends it alone.
        //
        // All three legs are checked, INCLUDING the question. This was written as a gap in round four -
        // "the package carries no mechanically extracted composer text, so there is nothing to compare
        // the question against" - and that was wrong. The parked text is ON THE SCREEN, which the package
        // does carry, so the question can be held to it exactly as a receipt is held to the reply. An
        // exception nobody can check is not an exception, it is a hole: without this leg a judge could
        // offer a one-tap Enter under any question at all, and the owner would confirm a send he cannot
        // see.
        if (menu.Submit != CarriageReturnText)
            return "the answer is a selection in a picker, offers nothing to select, and has nothing to "
                + "confirm either; a question with no buttons and no submit is one the owner cannot "
                + "answer at all";

        if (menu.SelectionMode != "single")
            return "a pick-any-that-apply menu with nothing to pick; the only answer that may carry no "
                + "options is the already-typed reply, and that is a single confirm";

        // The question must be ON THE SCREEN, word for word, after the same whitespace normalisation the
        // receipt check uses. The owner is about to send something with one tap; the only thing that can
        // tell him what, is the question, and the only thing that can prove the question is the parked
        // text is finding it where the parked text is.
        //
        // The question checked is the one that gets STORED, which is capped like every other prose field.
        // A question long enough to be cut will not be found and the answer is refused - toward red,
        // never toward quiet, which is the direction every doubt in this file falls.
        var screen = NormalizeScreen(package.ScreenRows);
        if (screen.Length == 0 || BriefBuilder.FindVerbatim(screen, menu.Question) is null)
            return "the answer offers a one-tap confirm but its question is not on the screen; the only "
                + "answer that may carry no options is the reply the person has already typed, and the "
                + "question has to quote it - otherwise the owner is asked to send something he cannot "
                + "read";

        return null;
    }

    /// <summary>The two characters an option's send may never contain, written as code points so this
    /// file stays plain keyboard text where it talks about them.</summary>
    private const char CarriageReturn = (char)13;

    private const char LineFeed = (char)10;

    /// <summary>The only non-empty submit a menu may carry.</summary>
    private static readonly string CarriageReturnText = CarriageReturn.ToString();

    // ==================================================================== pieces

    private readonly record struct OptionsResult(List<TurnVerdictOptionDto> Options, string? Reason);

    private static OptionsResult ReadOptions(JsonElement root)
    {
        var options = new List<TurnVerdictOptionDto>();
        // Present, and an array: the shape pass has already proved both, so an empty list here is the
        // judge saying there is nothing to offer - the ordinary shape of a report.
        var array = root.GetProperty("options");

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                return new OptionsResult(options, "an option is not an object");

            // The option's own shape, proved before any of it is read. An option is a button the owner
            // presses: a missing label, a missing consequence or a missing send are not empty strings to
            // be stored, they are an option that cannot be offered.
            var optionFault = ValidateOptionShape(element);
            if (optionFault is not null) return new OptionsResult(options, optionFault);

            var key = Str(element, "key");
            var send = element.GetProperty("send").GetString() ?? "";

            // Neither half of an option is ever cut. See MaxOptionNoteChars for why.
            if (key.Length > MaxOptionKeyChars)
                return new OptionsResult(options,
                    $"an option's key is {key.Length} characters, over the {MaxOptionKeyChars} character "
                    + "bound; it is the words on the button and it is not cut, because a shortened action "
                    + "is a different action");

            var note = Str(element, "note");
            if (note.Length > MaxOptionNoteChars)
                return new OptionsResult(options,
                    $"the option '{key}' has a note of {note.Length} characters, over the "
                    + $"{MaxOptionNoteChars} character bound; it is the consequence of pressing the button "
                    + "and it is not cut, because a shortened consequence is a different promise");

            options.Add(new TurnVerdictOptionDto
            {
                Key = key,
                Send = send,
                Recommended = element.GetProperty("recommended").ValueKind == JsonValueKind.True,
                Note = note,
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

        // At most one recommended, and more than one throws the whole answer away. Clearing the extra
        // flags and accepting was a silent repair of a malformed answer: the judge said two different
        // things were the one to do, and nothing here can know which it meant. Keeping the first is this
        // file guessing on the judge's behalf, and a guessed recommendation is a button the owner presses
        // believing a judge chose it.
        var recommendedCount = 0;
        foreach (var option in options)
            if (option.Recommended) recommendedCount++;
        if (recommendedCount > 1)
            return new OptionsResult(options,
                $"{recommendedCount} options are marked recommended; at most one may be, and the extra flags are "
                + "not cleared, because that would leave a recommendation this contract chose rather than "
                + "the judge");

        return new OptionsResult(options, null);
    }

    /// <summary>
    /// ONE OPTION'S OWN SHAPE. Every member is declared, so every member must be there and be what it
    /// is declared to be. None of them is synthesised: an option missing its label, its consequence or
    /// its bytes was stored with empty strings until round four, which is a button whose words, whose
    /// promise, or whose effect this contract wrote rather than the judge.
    ///
    /// The send is NOT trimmed, unlike every prose field here. It is typed into a live session verbatim,
    /// and what looks like tidying is a change to what gets typed. It may not be whitespace alone -
    /// spaces type spaces, which answers nothing - and the line-ending rule lives with the other
    /// cross-field rules in ValidateExecutable, because whether a carriage return is wrong depends on
    /// what the route will do with the record as a whole.
    /// </summary>
    private static string? ValidateOptionShape(JsonElement option)
    {
        foreach (var name in new[] { "key", "send", "note" })
        {
            if (!option.TryGetProperty(name, out var value))
                return $"an option has no '{name}'; every part of an option is declared, and a missing "
                    + "one is not an empty one - an option is a button, and this contract does not write "
                    + "its words, its consequence or its bytes on the judge's behalf";
            if (value.ValueKind != JsonValueKind.String)
                return $"an option's '{name}' is {value.ValueKind} where the shape declares a string";
            if ((value.GetString() ?? "").Trim().Length == 0)
                return $"an option's '{name}' is empty; an option that cannot be labelled, explained or "
                    + "sent is not an option, and dropping it would silently turn a choice into a "
                    + "single button";
        }

        if (!option.TryGetProperty("recommended", out var recommended))
            return "an option has no 'recommended'; the shape declares it on every option, and a missing "
                + "one is not a false one";
        if (recommended.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return $"an option's 'recommended' is {recommended.ValueKind} where the shape declares true "
                + "or false; the string \"true\" is not a boolean, and reading it as false would store "
                + "the opposite of what it says";

        return null;
    }

    private readonly record struct MenuResult(TurnVerdictMenuDto? Menu, string? Reason);

    private static MenuResult ReadMenu(JsonElement root)
    {
        // The shape pass has already refused anything that is neither an object nor null, so an absent
        // or null menu is the judge saying there is no picker.
        if (!root.TryGetProperty("menu", out var element) || element.ValueKind != JsonValueKind.Object)
            return new MenuResult(null, null);

        // The menu's own members are declared too, and none of them is written in. The question is what
        // the owner reads above the buttons; an empty one is a picker that asks nothing.
        if (!element.TryGetProperty("question", out var questionElement))
            return new MenuResult(null,
                "the menu has no question; it is the line the owner reads before choosing, and a picker "
                + "that asks nothing is one he answers blind");
        if (questionElement.ValueKind != JsonValueKind.String)
            return new MenuResult(null,
                $"the menu's question is {questionElement.ValueKind} where the shape declares a string");
        if ((questionElement.GetString() ?? "").Trim().Length == 0)
            return new MenuResult(null,
                "the menu's question is empty; a picker that asks nothing is one the owner answers blind");

        if (!element.TryGetProperty("submit", out var submitElement))
            return new MenuResult(null,
                "the menu has no submit; it says whether the picker acts on the key itself or needs a "
                + "confirm, and writing one in would be this contract deciding when a live session is "
                + "committed to");
        if (submitElement.ValueKind != JsonValueKind.String)
            return new MenuResult(null,
                $"the menu's submit is {submitElement.ValueKind} where the shape declares a string");

        var selectionMode = Str(element, "selectionMode");
        if (!TurnVerdictVocabulary.SelectionModes.Contains(selectionMode, StringComparer.Ordinal))
            return new MenuResult(null,
                selectionMode.Length == 0
                    ? "the menu carries no selectionMode; it decides whether one key answers the picker or "
                      + "several do, and writing one in would be this contract deciding how a live session "
                      + "gets typed into"
                    : $"unknown selectionMode '{selectionMode}'; it decides whether one key answers the "
                      + "picker or several do, and a wrong guess types the wrong thing into a live session");

        var submit = submitElement.GetString() ?? "";
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

    /// <summary>
    /// Did the judge answer with a JSON object at all - after the same fence and preamble absorption validation
    /// applies? The one mechanical line between an answer that carries words (every refusal of it still keeps its
    /// spoken text) and one that carries none: nothing, text that does not parse, or JSON that is not an object.
    /// The verdict service re-attempts only the second kind, and only for a stop somebody is listening to.
    /// </summary>
    public static bool IsReadableJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using var doc = JsonDocument.Parse(Unwrap(raw));
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// THE JUDGE'S DECISION OFF A REFUSED BUT READABLE ANSWER, FOR THE NARRATION CALL'S INPUT ONLY (the
    /// Wingman-on-every-turn mission, slice J, Architect ruling). A refused record carries no way to answer, no menu
    /// and no options, because those drive buttons that ACT on a live session, and a refusal must not offer any. But
    /// the narration only informs: on the corpus the shipped judge found the dangerous-delete permission prompt as a
    /// menu and was refused on its receipt, and the narration then retold a menu as prose with no press-a-button.
    ///
    /// So this reads, from the same answer, the state word, the menu question, the options' labels, notes and
    /// recommended flag, and what the agent recommends - as the judge wrote them, each read only when it is the
    /// declared type, and never validated as a whole. What it returns is never stored, never stamped on the row and
    /// never offered as a button. Null when the answer is not a JSON object.
    /// </summary>
    public static TurnVerdictDto? SalvageNarrationDecision(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(Unwrap(raw)); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var state = Str(root, "state");
            var decision = new TurnVerdictDto
            {
                Verdict = TurnVerdictVocabulary.IsWingmanState(state) ? TurnVerdictVocabulary.SplitState(state).Verdict : "",
                AgentRecommends = Cap(Str(root, "agentRecommends"), MaxAgentRecommendsChars),
            };
            if (root.TryGetProperty("menu", out var menu) && menu.ValueKind == JsonValueKind.Object
                && Str(menu, "question") is { Length: > 0 } question)
                decision.Menu = new TurnVerdictMenuDto { Question = Cap(question, MaxMenuQuestionChars) };
            // How the person answers is DERIVED here exactly as it is on the accepted path: a menu was drawn, or it
            // was not. A refused answer's own word for it is not read, because there no longer is one.
            decision.AnswerVia = decision.Menu is null ? AnswerViaReply : AnswerViaKeys;
            if (root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in options.EnumerateArray().Take(MaxOptions))
                {
                    if (option.ValueKind != JsonValueKind.Object || Str(option, "key") is not { Length: > 0 } key) continue;
                    decision.Options.Add(new TurnVerdictOptionDto
                    {
                        Key = Cap(key, MaxOptionKeyChars),
                        Note = Cap(Str(option, "note"), MaxOptionNoteChars),
                        Recommended = option.TryGetProperty("recommended", out var recommended) && recommended.ValueKind == JsonValueKind.True,
                    });
                }
            }
            return decision;
        }
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

    /// <summary>A refused judgement. It carries no prose at all: from contract v3 this call produces none, and
    /// the words the owner reads and hears come from the narration call, which is never made for a stop that has
    /// no accepted judgement to narrate.</summary>
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

    /// <summary>
    /// Cut a prose field to its bound at the last word boundary before it, so a cut field never ends
    /// mid-word.
    ///
    /// The label, the summary and the spoken section are READ - on a row, in a panel, out loud - and a
    /// hard cut at the character bound leaves "the deploy guard is bl", which reads as a broken product
    /// rather than as a long answer. The receipt is deliberately NOT cut this way, because it is not cut
    /// at all: a shortened quote is no longer what the agent said.
    ///
    /// A single unbroken run longer than the whole bound has no boundary to cut at. The hard bound then
    /// stands, because an empty field says less to the reader than a cut one.
    /// </summary>
    private static string CapAtWordBoundary(string value, int max)
    {
        if (value.Length <= max) return value;

        // The bound falls exactly between two words: what is kept is already whole.
        if (char.IsWhiteSpace(value[max])) return value[..max].TrimEnd();

        var head = value[..max];
        for (var i = head.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(head[i])) continue;
            var kept = head[..i].TrimEnd();
            if (kept.Length > 0) return kept;
            break;
        }

        return head;
    }
}
