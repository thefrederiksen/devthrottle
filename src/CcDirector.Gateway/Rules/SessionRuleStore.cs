using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
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
        // Where the rule applies (ruling P4). Absent means everywhere, which is what every screen rule was
        // before this existed, so a create body written the September way keeps working unchanged. A screen
        // rule may only apply everywhere in phase 1; the write gate refuses anything narrower.
        RuleAppliesTo? appliesTo = null)
    {
        FileLog.Write($"[SessionRuleStore] Create: instruction length={instruction?.Length ?? 0}");

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
                Kind = RuleKinds.Screen,
            };
            SetAppliesTo(entity, appliesTo ?? RuleAppliesTo.Everywhere);
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
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] Create: stored rule {entity.Id} in dry run");
            return ToRecord(entity);
        }
    }

    /// <summary>
    /// Store a new HOOK rule (Tech Lead rulings P1, P2, P4 and P7). Like every rule it is ALWAYS created in dry
    /// run. It says where it applies - that has to be said, for the same reason a screen rule's scope has to be
    /// - and its repository may be pasted in any form of the remote, because it is stored in its one spelling.
    /// </summary>
    /// <exception cref="RuleRejectedException">Where it applies is missing or not one of the four shapes, or
    /// the event, tool, handler, match text or result is not one phase 1 accepts; the reason says which.</exception>
    public SessionRule CreateHook(
        string instruction,
        RuleAppliesTo? appliesTo,
        string hookEvent,
        string tool,
        string handler,
        IEnumerable<string> match,
        string result,
        DateTime nowUtc)
    {
        FileLog.Write(
            $"[SessionRuleStore] CreateHook: event={hookEvent} tool={tool} handler={handler} " +
            $"appliesTo={appliesTo?.Kind ?? "(none)"}");

        // A MISSING PLACE IS NOT "EVERYWHERE". Everywhere is a choice a hook rule can make, said out loud; a
        // rule that did not say is not read as meaning the widest place there is.
        if (appliesTo is null)
        {
            const string reason =
                "a hook rule has to say where it applies - everywhere, on some machines, in a repository, or in a " +
                "repository on some machines. Everywhere is a choice you can make, but it has to be said.";
            FileLog.Write($"[SessionRuleStore] CreateHook REFUSED: {reason}");
            throw new RuleRejectedException(reason);
        }

        var created = nowUtc.ToUniversalTime();
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = new SessionRuleEntity
            {
                TenantId = ctx.ActiveTenant!,
                Kind = RuleKinds.Hook,
                Instruction = (instruction ?? "").Trim(),
                HookEvent = (hookEvent ?? "").Trim(),
                HookTool = (tool ?? "").Trim(),
                HookHandler = (handler ?? "").Trim(),
                // Literal text, kept EXACTLY as written (P1): a space at the edge is part of what is matched.
                HookMatch = (match ?? Array.Empty<string>()).Select(m => m ?? "").ToList(),
                HookResult = (result ?? "").Trim(),
                // ALWAYS dry run. There is deliberately no parameter for this.
                State = DryRunValue,
                CreatedUtc = created,
                UpdatedUtc = created,
            };
            SetAppliesTo(entity, appliesTo);

            try
            {
                SessionRuleRecordRules.CheckRule(entity, _registry);
            }
            catch (RuleRejectedException ex)
            {
                FileLog.Write($"[SessionRuleStore] CreateHook REFUSED: {ex.Reason}");
                throw;
            }

            ctx.SessionRules.Add(entity);
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] CreateHook: stored hook rule {entity.Id} in dry run");
            return ToRecord(entity);
        }
    }

    /// <summary>
    /// The hook rules that reach a session on <paramref name="machine"/> in <paramref name="repo"/>, each with
    /// how specific it is (wire contract W1). Screen rules are never part of it. The repository may be any form
    /// of the remote, or empty for a checkout with no remote, which only everywhere and machine rules reach.
    /// </summary>
    /// <exception cref="RuleRejectedException">No machine was named.</exception>
    public IReadOnlyList<EffectiveRule> EffectiveHookRules(string machine, string repo)
    {
        var theMachine = (machine ?? "").Trim();
        if (theMachine.Length == 0)
            throw new RuleRejectedException(
                "the effective rules are worked out for one machine, and no machine was named.");
        var theRepo = GitRemoteIdentity.Normalise(repo);

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var effective = ctx.SessionRules.AsNoTracking()
                .Where(r => r.Kind == RuleKinds.Hook)
                .OrderByDescending(r => r.CreatedUtc)
                .ToList()
                .Select(ToRecord)
                .Where(r => r.AppliesTo.Reaches(theMachine, theRepo))
                .Select(r => new EffectiveRule(r, r.AppliesTo.Rank))
                .OrderByDescending(e => e.Rank)
                .ToList();
            FileLog.Write(
                $"[SessionRuleStore] EffectiveHookRules: machine={theMachine} repo={theRepo} rules={effective.Count}");
            return effective;
        }
    }

    /// <summary>
    /// Record the decisions a hook made, as the Director forwards them (wire contract W5). Each accepted
    /// decision becomes one firing on its deciding rule. A decision already recorded - the same decision id in
    /// this account - is a DUPLICATE, not a second firing, so a retried post is harmless. A decision that names
    /// no rule, a rule that is not a hook rule, or is not a record of anything, is refused with the reason.
    /// Each decision is written on its own, so one refusal never costs the others.
    /// </summary>
    public HookFiringsAnswer RecordHookFirings(IEnumerable<HookDecision> decisions, DateTime nowUtc)
    {
        var all = (decisions ?? Array.Empty<HookDecision>()).ToList();
        FileLog.Write($"[SessionRuleStore] RecordHookFirings: {all.Count} decision(s)");

        var accepted = 0;
        var duplicates = 0;
        var refused = new List<HookFiringRefusal>();

        lock (_gate)
        {
            foreach (var decision in all)
            {
                var decisionId = decision.DecisionId ?? "";
                if (decision.Version != 1)
                {
                    Refuse(decisionId, $"this Gateway reads decision version 1, and this one is version {decision.Version}.");
                    continue;
                }

                using var ctx = _db.CreateContext();
                if (!string.IsNullOrWhiteSpace(decisionId)
                    && ctx.SessionRuleFirings.AsNoTracking().Any(f => f.DecisionId == decisionId))
                {
                    duplicates++;
                    continue;
                }

                var rule = ctx.SessionRules.AsNoTracking()
                    .Where(r => r.Id == decision.DecidingRuleId)
                    .Select(r => new { r.State, r.Kind, r.Instruction })
                    .FirstOrDefault();

                var entity = new SessionRuleFiringEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    Kind = RuleKinds.Hook,
                    RuleId = decision.DecidingRuleId,
                    DecisionId = decisionId,
                    SessionId = (decision.SessionId ?? "").Trim(),
                    OccurredUtc = decision.OccurredUtc.ToUniversalTime(),
                    Decision = (decision.Decision ?? "").Trim(),
                    // A hook rule's reason is its own sentence (wire contract W2).
                    Reason = rule?.Instruction ?? "",
                    Outcome = "dry run: recorded what this rule would have decided; the command was not blocked.",
                    HookEvent = (decision.Event ?? "").Trim(),
                    HookTool = (decision.Tool ?? "").Trim(),
                    Subject = decision.Subject ?? "",
                    Machine = (decision.Machine ?? "").Trim(),
                    Repo = (decision.Repo ?? "").Trim(),
                    Agent = (decision.Agent ?? "").Trim(),
                    AgentEvent = (decision.AgentEvent ?? "").Trim(),
                    AgentTool = (decision.AgentTool ?? "").Trim(),
                    OverriddenRuleIds = (decision.OverriddenRuleIds ?? Array.Empty<Guid>()).ToList(),
                    MatchedRuleIds = (decision.MatchedRuleIds ?? Array.Empty<Guid>()).ToList(),
                    EvaluationMicroseconds = decision.EvaluationMicroseconds,
                };

                try
                {
                    SessionRuleRecordRules.CheckFiring(entity, _registry, rule?.State, rule?.Kind, DryRunValue);
                }
                catch (RuleRejectedException ex)
                {
                    Refuse(decisionId, ex.Reason);
                    continue;
                }

                ctx.SessionRuleFirings.Add(entity);
                ctx.SaveChanges();
                accepted++;
                FileLog.Write(
                    $"[SessionRuleStore] RecordHookFirings: decision={decisionId} rule={decision.DecidingRuleId} " +
                    $"session={entity.SessionId} decision={entity.Decision}");
            }
        }

        FileLog.Write(
            $"[SessionRuleStore] RecordHookFirings: accepted={accepted} duplicates={duplicates} refused={refused.Count}");
        return new HookFiringsAnswer(accepted, duplicates, refused);

        void Refuse(string decisionId, string reason)
        {
            FileLog.Write($"[SessionRuleStore] RecordHookFirings REFUSED decision={decisionId}: {reason}");
            refused.Add(new HookFiringRefusal(decisionId, reason));
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
    public SessionRule Promote(Guid id, RulePromotionGrant grant, DateTime nowUtc)
    {
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

            if (string.Equals(entity.State, LiveValue, StringComparison.Ordinal))
                return ToRecord(entity);

            if (!string.Equals(entity.Kind, RuleKinds.Screen, StringComparison.Ordinal))
            {
                var reason = $"live {entity.Kind} rules arrive in phase 4. In phase 1 a {entity.Kind} rule stays " +
                             "in dry run and records what it would have done.";
                FileLog.Write($"[SessionRuleStore] Promote {id} REFUSED: {reason}");
                throw new RuleRejectedException(reason);
            }

            entity.State = LiveValue;
            entity.PromotedBy = grant.Actor;
            entity.UpdatedUtc = nowUtc.ToUniversalTime();
            // Tell the write gate that THIS context carries a promotion a person asked for. Without it the
            // gate refuses a rule moving to live, which is what closes the route straight through the
            // DbSet as well - see GatewayDbContext.
            ctx.PromotionInEffect = id;
            try { ctx.SaveChanges(); }
            finally { ctx.PromotionInEffect = null; }
            FileLog.Write($"[SessionRuleStore] Promote: rule {id} is now live, asked for by {grant.Actor}");
            return ToRecord(entity);
        }
    }

    /// <summary>Delete a rule. Its firings are left alone - the record outlives the rule.</summary>
    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionRules.FirstOrDefault(r => r.Id == id);
            if (entity is null) return false;
            ctx.SessionRules.Remove(entity);
            ctx.SaveChanges();
            FileLog.Write($"[SessionRuleStore] Delete: rule {id} removed; its firings are kept");
            return true;
        }
    }

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
            var rule = ctx.SessionRules.AsNoTracking()
                .Where(r => r.Id == ruleId)
                .Select(r => new { r.State, r.Kind })
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
                SessionRuleRecordRules.CheckFiring(entity, _registry, rule?.State, rule?.Kind, DryRunValue);
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

    /// <summary>Write where a rule applies onto its row, machine names trimmed and the repository in its one
    /// spelling (ruling P6), so a rule written with any form of the remote matches every form of it.</summary>
    private static void SetAppliesTo(SessionRuleEntity entity, RuleAppliesTo appliesTo)
    {
        entity.AppliesToKind = (appliesTo.Kind ?? "").Trim();
        entity.AppliesToMachines = (appliesTo.Machines ?? Array.Empty<string>()).Select(m => (m ?? "").Trim()).ToList();
        entity.AppliesToExceptMachines = (appliesTo.ExceptMachines ?? Array.Empty<string>()).Select(m => (m ?? "").Trim()).ToList();
        entity.AppliesToRepo = GitRemoteIdentity.Normalise(appliesTo.Repo);
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
        e.UpdatedUtc)
    {
        Kind = e.Kind,
        AppliesTo = new RuleAppliesTo(
            e.AppliesToKind, e.AppliesToMachines.ToList(), e.AppliesToExceptMachines.ToList(), e.AppliesToRepo),
        Hook = string.Equals(e.Kind, RuleKinds.Hook, StringComparison.Ordinal)
            ? new HookRuleTrigger(e.HookEvent, e.HookTool, e.HookHandler, e.HookMatch.ToList(), e.HookResult)
            : null,
    };

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
        e.Grounding)
    {
        Kind = e.Kind,
        DecisionId = e.DecisionId,
        Event = e.HookEvent,
        Tool = e.HookTool,
        Subject = e.Subject,
        Machine = e.Machine,
        Repo = e.Repo,
        Agent = e.Agent,
        AgentEvent = e.AgentEvent,
        AgentTool = e.AgentTool,
        OverriddenRuleIds = e.OverriddenRuleIds.ToList(),
        MatchedRuleIds = e.MatchedRuleIds.ToList(),
        EvaluationMicroseconds = e.EvaluationMicroseconds,
    };

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
