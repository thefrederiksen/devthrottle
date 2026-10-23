using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CcDirector.Gateway.Data;

/// <summary>
/// THE SECOND HALF OF FORGETTING A REMEMBERED SETTING (issue #4405). <c>GatewayDbContext.SaveChanges</c> moves an
/// account's settings number the moment its rows are saved, but inside a transaction those rows are not visible to
/// anyone else until the commit - so a reader in that window loads the OLD value under the NEW number. This moves the
/// number again when the transaction ends, which is the only place that moment can be seen.
/// </summary>
internal sealed class SettingsTransactionInterceptor : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        Ended(eventData);

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Ended(eventData);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        Ended(eventData);

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Ended(eventData);
        return Task.CompletedTask;
    }

    private static void Ended(TransactionEndEventData eventData)
    {
        if (eventData.Context is GatewayDbContext context) context.SettingsTransactionEnded();
    }
}
