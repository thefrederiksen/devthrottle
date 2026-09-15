using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
using CcDirector.Core.Skills;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The Claude Code driver: every claude.exe-specific keystroke and convention in one
/// place, extracted from what the Director and the HostedAgent already proved live:
///
///   - launch: "--dangerously-skip-permissions --session-id &lt;guid&gt;" (preassigned id
///     means the JSONL transcript path is known from birth; --resume for resumes)
///   - submit: backend.SendTextAsync - carries the TUI settle delay, explicit CR, and
///     the large-input @-temp-file trick (multi-line/huge prompts)
///   - cancel: a single Esc byte (0x1B) - identical to the Director's POST /escape
///   - clear: submit "/clear"; claude starts a NEW internal session id and transcript
///     file, which the host re-discovers via ListTranscripts
///   - transcripts: the ~/.claude/projects JSONL files, read with the same Core
///     parsers the Director uses
/// </summary>
public sealed class ClaudeDriver : IAgentDriver
{
    /// <summary>
    /// What Claude launches with when a caller supplies no arguments at all: the automatic
    /// permission mode, matching the catalog's default preset so every path agrees on what "the
    /// Claude default" means.
    /// </summary>
    public const string DefaultArgs = AgentToolCatalog.ClaudeAutomaticModeArg;

    /// <summary>
    /// What a HEADLESS Claude launches with - the full bypass, deliberately not the automatic mode
    /// above. A hosted agent runs with no human attached, so a permission prompt has nobody to
    /// answer it and the run would simply hang. The interactive default can afford to ask; this one
    /// cannot, so the difference is stated here rather than left to whichever constant was handy.
    /// </summary>
    public const string HeadlessDefaultArgs = AgentToolCatalog.ClaudeSkipPermissionsArg;

    private static readonly byte[] EscapeByte = [0x1B];
    private static readonly byte[] CtrlC = [0x03];

    private readonly ITranscriptReader _transcripts;
    public ClaudeDriver(
        ITranscriptReader? transcripts = null,
        TimeSpan? echoTimeout = null,
        TimeSpan? echoPollInterval = null)
    {
        _transcripts = transcripts ?? new ClaudeTranscriptReader();
    }

    public AgentKind Kind => AgentKind.ClaudeCode;

    /// <summary>
    /// See <see cref="IAgentDriver.SelfDescribingRowMarkers"/>. Taken on 15 September from the
    /// labelled corpus by counting which rows the content rule would WRONGLY have called new
    /// across 1,292 repaint-labelled episodes for this agent, most frequent first, plus the rows
    /// the scoring harness's own expression carried.
    ///
    /// Two choices worth knowing about:
    ///
    /// "esc to cancel" is a DIFFERENT row from "esc to interrupt" and both are here. The expression
    /// behind the published measurement carried only the second, which means the row rule's real
    /// score is very slightly BETTER than the 94.0 percent published rather than worse. That figure
    /// is not to be quietly adjusted for this - it has to be re-taken.
    ///
    /// The thinking line is NOT here and cannot be. Its shape is a glyph, a past-tense verb, "for"
    /// and a duration, and the verb rotates through a large vocabulary - so the only stable
    /// fragment is "ed for ", which also occurs in ordinary prose ("I refactored for clarity").
    /// Including it would suppress real replies, which is the one failure that costs the owner a
    /// turn, so it is left out and phantoms from that row remain. Expressing it would need a
    /// pattern, and this trait is deliberately strings.
    ///
    /// Two rows from the same evidence are deliberately absent because other conditions already
    /// remove them: a box rule drawn from line glyphs carries no letters or digits, so the
    /// substance floor drops it, and the workspace indicator carries a project name, which the
    /// exact-key and near-duplicate conditions drop because it is on both screens. A marker list
    /// that tried to cover those would be matching content.
    /// </summary>
    public IReadOnlyCollection<string> SelfDescribingRowMarkers { get; } =
    [
        // The most common by a wide margin, and it appears truncated at several widths - so both
        // halves of the row are listed rather than the whole of it.
        "new task?",
        "/clear to save",
        "ctrl+o to expand",
        "esc to interrupt",
        "esc to cancel",
        "bypass permissions",
        "auto-accept",
        "context left",
        // The update check every thirty minutes on an idle session: the row that started this.
        "Checking for updates",
        "Update installed",
        "IDE disconnected",
    ];

