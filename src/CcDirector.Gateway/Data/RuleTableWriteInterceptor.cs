using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CcDirector.Gateway.Data;

/// <summary>
/// THE HALF OF THE WRITE GATE THAT SaveChanges CANNOT SEE.
///
/// <c>GatewayDbContext.GuardRuleWrites</c> runs from <c>SaveChanges</c> and reads the change tracker. Bulk
/// operations do not go near either: an ORM bulk update issues its SQL immediately and tracks nothing, so
/// one line could move a rule from dry run to live - the single transition this whole feature exists to
/// put a person in front of - without ever meeting the gate. The independent inspection of landing B did
/// exactly that and read the rule back as live. A bulk delete could erase the firing record the same way,
/// and the record is the product.
///
/// So the rule tables are closed at the LAST place every route passes: the command itself. An UPDATE or a
/// DELETE naming either table is refused unless it was issued by a save that has already been through the
/// gate, which the context says by holding <c>SavingThroughTheGate</c> for the duration of that save.
///
/// WHAT THIS IS AND IS NOT. It matches the command TEXT, which is a blunt instrument, and it is deliberate:
/// there is no earlier hook that sees a bulk operation at all, and a check that cannot see the thing it is
/// guarding is not a check. It refuses only UPDATE and DELETE, so schema migration - which creates and
/// alters, and never updates a rule row - is untouched. Reads are untouched. And nothing in this Gateway
/// issues bulk SQL against these tables anyway; a structural test says so and names any type that starts.
/// This exists so that "nothing does" is not the only thing standing between a rule and being made live by
/// one line.
/// </summary>
internal sealed class RuleTableWriteInterceptor : DbCommandInterceptor
{
    /// <summary>The tables this closes - the rules, their firing record, and the history of their changes,
    /// which is append only. Written once, matched case-insensitively.</summary>
    private static readonly string[] TheRuleTables = { "session_rules", "session_rule_firings", "session_rule_changes" };

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Refuse(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Refuse(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>Bulk operations on some providers come back through the reader path, so it is closed too.
    /// A guard that covered one of two paths would be a guard nobody could rely on.</summary>
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Refuse(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Refuse(command, eventData);
        return ValueTask.FromResult(result);
    }

    private static void Refuse(DbCommand command, CommandEventData eventData)
    {
        if (eventData.Context is GatewayDbContext context && context.SavingThroughTheGate) return;

        var text = (command.CommandText ?? "").TrimStart();
        if (text.Length == 0) return;

        if (!text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var table in TheRuleTables)
        {
            if (text.IndexOf(table, StringComparison.OrdinalIgnoreCase) < 0) continue;

            throw new Rules.RuleRejectedException(
                "a rule and its record are changed through the rule store, which is where a rule is checked " +
                "and where dry run is enforced. This statement changes '" + table + "' directly, without " +
                "passing either - and moving a rule out of dry run is the one act that lets it type into " +
                "your sessions.");
        }
    }
}
