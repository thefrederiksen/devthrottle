using CcDirector.Gateway.Factory;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE ONE CHECK OF A MAIL DESK RULES DOCUMENT (Mail Desk mission, phase 1, contract section 3). Every save goes
/// through it, whoever makes it - a person, a session, or the command - so the rules phase 3 matches against are
/// always rules it can read. Every refusal is a sentence that names the field and says how to fix it.
///
/// It also gives the document its ONE SPELLING: patterns lower case, factories and seats folded as
/// <see cref="FactoryNames"/> folds them, texts trimmed. Nothing else is repaired; anything it cannot fold is refused.
/// The order of every list is kept exactly, because the order is meaning: the first matching rule wins.
/// </summary>
public static class MailDeskRulesValidator
{
    /// <summary>The longest a rule's name may be.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest a describing line may be: an allow entry's <c>what</c>, a block entry's <c>why</c>, a
    /// route's <c>handles</c>, or one of a rule's subject words.</summary>
    public const int MaxTextLength = 500;

    /// <summary>The most a whole document may take as JSON, in UTF-8 bytes. Far beyond any real list, and still a stop
    /// for a runaway writer.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    /// <summary>Check <paramref name="document"/>. On success the result carries the document in its one spelling.</summary>
    public static MailDeskRulesValidation Validate(MailDeskRulesDocument? document)
    {
        if (document is null)
            return MailDeskRulesValidation.Refused(new[] { "the rules document is missing: send 'rules', 'allow', 'block' and 'routes'" });

        var problems = new List<string>();
        var rules = document.Rules.Select((r, i) => Rule(r, i, problems)).ToList();
        var allow = document.Allow.Select((a, i) => Allow(a, i, problems)).ToList();
        var block = document.Block.Select((b, i) => Block(b, i, problems)).ToList();
        var routes = document.Routes.Select((r, i) => Route(r, i, problems)).ToList();

        // Rule names are unique, without regard to case: a rule is removed by its name, so two of one name could not
        // be told apart.
        foreach (var name in Duplicates(rules.Select(r => r.Name).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase))
            problems.Add($"rules: the name '{name}' is used by more than one rule; give each rule its own name");

        // A pattern appears once in a list, and never on both lists: a sender cannot be both wanted and blocked.
        foreach (var pattern in Duplicates(allow.Select(a => a.From).Where(p => p.Length > 0), StringComparer.Ordinal))
            problems.Add($"allow: '{pattern}' is on the allow-list more than once; keep one entry for it");
        foreach (var pattern in Duplicates(block.Select(b => b.From).Where(p => p.Length > 0), StringComparer.Ordinal))
            problems.Add($"block: '{pattern}' is on the block list more than once; keep one entry for it");
        foreach (var pattern in allow.Select(a => a.From).Intersect(block.Select(b => b.From), StringComparer.Ordinal).Where(p => p.Length > 0))
            problems.Add($"'{pattern}' is on both the allow-list and the block list; remove it from one of them");

        // A route appears once: the model chooses among routes by their line, and two lines for one seat is a choice
        // with no difference.
        foreach (var key in Duplicates(routes.Select(r => r.Key).Where(k => k != "/"), StringComparer.Ordinal))
            problems.Add($"routes: '{key}' is listed more than once; keep one line for it");

        if (problems.Count > 0) return MailDeskRulesValidation.Refused(problems);

        var folded = new MailDeskRulesDocument { Rules = rules, Allow = allow, Block = block, Routes = routes };
        var bytes = System.Text.Encoding.UTF8.GetByteCount(folded.ToJson());
        if (bytes > MaxDocumentBytes)
            return MailDeskRulesValidation.Refused(new[]
            {
                $"the rules document takes {bytes} bytes; it may take at most {MaxDocumentBytes}. Remove entries that are no longer needed",
            });
        return MailDeskRulesValidation.Valid(folded);
    }

    private static MailDeskRule Rule(MailDeskRule rule, int index, List<string> problems)
    {
        var field = $"rules entry {index + 1}";
        var name = (rule.Name ?? "").Trim();
        if (name.Length == 0)
            problems.Add($"{field}: 'name' is blank; give the rule a name, such as 'citizenship'");
        else if (name.Length > MaxNameLength)
            problems.Add($"{field}: 'name' takes at most {MaxNameLength} characters; this one is {name.Length}");
        var label = name.Length > 0 ? $"rule '{name}'" : field;

        var subjects = new List<string>();
        foreach (var (word, i) in (rule.When?.SubjectContains ?? Array.Empty<string>()).Select((w, i) => (w, i)))
        {
            var trimmed = (word ?? "").Trim();
            if (trimmed.Length == 0)
                problems.Add($"{label}: 'when.subjectContains' entry {i + 1} is blank; give the words the subject must contain, or leave the entry out");
            else if (trimmed.Length > MaxTextLength)
                problems.Add($"{label}: 'when.subjectContains' entry {i + 1} takes at most {MaxTextLength} characters; this one is {trimmed.Length}");
            subjects.Add(trimmed);
        }
        var senders = new List<string>();
        foreach (var (raw, i) in (rule.When?.FromAny ?? Array.Empty<string>()).Select((p, i) => (p, i)))
        {
            if (!MailDeskAddresses.TryPattern(raw, $"{label}: 'when.fromAny' entry {i + 1}", out var pattern, out var refusal))
                problems.Add(refusal!);
            senders.Add(pattern);
        }
        if (subjects.Count == 0 && senders.Count == 0)
            problems.Add($"{label}: 'when' has no condition; give 'subjectContains', 'fromAny', or both");

        var route = rule.Route is null
            ? null
            : Target(rule.Route, $"{label}: 'route'", problems);
        if (rule.Route is null)
            problems.Add($"{label}: 'route' is missing; say where the rule sends the email, as {{ \"factory\": ..., \"seat\": ... }}");

        return new MailDeskRule
        {
            Name = name,
            When = new MailDeskRuleWhen { SubjectContains = subjects, FromAny = senders },
            Route = route,
        };
    }

