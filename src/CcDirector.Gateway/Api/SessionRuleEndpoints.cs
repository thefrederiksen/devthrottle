using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The rule surface: read the standing instructions, write one, promote one out of dry run, delete one, and
/// read a rule's firing record. Device-authed client routes under the non-public "/gateway/..." prefix, so
/// the host-wide device-key middleware gates them exactly like every other client data endpoint.
///
/// THIS IS NOT THE AUTHORING CONVERSATION. Phase 3 builds the part where a person says a sentence and a
/// model turns it into a rule. These routes carry an ALREADY-BUILT rule, so that the phase 2 slice can put a
/// real rule into the real store from outside the process and then be watched acting on it. Every write goes
/// through <see cref="SessionRuleStore"/>, which is the gate: a rule naming a check we do not ship, or
/// supplying the wrong arguments to one we do, is refused here with the store's own stated reason.
///
/// A REFUSAL COMES BACK AS A REFUSAL. The store's reason is returned verbatim with a 400, never flattened
/// into a generic failure - a caller that cannot see why its rule was rejected will guess, and guessing is
/// how a rule ends up subtly different from the sentence that was meant.
///
/// TWO THINGS HAVE TO BE SAID OUT LOUD ON THESE ROUTES, and both used to be inferred. A write says which
/// sessions the rule may act on - "all-sessions" is a choice a caller can make, but an absent scope is no
/// longer read as meaning all of them. And a promotion says who is asking and what they are agreeing to;
/// an empty POST to the promote route now promotes nothing.
///
/// AGENTS MANAGE RULES (Session Rules part 2, phase 2, owner decision D7: "Any agent should be able to change
/// and set up the rules"). A session key may call every route here - <c>SessionKeyGuard.IsRuleRoute</c> - and
/// there is a CHANGE route, <c>PATCH /gateway/rules/{id}</c>, which carries any field of the rule's body. Every
/// write is recorded in the change history with who made it, read from the request and never from the body:
/// the calling session's id and the agent it runs, or the device or person. <c>GET /gateway/rules/history</c>
/// reads it back.
/// </summary>
[Rules.RuleFeature]
internal static class SessionRuleEndpoints
{
    /// <summary>How many changes the history answers with when the caller does not say.</summary>
    internal const int DefaultHistoryLimit = 50;

    /// <summary>The most changes one read of the history answers with.</summary>
    internal const int MaxHistoryLimit = 500;

    /// <param name="app">The route table.</param>
    /// <param name="store">The rule store every write goes through.</param>
    /// <param name="agentOf">The agent a calling session runs, read from the live roster, or null when the
    /// session is not on it. Only ever asked about the session whose key the middleware verified.</param>
    public static void Map(IEndpointRouteBuilder app, SessionRuleStore store, Func<SessionCredentialIdentity, string?> agentOf)
    {
        ArgumentNullException.ThrowIfNull(agentOf);

        // Every rule this account has, newest first - the account's own sentences.
        app.MapGet("/gateway/rules", () => Results.Json(new { rules = store.All().Select(SessionRuleWire.Project).ToList() }));

        // THE CHANGE HISTORY, newest first: who created, changed, promoted and deleted which rule, and what it was
        // before and after. Each filter narrows, and an unreadable one is refused rather than ignored - an
        // ignored filter answers a narrower question with the whole account's history.
        app.MapGet("/gateway/rules/history", (string? rule, string? session, string? action, int? limit) =>
        {
            Guid? ruleId = null;
            if (!string.IsNullOrWhiteSpace(rule))
            {
                if (!Guid.TryParse(rule.Trim(), out var parsed))
                    return Refused($"'{rule}' is not a rule id. A rule id is the id 'rules list' shows.");
                ruleId = parsed;
            }
            var take = limit ?? DefaultHistoryLimit;
            if (take > MaxHistoryLimit)
                return Refused($"at most {MaxHistoryLimit} changes are answered at once, and {take} were asked for.");
            try
            {
                var changes = store.History(ruleId, session, string.IsNullOrWhiteSpace(action) ? null : action.Trim(), take);
                return Results.Json(new { count = changes.Count, changes = changes.Select(SessionRuleWire.Project).ToList() });
            }
            catch (RuleRejectedException ex)
            {
                return Refused(ex.Reason);
            }
        });

        // One rule.
        app.MapGet("/gateway/rules/{id:guid}", (Guid id) =>
        {
            var rule = store.Get(id);
            return rule is null
                ? NoSuchRule(id)
                : Results.Json(new { rule = SessionRuleWire.Project(rule) });
        });

        // Write a rule. It is ALWAYS created in dry run - the store takes no state parameter at all. A body with
        // no kind is a screen rule, read exactly as it always was; "kind": "hook" is a hook rule.
        app.MapPost("/gateway/rules", (JsonElement body, HttpContext http) =>
        {
            try
            {
                var actor = ActorOf(http, agentOf);
                var rule = SessionRuleWire.CreateFromBody(store, body, DateTime.UtcNow, actor);
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules: stored {rule.Kind} rule {rule.Id} in dry run, by {actor.ChangedBy}");
                return Results.Json(new { rule = SessionRuleWire.Project(rule) });
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules REFUSED: {ex.Reason}");
                return Refused(ex.Reason);
            }
        });

        // CHANGE a rule: any field of its body, the rest kept. The rule goes back to dry run - see
        // SessionRuleStore.Change - and the answer says so, because the caller may have just promoted it. The caller's
        // fields are laid over the rule INSIDE the store's lock, over the rule as it is stored at that moment, so two
        // sessions changing different fields both keep their change. The read above only answers 404 early.
        app.MapPatch("/gateway/rules/{id:guid}", (Guid id, JsonElement body, HttpContext http) =>
        {
            var current = store.Get(id);
            if (current is null)
            {
                FileLog.Write($"[SessionRuleEndpoints] PATCH /gateway/rules/{id} REFUSED: no such rule");
                return NoSuchRule(id);
            }
            try
            {
                var actor = ActorOf(http, agentOf);
                var changes = body.Clone();
                var rule = store.Change(
                    id, stored => SessionRuleWire.ReadChange(stored, changes), DateTime.UtcNow, actor, out var stateBefore);
                FileLog.Write($"[SessionRuleEndpoints] PATCH /gateway/rules/{id}: changed by {actor.ChangedBy}");
                return Results.Json(new { rule = SessionRuleWire.Project(rule), wasLive = stateBefore == RuleState.Live });
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] PATCH /gateway/rules/{id} REFUSED: {ex.Reason}");
                return Refused(ex.Reason);
            }
        });

