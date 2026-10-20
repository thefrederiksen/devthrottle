using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Rules;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE WIRE SHAPE OF A RULE AND OF A FIRING, AND THE READING OF A WRITE - lifted out of the endpoint so it
/// can be tested without standing a web host up.
///
/// The projections used to live inside the route lambdas. That put the feature's whole public read surface
/// somewhere only a host-bound test could reach, and the host-bound suite is PARKED - so in practice
/// nothing tested it at all, and two accountability fields that had been added on purpose were simply
/// missing from it without anything noticing. The independent inspection of landing B found that.
///
/// So the mapping is a type. The routes stay a thin binding of HTTP to these methods, and what the account
/// actually receives is covered by ordinary unit tests that run in the fast gate.
/// </summary>
[Rules.RuleFeature]
internal static class SessionRuleWire
{
    /// <summary>How a caller says "every session" out loud.</summary>
    internal const string AllSessionsWireValue = SessionRuleBody.AllSessions;

    /// <summary>One rule, as the account reads it.</summary>
    internal static object Project(SessionRule r) => new
    {
        id = r.Id,
        instruction = r.Instruction,
        screenDescription = r.ScreenDescription,
        triggerWords = r.TriggerWords,
        checks = r.Calls.Select(c => c.Describe()).ToList(),
        scope = new { agent = r.Scope.Agent, repository = r.Scope.Repository, machine = r.Scope.Machine, mission = r.Scope.Mission },
        cooldownSeconds = r.CooldownSeconds,
        dailyCap = r.DailyCap,
        state = RuleWireNames.ToWireName(r.State.ToString()),
        // WHO MADE IT LIVE. Dry run is the bound that puts a person between a standing instruction and its
        // first real use, and this is the only place the account can find out who that person was. It was
        // stored and not delivered, which for a reader is the same as not existing.
        promotedBy = r.PromotedBy,
        createdUtc = r.CreatedUtc,
        updatedUtc = r.UpdatedUtc,
    };

    /// <summary>One firing, as the account reads it. The record is the product, so this is the shape in
    /// which the product is actually delivered.</summary>
    internal static object Project(SessionRuleFiring f) => new
    {
        id = f.Id,
        ruleId = f.RuleId,
        sessionId = f.SessionId,
        occurredUtc = f.OccurredUtc,
        screenText = f.ScreenText,
        understanding = f.Understanding,
        decision = f.Decision,
        reason = f.Reason,
        checksRun = f.PrimitiveRuns.Select(p => new { name = p.Name, arguments = p.Arguments, answer = p.Answer }).ToList(),
        typedText = f.TypedText,
        outcome = f.Outcome,
        // WHAT CHECKING THE STATED REASON AGAINST THE SCREEN FOUND (Architect ruling A12). It is never
        // blank, and delivering it is the whole point of it: a run in which that check never happened must
        // not read the same as one in which it ran and found nothing wrong.
        grounding = f.Grounding,
    };

    /// <summary>
    /// The scope, which has to be SAID. An absent scope used to become "every session" - the widest value
    /// there is - so the wire could not tell a deliberate choice from an omission, and malformed authoring
    /// output would have been read as permission to act on everything. Now: the string "all-sessions" is
    /// the explicit way to say every session, an object naming at least one part is a narrower scope, and
    /// anything else is null, which the store refuses with a reason.
    /// </summary>
    internal static RuleScope? ReadScope(JsonElement body)
    {
        if (!body.TryGetProperty("scope", out var scope)) return null;

        if (scope.ValueKind == JsonValueKind.String)
            return string.Equals(scope.GetString()?.Trim(), AllSessionsWireValue, StringComparison.OrdinalIgnoreCase)
                ? RuleScope.AllSessions
                : null;

        if (scope.ValueKind != JsonValueKind.Object) return null;

        var built = new RuleScope(
            RuleCallJson.Text(scope, "agent"),
            RuleCallJson.Text(scope, "repository"),
            RuleCallJson.Text(scope, "machine"),
            RuleCallJson.Text(scope, "mission"));

        // An object with nothing in it is not a choice of "all sessions"; it is the same omission wearing
        // a pair of braces.
        return built == RuleScope.AllSessions ? null : built;
    }

    internal static IReadOnlyList<string> Strings(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return array.EnumerateArray().Select(RuleCallJson.Scalar).ToList();
    }

