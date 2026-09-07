namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The FOLDED "which model is this session running" verdict for one session - the finished strings every
/// surface renders VERBATIM (issue devthrottle_internal#1340).
///
/// The fact itself (<see cref="SessionDto.CurrentModel"/>) has been on the wire since issue #815 and was
/// shown to a human in exactly one place: the Cockpit's History page, for sessions that had already ended.
/// Putting it on the LIVE surfaces meant four of them - the desktop rail, the Fleet Map card, the Cockpit
/// roster, and the command line table - and the interesting part is not the model, it is the two ABSENCES,
/// which mean opposite things and must never read the same:
///
///   - the session CAN report its model and simply has not finished a turn yet (it is coming), versus
///   - the agent cannot report its model at all - Gemini records it nowhere readable, Cursor was never
///     live-verified - so it is NEVER coming.
///
/// Four surfaces each deciding that for themselves is four chances to render a hopeful "loading" for a
/// value that will never arrive, which is the same defect shape as the Voice screen's impossible
/// "Generate narration now" button (see <see cref="VoiceDisplay"/>): a client that has to guess renders
/// something PLAUSIBLE instead of something TRUE. So the rule is folded once, here, and the clients render
/// the strings.
///
/// This lives in the contracts assembly rather than in the Gateway because both callers need it and they
/// are not the same kind of caller: the Gateway stamps it during the /sessions aggregation for the browser
/// clients, and the desktop rail folds its OWN locally-mapped <see cref="SessionDto"/> through the same
/// function. That is not the desktop ruling for itself - the two inputs, <see cref="SessionDto.CurrentModel"/>
/// and <see cref="SessionDto.DriverCapabilities"/>, are facts the DIRECTOR produces and owns firsthand (the
/// records watcher writes one, the driver declares the other), so the local answer is the same answer, one
/// rule, and it still reads correctly with no Gateway attached. Compare the session COLOUR, which the
/// desktop must never fold locally because that fold reads inputs only the Gateway has.
/// </summary>
public sealed class ModelDisplay
{
    /// <summary>Machine-readable state key, for the client's style lookup only (never re-ruling):
    /// <c>reported</c> (the records name a model), <c>notRecordedYet</c> (this session can report one but
    /// has completed no turn), or <c>notReported</c> (this agent cannot report one at all).</summary>
    public string Kind { get; set; } = "";

    /// <summary>The badge text, rendered verbatim and never assembled on the client. The recorded id
    /// shortened for width when a model is known (e.g. <c>fable-5</c>), else the honest words for the
    /// absence (<c>no model yet</c> / <c>model not reported</c>).</summary>
    public string Text { get; set; } = "";

    /// <summary>The FULL recorded model id, exactly as the agent's own records spell it
    /// (e.g. <c>claude-fable-5</c>, <c>gpt-5.6-sol</c>), or null in both absent states. This is what the
    /// "By model" lane headers and the command line table print - only the badge is shortened.</summary>
    public string? ModelId { get; set; }

    /// <summary>The tooltip, rendered verbatim: the full id when one is known, else the sentence that says
    /// WHICH absence this is. Never null - a badge with no explanation is what sent a reader looking for a
    /// setting that does not exist.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>True in both absent states, so a client can style the badge as muted/outlined without
    /// branching on <see cref="Kind"/>. A convenience for STYLING, never a decision.</summary>
    public bool IsAbsent { get; set; }

    /// <summary>
    /// Anything in this object a reading build does not know a field for, kept verbatim.
    ///
    /// It is here because a workspace stores one of these, and the claim made when the bags went in was
    /// that EVERY extensible object in a stored workspace carried one. This was the one that did not -
    /// caught by a reviewer, and worth correcting rather than quietly closing, because the claim was
    /// wider than the code by exactly one type.
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }
}

/// <summary>
/// The ONE fold that turns a session's recorded model plus its driver capabilities into the
/// <see cref="ModelDisplay"/> every surface renders. Pure and static so it is unit-tested without a
/// Gateway, a Director, or an Avalonia app.
/// </summary>
public static class ModelDisplayFold
{
    /// <summary>The capability a driver declares when it can read the model out of the tool's own records
    /// (<c>IAgentDriver.ReadCurrentModel</c>). Its ABSENCE is the whole difference between "not yet" and
    /// "never" - the two states this fold exists to keep apart.</summary>
    public const string ModelReportCapability = "ModelReport";