    public DriverCapabilities Capabilities =>
        DriverCapabilities.ClearContext
        | DriverCapabilities.Cancel
        | DriverCapabilities.Interrupt
        | DriverCapabilities.History
        | DriverCapabilities.TranscriptRead
        | DriverCapabilities.PreassignedSessionId
        | DriverCapabilities.ModelSelection
        | DriverCapabilities.ContextUsage
        | DriverCapabilities.ModelReport
        | DriverCapabilities.TokenUsage
        | DriverCapabilities.CompactContext
        | DriverCapabilities.CompactCompletionReport;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => BuiltInSlashCommands.All
        .Select(command => new AgentSlashCommand(
            command.Name,
            command.Description,
            command.Category,
            command.Source,
            AgentKind.ClaudeCode,
            Documentation: command.Documentation))
        .ToList();

    public string ModelFlag => "--model";

    /// <summary>
    /// The Claude Code models the picker offers. Hard-coded here (not read from the API - there is
    /// no CLI to list models) so the picker always shows sensible choices on a fresh machine. The
    /// "1M context" variants use the <c>[1m]</c> suffix, which is how Claude Code requests the
    /// 1-million-token window - there is no separate flag. "Use the tool's own default" is NOT in
    /// this list: it is the unset-model state, surfaced separately by the picker.
    /// </summary>
    public IReadOnlyList<AgentModelOption> KnownModels =>
    [
        new("opus[1m]", "Opus 4.8 (1M context)", "Most capable; 1-million-token window.", "1M context"),
        new("opus", "Opus 4.8", "Most capable; standard context window."),
        new("sonnet", "Sonnet 4.6", "Balanced speed and intelligence."),
        new("haiku", "Haiku 4.5", "Fastest and lowest cost."),
        new("fable", "Fable 5", "Anthropic's most capable model for the hardest work."),
    ];

