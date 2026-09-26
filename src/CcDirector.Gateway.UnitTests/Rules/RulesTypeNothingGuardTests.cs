using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// EXACTLY ONE THING IN THIS FEATURE CAN TYPE, AND THIS IS WHAT PROVES IT. Phase 1 typed nothing at all;
/// phase 2 types, which is the whole point of it - so the guard is not deleted, it is TIGHTENED. The set of
/// types in the rules namespace that reach the seam that types into a session must be exactly one, and it
/// must be the production environment wiring. The evaluator - which is where the dry-run decision is made -
/// must not be able to type at all, which is what makes "dry run types nothing" a property of the structure
/// rather than a branch somebody has to keep remembering.
///
/// IT IS A REFERENCE ASSERTION IN THE BUILT ASSEMBLY, DELIBERATELY NOT A SOURCE-TEXT SCAN, and the
/// distinction is the point. A grep for "prompt" over the rules directory would pass just as happily if
/// the rules directory were EMPTY, or if the call sat one helper away in another file. Reading the
/// compiled metadata cannot be dodged by moving text, and - the part that matters most - the scanner is
/// PROVEN TO WORK on a known-positive first: it is pointed at code that really does type into sessions and
/// required to find the seam there. If that instrument check ever comes back clean, the scanner is broken
/// and every "no reference found" below it means nothing.
///
/// IT FOLLOWS CALLS NOW, WHICH IT DID NOT. The independent inspection of landing A found the scanner read
/// each method's IMMEDIATE operands only, so a rules type that reached the send through a helper in
/// another namespace left it green. It was run against exactly that shape - a probe committed on purpose
/// and left in the history - and it passed. It now walks the call graph, so a route through any number of
/// helpers inside this assembly is found.
///
/// WHAT IT COVERS, EXACTLY, because a guard described more broadly than it holds is worse than none:
///
///  - Every type of the FEATURE: the rules namespace, and the rule and firing entities, which live in the
///    data namespace and were outside the old filter entirely.
///  - STATIC call edges inside the Gateway assembly, followed transitively. A call to a method in another
///    assembly is not followed into it.
///  - It does NOT follow VIRTUAL DISPATCH. The evaluator calls
///    <c>IRuleEnvironment.TypeIntoSessionAsync</c>, and reaching the send through that seam is the design
///    rather than a leak - it is what the dry-run branch sits in front of. So the assertion about the
///    evaluator below is that it cannot reach the send EXCEPT through its environment, and that is what it
///    says.
/// </summary>
public sealed class RulesTypeNothingGuardTests
{
    /// <summary>THE SEAM THAT TYPES: the prompt verb, which puts text into a session's composer and presses
    /// Enter. Naming the METHOD rather than its class matters - the same class also READS the screen, and a
    /// guard that could not tell a read from a keystroke would refuse the read as well.
    ///
    /// It names <c>SendPromptAsync</c> rather than the older <c>PostPromptAsync</c> because the send moved:
    /// the tuple-returning method now delegates to this one, which is the single place a prompt actually
    /// leaves the Gateway. Guarding the wrapper would have missed a caller that used the new method
    /// directly - and this feature is now exactly such a caller, because it needs the three outcomes the
    /// wrapper's boolean cannot carry.</summary>
    private const string TypingSeam = "CcDirector.Gateway.Api.SessionVerbClient::SendPromptAsync";

    /// <summary>The older tuple-returning method, which is now a WRAPPER over the seam: it calls it and
    /// flattens the three outcomes into one boolean for the callers that only need "did it work".</summary>
    private const string TheWrapperOverTheSeam = "CcDirector.Gateway.Api.SessionVerbClient::PostPromptAsync";

