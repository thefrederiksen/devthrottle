using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// A RULE IN THE SHAPE IT IS WRITTEN IN - the body <c>POST /gateway/rules</c> accepts - built from a stored rule.
///
/// It exists for two readers, and both need exactly the write shape rather than the read shape:
///
///  - A CHANGE is the rule's current body with the caller's fields laid over it, read back through the same
///    readers a create uses. So a field the create learns to read (phase 1 adds a kind and scopes) flows through
///    a change as soon as it is added HERE, and a change cannot accept a field a create would refuse.
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

    /// <summary>The rule as a create body.</summary>
    public static JsonObject Of(SessionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new JsonObject
        {
            ["instruction"] = rule.Instruction,
            ["screenDescription"] = rule.ScreenDescription,
            ["triggerWords"] = new JsonArray(rule.TriggerWords.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["checks"] = new JsonArray(rule.Calls.Select(c => (JsonNode?)Call(c)).ToArray()),
            ["scope"] = Scope(rule.Scope),
            ["cooldownSeconds"] = rule.CooldownSeconds,
            ["dailyCap"] = rule.DailyCap,
        };
    }

    /// <summary>The rule as the change history keeps it: its body, its state and who made it live. Compact
    /// JSON text, one line.</summary>
    public static string Snapshot(SessionRule rule)
    {
        var body = Of(rule);
        body["state"] = RuleWireNames.ToWireName(rule.State.ToString());
        body["promotedBy"] = rule.PromotedBy;
        return body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

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