    /// <summary>
    /// The configured Claude Code default model, read from <c>~/.claude/settings.json</c>'s
    /// <c>model</c> key. Null when the file/key is absent (the account-tier default applies) or
    /// unreadable. A display hint only - this is never written and never composes the launch line.
    /// </summary>
    public string? ReadConfiguredDefaultModel()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".claude", "settings.json");
            if (!File.Exists(path))
                return null;

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            if (node?["model"] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var model)
                && !string.IsNullOrWhiteSpace(model))
                return model;
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeDriver] ReadConfiguredDefaultModel: could not read settings.json: {ex.Message}");
            return null;
        }
    }

    public string ResolveExecutable(string? configuredPath)
    {
        // Resolve through the shared, platform-aware ExecutableResolver (the same helper Codex and
        // Copilot use): on macOS and Linux it matches the extensionless "claude"; on Windows it applies
        // PATHEXT to find claude.exe. This is what lets the warm brain spawn its agent off Windows - a
        // hand-rolled claude.exe-only search could not. An explicit configured path is resolved directly.
        var configured = string.IsNullOrWhiteSpace(configuredPath) ? "claude" : configuredPath.Trim();
        var resolved = ExecutableResolver.Resolve(configured);
        if (resolved is not null)
            return resolved;

        // Keep FileNotFoundException: HostedAgent.StartAsync catches exactly this type to report a
        // clean "agent not installed" startup error.
        throw new FileNotFoundException(
            $"[ClaudeDriver] Could not resolve the Claude Code CLI from '{configured}'. " +
            "Install Claude Code or configure an explicit path.");
    }

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId)
    {
        var args = string.IsNullOrWhiteSpace(baseArgs) ? DefaultArgs : baseArgs;
        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            FileLog.Write($"[ClaudeDriver] BuildLaunchSpec: resume={resumeSessionId}");
            return new AgentLaunchSpec($"{args} --resume {resumeSessionId}".Trim(), null);
        }

        var preassigned = Guid.NewGuid().ToString();
        FileLog.Write($"[ClaudeDriver] BuildLaunchSpec: preassigned={preassigned}");
        return new AgentLaunchSpec($"{args} --session-id {preassigned}".Trim(), preassigned);
    }

    /// <summary>
    /// ECHO-VERIFIED submit. claude's TUI input handling has a race right after a
    /// turn ends: text typed into a repainting composer can lose its Enter, or worse,
    /// pick up a stray leading "/" that turns the prompt into a bogus slash command
    /// (observed live during the hosted-agent QA: "Unknown command: /Write"). So this
    /// driver never trusts a blind write: type the text WITHOUT Enter, wait until the
    /// composer's echo in the terminal byte stream matches what was typed (and is NOT
    /// "/"-corrupted), and only then press Enter. One Esc-and-retype recovery attempt,
    /// loudly logged; a second mismatch throws.
    ///
    /// Multi-line / huge prompts delegate to the backend's @-temp-file mechanism
    /// unchanged (production-proven by the Director; its echo is the @-reference,
    /// which the backend owns).
    /// </summary>
    public Task SubmitAsync(ISessionBackend backend, string text) =>
        TerminalSubmit.SharedSubmitAsync(backend, text, "ClaudeDriver");

    /// <summary>Drop ANSI escape sequences from a terminal chunk (delegates to the shared helper).</summary>
    public static string StripAnsi(string raw) => TerminalSubmit.StripAnsi(raw);

    /// <summary>Letters, digits and '/' only - the echo comparison alphabet (shared helper).</summary>
    public static string NormalizeForEcho(string s) => TerminalSubmit.NormalizeForEcho(s);

    public Task CancelAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] CancelAsync: sending Esc");
        backend.Write(EscapeByte);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] InterruptAsync: sending Ctrl+C");
        backend.Write(CtrlC);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Claude's double-Esc: with an idle composer, two Esc presses open the Rewind
    /// picker ("Restore the code and/or conversation to the point before..."). The gap
    /// between the presses matters and was tuned LIVE: 120ms gets coalesced and no
    /// picker appears; 350ms reliably registers as the deliberate double-press
    /// (Director QA DQ-5, docs/features/director-drivers/QA_REPORT.html).
    /// </summary>
    public async Task ShowHistoryAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] ShowHistoryAsync: sending double-Esc (350ms gap)");
        backend.Write(EscapeByte);
        await Task.Delay(TimeSpan.FromMilliseconds(350));
        backend.Write(EscapeByte);
    }

    public Task ClearContextAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] ClearContextAsync: submitting /clear");
        return backend.SendTextAsync("/clear");
    }

    /// <summary>
    /// Claude's <c>/compact</c>: it summarizes the conversation so far and continues from the summary,
    /// in the SAME agent session. Submitted the same way a person would type it - through the shared
    /// composer submit, so a composer that is not accepting input fails loud rather than dropping the
    /// command into the void.
    /// </summary>
    public Task CompactContextAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] CompactContextAsync: submitting /compact");
        return backend.SendTextAsync("/compact");
    }

    /// <summary>
    /// Has claude finished a compaction since <paramref name="sinceUtc"/>? Read from its own transcript:
    /// a completed compaction appends an entry carrying <c>isCompactSummary</c>, stamped with the moment
    /// it landed. The mark must be NEWER than the moment we submitted the command, so an older compaction
    /// in the same conversation - and this session has usually compacted before - can never be mistaken
    /// for the one we just asked for.
    /// </summary>
    public bool HasCompactedSince(string agentSessionId, string workingDirectory, DateTime sinceUtc)
    {
        var last = _transcripts.LastCompactionUtc(agentSessionId, workingDirectory);
        return last is not null && last.Value >= sinceUtc;
    }

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory)
        => _transcripts.ReadWidgets(agentSessionId, workingDirectory);

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory)
        => _transcripts.ReadUsage(agentSessionId, workingDirectory);

    /// <summary>
    /// How full the Claude context window is right now (capability
    /// <see cref="DriverCapabilities.ContextUsage"/>). Reuses the existing transcript token walk for
    /// the used-token count, then sizes the window. The AUTHORITATIVE window signal is the launch
    /// model id parsed from <paramref name="launchArgs"/> (e.g. <c>--model opus[1m]</c>): Claude's
    /// transcript records the base model id WITHOUT the <c>[1m]</c> suffix, so a 1-million-token Opus
    /// session below 200k tokens would otherwise be sized against 200k and read far too high (issue
    /// #803). When the launch model is unknown, we fall back to the transcript model with upward
    /// self-correction. Null until the first usage-bearing assistant line exists (no turn yet); when
    /// neither model maps, the window and percent are null (the raw-number fallback).
    /// </summary>
    public ContextUsageDto? ReadContextUsage(string agentSessionId, string workingDirectory, string? launchArgs)
    {
        var usage = _transcripts.ReadUsage(agentSessionId, workingDirectory);
        if (usage is null || usage.AssistantMessageCount == 0)
            return null;

        // NO WINDOW IS REPORTED, BECAUSE CLAUDE HAS NOT TOLD US ONE (issue #1100).
        //
        // This used to map the model id to a window through a table of family names: an id containing
        // "opus" meant 200,000 unless it also contained "[1m]". On the model the whole fleet runs, both
        // inputs to that table fail. The launch arguments carry no model at all when it was chosen inside
        // Claude Code with /model, and the transcript records the BASE id - a 1-million session and a
        // 200k session are byte-identical there, because Claude Code logs "claude-opus-5" and discards
        // the alias. So the table saw "opus", returned 200,000, and the gauge showed 92% in red for a
        // session with roughly 800,000 tokens of headroom.
        //
        // Adding the new model to the table would have cleared that screenshot and fixed nothing. A table
        // of model facts is a stale measurement: correct the day it is written and rotting silently from
        // then on, with no error, no log line and no failing test - just a plausible wrong number on
        // screen. It was wrong the day Opus 5 shipped and it would be wrong again on the next model, the
        // next window size and the next alias.
        //
        // The rule now, for every driver: A DRIVER MAY ONLY REPORT A CONTEXT WINDOW ITS AGENT TOLD IT, AND
        // MAY NEVER DERIVE ONE. The derivation is deleted rather than left unused, because code that stays
        // gets called again. Claude Code does have a route to tell us - the status line receives
        // context_window.context_window_size for the live session - and building it is tracked separately.
        // Until then the honest answer is that we do not know, and the gauge shows the raw token count
        // with no percentage, no bar and no colour. That is a correct shipped state: a missing number is
        // an annoyance, a confident wrong one gets acted on.
        return new ContextUsageDto
        {
            UsedTokens = usage.ContextTokens,
            WindowTokens = null,
            PercentUsed = null,
            AsOfUtc = usage.LastMessageUtc,
            WindowSource = nameof(ContextWindowSource.Unknown),
        };
    }

    /// <summary>
    /// The model this Claude session is currently using (capability
    /// <see cref="DriverCapabilities.ModelReport"/>). The transcript is the truth: every assistant
    /// line records <c>message.model</c> and the LATEST line wins, so a mid-session /model switch is
    /// reflected. Before the first turn the transcript carries no model, so the <c>--model</c> value
    /// from the launch arguments is the answer (it is the tool's real instruction until the first
    /// turn proves the concrete id); null when neither exists (the tool's own default, id unknown
    /// until the first turn).
    /// </summary>
    public string? ReadCurrentModel(string agentSessionId, string workingDirectory, string? launchArgs)
    {
        var usage = _transcripts.ReadUsage(agentSessionId, workingDirectory);
        if (!string.IsNullOrWhiteSpace(usage?.ContextModel))
            return usage.ContextModel;
        return ExtractLaunchModelId(launchArgs);
    }

    /// <summary>Extracts the value passed after this driver's <see cref="ModelFlag"/> (<c>--model</c>)
    /// from a launch command line, e.g. <c>opus[1m]</c> from
    /// <c>--dangerously-skip-permissions --model opus[1m]</c>. Handles both the space form
    /// (<c>--model opus[1m]</c>) and the equals form (<c>--model=opus[1m]</c>). Returns null when no
    /// model flag is present (the session uses the provider default), driving the transcript
    /// fallback.</summary>
    private string? ExtractLaunchModelId(string? launchArgs)
    {
        if (string.IsNullOrWhiteSpace(launchArgs))
            return null;

        var tokens = launchArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Equals(ModelFlag, StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
                return tokens[i + 1];
            if (token.StartsWith(ModelFlag + "=", StringComparison.OrdinalIgnoreCase))
                return token[(ModelFlag.Length + 1)..];
        }

        return null;
    }

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory)
        => _transcripts.ListTranscripts(workingDirectory);
}