    /// <summary>
    /// BOTH WAYS INTO THE KEYSTROKE, because reaching either of them is typing and a guard that watched
    /// only one would be blind to the other.
    ///
    /// The wrapper has to be named separately rather than found by following calls, and the reason is a
    /// real limit of this scanner worth writing down: an ASYNC method's body lives in a compiler-generated
    /// state machine, and nothing in the metadata CALLS that state machine by name - the builder starts it
    /// through a generic method in another assembly. So there is no edge from an async wrapper to its own
    /// body, and a backward walk from the seam stops dead at any async wrapper in the chain. The walk is
    /// still worth having for ordinary chains; it simply cannot cross that one boundary, and pretending
    /// otherwise would make every clean result below it meaningless.
    /// </summary>
    private static readonly string[] TypingSeams = { TypingSeam, TheWrapperOverTheSeam };

    /// <summary>The lower-level router the prompt verb goes through. Nothing in this feature reaches it
    /// directly - the feature types through the verb client like every other caller.</summary>
    private const string CommandRouter = "CcDirector.Gateway.Api.DirectorCommandRouter";

    /// <summary>The namespace this feature is built in.</summary>
    private const string RulesNamespace = "CcDirector.Gateway.Rules";

    /// <summary>Code that really does type into sessions, so the scanner can be proven against it: the
    /// session supervisor's production wiring, which sends "continue" into a parked session.</summary>
    private const string KnownTypist = "CcDirector.Gateway.Supervision.GatewaySupervisorEnvironment";

    /// <summary>The ONE type in this feature that is allowed to type. Named, so a second one is a failing
    /// test rather than a thing nobody notices.</summary>
    private const string TheOnlyTypist = "CcDirector.Gateway.Rules.GatewayRuleEnvironment";

    /// <summary>Where the dry-run decision is made. It must be structurally incapable of typing.</summary>
    private const string TheEvaluator = "CcDirector.Gateway.Rules.RuleEvaluator";

    /// <summary>
    /// THE ASSEMBLY AND ITS CALL GRAPH, READ ONCE FOR THE WHOLE TEST PROCESS.
    ///
    /// This is not a micro-optimisation, it is the difference between the gate running and the gate being
    /// killed. Building the graph means resolving every call site in the Gateway assembly to a definition,
    /// and doing that once per test pushed this project past the two-minute ceiling the local gate enforces
    /// - the suite was STOPPED, which is not a failure and is not a pass either. Built once, shared, and
    /// held for the life of the process, because a module that is disposed takes the type handles in the
    /// index with it.
    /// </summary>
    private static ModuleDefinition GatewayModule() => TheBuiltGatewayAssembly.Module;

    /// <summary>Who calls what, plus where each method lives. Seam-independent, so one build serves every
    /// question any test asks.</summary>
    private sealed record CallIndex(
        Dictionary<string, TypeDefinition> Owner,
        Dictionary<string, string> Name,
        Dictionary<string, List<string>> Callers);

    private static readonly Lazy<CallIndex> Graph = new(BuildCallIndex, isThreadSafe: true);

