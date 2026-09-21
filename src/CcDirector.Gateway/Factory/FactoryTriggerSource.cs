using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Triggers;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// The account's triggers as the Factory Agents fold reads them, and Pause and Resume from its pages - both through
/// the trigger service, so a page and <c>cc-devthrottle trigger</c> see one status and change one row.
/// </summary>
public static class FactoryTriggerSource
{
    /// <summary>Every trigger of the account, with the status the trigger service decides now.</summary>
    public static IReadOnlyList<FactoryTriggerFacts> Facts(TriggerService triggers, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        var facts = triggers.Store.List(tenant).Select(t => Facts(triggers.ToDto(t))).ToList();
        FileLog.Write($"[FactoryTriggerSource] Facts: triggers={facts.Count}, red={facts.Count(f => f.Red)}");
        return facts;
    }

    /// <summary>One trigger, from the service's own DTO: its status is the service's verdict, never re-decided.</summary>
    public static FactoryTriggerFacts Facts(TriggerDto t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var red = t.Status == TriggerStatusKind.Red;
        return new FactoryTriggerFacts(t.Id, t.Name, t.Factory, t.FactoryAgent, t.IntervalSeconds, t.Paused,
            red, red ? t.StatusText : null, t.LastCheckUtc, LastResult(t.LastOutcome));
    }

    /// <summary>What a trigger's last check came to, in words. Null when it has never checked.</summary>
    public static string? LastResult(string? outcome) => outcome switch
    {
        null => null,
        TriggerRunOutcome.NothingToDo => "nothing to do",
        TriggerRunOutcome.Started => "started a session",
        TriggerRunOutcome.Paused => "work waiting, paused",
        TriggerRunOutcome.SkippedRunning => "work waiting, its last session still running",
        TriggerRunOutcome.Failed => "failed",
        _ => throw new InvalidOperationException($"Trigger run outcome '{outcome}' has no words on the Factory Agents pages."),
    };

    /// <summary>
    /// Pause or resume one trigger on the owner's word. Resume goes through the service, which also releases a stuck
    /// lock and records who did it. False when there is no such trigger.
    /// </summary>
    public static async Task<bool> SetPausedAsync(TriggerService triggers, TenantId tenant, string triggerId, bool paused,
        string by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        FileLog.Write($"[FactoryTriggerSource] SetPausedAsync: trigger={triggerId}, paused={paused}, by={by}");
        var changed = paused
            ? triggers.Store.SetPaused(tenant, triggerId, true)
            : await triggers.ResumeAsync(tenant, triggerId, by, ct).ConfigureAwait(false);
        return changed is not null;
    }
}
