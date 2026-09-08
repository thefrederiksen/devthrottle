using System.Globalization;
using System.Text.Json;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// THE ONE TABLE of the mentor's tools as the agent loop offers them: the eleven tools of the skill's table
/// (SKILL.md, "The run and the tools"), each a native function of the same name and the same parameter names the
/// skill's command line takes, with a JSON schema built from this table and a dispatcher onto the bound
/// <see cref="ToolSurface"/>. A unit test asserts every tool name in SKILL.md's table is offered as a function and
/// every function offered is in the table, so the skill the model reads and the functions it is handed can never
/// disagree.
///
/// The description of each function is the skill's own "Call it when" cell, so the model reads the same words in
/// the prompt and in the function list. The dispatcher hands the arguments to the surface as the surface's own
/// methods take them; a missing required argument is handed through as null so the SURFACE refuses it with the
/// reference's message and the refusal lands in the tool log like every other call. Only an argument of the wrong
/// JSON type is refused here, before any tool runs, because no tool exists to receive it.
/// </summary>
public static class MentorTools
{
    /// <summary>One offered function: its name, the skill's "Call it when" cell, its parameters and its dispatcher.</summary>
    public sealed record Tool(string Name, string Description, IReadOnlyList<Parameter> Parameters, Func<ToolSurface, Arguments, object?> Invoke);

    /// <summary>One parameter: its name, its JSON type ("string" or "integer"), whether it is required, and its description.</summary>
    public sealed record Parameter(string Name, string Type, bool Required, string Description);

    /// <summary>The parsed arguments of one function call, read by name and by type.</summary>
    public sealed class Arguments
    {
        private readonly Dictionary<string, JsonElement> _values;

        public Arguments(Dictionary<string, JsonElement> values) => _values = values;

