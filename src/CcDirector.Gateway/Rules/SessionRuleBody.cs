using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// A RULE IN THE SHAPE IT IS WRITTEN IN - the body <c>POST /gateway/rules</c> accepts - built from a stored rule.
///
/// It exists for two readers, and both need exactly the write shape rather than the read shape:
///
///  - A CHANGE is the rule's current body with the caller's fields laid over it, read back through the same
///    readers a create uses. So a field the create learns to read flows through a change as soon as it is added
///    HERE - as phase 1's kind, where a rule applies and a hook rule's trigger do - and a change cannot accept a
///    field a create would refuse.
///  - The CHANGE HISTORY stores the rule before and after in this shape, plus its state, so the two sides of a
///    change compare field by field with what the caller sent.
///
/// The read projection (<c>SessionRuleWire.Project</c>) is not this shape: it renders checks as sentences for a
/// person and spells "every session" as four nulls, neither of which a create would accept back.
///
/// ROUND TRIP IS THE CONTRACT. Reading <see cref="Of"/> back through the create readers gives the same rule;
/// <c>SessionRuleBodyTests</c> holds that, so a field added to the create and forgotten here fails a test
/// instead of being quietly dropped by every change.
/// </summary>
public static class SessionRuleBody
{
    /// <summary>How a body says "every session" out loud.</summary>
    public const string AllSessions = "all-sessions";

    /// <summary>
    /// The rule as a create body OF ITS KIND (ruling P18). A screen rule's body is the September body plus its kind
    /// and where it applies; a hook rule's is the body a hook create takes - its kind, its sentence, where it
    /// applies, and its event, tool, handler, match text and result - and none of the screen fields, which a hook
    /// create refuses. So a change to either kind carries exactly the fields that kind is written with, and the
    /// history's before and after hold every one of them.
    /// </summary>
    public static JsonObject Of(SessionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.Equals(rule.Kind, RuleKinds.Hook, StringComparison.Ordinal))
        {
            var hook = rule.Hook
                ?? throw new InvalidOperationException($"the hook rule {rule.Id} has no trigger to write a body from.");
            return new JsonObject
            {
                ["kind"] = rule.Kind,
                ["instruction"] = rule.Instruction,
                ["appliesTo"] = AppliesTo(rule.AppliesTo),
                ["event"] = hook.Event,
                ["tool"] = hook.Tool,
                ["handler"] = hook.Handler,
                ["match"] = Texts(hook.Match),
                ["result"] = hook.Result,
            };
        }

        return new JsonObject
        {
            ["kind"] = rule.Kind,
            ["instruction"] = rule.Instruction,
            ["screenDescription"] = rule.ScreenDescription,
            ["triggerWords"] = Texts(rule.TriggerWords),
            ["checks"] = new JsonArray(rule.Calls.Select(c => (JsonNode?)Call(c)).ToArray()),
            ["scope"] = Scope(rule.Scope),
            ["cooldownSeconds"] = rule.CooldownSeconds,
            ["dailyCap"] = rule.DailyCap,
            ["appliesTo"] = AppliesTo(rule.AppliesTo),
        };
    }

    /// <summary>
    /// Every field a create of ANY kind reads, derived from the bodies themselves and never listed. A change may
    /// name one its rule's kind does not carry, so that the create's reader refuses it with the create's own reason
    /// - "a hook rule has no dailyCap" says more than "dailyCap is not a field".
    /// </summary>
    public static IReadOnlyList<string> FieldsOfEveryKind { get; } = EveryKindsFields();

    private static IReadOnlyList<string> EveryKindsFields()
    {
        var at = DateTime.UnixEpoch;
        var screen = new SessionRule(Guid.Empty, "", "", Array.Empty<string>(), Array.Empty<RulePrimitiveCall>(),
            RuleScope.AllSessions, 0, 0, RuleState.DryRun, "", at, at);
        var hook = screen with { Kind = RuleKinds.Hook, Hook = new HookRuleTrigger("", "", "", Array.Empty<string>(), "") };
        return Of(screen).Select(p => p.Key).Union(Of(hook).Select(p => p.Key)).ToList();
    }

    private static JsonArray Texts(IEnumerable<string> texts) =>
        new(texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());

    /// <summary>Where the rule applies, every part written out so the history reads in one shape.</summary>
    private static JsonObject AppliesTo(RuleAppliesTo appliesTo) => new()
    {
        ["kind"] = appliesTo.Kind,
        ["machines"] = Texts(appliesTo.Machines),
        ["exceptMachines"] = Texts(appliesTo.ExceptMachines),
        ["repo"] = appliesTo.Repo,
    };

    /// <summary>The rule as the change history keeps it: its body, its state and who made it live. Compact
    /// JSON text, one line.</summary>
    public static string Snapshot(SessionRule rule)
    {
        var body = Of(rule);
        body["state"] = RuleWireNames.ToWireName(rule.State.ToString());
        body["promotedBy"] = rule.PromotedBy;
        // Readable as stored: an apostrophe or a non-ASCII letter is kept as itself rather than escaped, so a direct
        // reader of the table sees the sentence that was said. The wire re-parses it either way.
        return body.ToJsonString(SnapshotOptions);
    }

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One check in the shape <see cref="RuleCallJson.ReadCall"/> reads. A literal is ALWAYS written as a list,
    /// even of one value: a single literal written as a string would be read back as a request to read an input
    /// whenever its text happened to sit in angle brackets.
    /// </summary>
    private static JsonObject Call(RulePrimitiveCall call)
    {
        var inputSource = RuleWireNames.ToWireName(nameof(RuleArgumentSource.Input));
        var arguments = new JsonObject();
        foreach (var argument in call.Arguments)
        {
            arguments[argument.Parameter] = argument.Source == inputSource
                ? JsonValue.Create("<" + string.Join(",", argument.Values) + ">")
                : new JsonArray(argument.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        return new JsonObject { ["name"] = call.Name, ["arguments"] = arguments };
    }

    /// <summary>The scope said out loud: "all-sessions", or an object naming only the parts that are set.</summary>
    private static JsonNode Scope(RuleScope scope)
    {
        if (scope == RuleScope.AllSessions) return JsonValue.Create(AllSessions)!;
        var parts = new JsonObject();
        if (scope.Agent is not null) parts["agent"] = scope.Agent;
        if (scope.Repository is not null) parts["repository"] = scope.Repository;
        if (scope.Machine is not null) parts["machine"] = scope.Machine;
        if (scope.Mission is not null) parts["mission"] = scope.Mission;
        return parts;
    }
}
