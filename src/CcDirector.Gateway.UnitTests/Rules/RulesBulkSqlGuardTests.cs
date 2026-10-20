using Mono.Cecil;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// NOTHING IN THIS GATEWAY ISSUES BULK SQL AGAINST THE RULE TABLES, AND THE GUARD THAT SAYS SO IS READ
/// FROM THE BUILT ASSEMBLY.
///
/// The runtime closure is <c>RuleTableWriteInterceptor</c>, and it is the one that actually stops a bulk
/// update from moving a rule out of dry run - proved by the tests that watch it refuse one. This is the
/// second half: a bulk statement refused at run time is a fault somebody has to notice, and a bulk
/// statement that was never written is a fault that cannot happen. So this fails the moment any production
/// type starts issuing one against these two tables, which is a review-time answer rather than an
/// incident-time one.
///
/// It is scoped to the rule entities on purpose. Bulk operations are legitimate elsewhere in the Gateway -
/// the activity log, the session history, the device registry all prune with them - and a blanket ban
/// would be a rule nobody could keep, which is a rule that gets deleted.
/// </summary>
public sealed class RulesBulkSqlGuardTests
{
    private static readonly string[] BulkOperations =
    {
        "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
    };

    private static readonly string[] TheRuleEntities =
    {
        "CcDirector.Gateway.Data.Entities.SessionRuleEntity",
        "CcDirector.Gateway.Data.Entities.SessionRuleFiringEntity",
        "CcDirector.Gateway.Data.Entities.SessionRuleChangeEntity",
    };

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) =>
        TheBuiltGatewayAssembly.AllTypes();

    private static TypeDefinition Outermost(TypeDefinition type) => TheBuiltGatewayAssembly.Outermost(type);

    /// <summary>
    /// Every bulk call site in the assembly, as "type -> the entity it operates on". The entity is read
    /// from the call's GENERIC ARGUMENT, which is where it really is: <c>ExecuteDelete</c> is an extension
    /// on <c>IQueryable&lt;T&gt;</c>, so T names the table without any string matching on SQL.
    /// </summary>
    private static List<(string Type, string Entity)> BulkCallSites(ModuleDefinition module)
    {
        var found = new List<(string, string)>();
        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called) continue;
                    if (!BulkOperations.Contains(called.Name, StringComparer.Ordinal)) continue;
                    if (called is not GenericInstanceMethod generic) continue;
                    foreach (var argument in generic.GenericArguments)
                        found.Add((Outermost(type).FullName, argument.FullName));
                }
            }
        }
        return found;
    }

    [Fact]
    public void The_scanner_finds_the_bulk_calls_that_really_are_in_this_assembly()
    {
        // THE INSTRUMENT, and this guard needs one more than most: its pass condition is an ABSENCE, so a
        // scanner that found nothing at all would certify the whole assembly as clean. The Gateway really
        // does prune with bulk operations elsewhere, so the list must not be empty.
        var sites = BulkCallSites(TheBuiltGatewayAssembly.Module);

        Assert.True(sites.Count > 0,
            "the scanner found no bulk operation anywhere in the Gateway, but several stores prune with " +
            "them - so the scanner is broken, and the assertion below would mean nothing.");
    }

    [Fact]
    public void No_production_code_issues_bulk_sql_against_a_rule_or_a_firing()
    {
        var offenders = BulkCallSites(TheBuiltGatewayAssembly.Module)
            .Where(s => TheRuleEntities.Contains(s.Entity, StringComparer.Ordinal))
            .Select(s => s.Type + " -> " + s.Entity)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "a bulk statement does not pass through SaveChanges, so it meets neither the write gate nor " +
            "dry run. These issue one against a rule or its record: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// THE INTERCEPTOR IS INSTALLED WHEREVER THE OPTIONS ARE BUILT, AND THERE IS MORE THAN ONE SUCH PLACE.
    ///
    /// SQLite locally and Postgres hosted are configured separately. A guard installed in one of the two is
    /// absent on the other, and it would be absent on the one that runs in the cloud while passing every
    /// test on the machine where it was written. Pooling forbids putting it in <c>OnConfiguring</c>, so the
    /// call sites are counted instead.
    /// </summary>
    [Fact]
    public void Every_place_that_builds_a_context_factory_installs_the_interceptors()
    {
        var module = TheBuiltGatewayAssembly.Module;
        var database = AllTypes(module)
            .Where(t => Outermost(t).FullName == "CcDirector.Gateway.Data.GatewayDatabase")
            .ToList();

        var factories = 0;
        var installs = 0;
        foreach (var type in database)
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called) continue;
                    if (called.Name == "AddPooledDbContextFactory") factories++;
                    if (called.Name == "WithGatewayInterceptors") installs++;
                }
            }

        Assert.True(factories > 0, "no context factory is built in GatewayDatabase, so the scanner is broken.");
        Assert.Equal(factories, installs);
    }
}
