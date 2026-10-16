using CcDirector.Core.Utilities;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;

namespace CcDirector.Gateway.ApiModeDisplay;

/// <summary>
/// One stored receipt, as much of it as the session verdict needs (<see cref="ApiModeSessionFold"/>): its state, its
/// queue reason, the turn that carried it and that turn's outcome. Never the acknowledgement or the refusal reason - a
/// roster read does not carry up to 64 kilobytes per prompt to count turns.
/// </summary>
public sealed record ApiModeVerdictReceipt(
    string PromptId,
    ApiModeReceiptState State,
    string? QueueReason,
    string? TurnId,
    string? TurnOutcome,
    string? TurnOutcomeReason);

/// <summary>
/// THE ROSTER'S ONE RECEIPT READ (mission #4070, phase 2; review finding 1 on pull request #4086). For an account and
/// the API-mode sessions on its roster: every stored receipt of every one of them, in ONE query, projected to the
/// columns the verdict needs, grouped by session, oldest prompt first - the order <see cref="Receipts.ApiModeReceiptStore.Read"/>
/// gives. Read straight from the database, so it never takes the receipt store's write lock: a roster read never waits
/// behind the event ingest, and the ingest never waits behind a roster read.
///
/// Tenant-scoped like every read: the context is created for the account given, and the database's global filter keeps
/// every other account's rows out, whatever session ids are asked for.
/// </summary>
public sealed class ApiModeVerdictReceipts
{
    private static readonly IReadOnlyList<ApiModeVerdictReceipt> NoReceipts = Array.Empty<ApiModeVerdictReceipt>();

    private readonly GatewayDatabase _db;

    public ApiModeVerdictReceipts(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Each asked-for session's receipts in <paramref name="tenant"/>, oldest prompt first. Every asked-for id is
    /// in the answer; a session with no receipts answers an empty list.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ApiModeVerdictReceipt>> Read(TenantId tenant, IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        var ids = sessionIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        var answer = ids.ToDictionary(id => id, _ => NoReceipts, StringComparer.Ordinal);
        if (ids.Count == 0) return answer;

        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.ApiModeReceipts
            .Where(r => ids.Contains(r.SessionId))
            .OrderBy(r => r.SessionId)
            .ThenBy(r => r.FirstGenerationStartedUtc)
            .ThenBy(r => r.FirstSequence)
            .ThenBy(r => r.PromptId)
            .Select(r => new { r.SessionId, r.PromptId, r.State, r.QueueReason, r.TurnId, r.TurnOutcome, r.TurnOutcomeReason })
            .ToList();

        foreach (var group in rows.GroupBy(r => r.SessionId, StringComparer.Ordinal))
            answer[group.Key] = group
                .Select(r => new ApiModeVerdictReceipt(r.PromptId, Enum.Parse<ApiModeReceiptState>(r.State), r.QueueReason, r.TurnId, r.TurnOutcome, r.TurnOutcomeReason))
                .ToList();
        FileLog.Write($"[ApiModeVerdictReceipts] Read: tenant={tenant}, sessions={ids.Count}, receipts={rows.Count}");
        return answer;
    }
}