    private static MailDeskAllowEntry Allow(MailDeskAllowEntry entry, int index, List<string> problems)
    {
        var field = $"allow entry {index + 1}";
        if (!MailDeskAddresses.TryPattern(entry.From, $"{field}: 'from'", out var from, out var refusal))
            problems.Add(refusal!);
        var label = from.Length > 0 ? $"allow entry '{from}'" : field;
        var what = Text(entry.What, $"{label}: 'what'", "say what these emails are, such as 'build notifications'", problems);

        MailDeskAllowDo? action = null;
        var allowed = $"{MailDeskWords.InWords(MailDeskWords.AllowActions.All)} or {{ \"route\": {{ \"factory\": ..., \"seat\": ... }} }}";
        if (entry.Do is null || (entry.Do.Action is null && entry.Do.Route is null))
            problems.Add($"{label}: 'do' is missing; give {allowed}");
        else if (entry.Do.Route is not null)
        {
            if (entry.Do.Action is not null)
                problems.Add($"{label}: 'do' is either a word or a route, not both; give {allowed}");
            else
                action = new MailDeskAllowDo { Route = Target(entry.Do.Route, $"{label}: 'do.route'", problems) };
        }
        else if (!MailDeskWords.IsOneOf(entry.Do.Action, MailDeskWords.AllowActions.All))
            problems.Add($"{label}: 'do' is '{entry.Do.Action}', which the Mail Desk does not know; give {allowed}");
        else
            action = new MailDeskAllowDo { Action = entry.Do.Action };

        return new MailDeskAllowEntry { From = from, What = what, Do = action };
    }

    private static MailDeskBlockEntry Block(MailDeskBlockEntry entry, int index, List<string> problems)
    {
        var field = $"block entry {index + 1}";
        if (!MailDeskAddresses.TryPattern(entry.From, $"{field}: 'from'", out var from, out var refusal))
            problems.Add(refusal!);
        var label = from.Length > 0 ? $"block entry '{from}'" : field;
        var why = Text(entry.Why, $"{label}: 'why'", "say why this sender is blocked, such as 'cold sales, never answered'", problems);
        return new MailDeskBlockEntry { From = from, Why = why };
    }

    private static MailDeskRouteEntry Route(MailDeskRouteEntry entry, int index, List<string> problems)
    {
        var target = Target(new MailDeskRouteTarget { Factory = entry.Factory, Seat = entry.Seat }, $"routes entry {index + 1}", problems);
        var label = target.Factory.Length > 0 && target.Seat.Length > 0 ? $"route '{target.Key}'" : $"routes entry {index + 1}";
        var handles = Text(entry.Handles, $"{label}: 'handles'",
            "say in one line what this seat handles, such as 'customers of DevThrottle: support, billing, bugs'", problems);
        return new MailDeskRouteEntry { Factory = target.Factory, Seat = target.Seat, Handles = handles };
    }

    /// <summary>A factory and a seat, folded: the factory must pass <see cref="FactoryNames.TryFactory"/>, and the seat,
    /// besides not being blank, follows the same rule as every seat id (<see cref="FactoryNames.TrySeat"/>), so a
    /// route can name only a seat that could exist.</summary>
    private static MailDeskRouteTarget Target(MailDeskRouteTarget target, string label, List<string> problems)
    {
        if (!FactoryNames.TryFactory(target.Factory, out var factory, out var factoryRefusal))
            problems.Add($"{label}: 'factory' is not a factory name - {factoryRefusal}");
        string seat;
        if (string.IsNullOrWhiteSpace(target.Seat))
        {
            seat = "";
            problems.Add($"{label}: 'seat' is blank; name the seat of the factory that takes the email");
        }
        else if (!FactoryNames.TrySeat(target.Seat, out seat, out var seatRefusal))
            problems.Add($"{label}: 'seat' is not a seat name - {seatRefusal}");
        return new MailDeskRouteTarget { Factory = factory, Seat = seat };
    }

    private static string Text(string? raw, string label, string fix, List<string> problems)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) problems.Add($"{label} is blank; {fix}");
        else if (text.Length > MaxTextLength) problems.Add($"{label} takes at most {MaxTextLength} characters; this one is {text.Length}");
        return text;
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string> values, StringComparer comparer)
        => values.GroupBy(v => v, comparer).Where(g => g.Count() > 1).Select(g => g.Key);
}

/// <summary>The result of <see cref="MailDeskRulesValidator.Validate"/>: the folded document, or every problem.</summary>
public sealed record MailDeskRulesValidation
{
    /// <summary>The document in its one spelling, or null when it was refused.</summary>
    public MailDeskRulesDocument? Document { get; private init; }

    /// <summary>Every refusal, one sentence each, in document order. Empty when the document is valid.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = Array.Empty<string>();

    public bool IsValid => Problems.Count == 0;

    /// <summary>The problems as one answer for a route to send back.</summary>
    public string Refusal => string.Join("; ", Problems);

    internal static MailDeskRulesValidation Valid(MailDeskRulesDocument document) => new() { Document = document };

    internal static MailDeskRulesValidation Refused(IReadOnlyList<string> problems) => new() { Problems = problems };
}