        /// <summary>The argument as a string, or null when absent or JSON null (the surface refuses a null it needs).</summary>
        public string? String(string name)
        {
            if (!_values.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
            if (element.ValueKind == JsonValueKind.String) return element.GetString();
            // A number or a boolean where a string was asked for is handed through as its text: a model that writes
            // a minute as a bare token has still named it, and the surface's own check decides.
            return element.GetRawText();
        }

        /// <summary>The argument as an integer, or <paramref name="fallback"/> when absent; a value that is not a
        /// whole number is a <see cref="MentorArgumentException"/>.</summary>
        public int Integer(string name, int fallback)
        {
            if (!_values.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null) return fallback;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var whole)) return whole;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            throw new MentorArgumentException("the argument '" + name + "' must be a whole number, got " + element.GetRawText());
        }
    }

    /// <summary>A function call whose arguments could not be read as the schema says (not a tool refusal: the tool
    /// never ran and nothing is in the tool log).</summary>
    public sealed class MentorArgumentException : Exception
    {
        public MentorArgumentException(string message) : base(message) { }
    }

    private const string Session = "the FULL session id, copied from the id field of a session_index row";
    private const string Minute = "a local minute, YYYY-MM-DD HH:MM, copied from a tool's answer";

    /// <summary>The eleven tools in the skill table's order.</summary>
    public static readonly Tool[] All =
    {
        new("week_overview",
            "First. The week's numbers with their baselines and a coverage block. Once.",
            Array.Empty<Parameter>(),
            (surface, _) => surface.WeekOverview()),
        new("session_index",
            "Second. The map of the week's sessions. Once.",
            Array.Empty<Parameter>(),
            (surface, _) => surface.SessionIndex()),
        new("prior_weeks",
            "A number needs its earlier weeks and the overview's baseline is not enough.",
            new[]
            {
                new Parameter("metric", "string", true, "<group>.<key>, for example rhythm.sessions_started"),
                new Parameter("n", "integer", false, "how many prior weeks, default 4, at most " + ToolSurface.MaxPriorWeeks),
            },
            (surface, args) => surface.PriorWeeks(args.String("metric"), args.Integer("n", 4))),
        new("session_prompts",
            "The map or a search hit points at a session. Every prompt of it, in order.",
            new[] { new Parameter("session", "string", true, Session) },
            (surface, args) => surface.SessionPrompts(args.String("session"))),
        new("prompt_search",
            "You look for a phrase the rubric names, across every session, or in one session with session.",
            new[]
            {
                new Parameter("query", "string", true, "the phrase, matched case-insensitively as a substring"),
                new Parameter("limit", "integer", false, "how many hits at most, default " + ToolSurface.DefaultSearchLimit),
                new Parameter("session", "string", false, "restrict the search to this session: " + Session),
            },
            (surface, args) => surface.PromptSearch(args.String("query"), args.Integer("limit", ToolSurface.DefaultSearchLimit), args.String("session"))),
        new("dimension_candidates",
            "Once per rubric dimension, before you search. It proposes; you judge.",
            new[]
            {
                new Parameter("dimension", "string", true, "one of " + string.Join(", ", ToolSurface.DimensionKeys)),
                new Parameter("limit", "integer", false, "how many candidate prompts at most, default " + ToolSurface.DefaultSearchLimit),
            },
            (surface, args) => surface.DimensionCandidates(args.String("dimension"), args.Integer("limit", ToolSurface.DefaultSearchLimit))),
        new("session_outcomes",
            "You need to tell a session that shipped from one that did not.",
            new[] { new Parameter("session", "string", true, Session) },
            (surface, args) => surface.SessionOutcomes(args.String("session"))),
        new("turn_record",
            "One moment needs the agent's side: the reply and the terminal.",
            new[]
            {
                new Parameter("session", "string", true, Session),
                new Parameter("at", "string", true, Minute),
            },
            (surface, args) => surface.TurnRecord(args.String("session"), args.String("at"))),
        new("cite",
            "Before you write any citation. Answers the citation string and the prompt text.",
            new[]
            {
                new Parameter("session", "string", true, Session),
                new Parameter("at", "string", true, Minute),
            },
            (surface, args) => surface.Cite(args.String("session"), args.String("at"))),
        new("verify_quote",
            "Before you write any quotation. Answers true or false.",
            new[]
            {
                new Parameter("session", "string", true, Session),
                new Parameter("at", "string", true, Minute),
                new Parameter("fragment", "string", true, "the fragment, copied character for character from the prompt text cite answered"),
            },
            (surface, args) => surface.VerifyQuote(args.String("session"), args.String("at"), args.String("fragment"))),
        new("note",
            "Each time you find a candidate. Writes it to the run's notes.",
            new[] { new Parameter("text", "string", true, "what you saw, the session id and minute, and the citations you will use") },
            (surface, args) => surface.Note(args.String("text"))),
    };

    public static readonly string[] Names = All.Select(t => t.Name).ToArray();

    /// <summary>The tool by name, or null.</summary>
    public static Tool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>The <c>tools</c> array of a chat-completions request: one function entry per tool, the schema built
    /// from the table (an object with typed properties and the required names; a tool with no parameters offers an
    /// empty object).</summary>
    public static List<Dictionary<string, object?>> FunctionDefinitions()
    {
        var tools = new List<Dictionary<string, object?>>();
        foreach (var tool in All)
        {
            var properties = new Dictionary<string, object?>();
            foreach (var parameter in tool.Parameters)
                properties[parameter.Name] = new Dictionary<string, object?> { ["type"] = parameter.Type, ["description"] = parameter.Description };
            var schema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = tool.Parameters.Where(p => p.Required).Select(p => (object?)p.Name).ToList(),
                ["additionalProperties"] = false,
            };
            tools.Add(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = schema,
                },
            });
        }
        return tools;
    }

    /// <summary>Parse a function call's <c>arguments</c> JSON text into <see cref="Arguments"/>; an empty or blank
    /// text is no arguments; text that is not a JSON object is a <see cref="MentorArgumentException"/>.</summary>
    public static Arguments ParseArguments(string? argumentsJson)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new Arguments(values);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException error)
        {
            throw new MentorArgumentException("the arguments are not JSON: " + error.Message);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new MentorArgumentException("the arguments must be one JSON object, got " + document.RootElement.ValueKind);
            foreach (var property in document.RootElement.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return new Arguments(values);
    }

    /// <summary>Run one function call against the surface. The answer is the tool's JSON text (<see cref="ParityJson.Compact"/>,
    /// the tool log's own form); a refusal - a <see cref="ToolError"/> the surface raised and logged, or an argument the
    /// schema does not allow - is <c>{"error": "&lt;message&gt;"}</c>. Any other exception is a defect and goes up.</summary>
    public static (string Text, bool Ok) Call(ToolSurface surface, string name, string? argumentsJson)
    {
        var tool = Find(name);
        if (tool is null)
            return (ParityJson.Compact(new Dictionary<string, object?> { ["error"] = "no tool named '" + name + "'; the tools are " + string.Join(", ", Names) }), false);
        Arguments arguments;
        try
        {
            arguments = ParseArguments(argumentsJson);
        }
        catch (MentorArgumentException error)
        {
            return (ParityJson.Compact(new Dictionary<string, object?> { ["error"] = name + ": " + error.Message }), false);
        }
        try
        {
            return (ParityJson.Compact(tool.Invoke(surface, arguments)), true);
        }
        catch (ToolError error)
        {
            return (ParityJson.Compact(new Dictionary<string, object?> { ["error"] = error.Message }), false);
        }
        catch (MentorArgumentException error)
        {
            return (ParityJson.Compact(new Dictionary<string, object?> { ["error"] = name + ": " + error.Message }), false);
        }
    }
}
