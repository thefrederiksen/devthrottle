using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Rules;

/// <summary>
/// The Gateway-owned store of standing instructions and the record of every time one fired (the Session
/// Rules mission, phase 1). Rules are durable and shared - the Cockpit, the phone and the evaluator all
/// read the same rules - so they live here, on the Gateway, in the EF data layer, tenant-scoped.
///
/// THIS IS THE FRONT DOOR. It is NOT the only door, and the difference is worth being exact about,
/// because this comment used to claim it was. It said nothing reached <c>session_rules</c> without passing
/// <see cref="RuleCallValidator"/>, and that was false: the entity, its setters, the DbSet and the context
/// factory are all public, so a caller could build a rule with an arbitrary call document, an arbitrary
/// tenant and a live state, add it and save it - meeting nothing in this file. An independent inspection
/// found the claim and the code disagreeing.
///
/// The claim was made TRUE rather than deleted. The validator, dry run and the tenant check now run in
/// <c>GatewayDbContext.SaveChanges</c>, which every route to the table ends at, so the structural boundary
/// exists where the claim always said it did. What this file adds on top of that gate is the plain-English
/// refusal a person reads: which check was named, which value was missing, and what the product ships.
///
/// DRY RUN IS ENFORCED, NOT DOCUMENTED. <see cref="Create"/> takes no state and always writes a dry-run
/// rule, so no caller can create a live one, and the write gate refuses a new rule that is not in dry run
/// however it was built. A person promotes it with <see cref="Promote"/>, which requires a
/// <see cref="RulePromotionGrant"/> - evidence, mintable only from an authenticated request, that names
/// the one rule it is for. And a firing recorded against a dry-run rule may not claim to have typed
/// anything, so "dry run types nothing" is a property of the writer rather than a promise about the
/// reader.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context.
/// </summary>
public sealed class SessionRuleStore : IRuleReading
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly RulePrimitiveRegistry _registry;

    private static readonly string DryRunValue = RuleWireNames.ToWireName(nameof(RuleState.DryRun));
    private static readonly string LiveValue = RuleWireNames.ToWireName(nameof(RuleState.Live));

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="registry">The verified checks a rule may name. Defaults to what the product ships.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public SessionRuleStore(GatewayDatabase db, RulePrimitiveRegistry? registry = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? RulePrimitiveRegistry.Default;
    }

    /// <summary>
    /// Store a new rule. It is ALWAYS created in dry run - there is no parameter that could make it live.
    /// </summary>
    /// <exception cref="RuleRejectedException">The instruction, the bounds or one of the calls is not
    /// something that can be stored; the reason says which and why.</exception>
    public SessionRule Create(
        string instruction,
        string screenDescription,
        IEnumerable<string> triggerWords,
        IEnumerable<RulePrimitiveCall> calls,
        // DELIBERATELY NULLABLE. A missing scope is a real thing that arrives at runtime - it is what
        // malformed or incomplete authoring output looks like - and the type says so, so the refusal below
        // is part of the contract rather than a guard against something the signature denies can happen.
        RuleScope? scope,
        int cooldownSeconds,
        int dailyCap,
        DateTime nowUtc,
        RuleChangeActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        FileLog.Write($"[SessionRuleStore] Create: instruction length={instruction?.Length ?? 0}, by={actor.ChangedBy}");

        var sentence = (instruction ?? "").Trim();
        var description = (screenDescription ?? "").Trim();
        var words = (triggerWords ?? Array.Empty<string>())
            .Select(w => (w ?? "").Trim())
            .Where(w => w.Length > 0)
            .ToList();
        var theCalls = (calls ?? Array.Empty<RulePrimitiveCall>()).ToList();

        // A MISSING SCOPE IS NOT "EVERY SESSION". Scope is a real safety bound, and turning an omission
        // into the WIDEST possible value is a fail-open: the contract could not tell "the account chose
        // all sessions" from "the authoring output left the field out". All sessions is still a scope a
        // rule can have - it is RuleScope.AllSessions, said out loud - but it has to be said.
        if (scope is null)
            throw new RuleRejectedException(
                "a rule has to say which sessions it may act on. Every session is a choice you can make, " +
                "but it is a choice - a rule that did not say is not read as meaning all of them.");
        var theScope = scope;
        var created = nowUtc.ToUniversalTime();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = new SessionRuleEntity
            {
                TenantId = ctx.ActiveTenant!,
                Instruction = sentence,
                ScreenDescription = description,
                TriggerWords = words,
                Calls = theCalls.Select(CopyOf).ToList(),
                ScopeAgent = Blank(theScope.Agent),
                ScopeRepository = Blank(theScope.Repository),
                ScopeMachine = Blank(theScope.Machine),
                ScopeMission = Blank(theScope.Mission),
                CooldownSeconds = cooldownSeconds,
                DailyCap = dailyCap,
                // ALWAYS dry run. There is deliberately no parameter for this.
                State = DryRunValue,
                CreatedUtc = created,
                UpdatedUtc = created,
            };
            // CHECKED BEFORE IT IS ADDED, from the one place that says what a rule is. The write gate in
            // the context checks the same thing on the way out, so a caller that went round this store
            // meets the same rules - but refusing here means nothing is ever attached to the context, and
            // the reason reads as a refusal about the call rather than about a row.
            try
            {
                SessionRuleRecordRules.CheckRule(entity, _registry);
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleStore] Create REFUSED: {ex.Reason}");
                throw;
            }

            ctx.SessionRules.Add(entity);
            var stored = ToRecord(entity);
            // THE HISTORY IS WRITTEN IN THE SAME SAVE, so a rule that exists and a history that does not say
            // who made it cannot both be true.
            ctx.SessionRuleChanges.Add(ChangeRow(ctx, entity.Id, RuleChangeActions.Create, actor, created, null, stored));
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] Create: stored rule {entity.Id} in dry run, by {actor.ChangedBy}");
            return stored;
        }
    }

    /// <summary>
    /// Change a rule's body: its instruction, screen description, trigger words, checks, scope and ceilings -
    /// the same fields a create takes, checked the same way.
    ///
    /// A CHANGED RULE GOES BACK TO DRY RUN. Dry run is what puts a person between a standing instruction and the
    /// first time it types anything, and a changed instruction is one nobody has yet watched behave - so the
    /// rule records what it would do again until it is promoted again. This is also what lets a change pass the
    /// write gate without a promotion grant: the gate refuses any save that leaves a rule live without one.
    /// </summary>
    /// <exception cref="RuleRejectedException">There is no such rule, the change changes nothing, or the
    /// changed rule is not something that can be stored; the reason says which.</exception>
    public SessionRule Change(
        Guid id,
        string instruction,
        string screenDescription,
        IEnumerable<string> triggerWords,
        IEnumerable<RulePrimitiveCall> calls,
        RuleScope? scope,
        int cooldownSeconds,
        int dailyCap,
        DateTime nowUtc,
        RuleChangeActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        FileLog.Write($"[SessionRuleStore] Change: rule={id}, by={actor.ChangedBy}");

        // The same refusal a create gives, for the same reason: an omitted scope is not "every session".
        if (scope is null)
            throw new RuleRejectedException(
                "a rule has to say which sessions it may act on. Every session is a choice you can make, " +
                "but it is a choice - a rule that did not say is not read as meaning all of them.");
        var changed = nowUtc.ToUniversalTime();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.FirstOrDefault(r => r.Id == id)
                ?? throw new RuleRejectedException($"there is no rule with the id {id}, so there is nothing to change.");
            var before = ToRecord(entity);

            entity.Instruction = (instruction ?? "").Trim();
            entity.ScreenDescription = (screenDescription ?? "").Trim();
            entity.TriggerWords = (triggerWords ?? Array.Empty<string>())
                .Select(w => (w ?? "").Trim())
                .Where(w => w.Length > 0)
                .ToList();
            entity.Calls = (calls ?? Array.Empty<RulePrimitiveCall>()).Select(CopyOf).ToList();
            entity.ScopeAgent = Blank(scope.Agent);
            entity.ScopeRepository = Blank(scope.Repository);
            entity.ScopeMachine = Blank(scope.Machine);
            entity.ScopeMission = Blank(scope.Mission);
            entity.CooldownSeconds = cooldownSeconds;
            entity.DailyCap = dailyCap;

            // A CHANGE THAT CHANGES NOTHING IS REFUSED, not recorded: a history row saying a rule was changed
            // when it was not would be a false record, and putting a live rule back in dry run for nothing would
            // be a quiet loss of what a person agreed to.
            if (SessionRuleBody.Of(ToRecord(entity)).ToJsonString() == SessionRuleBody.Of(before).ToJsonString())
                throw new RuleRejectedException(
                    $"this change leaves the rule {id} exactly as it was, so nothing was changed. Name at least " +
                    "one field with a new value.");

            try
            {
                SessionRuleRecordRules.CheckRule(entity, _registry);
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleStore] Change REFUSED: {ex.Reason}");
                throw;
            }

            entity.State = DryRunValue;
            entity.PromotedBy = "";
            entity.UpdatedUtc = changed;
            var after = ToRecord(entity);
            ctx.SessionRuleChanges.Add(ChangeRow(ctx, id, RuleChangeActions.Change, actor, changed, before, after));
            ctx.SaveChanges();
            FileLog.Write(
                $"[SessionRuleStore] Change: rule {id} changed by {actor.ChangedBy}; it is in dry run " +
                $"(was {RuleWireNames.ToWireName(before.State.ToString())})");
            return after;
        }
    }

    /// <summary>The rule with this id, or null when there is none.</summary>
    public SessionRule? Get(Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.AsNoTracking().FirstOrDefault(r => r.Id == id);
            return entity is null ? null : ToRecord(entity);
        }
    }

    /// <summary>Every rule the account has, newest first.</summary>
    public IReadOnlyList<SessionRule> All()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionRules.AsNoTracking()
                .OrderByDescending(r => r.CreatedUtc)
                .ToList()
                .Select(ToRecord)
                .ToList();
        }
    }

    /// <summary>Move a rule out of dry run and into live. A rule that is already live is returned
    /// unchanged. Only a person does this - a rule can never promote itself (owner ruling 14, bound 6) -
    /// so this takes a <see cref="RulePromotionGrant"/>, which can only be minted from an authenticated
    /// inbound request and names the one rule it is evidence for.</summary>
    /// <exception cref="RuleRejectedException">There is no such rule, or the call carried no evidence that
    /// a person asked, or the grant was obtained for a different rule.</exception>
    public SessionRule Promote(Guid id, RulePromotionGrant grant, DateTime nowUtc, RuleChangeActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (grant is null)
            throw new RuleRejectedException(
                "a rule is moved out of dry run by a person, and this call carried no evidence that a " +
                "person asked. Nothing that runs on its own can promote a rule.");

        if (grant.RuleId != id)
            throw new RuleRejectedException(
                $"this evidence was given for the rule {grant.RuleId}, not for {id}. A person agrees to " +
                "one rule going live, not to whichever rule is asked for next.");

        // SPENT HERE, ONCE. A person agreed to one rule going live on one occasion; evidence that could be
        // presented twice is evidence that could be captured and replayed.
        if (!grant.TryConsume())
            throw new RuleRejectedException(
                "this evidence has already been used to promote a rule. A person agrees once, to one rule, " +
                "and the same agreement cannot be presented again.");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.FirstOrDefault(r => r.Id == id)
                ?? throw new RuleRejectedException($"there is no rule with the id {id}.");

            // Already live: nothing changes, so nothing is recorded.
            if (string.Equals(entity.State, LiveValue, StringComparison.Ordinal))
                return ToRecord(entity);

            var before = ToRecord(entity);
            entity.State = LiveValue;
            entity.PromotedBy = grant.Actor;
            entity.UpdatedUtc = nowUtc.ToUniversalTime();
            var after = ToRecord(entity);
            ctx.SessionRuleChanges.Add(
                ChangeRow(ctx, id, RuleChangeActions.Promote, actor, entity.UpdatedUtc, before, after));
            // Tell the write gate that THIS context carries a promotion a person asked for. Without it the
            // gate refuses a rule moving to live, which is what closes the route straight through the
            // DbSet as well - see GatewayDbContext.
            ctx.PromotionInEffect = id;
            try { ctx.SaveChanges(); }
            finally { ctx.PromotionInEffect = null; }
            FileLog.Write($"[SessionRuleStore] Promote: rule {id} is now live, asked for by {grant.Actor}");
            return after;
        }
    }

    /// <summary>Delete a rule. Its firings and its change history are left alone - the record outlives the
    /// rule.</summary>
    public bool Delete(Guid id, DateTime nowUtc, RuleChangeActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.FirstOrDefault(r => r.Id == id);
            if (entity is null) return false;
            var before = ToRecord(entity);
            ctx.SessionRules.Remove(entity);
            ctx.SessionRuleChanges.Add(
                ChangeRow(ctx, id, RuleChangeActions.Delete, actor, nowUtc.ToUniversalTime(), before, null));
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] Delete: rule {id} removed by {actor.ChangedBy}; its firings are kept");
            return true;
        }
    }

    /// <summary>
    /// The change history, newest first: every create, change, promote and delete, with who made it. Each filter
    /// narrows; null means any.
    /// </summary>
    /// <param name="ruleId">Only changes to this rule - including one that has since been deleted.</param>
    /// <param name="sessionId">Only changes made by this session.</param>
    /// <param name="action">Only this action (see <see cref="RuleChangeActions"/>).</param>
    /// <param name="limit">The most rows to return, at least one.</param>
    /// <exception cref="RuleRejectedException">The action is not one a change can be, or the limit is below one.</exception>
    public IReadOnlyList<SessionRuleChange> History(Guid? ruleId, string? sessionId, string? action, int limit)
    {
        if (action is not null && !RuleChangeActions.All.Contains(action, StringComparer.Ordinal))
            throw new RuleRejectedException(
                $"'{action}' is not a change a rule can have. Changes are: {string.Join(", ", RuleChangeActions.All)}.");
        if (limit < 1)
            throw new RuleRejectedException($"the number of changes to show has to be at least 1, and was {limit}.");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rows = ctx.SessionRuleChanges.AsNoTracking();
            if (ruleId is { } rule) rows = rows.Where(c => c.RuleId == rule);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var session = sessionId.Trim();
                rows = rows.Where(c => c.SessionId == session);
            }
            if (action is not null) rows = rows.Where(c => c.Action == action);
            return rows
                .OrderByDescending(c => c.OccurredUtc)
                .Take(limit)
                .ToList()
                .Select(c => new SessionRuleChange(
                    c.Id, c.RuleId, c.Action, c.ChangedBy, c.SessionId, c.Agent, c.OccurredUtc, c.Before, c.After))
                .ToList();
        }
    }

    /// <summary>One history row for a change about to be saved. Before and after are the rule's snapshot, or
    /// empty for the side that does not exist.</summary>
    private static SessionRuleChangeEntity ChangeRow(
        GatewayDbContext ctx, Guid ruleId, string action, RuleChangeActor actor, DateTime occurredUtc,
        SessionRule? before, SessionRule? after) => new()
    {
        TenantId = ctx.ActiveTenant!,
        RuleId = ruleId,
        Action = action,
        ChangedBy = (actor.ChangedBy ?? "").Trim(),
        SessionId = string.IsNullOrWhiteSpace(actor.SessionId) ? null : actor.SessionId.Trim(),
        Agent = string.IsNullOrWhiteSpace(actor.Agent) ? null : actor.Agent.Trim(),
        OccurredUtc = occurredUtc,
        Before = before is null ? "" : SessionRuleBody.Snapshot(before),
        After = after is null ? "" : SessionRuleBody.Snapshot(after),
    };

    /// <summary>
    /// Record one firing. A firing against a rule in DRY RUN may not claim to have typed anything; that is
    /// refused with a reason rather than silently blanked, because a store that quietly edits what it was
    /// told is a store nobody can read as evidence.
    /// </summary>
    /// <exception cref="RuleRejectedException">There is no such rule, a dry-run rule was recorded as
    /// having typed something, or the record is not a record of anything.</exception>
    public SessionRuleFiring RecordFiring(
        Guid ruleId,
        string sessionId,
        string screenText,
        string understanding,
        string decision,
        string reason,
        IEnumerable<RulePrimitiveRun> primitiveRuns,
        string typedText,
        string outcome,
        string grounding,
        DateTime nowUtc)
    {
        var typed = typedText ?? "";
        var runs = (primitiveRuns ?? Array.Empty<RulePrimitiveRun>()).ToList();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var state = ctx.SessionRules.AsNoTracking()
                .Where(r => r.Id == ruleId)
                .Select(r => r.State)
                .FirstOrDefault();

            var entity = new SessionRuleFiringEntity
            {
                TenantId = ctx.ActiveTenant!,
                RuleId = ruleId,
                SessionId = (sessionId ?? "").Trim(),
                OccurredUtc = nowUtc.ToUniversalTime(),
                // The screen and the understanding are the two parts that CAN legitimately be empty: a
                // terminal really can be blank, and a reply that was refused really did give no
                // understanding. Every other part of the record is required by the shared rules below.
                ScreenText = screenText ?? "",
                Understanding = understanding ?? "",
                Decision = (decision ?? "").Trim(),
                Reason = (reason ?? "").Trim(),
                PrimitiveRuns = runs
                    .Select(r => r is null
                        ? null!
                        : new RulePrimitiveRunEntity { Name = r.Name, Arguments = r.Arguments, Answer = r.Answer })
                    .ToList(),
                TypedText = typed,
                Outcome = (outcome ?? "").Trim(),
                Grounding = (grounding ?? "").Trim(),
            };

            // THE RECORD IS THE PRODUCT (owner ruling 14), so a record of nothing is refused rather than
            // written with its missing parts turned into empty strings - from the one place that says what
            // a firing is, which the write gate in the context asks as well.
            try
            {
                SessionRuleRecordRules.CheckFiring(entity, _registry, state, DryRunValue);
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleStore] RecordFiring REFUSED: {ex.Reason}");
                throw;
            }

            ctx.SessionRuleFirings.Add(entity);
            ctx.SaveChanges();
            FileLog.Write(
                $"[SessionRuleStore] RecordFiring: rule={ruleId} session={entity.SessionId} " +
                $"decision={entity.Decision} typed={(typed.Length > 0 ? "yes" : "no")}");
            return ToRecord(entity);
        }
    }

    /// <summary>
    /// Say what became of a firing that was written down BEFORE its keystroke went out.
    ///
    /// THE RECORD IS WRITTEN FIRST AND RECONCILED AFTER, and that ordering is the point of this method
    /// existing at all. The evaluator used to type and then record, so a store refusal, a database error,
    /// or a rule deleted during the model call each produced an action against somebody's session that
    /// nothing durable accounted for. The record is the product; an action nobody can reconstruct is an
    /// action nobody can supervise.
    ///
    /// It changes only what became of the send - never the screen, the decision or the reason, which were
    /// settled before the keystroke and must read the same afterwards.
    /// </summary>
    /// <exception cref="RuleRejectedException">There is no such firing, the outcome says nothing, or the
    /// rule is in dry run and this says it typed something.</exception>
    public SessionRuleFiring CompleteFiring(Guid firingId, string typedText, string outcome, DateTime nowUtc)
    {
        var typed = typedText ?? "";
        RequireSomething(outcome, "what happened next");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRuleFirings.FirstOrDefault(f => f.Id == firingId)
                ?? throw new RuleRejectedException(
                    $"there is no firing with the id {firingId}, so there is nothing to say happened.");

            var rule = ctx.SessionRules.AsNoTracking().FirstOrDefault(r => r.Id == entity.RuleId);
            if (rule is not null && typed.Length > 0
                && string.Equals(rule.State, DryRunValue, StringComparison.Ordinal))
                throw new RuleRejectedException(
                    "this rule is in dry run, so it types nothing - a firing cannot be completed as having " +
                    "typed '" + typed + "'.");

            entity.TypedText = typed;
            entity.Outcome = outcome.Trim();
            ctx.SaveChanges();
            FileLog.Write(
                $"[SessionRuleStore] CompleteFiring: firing={firingId} typed={(typed.Length > 0 ? "yes" : "no")}");
            return ToRecord(entity);
        }
    }

    /// <summary>Every firing of one rule, newest first.</summary>
    public IReadOnlyList<SessionRuleFiring> FiringsFor(Guid ruleId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionRuleFirings.AsNoTracking()
                .Where(f => f.RuleId == ruleId)
                .OrderByDescending(f => f.OccurredUtc)
                .ToList()
                .Select(ToRecord)
                .ToList();
        }
    }

    /// <summary>Refuse a required part of the record that is missing, saying what it was for.</summary>
    private static void RequireSomething(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new RuleRejectedException(
                "the record is the product, so a firing has to say " + what + ".");
    }

    /// <summary>An empty or all-whitespace scope part means "any", which is stored as nothing at all.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A copy of a call, so the stored rule cannot be changed under us by whoever passed it in.</summary>
    private static RulePrimitiveCall CopyOf(RulePrimitiveCall call) => new()
    {
        Name = call.Name,
        Arguments = call.Arguments
            .Select(a => new RuleArgument
            {
                Parameter = a.Parameter,
                Source = a.Source,
                Values = a.Values.ToList(),
            })
            .ToList(),
    };

    private static SessionRule ToRecord(SessionRuleEntity e) => new(
        e.Id,
        e.Instruction,
        e.ScreenDescription,
        e.TriggerWords.ToList(),
        e.Calls.Select(CopyOf).ToList(),
        new RuleScope(e.ScopeAgent, e.ScopeRepository, e.ScopeMachine, e.ScopeMission),
        e.CooldownSeconds,
        e.DailyCap,
        StateOf(e.State),
        e.PromotedBy,
        e.CreatedUtc,
        e.UpdatedUtc);

    private static SessionRuleFiring ToRecord(SessionRuleFiringEntity e) => new(
        e.Id,
        e.RuleId,
        e.SessionId,
        e.OccurredUtc,
        e.ScreenText,
        e.Understanding,
        e.Decision,
        e.Reason,
        e.PrimitiveRuns.Select(r => new RulePrimitiveRun(r.Name, r.Arguments, r.Answer)).ToList(),
        e.TypedText,
        e.Outcome,
        e.Grounding);

    /// <summary>The stored state value as a state. An unrecognised value fails loudly rather than being
    /// treated as dry run - a rule whose state we cannot read must not be quietly assumed harmless, and
    /// must not be quietly assumed live either.</summary>
    private static RuleState StateOf(string stored)
    {
        if (string.Equals(stored, DryRunValue, StringComparison.Ordinal)) return RuleState.DryRun;
        if (string.Equals(stored, LiveValue, StringComparison.Ordinal)) return RuleState.Live;
        throw new InvalidOperationException(
            $"stored rule state '{stored}' is not one this build knows. Known states: {DryRunValue}, {LiveValue}.");
    }
}