    /// <summary>
    /// The checks, read by the SAME reader the agent's reply goes through - one meaning of a check written
    /// as JSON in this feature, and now really one rather than two.
    ///
    /// This used to read them only when the property was an array and to drop any entry that was not an
    /// object, silently, while the route's own comment claimed both paths used the same reader. A check
    /// that disappears takes its refusal with it, and a caller could ask for two checks, have one quietly
    /// removed and receive a rule that runs the other one alone.
    /// </summary>
    /// <exception cref="RuleRejectedException">The checks are not a list of checks; the reason says why.</exception>
    internal static IReadOnlyList<RulePrimitiveCall> Calls(JsonElement body)
    {
        var calls = RuleCallJson.ReadChecks(body, "checks", required: true, out var problem);
        if (calls is null) throw new RuleRejectedException(problem!);
        return calls;
    }

    /// <summary>A whole number off the body, or 0 when it is absent or not a number - which the store refuses
    /// with its own reason. A number that is not a whole number is refused here, because reading 2.5 as an
    /// error in the server would answer a caller's mistake with a failure that names nothing.</summary>
    /// <exception cref="RuleRejectedException">The value is a number but not a whole one.</exception>
    internal static int Number(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return 0;
        if (!value.TryGetInt32(out var number))
            throw new RuleRejectedException($"'{name}' has to be a whole number, and was {value.GetRawText()}.");
        return number;
    }

    /// <summary>
    /// A WRITE, READ ONCE. Create and change both read their body here, so the two cannot come to disagree about
    /// what a field means - and a field the create learns to read is read by the change in the same edit.
    /// </summary>
    /// <exception cref="RuleRejectedException">The checks are not a list of checks; the reason says why.</exception>
    internal static SessionRuleDraft ReadDraft(JsonElement body) => new(
        RuleCallJson.Text(body, "instruction") ?? "",
        RuleCallJson.Text(body, "screenDescription") ?? "",
        Strings(body, "triggerWords"),
        Calls(body),
        ReadScope(body),
        Number(body, "cooldownSeconds"),
        Number(body, "dailyCap"));

    /// <summary>
    /// A CHANGE IS THE RULE'S CURRENT BODY WITH THE CALLER'S FIELDS LAID OVER IT, read back through
    /// <see cref="ReadDraft"/> - the same reader a create uses. The fields a change may carry are exactly the
    /// fields of <see cref="SessionRuleBody.Of"/>: derived from the body, never listed here, so a field added to
    /// a rule's body can be changed the moment it exists, with nothing to remember in this method.
    ///
    /// A field the body does not have is REFUSED, never ignored: a caller that misspells "dailyCap" and is told
    /// the change worked has a rule that is not the one it meant. A rule's state is the one field named on
    /// purpose, because it is the one a caller is most likely to try: it moves by promote, and a change puts the
    /// rule back in dry run.
    /// </summary>
    /// <exception cref="RuleRejectedException">The body is not an object, names nothing, or names a field a change
    /// cannot carry; the reason says which.</exception>
    internal static SessionRuleDraft ReadChange(SessionRule current, JsonElement changes)
    {
        if (changes.ValueKind != JsonValueKind.Object)
            throw new RuleRejectedException(
                "a change is an object naming the fields to change and their new values, for example " +
                "{\"dailyCap\": 3}.");

        var merged = SessionRuleBody.Of(current);
        var changeable = merged.Select(p => p.Key).ToList();
        var named = 0;
        foreach (var field in changes.EnumerateObject())
        {
            named++;
            if (field.Name is "state" or "promotedBy")
                throw new RuleRejectedException(
                    $"'{field.Name}' is not changed by a change. A rule is moved out of dry run by promote, and " +
                    "any change puts it back in dry run.");
            if (!merged.ContainsKey(field.Name))
                throw new RuleRejectedException(
                    $"'{field.Name}' is not a field a change can carry. A change carries: {string.Join(", ", changeable)}.");
            merged[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        }

        if (named == 0)
            throw new RuleRejectedException(
                $"this change names no fields, so there is nothing to change. A change carries: {string.Join(", ", changeable)}.");

        using var document = JsonDocument.Parse(merged.ToJsonString());
        return ReadDraft(document.RootElement.Clone());
    }

    /// <summary>One recorded change, as the account reads it. Before and after are the rule's body and state as
    /// JSON objects, null on the side that does not exist.</summary>
    internal static object Project(SessionRuleChange c) => new
    {
        id = c.Id,
        ruleId = c.RuleId,
        action = c.Action,
        changedBy = c.ChangedBy,
        sessionId = c.SessionId,
        agent = c.Agent,
        occurredUtc = c.OccurredUtc,
        before = c.Before.Length == 0 ? null : JsonNode.Parse(c.Before),
        after = c.After.Length == 0 ? null : JsonNode.Parse(c.After),
    };
}