    /// <summary>Badge text longer than this is truncated. The full id is always in the tooltip, so a
    /// truncation loses nothing a reader cannot get back by hovering; a badge wide enough for any id
    /// would squeeze the session name on every row that has a short one.</summary>
    private const int MaxBadgeLength = 22;

    /// <summary>Words for a session that can report its model and has not finished a turn yet.</summary>
    private const string NotRecordedYetText = "no model yet";

    /// <summary>Words for an agent that cannot report its model at all.</summary>
    private const string NotReportedText = "model not reported";

    /// <summary>Fold a session's DTO. Never returns null - every session has a model display, because
    /// "nothing here" is one of the three answers and a blank badge is not one of them.</summary>
    public static ModelDisplay For(SessionDto session)
        => For(session.CurrentModel, session.DriverCapabilities);

    /// <summary>The fold itself, over its two raw inputs, so a test states the case rather than building
    /// a whole session.</summary>
    public static ModelDisplay For(string? currentModel, IEnumerable<string>? driverCapabilities)
    {
        var model = (currentModel ?? "").Trim();
        if (model.Length > 0)
        {
            return new ModelDisplay
            {
                Kind = "reported",
                Text = ShortenForBadge(model),
                ModelId = model,
                Tooltip = model,
                IsAbsent = false,
            };
        }

        // The capability list is what tells the two absences apart, and it is already on the wire - no new
        // field was needed to carry the difference, only the discipline to READ it. A Director that
        // predates the driver layer reports no capabilities at all; that reads as "cannot report", which is
        // the truthful answer for it, since nothing on it will ever produce a model.
        var canReport = driverCapabilities is not null
            && driverCapabilities.Any(c => string.Equals(c, ModelReportCapability, StringComparison.OrdinalIgnoreCase));

        return canReport
            ? new ModelDisplay
            {
                Kind = "notRecordedYet",
                Text = NotRecordedYetText,
                ModelId = null,
                // States the FACT (nothing recorded) and the MECHANISM (records, at turn-end), and stops
                // there. It used to add "this session has not completed a turn", which was a cause nobody
                // observed: a null model also means a read that could not be taken - torn records, the
                // agent restarting, a session restored from a store the driver cannot read yet - and
                // SessionRecordsWatcher keeps the last known value rather than clearing on those, so the
                // null is exactly as consistent with a failed read as with a first turn. Naming the wrong
                // one reads as diagnosis and sends a reader looking in the wrong place.
                Tooltip = "No model recorded yet. It is read from the agent's own records at each "
                          + "turn-end, and nothing has been recorded for this session so far.",
                IsAbsent = true,
            }
            : new ModelDisplay
            {
                Kind = "notReported",
                Text = NotReportedText,
                ModelId = null,
                Tooltip = "This agent does not report the model it is running, so there is nothing to show. "
                          + "Nothing is loading and nothing is wrong with this session.",
                IsAbsent = true,
            };
    }

    /// <summary>
    /// The recorded id shortened to fit a badge that sits beside the agent's name.
    ///
    /// One rule and one exception: drop a leading <c>claude-</c>, because the badge it rides on already
    /// says "Claude Code" and a badge reading "Claude Code | claude-fable-5" spends half its width saying
    /// the word twice. Every other id is used exactly as the records spell it - <c>gpt-5.6-sol</c> stays
    /// <c>gpt-5.6-sol</c> - because a prettier short form is a name the records would not recognise, and
    /// this value's whole worth is that it is what the tool actually wrote down.
    /// </summary>
    public static string ShortenForBadge(string modelId)
    {
        var id = (modelId ?? "").Trim();
        const string claudePrefix = "claude-";
        if (id.StartsWith(claudePrefix, StringComparison.OrdinalIgnoreCase) && id.Length > claudePrefix.Length)
            id = id.Substring(claudePrefix.Length);

        return id.Length <= MaxBadgeLength ? id : id.Substring(0, MaxBadgeLength - 3) + "...";
    }
}
