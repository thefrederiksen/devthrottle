using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CcDirector.Gateway.Data;

/// <summary>
/// A WRITE TO A TABLE A DEVICE KEY CHECK READS MAKES EVERY REMEMBERED KEY BE CHECKED AGAIN (issue #4409) - see
/// <see cref="Pairing.DeviceCredentialGeneration"/>.
///
/// It watches the COMMAND, not the change tracker, because the revoke paths include bulk updates that issue their SQL
/// directly and never reach SaveChanges; the command is the one place every write passes. It matches the command text,
/// the same blunt instrument <see cref="RuleTableWriteInterceptor"/> uses and for the same reason. Erring wide is
/// cheap: a write that only looks like one of these makes a few keys be read again. Erring narrow is a revoked key that
/// keeps working, so the match is any insert, update, delete or merge that names one of the tables.
///
/// The number moves AFTER the command has run, failed or not. Inside a transaction the change is not visible to other
/// connections until the commit, so the context is marked and the number moves again when the transaction ends
/// (<see cref="SettingsTransactionInterceptor"/>, which calls <see cref="GatewayDbContext.TransactionEnded"/>).
/// </summary>
internal sealed class CredentialTableWriteInterceptor : DbCommandInterceptor
{
    /// <summary>Every table <see cref="Pairing.DeviceRegistry.ResolveCredential"/> reads, matched case-insensitively - and
    /// <c>teams</c>, because deleting a team removes its <c>team_members</c> rows by a database cascade whose command
    /// names only <c>teams</c>.</summary>
    private static readonly string[] TheCredentialTables = { "device_credentials", "tenants", "team_members", "teams" };

    /// <summary>A write verb as a keyword: not inside a quoted or longer name ("UpdatedAtUtc"), followed by space or a
    /// line break. A read with FOR UPDATE also matches, which only costs a re-check.</summary>
    private static readonly Regex WriteVerb = new(@"(?<![\w""])(INSERT|UPDATE|DELETE|MERGE)\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Observe(command, eventData.Context);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>SaveChanges sends its inserts and updates through the reader path on both providers (they return columns,
    /// or <c>RETURNING 1</c> on SQLite).</summary>
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Observe(command, eventData.Context);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Observe(command, eventData.Context);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>A failed write may still have changed rows before it failed; forgetting early costs only a read.</summary>
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        Observe(command, eventData.Context);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData.Context);
        return Task.CompletedTask;
    }

    private static void Observe(DbCommand command, Microsoft.EntityFrameworkCore.DbContext? context)
    {
        if (!WritesACredentialTable(command.CommandText)) return;
        Pairing.DeviceCredentialGeneration.Bump();
        if (context is GatewayDbContext gateway && gateway.Database.CurrentTransaction is not null)
            gateway.CredentialsWrittenInTransaction = true;
    }

    /// <summary>True when the command text writes one of the credential tables. Internal for the test that pins it.</summary>
    internal static bool WritesACredentialTable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var namesATable = false;
        foreach (var table in TheCredentialTables)
        {
            if (text.IndexOf(table, StringComparison.OrdinalIgnoreCase) >= 0) { namesATable = true; break; }
        }
        if (!namesATable) return false;
        return WriteVerb.IsMatch(text);
    }
}
