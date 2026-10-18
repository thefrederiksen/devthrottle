using System.Diagnostics;
using System.Text.Json;

namespace CcDirector.RuleHook;

/// <summary>
/// One evaluation of one Claude Code PreToolUse event for the shell tool.
///
/// Reads the event from standard input and the session's rules file (W2) named by
/// <see cref="RulesFileEnvVar"/>, decides with <see cref="RuleResolver"/>, drops the decision for the
/// Director (W3) when a rule matched, and writes one evaluation-log line (W4) whatever happened.
///
/// IT NEVER BLOCKS, BY CONSTRUCTION (ruling P10). <see cref="Run"/> returns 0 on every path and nothing
/// here writes to standard output - for a PreToolUse hook, exit 0 with nothing printed is exactly "leave
/// the command alone", which was checked against the installed Claude Code before this was written. The
/// only catch is at this entry point, and what it catches goes into the evaluation log.
///
/// A process whose environment has no <see cref="RulesFileEnvVar"/> is not a Director session (a user's
/// own Claude Code, which shares nothing with this hook but the settings it never loads) or a Director
/// that predates rules: it has nowhere to log, so it does nothing.
/// </summary>
internal sealed class HookRunner
{
    /// <summary>Names the session's rules file (W2). Stamped by the Director.</summary>
    public const string RulesFileEnvVar = "CC_SESSION_RULES_FILE";

    /// <summary>Names the session's decision drop directory (W3). Stamped and created by the Director.</summary>
    public const string DecisionDirEnvVar = "CC_SESSION_DECISION_DIR";

    /// <summary>The longest command text a decision records (W3).</summary>
    public const int SubjectLimit = 4000;

    /// <summary>The evaluation log is cut back to its newest half when it passes this (W4).</summary>
    public const long DefaultLogLimitBytes = 1024 * 1024;

    private readonly Func<string, string?> _environment;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string> _randomHex;
    private readonly long _logLimitBytes;

    /// <param name="environment">Reads one environment variable.</param>
    /// <param name="utcNow">The clock; a test pins it.</param>
    /// <param name="randomHex">Eight lowercase hex characters, unique per decision.</param>
    /// <param name="logLimitBytes">The evaluation-log size that triggers the cut.</param>
    public HookRunner(Func<string, string?> environment, Func<DateTime> utcNow, Func<string> randomHex, long logLimitBytes = DefaultLogLimitBytes)
    {
        _environment = environment;
        _utcNow = utcNow;
        _randomHex = randomHex;
        _logLimitBytes = logLimitBytes;
    }

    /// <summary>Evaluate the event on <paramref name="standardInput"/>. Always returns 0.</summary>
    public int Run(TextReader standardInput)
    {
        var rulesFile = _environment(RulesFileEnvVar);
        if (string.IsNullOrEmpty(rulesFile))
            return 0;

        var started = Stopwatch.GetTimestamp();
        var occurred = _utcNow();
        var tool = "-";
        var rulesRead = 0;
        var matched = 0;
        string outcome;
        try
        {
            outcome = Evaluate(standardInput, rulesFile, started, occurred, ref tool, ref rulesRead, ref matched);
        }
        catch (Exception ex)
        {
            outcome = "error: " + OneLine(ex.Message);
        }

        try
        {
            EvaluationLog.Append(rulesFile, occurred, tool, rulesRead, matched, outcome,
                (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds, _logLimitBytes);
        }
        catch (Exception)
        {
            // The log is the hook's only voice, and it could not be written (a full disk, a removed
            // directory). There is nowhere left to say so that Claude Code would not treat as hook output,
            // and P10 forbids failing the command over it. Exit 0 regardless.
        }
        return 0;
    }

    private string Evaluate(TextReader standardInput, string rulesFile, long started, DateTime occurred,
        ref string tool, ref int rulesRead, ref int matched)
    {
        var raw = standardInput.ReadToEnd();
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("no event on standard input");
        var hookEvent = JsonSerializer.Deserialize(raw, RuleHookJson.Default.ClaudeCodeToolEvent)
            ?? throw new InvalidOperationException("the event on standard input is null");

        var agentTool = hookEvent.ToolName ?? "";
        // Claude Code 2.1.302 lists its shell tools as Bash and PowerShell (the Windows one). The rule
        // language names the agent-neutral "shell"; the agent's own name is kept as agentTool.
        if (agentTool is not ("Bash" or "PowerShell"))
            throw new InvalidOperationException($"tool '{agentTool}' is not a shell tool");
        tool = RuleResolver.ShellTool;
        var command = hookEvent.ToolInput?.Command
            ?? throw new InvalidOperationException($"the {agentTool} event has no command");

        if (!File.Exists(rulesFile))
            return "no_rules_file";
        var file = JsonSerializer.Deserialize(File.ReadAllText(rulesFile), RuleHookJson.Default.RulesFile)
            ?? throw new RulesFileException("the rules file is null");
        var rules = RuleResolver.Validate(file);
        rulesRead = rules.Count;

        var resolution = RuleResolver.Decide(rules, command);
        matched = resolution.MatchedRuleIds.Count;
        if (resolution.Decision is null)
            return "no_match";

        try
        {
            DropDecision(file, hookEvent, agentTool, command, resolution, occurred, started);
        }
        catch (Exception ex)
        {
            return $"error: {resolution.Decision} not recorded: {OneLine(ex.Message)}";
        }
        return resolution.Decision;
    }

    private void DropDecision(RulesFile file, ClaudeCodeToolEvent hookEvent, string agentTool, string command,
        Resolution resolution, DateTime occurred, long started)
    {
        var directory = _environment(DecisionDirEnvVar);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException($"{DecisionDirEnvVar} is not set");
        // The Director creates the drop directory at launch, and its name is the session's capability.
        // A missing one is the Director's defect to show, not the hook's to paper over by inventing it.
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"decision directory {directory} does not exist");

        var decisionId = occurred.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + _randomHex();
        var record = new DecisionRecord
        {
            DecisionId = decisionId,
            SessionId = file.SessionId ?? "",
            OccurredUtc = occurred.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            Agent = "claude_code",
            Event = RuleResolver.BeforeCommandEvent,
            AgentEvent = hookEvent.HookEventName ?? "",
            Tool = RuleResolver.ShellTool,
            AgentTool = agentTool,
            Subject = CutSubject(command),
            Machine = file.Machine ?? "",
            Repo = file.Repo ?? "",
            Decision = resolution.Decision!,
            DecidingRuleId = resolution.DecidingRule!.Id!,
            OverriddenRuleIds = [.. resolution.OverriddenRuleIds],
            MatchedRuleIds = [.. resolution.MatchedRuleIds],
            EvaluationMicroseconds = (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds,
        };

        // One file per decision, so back-to-back commands never overwrite each other; written to a .tmp
        // sibling and renamed so the Director's pickup never reads half a decision.
        var path = Path.Combine(directory, decisionId + ".json");
        var tmp = Path.Combine(directory, decisionId + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, RuleHookJson.Default.DecisionRecord));
        File.Move(tmp, path);
    }

    /// <summary>The command cut to <see cref="SubjectLimit"/> characters, never through the middle of a
    /// character that takes two (an emoji, for one), which would leave invalid text in the record.</summary>
    internal static string CutSubject(string command)
    {
        if (command.Length <= SubjectLimit)
            return command;
        var length = char.IsHighSurrogate(command[SubjectLimit - 1]) ? SubjectLimit - 1 : SubjectLimit;
        return command[..length];
    }

    private static string OneLine(string message) => message.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
