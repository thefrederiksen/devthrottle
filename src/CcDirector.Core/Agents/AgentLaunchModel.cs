using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>What composing a model into a launch line came to: the line, or the reason it cannot be done.</summary>
public sealed record AgentLaunchModelResult(string? Arguments, string? Refusal, bool ArgsNamedAModel)
{
    /// <summary>True when the model could be composed (or the person's own arguments already name one).</summary>
    public bool Ok => Refusal is null;
}

/// <summary>
/// THE MODEL OF A SEAT, ON ITS AGENT'S OWN LAUNCH LINE (Factory Definition mission, phase 5, design section 2). A
/// seat's model reaches the agent the way that agent takes one - Claude Code <c>--model</c>, Codex <c>-m</c>, and so
/// on - replacing any model the agent's DEFAULT arguments carry. A person's explicit arguments are never touched: when
/// they already name a model, they win, and the definition's model is not applied (Tech Lead ruling, 15 October 2026).
///
/// Every flag here was read from the installed tool's own <c>--help</c> on 15 October 2026 (codex, gemini, pi,
/// opencode, grok, copilot), as <see cref="AgentToolCatalog"/>'s flags were. Cursor's agent was not installed to read,
/// so it is treated as taking no model until its help is read; a raw command line takes none.
/// </summary>
public static class AgentLaunchModel
{
    /// <summary>The flag each agent takes a model on, or null when it takes none that has been verified.</summary>
    public static string? FlagFor(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "--model",
        AgentKind.Codex => "-m",
        AgentKind.Gemini => "--model",
        AgentKind.Pi => "--model",
        AgentKind.OpenCode => "--model",
        AgentKind.Grok => "--model",
        AgentKind.Copilot => "--model",
        _ => null,
    };

    /// <summary>
    /// Every spelling of the model flag an agent accepts, so a model already on a default line is found and replaced
    /// whichever way it was written. Codex, Gemini, OpenCode and Grok take both <c>-m</c> and <c>--model</c>.
    /// </summary>
    private static IReadOnlyList<string> SpellingsFor(AgentKind kind) => kind switch
    {
        AgentKind.Codex or AgentKind.Gemini or AgentKind.OpenCode or AgentKind.Grok => new[] { "-m", "--model" },
        AgentKind.ClaudeCode or AgentKind.Pi or AgentKind.Copilot => new[] { "--model" },
        _ => Array.Empty<string>(),
    };

    /// <summary>The refusal for an agent that takes no model, naming the model the seat asked for.</summary>
    public static string NoModelFlag(AgentKind kind, string model) =>
        $"The agent {kind} takes no model on its command line, so the model '{model}' cannot be applied and no session " +
        "was started. Remove the model from the seat, or give the seat an agent that takes one.";

    /// <summary>
    /// Compose <paramref name="model"/> into a launch line. <paramref name="arguments"/> is the line as it stands;
    /// <paramref name="argumentsAreExplicit"/> says whether the person gave it (their <c>Args</c>) or it is the agent's
    /// configured default. A default line has any model it carries replaced; an explicit line that names a model is
    /// returned untouched; an explicit line that names none gets the model added.
    /// </summary>
    public static AgentLaunchModelResult Compose(AgentKind kind, string? arguments, bool argumentsAreExplicit, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var trimmedModel = model.Trim();
        var line = (arguments ?? "").Trim();
        if (FlagFor(kind) is not { } flag)
        {
            FileLog.Write($"[AgentLaunchModel] Compose REFUSED: agent={kind} takes no model, model={trimmedModel}");
            return new AgentLaunchModelResult(null, NoModelFlag(kind, trimmedModel), false);
        }
        if (trimmedModel.Any(c => char.IsWhiteSpace(c) || c is '"' or '\''))
            return new AgentLaunchModelResult(null,
                $"The model '{trimmedModel}' has a space or a quote in it, so it cannot be put on the {kind} command line and no session was started.", false);

        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var spellings = SpellingsFor(kind);
        var named = NamesAModel(tokens, spellings);
        if (argumentsAreExplicit && named)
        {
            FileLog.Write($"[AgentLaunchModel] Compose: agent={kind}, the person's own arguments name a model; '{trimmedModel}' from the definition is not applied");
            return new AgentLaunchModelResult(line, null, ArgsNamedAModel: true);
        }

        var kept = new List<string>(tokens.Count + 2);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (spellings.Any(s => token.Equals(s, StringComparison.Ordinal)))
            {
                i++; // the value that followed it
                continue;
            }
            if (spellings.Any(s => token.StartsWith(s + "=", StringComparison.Ordinal)))
                continue;
            kept.Add(token);
        }
        kept.Add(flag);
        kept.Add(trimmedModel);
        var composed = string.Join(' ', kept);
        FileLog.Write($"[AgentLaunchModel] Compose: agent={kind}, model={trimmedModel}, replaced={named}, line=\"{composed}\"");
        return new AgentLaunchModelResult(composed, null, false);
    }

    private static bool NamesAModel(List<string> tokens, IReadOnlyList<string> spellings)
        => tokens.Any(t => spellings.Any(s => t.Equals(s, StringComparison.Ordinal) || t.StartsWith(s + "=", StringComparison.Ordinal)));
}