    private static CallIndex BuildCallIndex()
    {
        var module = GatewayModule();
        var owner = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        var name = new Dictionary<string, string>(StringComparer.Ordinal);
        var callers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                owner[method.FullName] = type;
                name[method.FullName] = method.Name;
                if (!method.HasBody) continue;

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference callee) continue;
                    var key = DefinitionKey(callee);
                    if (key is null) continue;
                    if (!callers.TryGetValue(key, out var list)) callers[key] = list = new List<string>();
                    list.Add(method.FullName);
                }
            }
        }
        return new CallIndex(owner, name, callers);
    }

    /// <summary>Every method whose OWN body references <paramref name="seam"/>, among the types this
    /// filter selects. One hop only - this is the scanner as it was, kept because the transitive one is
    /// measured against it.</summary>
    private static List<string> MethodsReaching(ModuleDefinition module, string seam, Func<TypeDefinition, bool> include) =>
        MethodsReaching(module, new[] { seam }, include);

    /// <summary>Every method whose OWN body references ANY of <paramref name="seams"/>.</summary>
    private static List<string> MethodsReaching(ModuleDefinition module, string[] seams, Func<TypeDefinition, bool> include)
    {
        var found = new List<string>();
        foreach (var type in AllTypes(module))
        {
            if (!include(type)) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                if (Mentions(method, seams)) found.Add(Outermost(type).FullName + "." + method.Name);
            }
        }
        return found;
    }

    /// <summary>Whether a method's own body names any of the seams.</summary>
    private static bool Mentions(MethodDefinition method, string[] seams)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            var reference = instruction.Operand switch
            {
                MethodReference m => (m.DeclaringType?.FullName ?? "") + "::" + m.Name,
                FieldReference f => (f.DeclaringType?.FullName ?? "") + "::" + f.Name,
                TypeReference t => t.FullName,
                _ => null,
            };
            if (reference is null) continue;
            foreach (var seam in seams)
                if (reference.StartsWith(seam, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Every method that reaches <paramref name="seam"/> DIRECTLY OR THROUGH ANY CHAIN OF CALLS inside
    /// this assembly, among the types the filter selects. This is the answer the guard is actually about:
    /// a rules type that sends through a helper in another namespace is typing, and a scanner that only
    /// read immediate operands called that clean.
    ///
    /// Edges are static call sites, resolved to a method DEFINITION in this module; a call into another
    /// assembly is an edge to nothing, and a virtual call is an edge to the method that was named, not to
    /// its implementations. Both limits are stated on the class, and neither is silent.
    /// </summary>
    private static List<string> MethodsReachingThroughCalls(
        ModuleDefinition module, string seam, Func<TypeDefinition, bool> include) =>
        MethodsReachingThroughCalls(module, new[] { seam }, include);

    /// <summary>As above, for a set of seams: reaching ANY of them counts.</summary>
    private static List<string> MethodsReachingThroughCalls(
        ModuleDefinition module, string[] seams, Func<TypeDefinition, bool> include)
    {
        var graph = Graph.Value;
        var reached = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in AllTypes(module))
            foreach (var method in type.Methods)
                if (method.HasBody && Mentions(method, seams))
                    reached.Add(method.FullName);

        var queue = new Queue<string>(reached);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!graph.Callers.TryGetValue(current, out var itsCallers)) continue;
            foreach (var caller in itsCallers)
                if (reached.Add(caller)) queue.Enqueue(caller);
        }

        return reached
            .Where(m => graph.Owner.TryGetValue(m, out var t) && include(t))
            .Select(m => Outermost(graph.Owner[m]).FullName + "." + graph.Name[m])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The called method's definition key, or null when it cannot be resolved in this
    /// assembly.</summary>
    private static string? DefinitionKey(MethodReference reference)
    {
        try
        {
            var definition = reference.Resolve();
            return definition?.FullName;
        }
        catch
        {
            // A reference into an assembly that is not beside us is not an edge we can follow. It is not
            // an error, and it is not a clean result either - the class comment says so.
            return null;
        }
    }

    /// <summary>The distinct types among those methods.</summary>
    private static List<string> TypesOf(IEnumerable<string> methods) => methods
        .Select(m => m[..m.LastIndexOf('.')])
        .Distinct(StringComparer.Ordinal)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The type a nested one belongs to, walking all the way out. An ASYNC METHOD'S BODY DOES NOT LIVE IN
    /// THE METHOD: the compiler moves it into a generated nested state machine, and in the metadata that
    /// nested type carries an EMPTY namespace and a name of its own. A guard that did not walk out to the
    /// declaring type would therefore find nothing for every async method in the namespace it is guarding -
    /// and would report that as a clean result. This one cost a red before it was noticed.
    /// </summary>
    private static TypeDefinition Outermost(TypeDefinition type) => TheBuiltGatewayAssembly.Outermost(type);

    /// <summary>The namespace a type really belongs to - its own, or its outermost declaring type's.</summary>
    private static string NamespaceOf(TypeDefinition type) => Outermost(type).Namespace ?? "";

    /// <summary>The namespace the feature's stored rows live in.</summary>
    private const string EntitiesNamespace = "CcDirector.Gateway.Data.Entities";

    /// <summary>
    /// EVERY TYPE OF THIS FEATURE, which is more than one namespace. The guard used to select the rules
    /// namespace alone, and the landing that introduced it also added the rule and firing entities in the
    /// data namespace - so the feature's own stored rows were outside the thing guarding the feature.
    /// The entities are picked out by the names the feature gives them rather than by a list kept here,
    /// so a third one is covered on the day it is written.
    /// </summary>
    private static bool IsFeatureType(TypeDefinition type)
    {
        if (NamespaceOf(type).StartsWith(RulesNamespace, StringComparison.Ordinal)) return true;

        // MARKED PIECES, wherever they live. The guard used to select the rules namespace plus the two
        // stored-row types, and the feature had already grown outside that: the rule endpoints live in the
        // API namespace, and the launch lived inside the Gateway host. Both were listed as phase 2 feature
        // pieces and both were outside the thing guarding the feature, so typing or command-routing code
        // placed in either could stay green. The marker travels with the type; a list kept here would have
        // to be remembered.
        if (Outermost(type).CustomAttributes.Any(a =>
                a.AttributeType.FullName == "CcDirector.Gateway.Rules.RuleFeatureAttribute"))
            return true;

        if (!string.Equals(NamespaceOf(type), EntitiesNamespace, StringComparison.Ordinal)) return false;
        var simple = Outermost(type).Name;
        return simple.StartsWith("SessionRule", StringComparison.Ordinal)
            || simple.StartsWith("RulePrimitive", StringComparison.Ordinal);
    }

    /// <summary>Every type in the module, nested ones included - a compiler-generated closure is still a
    /// type that could hold the call.</summary>
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) =>
        TheBuiltGatewayAssembly.AllTypes();

    [Fact]
    public void The_scanner_finds_the_typing_seam_where_it_really_is()
    {
        // THE INSTRUMENT. If this comes back empty the scanner is broken, and every guard below it would
        // certify a run that never looked at anything.
        //
        // It follows CALLS, because the known typist reaches the seam through the tuple-returning wrapper
        // over it rather than by calling the seam itself. A one-hop read here would find nothing and would
        // be reporting the scanner broken when it is not.
        var module = GatewayModule();
        var typists = MethodsReachingThroughCalls(module, TypingSeams,
            t => Outermost(t).FullName.StartsWith(KnownTypist, StringComparison.Ordinal));

        Assert.True(typists.Count > 0,
            "the scanner found no reference to " + TypingSeam + " inside " + KnownTypist +
            ", which really does type into sessions - so the scanner is broken, not the code.");
    }

    [Fact]
    public void The_rules_namespace_exists_and_has_types_in_it()
    {
        // The second instrument: a guard over an empty namespace passes and proves nothing.
        var module = GatewayModule();
        var ruleTypes = AllTypes(module)
            .Where(t => NamespaceOf(t).StartsWith(RulesNamespace, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(ruleTypes.Count > 0, "no types were found in " + RulesNamespace);
    }

    [Fact]
    public void The_feature_includes_its_stored_rows_and_not_the_whole_data_namespace()
    {
        // The third instrument. The guard's scope grew to cover the rule and firing entities, and a scope
        // that quietly selected nothing - or everything - would make every assertion below it meaningless
        // in opposite directions.
        var module = GatewayModule();
        var feature = AllTypes(module).Where(IsFeatureType).Select(t => Outermost(t).FullName).Distinct().ToList();

        Assert.Contains("CcDirector.Gateway.Data.Entities.SessionRuleEntity", feature);
        Assert.Contains("CcDirector.Gateway.Data.Entities.SessionRuleFiringEntity", feature);
        Assert.Contains("CcDirector.Gateway.Rules.RuleEvaluator", feature);

        // The two pieces that were listed as part of this feature and were outside the guard: the route a
        // rule arrives on, and the place a pass is started from.
        Assert.Contains("CcDirector.Gateway.Api.SessionRuleEndpoints", feature);
        Assert.Contains("CcDirector.Gateway.Rules.RuleTurnEndLauncher", feature);

        // And the scope must still be a scope: the Gateway host runs the whole rest of the product and is
        // not part of this feature, so a marker that swallowed it would make every assertion below
        // meaningless in the other direction.
        Assert.DoesNotContain("CcDirector.Gateway.Data.Entities.CronJobEntity", feature);
        Assert.DoesNotContain("CcDirector.Gateway.GatewayHost", feature);
    }

    /// <summary>
    /// A seam with CHAINS behind it, used only to prove the traversal traverses. It is deliberately NOT
    /// the typing seam, and the reason is worth writing down: with the probe removed, nothing in this
    /// assembly reaches the prompt verb except by calling it directly, so on that seam a call-graph walk
    /// and a one-hop read give the same four methods - which is the truth about the code, and useless as a
    /// test of the walk. The log write is called from all over the Gateway by methods that are themselves
    /// called, so it exercises the edges. The traversal is one algorithm, so proving it on this seam
    /// proves it for the seam the guard is about.
    /// </summary>
    private const string SeamWithChainsBehindIt = "CcDirector.Core.Utilities.FileLog::Write";

    [Fact]
    public void Following_calls_finds_more_than_reading_one_method_at_a_time()
    {
        // THE INSTRUMENT FOR THE TRAVERSAL ITSELF. A call-graph walk that followed no edges would answer
        // exactly what the old one-hop scanner answered, and every assertion built on it would be the old
        // guard wearing a new name. So it has to find strictly more.
        var module = GatewayModule();
        var oneHop = MethodsReaching(module, SeamWithChainsBehindIt, _ => true)
            .Distinct(StringComparer.Ordinal).ToList();
        var throughCalls = MethodsReachingThroughCalls(module, SeamWithChainsBehindIt, _ => true);

        Assert.True(oneHop.Count > 0, "the one-hop scanner found nothing, so the comparison is meaningless.");
        Assert.True(throughCalls.Count > oneHop.Count,
            "following calls found " + throughCalls.Count + " methods reaching " + SeamWithChainsBehindIt +
            " and reading one method at a time found " + oneHop.Count +
            ". They are the same, so the traversal is not traversing.");
    }

    /// <summary>The types allowed to CALL the send seam directly. Six, named, and no more: the verb client
    /// itself - whose older tuple-returning method is now a wrapper over the seam - and four callers that
    /// need the three outcomes the wrapper's boolean cannot carry: this feature's production wiring, and the
    /// answer route's tunnel channel (the Wingman-on-every-turn mission, slice E), whose ledger says "refused"
    /// only when nothing left the Gateway and "unconfirmed" when the bytes went out unanswered. The fourth is the
    /// dev report delivery (issue #2958), for the same reason: an owner's held item stays held only when nothing
    /// left the Gateway, and a send that went out unanswered is recorded "sent, not confirmed" and never typed a
    /// second time. The fifth is the Fleet Manager's event delivery (the Fleet Manager mission, step 4), which
    /// types ONE prompt into an idle Fleet Manager session at its turn-end boundary and records the events as
    /// delivered only on an accepted send - a send nothing confirmed must leave them owed, and a send that never
    /// left must say so in the log. The sixth is the voice delivery's send-and-ask piece (the Voice Delivery mission,
    /// phase 2), shared by the dictation complete and a claimed "Send anyway": a send that went out unanswered is
    /// followed by a question to the Director, never read as a failure that types the words again - so it must see
    /// "unanswered" apart from "never left" and "refused". The seventh is the typed prompt route's send (the Voice
    /// Delivery mission, phase 5): every typed prompt carries a delivery id, and one that went out unanswered is HELD
    /// and asked about by the Gateway later, never read as a failure the caller re-sends - so it must see the same three
    /// outcomes apart. It also sends, exactly once, a typed prompt that never left the Gateway because its session could
    /// not be located, when that session is reachable again within five minutes (contract section 10). Every other caller in the Gateway goes through the wrapper.</summary>
    private static readonly string[] AllowedDirectCallersOfTheSeam =
    {
        "CcDirector.Gateway.Api.DeliverySendAndAsk",
        "CcDirector.Gateway.Api.SessionVerbClient",
        "CcDirector.Gateway.Api.TunnelTurnVerdictAnswerChannel",
        "CcDirector.Gateway.Api.TypedPromptDelivery",
        "CcDirector.Gateway.DevReports.DevReportDelivery",
        "CcDirector.Gateway.Fleet.GatewayFleetManagerEventEnvironment",
        "CcDirector.Gateway.Rules.GatewayRuleEnvironment",
    };

    [Fact]
    public void The_send_seam_has_exactly_four_direct_callers_and_all_are_named()
    {
        // This test used to say that every method reaching the prompt verb called it itself, and that the
        // day that stopped being true it would fail and whoever read it would find out here. That day is
        // this one: the send moved down one level so the three outcomes of a send could be kept apart, and
        // the older method is now a wrapper over it.
        //
        // So the fact is restated rather than loosened. A guard that merely permitted indirection would
        // permit any number of new routes to the keystroke to appear unnoticed, which is the opposite of
        // what it is for. The DIRECT callers of the seam are named, and an unnamed one fails this test - as the
        // answer route's channel, the dev report delivery and the Fleet Manager's event delivery did, until each
        // was classified above.
        var module = GatewayModule();
        var callers = TypesOf(MethodsReaching(module, TypingSeam, _ => true).Distinct(StringComparer.Ordinal));

        Assert.True(callers.Count > 0, "nothing at all reaches " + TypingSeam + ", so the scanner is broken.");
        Assert.Equal(AllowedDirectCallersOfTheSeam.OrderBy(n => n, StringComparer.Ordinal).ToList(), callers);
    }

    [Fact]
    public void Exactly_one_type_in_the_whole_feature_can_type_and_it_is_the_named_one()
    {
        var module = GatewayModule();
        var typists = TypesOf(MethodsReachingThroughCalls(module, TypingSeams, IsFeatureType));

        // A PRESENCE first: the one thing that is supposed to be able to type must actually be there. An
        // empty list would otherwise pass the "nothing else can type" half while proving that the feature
        // cannot type at all.
        Assert.Contains(TheOnlyTypist, typists);
        Assert.Equal(new[] { TheOnlyTypist }, typists);
    }

    [Fact]
    public void The_evaluator_reaches_the_send_only_through_its_environment_seam()
    {
        var module = GatewayModule();
        var offenders = MethodsReachingThroughCalls(module, TypingSeams,
            t => Outermost(t).FullName.StartsWith(TheEvaluator, StringComparison.Ordinal));

        // Said exactly: the evaluator DOES reach the send, through IRuleEnvironment, and that is the
        // design - the dry-run branch sits in front of it. What must not exist is a static route from the
        // evaluator to the prompt verb that goes round that seam, because the branch would then be
        // something somebody has to remember rather than something the shape of the code enforces.
        Assert.True(offenders.Count == 0,
            "the evaluator decides whether a rule is in dry run, so it must reach the send only through its " +
            "environment seam - but these reach " + TypingSeam + " without it: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Nothing_in_the_rules_namespace_reaches_the_command_router_directly()
    {
        var module = GatewayModule();

        // The instrument for THIS seam, so the assertion below cannot pass on a scanner that finds nothing:
        // the endpoint layer really does reach the router.
        var known = MethodsReaching(module, CommandRouter,
            t => Outermost(t).FullName.StartsWith("CcDirector.Gateway.Api.GatewayEndpoints", StringComparison.Ordinal));
        Assert.True(known.Count > 0,
            "the scanner found no reference to " + CommandRouter + " in the endpoint layer, which really " +
            "does use it - so the scanner is broken, not the code.");

        var offenders = MethodsReachingThroughCalls(module, CommandRouter, IsFeatureType);

        Assert.True(offenders.Count == 0,
            "this feature types through the prompt verb like every other caller, but these reach " +
            CommandRouter + ": " + string.Join(", ", offenders));
    }
}
