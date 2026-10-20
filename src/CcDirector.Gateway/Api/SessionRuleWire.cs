using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
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
        // Session Rules part 2, phase 1: what starts the rule, where it applies, and a hook rule's trigger.
        // Flat, and empty for a rule of another kind, so every rule reads in one shape.
        kind = r.Kind,
        appliesTo = new
        {
            kind = r.AppliesTo.Kind,
            machines = r.AppliesTo.Machines,
            exceptMachines = r.AppliesTo.ExceptMachines,
            repo = r.AppliesTo.Repo,
        },
        @event = r.Hook?.Event ?? "",
        tool = r.Hook?.Tool ?? "",
        handler = r.Hook?.Handler ?? "",
        match = r.Hook?.Match ?? (IReadOnlyList<string>)Array.Empty<string>(),
        result = r.Hook?.Result ?? "",
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
        // Session Rules part 2, phase 1: a hook firing carries the decision the hook made (wire contract W5).
        kind = f.Kind,
        decisionId = f.DecisionId,
        @event = f.Event,
        tool = f.Tool,
        subject = f.Subject,
        machine = f.Machine,
        repo = f.Repo,
        agent = f.Agent,
        agentEvent = f.AgentEvent,
        agentTool = f.AgentTool,
        overriddenRuleIds = f.OverriddenRuleIds,
        matchedRuleIds = f.MatchedRuleIds,
        evaluationMicroseconds = f.EvaluationMicroseconds,
    };

    // ---- Session Rules part 2, phase 1: kinds, appliesTo, effective rules and hook firings ---------------

    /// <summary>
    /// Create a rule from a POST /gateway/rules body. A body with no <c>kind</c> is a screen rule, read exactly
    /// as before - the September body keeps working unchanged. A hook rule is <c>"kind": "hook"</c> with
    /// <c>appliesTo</c>, <c>event</c>, <c>tool</c>, <c>handler</c>, <c>match</c> and <c>result</c>.
    /// </summary>
    /// <exception cref="RuleRejectedException">The body is not a rule that can be stored; the reason says why.</exception>
    internal static SessionRule CreateFromBody(SessionRuleStore store, JsonElement body, DateTime nowUtc, RuleChangeActor actor)
    {
        var kind = ReadKind(body);
        switch (kind)
        {
            case RuleKinds.Screen:
                return store.Create(
                    RuleCallJson.Text(body, "instruction") ?? "",
                    RuleCallJson.Text(body, "screenDescription") ?? "",
                    Strings(body, "triggerWords"),
                    Calls(body),
                    ReadScope(body),
                    Number(body, "cooldownSeconds"),
                    Number(body, "dailyCap"),
                    nowUtc,
                    actor,
                    ReadAppliesTo(body));

            case RuleKinds.Hook:
                RefuseScreenFieldsOnAHookRule(body);
                return store.CreateHook(
                    RuleCallJson.Text(body, "instruction") ?? "",
                    ReadAppliesTo(body),
                    RuleCallJson.Text(body, "event") ?? "",
                    RuleCallJson.Text(body, "tool") ?? "",
                    RuleCallJson.Text(body, "handler") ?? "",
                    Strings(body, "match"),
                    RuleCallJson.Text(body, "result") ?? "",
                    nowUtc,
                    actor);

            case RuleKinds.SessionStart:
                throw new RuleRejectedException(
                    "session start rules are not built yet - they arrive in phase 5.");

            default:
                throw new RuleRejectedException(
                    $"'{kind}' is not a kind of rule this build knows. A rule is one of: " +
                    string.Join(", ", RuleKinds.All) + ".");
        }
    }

    /// <summary>The rule's kind; a body that does not say is a screen rule, which is what every rule was before
    /// kinds existed.</summary>
    /// <exception cref="RuleRejectedException">The kind is there and is not text.</exception>
    private static string ReadKind(JsonElement body)
    {
        if (!body.TryGetProperty("kind", out var kind)) return RuleKinds.Screen;
        if (kind.ValueKind != JsonValueKind.String)
            throw new RuleRejectedException(
                "a rule's kind is text - one of: " + string.Join(", ", RuleKinds.All) + ".");
        return (kind.GetString() ?? "").Trim();
    }

    /// <summary>
    /// The screen rule's fields mean nothing for a hook rule, so a hook body carrying them is refused rather
    /// than stored with them quietly dropped - a caller who sent them believed they would do something.
    /// </summary>
    private static void RefuseScreenFieldsOnAHookRule(JsonElement body)
    {
        if (body.TryGetProperty("checks", out var checks)
            && !(checks.ValueKind == JsonValueKind.Array && checks.GetArrayLength() == 0))
            throw new RuleRejectedException(
                "a hook rule cannot run checks yet - the Check handler arrives in phase 4. In phase 1 a hook rule " +
                "matches literal text.");

        if (body.TryGetProperty("scope", out _))
            throw new RuleRejectedException(
                "a hook rule says where it applies with appliesTo, and has no session scope - one rule, one " +
                "scope system.");

        if (!string.IsNullOrWhiteSpace(RuleCallJson.Text(body, "screenDescription")))
            throw new RuleRejectedException("a hook rule does not watch the screen, so it has no screen description.");

        if (Strings(body, "triggerWords").Count > 0)
            throw new RuleRejectedException(
                "a hook rule does not watch the screen, so it has no trigger words - its match text is what it " +
                "looks for.");

        // The ceilings bound how often a screen rule ACTS on a session. A phase 1 hook rule acts on nothing - it
        // records what it would have decided - so a bound written on one would be stored as nothing at all.
        foreach (var bound in new[] { "cooldownSeconds", "dailyCap" })
            if (body.TryGetProperty(bound, out _))
                throw new RuleRejectedException(
                    $"a hook rule has no {bound} - it is decided by the hook on every command it polices, and a " +
                    "bound written here would be stored as nothing.");
    }

    /// <summary>
    /// Where the rule applies (ruling P4): <c>{ "kind", "machines", "exceptMachines", "repo" }</c>, or null when
    /// the body does not say. What null means is the store's to decide for each kind.
    /// </summary>
    /// <exception cref="RuleRejectedException">It is there and is not an object.</exception>
    internal static RuleAppliesTo? ReadAppliesTo(JsonElement body)
    {
        if (!body.TryGetProperty("appliesTo", out var appliesTo)) return null;
        if (appliesTo.ValueKind != JsonValueKind.Object)
            throw new RuleRejectedException(
                "appliesTo is an object - { \"kind\": \"everywhere\" | \"machine\" | \"repo\" | " +
                "\"repo_on_machine\", \"machines\": [...], \"exceptMachines\": [...], \"repo\": \"...\" }.");

        return new RuleAppliesTo(
            RuleCallJson.Text(appliesTo, "kind") ?? "",
            Strings(appliesTo, "machines"),
            Strings(appliesTo, "exceptMachines"),
            RuleCallJson.Text(appliesTo, "repo") ?? "");
    }

    /// <summary>
    /// The query of GET /gateway/rules/effective (wire contract W1): the kind, which is <c>hook</c> in phase 1,
    /// the machine, which is required, and the repository, which may be empty and is returned in its one
    /// spelling.
    /// </summary>
    /// <exception cref="RuleRejectedException">The kind is missing or not hook, or no machine was named.</exception>
    internal static (string Machine, string Repo) ReadEffectiveQuery(string? kind, string? machine, string? repo)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new RuleRejectedException("say which kind of rules: kind=hook.");
        if (!string.Equals(kind.Trim(), RuleKinds.Hook, StringComparison.Ordinal))
            throw new RuleRejectedException(
                $"the effective rules are served for kind=hook in phase 1, and this asked for '{kind}'.");
        if (string.IsNullOrWhiteSpace(machine))
            throw new RuleRejectedException(
                "the effective rules are worked out for one machine - say which: machine=<name>.");
        return (machine.Trim(), GitRemoteIdentity.Normalise(repo));
    }

    /// <summary>The answer of GET /gateway/rules/effective (wire contract W1), each rule in the W2 item shape.</summary>
    internal static object ProjectEffective(string machine, string repo, IReadOnlyList<EffectiveRule> rules) => new
    {
        machine,
        repo,
        rules = rules.Select(e => new
        {
            id = e.Rule.Id,
            rank = e.Rank,
            appliesTo = e.Rule.AppliesTo.Kind,
            @event = e.Rule.Hook?.Event ?? "",
            tool = e.Rule.Hook?.Tool ?? "",
            match = e.Rule.Hook?.Match ?? (IReadOnlyList<string>)Array.Empty<string>(),
            result = e.Rule.Hook?.Result ?? "",
            reason = e.Rule.Instruction,
            state = RuleWireNames.ToWireName(e.Rule.State.ToString()),
        }).ToList(),
    };

    /// <summary>
    /// The decisions in a POST /gateway/rules/hook-firings body (wire contract W5), each in the W3 shape. An
    /// item that cannot be read is added to <paramref name="unreadable"/> with its reason - by its decision id
    /// when it has one - and the others are still read, so one bad file never holds up the rest.
    /// </summary>
    /// <exception cref="RuleRejectedException">The body has no decisions list.</exception>
    internal static IReadOnlyList<HookDecision> ReadHookDecisions(JsonElement body, List<HookFiringRefusal> unreadable)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("decisions", out var list)
            || list.ValueKind != JsonValueKind.Array)
            throw new RuleRejectedException("the body is { \"decisions\": [ ... ] }, and it has no decisions list.");

        // ONE LIMIT, BOTH ENDS, AND NOTHING LOST TO IT (ruling P17). Every decision is recorded under the store's
        // one write lock, which the screen evaluator and every rules route also wait on, so a post is bounded by
        // the limit the Director batches by. A post over it is refused WHOLE: the Director deletes a decision
        // refused by id, so refusing the ones past the limit one by one would destroy real records, while a
        // refused post is a failed post and the Director keeps every drop.
        var count = list.GetArrayLength();
        if (count > HookFiringLimits.MaxDecisionsPerPost)
            throw new RuleRejectedException(
                $"one post carries at most {HookFiringLimits.MaxDecisionsPerPost} decisions, and this one carries " +
                $"{count}. Nothing in it was recorded; send them in smaller posts.");

        var read = new List<HookDecision>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                unreadable.Add(new HookFiringRefusal("", "a decision is an object, and this one is not."));
                continue;
            }

            var decisionId = RuleCallJson.Text(item, "decisionId") ?? "";
            var problem = ReadHookDecision(item, decisionId, out var decision);
            if (problem is not null)
            {
                unreadable.Add(new HookFiringRefusal(decisionId, problem));
                continue;
            }
            read.Add(decision!);
        }
        return read;
    }

    /// <summary>One W3 decision, or the reason it cannot be read.</summary>
    private static string? ReadHookDecision(JsonElement item, string decisionId, out HookDecision? decision)
    {
        decision = null;

        if (!item.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt32(out var version))
            return "a decision says which version of the decision file it is, and this one does not.";

        if (!Guid.TryParse(RuleCallJson.Text(item, "decidingRuleId"), out var decidingRuleId))
            return "decidingRuleId is not a rule id.";

        if (!DateTime.TryParse(RuleCallJson.Text(item, "occurredUtc"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var occurredUtc))
            return "occurredUtc is not a time.";

        var overridden = Ids(item, "overriddenRuleIds");
        if (overridden is null) return "overriddenRuleIds is not a list of rule ids.";
        var matched = Ids(item, "matchedRuleIds");
        if (matched is null) return "matchedRuleIds is not a list of rule ids.";

        long microseconds = 0;
        if (item.TryGetProperty("evaluationMicroseconds", out var micro) && !micro.TryGetInt64(out microseconds))
            return "evaluationMicroseconds is not a whole number.";

        decision = new HookDecision(
            version,
            decisionId,
            RuleCallJson.Text(item, "sessionId") ?? "",
            DateTime.SpecifyKind(occurredUtc, DateTimeKind.Utc),
            RuleCallJson.Text(item, "agent") ?? "",
            RuleCallJson.Text(item, "event") ?? "",
            RuleCallJson.Text(item, "agentEvent") ?? "",
            RuleCallJson.Text(item, "tool") ?? "",
            RuleCallJson.Text(item, "agentTool") ?? "",
            RuleCallJson.Text(item, "subject") ?? "",
            RuleCallJson.Text(item, "machine") ?? "",
            RuleCallJson.Text(item, "repo") ?? "",
            RuleCallJson.Text(item, "decision") ?? "",
            decidingRuleId,
            overridden,
            matched,
            microseconds);
        return null;
    }

    /// <summary>A list of rule ids; empty when absent, null when it is there and is not a list of ids.</summary>
    private static IReadOnlyList<Guid>? Ids(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var array)) return Array.Empty<Guid>();
        if (array.ValueKind != JsonValueKind.Array) return null;
        var ids = new List<Guid>();
        foreach (var entry in array.EnumerateArray())
        {
            if (!Guid.TryParse(RuleCallJson.Scalar(entry), out var id)) return null;
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>The answer of POST /gateway/rules/hook-firings (wire contract W5).</summary>
    internal static object ProjectHookFiringsAnswer(HookFiringsAnswer answer) => new
    {
        accepted = answer.Accepted,
        duplicates = answer.Duplicates,
        refused = answer.Refused.Select(r => new { decisionId = r.DecisionId, reason = r.Reason }).ToList(),
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

