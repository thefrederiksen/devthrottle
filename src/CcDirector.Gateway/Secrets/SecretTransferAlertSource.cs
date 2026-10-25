using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Secrets;

/// <summary>
/// THE SECRET TRANSFERS, AS ALERTS (the Alerts mission). A pure mapping over <see cref="SecretTransferStore"/>: its own
/// <see cref="SecretTransferStore.List"/> settles expiry and orphaned deliveries, and the detail is the same
/// <see cref="SecretTransferDto"/> the transfer routes serve (<see cref="SecretTransferEndpoints.ToDto"/>), so the detail
/// view answers through the existing answer route with its place-of-answer rules intact.
///
/// A transfer needs an answer exactly while <see cref="SecretTransferDto.CanAnswer"/> is true: waiting and unexpired.
/// </summary>
public sealed class SecretTransferAlertSource : IAlertSource
{
    private readonly SecretTransferStore _store;

    public SecretTransferAlertSource(SecretTransferStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Kind => AlertKinds.SecretTransfer;

    public IReadOnlyList<AlertDto> List(TenantId tenant, DateTime nowUtc)
    {
        var alerts = _store.List(tenant, nowUtc - AlertWindow.Earlier, nowUtc)
            .Select(row => Fold(row, nowUtc))
            .Where(a => AlertWindow.Keeps(a.NeedsAnswer, a.UpdatedAtUtc, nowUtc))
            .ToList();
        FileLog.Write($"[SecretTransferAlertSource] List: tenant={tenant.ToLogString()}, alerts={alerts.Count}");
        return alerts;
    }

    /// <summary>One transfer as the owner reads it.</summary>
    internal static AlertDto Fold(SecretTransferEntity row, DateTime nowUtc)
    {
        var dto = SecretTransferEndpoints.ToDto(row, nowUtc);
        var what = $"{dto.Entry} to {dto.ToMachine}";
        var (status, title, summary) = dto.State switch
        {
            SecretTransferStates.Waiting => ("Waiting", $"Send the secret {what}?", $"{dto.AskedBy} asks: {dto.Reason}"),
            SecretTransferStates.Approved => ("Approved", $"Sending the secret {what}",
                $"Approved {SecretTransferPlaces.Words(dto.AnsweredWhere)}. Delivering now."),
            SecretTransferStates.Denied => ("Denied", $"Secret {what}", Finished(dto)),
            SecretTransferStates.Expired => ("Expired", $"Secret {what}", Finished(dto)),
            SecretTransferStates.Delivered => ("Delivered", $"Secret {what}", Finished(dto)),
            SecretTransferStates.Failed => ("Failed", $"Secret {what}", Finished(dto)),
            _ => throw new InvalidOperationException($"secret transfer {dto.TransferId} is in state '{dto.State}', which has no alert words"),
        };
        return new AlertDto
        {
            Id = AlertKinds.IdOf(AlertKinds.SecretTransfer, dto.TransferId),
            Kind = AlertKinds.SecretTransfer,
            NeedsAnswer = dto.CanAnswer,
            Title = title,
            Summary = summary,
            Status = status,
            CreatedAtUtc = dto.CreatedAtUtc,
            UpdatedAtUtc = AlertWindow.Latest(dto.CreatedAtUtc, dto.AnsweredAtUtc, dto.FinishedAtUtc),
            ExpiresAtUtc = dto.State == SecretTransferStates.Waiting ? dto.ExpiresAtUtc : null,
            Detail = dto,
        };
    }

    /// <summary>A finished transfer's outcome sentence, which the store writes for every finished state.</summary>
    private static string Finished(SecretTransferDto dto)
        => dto.Outcome ?? throw new InvalidOperationException(
            $"secret transfer {dto.TransferId} is {dto.State} but carries no outcome sentence");
}