        // Move a rule out of dry run. Only an AUTHENTICATED REQUEST does this, and this route is the only place
        // in the whole Gateway that can obtain the evidence: the grant's factory is internal, takes THE REQUEST
        // rather than an identity somebody typed, and a structural test over the built assembly asserts that no
        // other type reaches it or reaches Promote. The evaluator has no inbound request, so there is nothing
        // it could pass - see RulePromotionGrant, which states exactly what that does and does not enforce. An
        // agent session may promote since owner decision D7, and is named and recorded when it does.
        app.MapPost("/gateway/rules/{id:guid}/promote", (Guid id, HttpContext http, JsonElement body) =>
        {
            // No such rule is a 404 here as on every other rule route, not a refusal of the request.
            if (store.Get(id) is null) return NoSuchRule(id);
            try
            {
                var grant = RulePromotionGrant.FromAuthenticatedRequest(
                    id, http, RuleCallJson.Text(body, "acknowledgement"), DateTime.UtcNow);
                return Results.Json(new { rule = SessionRuleWire.Project(store.Promote(id, grant, DateTime.UtcNow, ActorOf(http, agentOf))) });
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] promote {id} REFUSED: {ex.Reason}");
                return Refused(ex.Reason);
            }
        });

        // Delete a rule. Its firings and its change history are left alone - the record outlives the rule.
        app.MapDelete("/gateway/rules/{id:guid}", (Guid id, HttpContext http) =>
            Results.Json(new { deleted = store.Delete(id, DateTime.UtcNow, ActorOf(http, agentOf)) }));

        // WIRE CONTRACT W1: the hook rules that reach one machine and one repository, each with its rank. The
        // Director asks with its own credential to keep each session's rules file current; the hook decides from
        // that file alone, so nothing on the decision path ever calls here.
        app.MapGet("/gateway/rules/effective", (string? kind, string? machine, string? repo) =>
        {
            FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/effective: kind={kind} machine={machine} repo={repo}");
            try
            {
                var (theMachine, theRepo) = SessionRuleWire.ReadEffectiveQuery(kind, machine, repo);
                var effective = store.EffectiveHookRules(theMachine, theRepo);
                return Results.Json(SessionRuleWire.ProjectEffective(theMachine, theRepo, effective));
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/effective REFUSED: {ex.Reason}");
                return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // WIRE CONTRACT W5: decisions the hook made, forwarded by the Director. Each accepted decision becomes a
        // firing on its deciding rule; a decision already recorded is a duplicate, so a retried post is harmless.
        // A decision that cannot be read or recorded is refused BY ITS ID with the reason, and the rest are
        // still recorded - the answer is 200 whenever the body was a list of decisions at all.
        app.MapPost("/gateway/rules/hook-firings", (JsonElement body) =>
        {
            try
            {
                var unreadable = new List<HookFiringRefusal>();
                var decisions = SessionRuleWire.ReadHookDecisions(body, unreadable);
                var recorded = store.RecordHookFirings(decisions);
                var answer = recorded with { Refused = unreadable.Concat(recorded.Refused).ToList() };
                FileLog.Write(
                    $"[SessionRuleEndpoints] POST /gateway/rules/hook-firings: accepted={answer.Accepted} " +
                    $"duplicates={answer.Duplicates} refused={answer.Refused.Count}");
                return Results.Json(SessionRuleWire.ProjectHookFiringsAnswer(answer));
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] POST /gateway/rules/hook-firings REFUSED: {ex.Reason}");
                return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // THE RECORD, which is the product: every firing of one rule, newest first.
        app.MapGet("/gateway/rules/{id:guid}/firings", (Guid id) =>
            Results.Json(new { firings = store.FiringsFor(id).Select(SessionRuleWire.Project).ToList() }));

        // THE RULES PAGE (Session Rules phase 3, wire contracts P1-P3). Three READ-ONLY folds over the store, for
        // people: every label, count, tab and verdict is worked out in RulesPageFold and sent finished (ruling T1).
        // A session key does not reach them (ruling T2) - agents read everything through 'cc-devthrottle rules'.
        // The day is the viewer's (T3): the client sends its time zone, and a missing or unknown one is refused.
        app.MapGet("/gateway/rules/page", (string? tz) =>
        {
            FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/page: tz={tz}");
            try
            {
                var zone = RulesPageFold.ReadZone(tz);
                return Results.Json(RulesPageFold.Page(SessionRuleWire.ReadPageSource(store), zone, DateTime.UtcNow));
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/page REFUSED: {ex.Reason}");
                return Refused(ex.Reason);
            }
        });

        app.MapGet("/gateway/rules/{id:guid}/page", (Guid id, string? tz) =>
        {
            FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/{id}/page: tz={tz}");
            try
            {
                var zone = RulesPageFold.ReadZone(tz);
                var page = RulesPageFold.RulePageFor(SessionRuleWire.ReadPageSource(store), id, zone, DateTime.UtcNow);
                return page is null ? NoSuchRule(id) : Results.Json(page);
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/{id}/page REFUSED: {ex.Reason}");
                return Refused(ex.Reason);
            }
        });

        app.MapGet("/gateway/rules/preview", (string? machine, string? repo, string? command) =>
        {
            FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/preview: machine={machine} repo={repo}");
            try
            {
                return Results.Json(RulesPageFold.Preview(SessionRuleWire.ReadPageSource(store), machine, repo, command));
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleEndpoints] GET /gateway/rules/preview REFUSED: {ex.Reason}");
                return Refused(ex.Reason);
            }
        });
    }

    /// <summary>
    /// WHO IS MAKING THIS CHANGE, read from what the request pipeline verified and never from the body: the same
    /// name a promotion grant carries, plus - for a session key - the session's id and the agent the live roster
    /// says it runs. A caller the pipeline named nobody (only possible on a Gateway whose sign-in check is off)
    /// is recorded as exactly that, rather than refused, because such a Gateway serves its one owner.
    /// </summary>
    internal static RuleChangeActor ActorOf(HttpContext http, Func<SessionCredentialIdentity, string?> agentOf)
    {
        // A caller the pipeline AUTHENTICATED but could not name is the shared machine token, which a self-host
        // Gateway accepts and which belongs to no device and no person. Said as exactly that; only a request that
        // carried no checked credential at all - a Gateway with its sign-in check off - is "unnamed".
        var name = RulePromotionGrant.CallerOf(http)
            ?? (http.Items.ContainsKey(AuthMiddleware.AuthenticatedCredentialItemKey) ? MachineTokenCaller : UnnamedCaller);
        var session = AuthMiddleware.CallingSession(http);
        if (session is null) return new RuleChangeActor(name, null, null);
        return new RuleChangeActor(name, session.SessionId.ToString(), agentOf(session) ?? AgentNotOnRoster);
    }

    /// <summary>What the history says when the request was authenticated by the shared machine token.</summary>
    internal const string MachineTokenCaller = "machine-token (the Gateway's shared token, which names no one)";

    /// <summary>What the history says when the request carried no credential the Gateway checked.</summary>
    internal const string UnnamedCaller = "unnamed (no credential was checked on this request)";

    /// <summary>What the history says when the calling session is not on the live roster, so its agent is not
    /// known. Said, not guessed.</summary>
    internal const string AgentNotOnRoster = "unknown (the session was not on the live roster)";

    private static IResult Refused(string reason) =>
        Results.Json(new { error = reason }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult NoSuchRule(Guid id) =>
        Results.Json(new { error = $"there is no rule with the id {id}." }, statusCode: StatusCodes.Status404NotFound);
}
