using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// HOW THE GATEWAY KNOWS A CHECK REPORT IS A MAIL DESK REPORT (the Mail Desk mission, phase 1 contract, section 1):
/// the reported trigger is a DEFINITION trigger of the template's factory (<see cref="MailDeskTemplate.Factory"/>)
/// whose check path is the template's screening check (<see cref="MailDeskTemplate.CheckPath"/>). The two names are
/// held once, in the template that defines the factory.
///
/// It is read from the trigger alone, never from whether a channel row exists, so a published Mail Desk factory with
/// its channels removed still cannot fall back to "a count above zero starts the seat". Phase 3 uses the same
/// predicate for its hand-off.
/// </summary>
public static class MailDeskStart
{
    /// <summary>
    /// True when <paramref name="trigger"/> is the Mail Desk screening check: a definition trigger of the factory
    /// <see cref="MailDeskTemplate.Factory"/> (folded the way every factory name is, <see cref="FactoryNames.TryFactory"/>)
    /// whose check path is exactly <see cref="MailDeskTemplate.CheckPath"/>. A command trigger is never a Mail Desk
    /// start, whatever its factory.
    /// </summary>
    public static bool Is(TriggerEntity trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!trigger.IsDefinitionCheck) return false;
        if (!FactoryNames.TryFactory(trigger.Factory, out var factory, out _)) return false;
        return string.Equals(factory, MailDeskTemplate.Factory, StringComparison.Ordinal)
               && string.Equals(trigger.CheckPath, MailDeskTemplate.CheckPath, StringComparison.Ordinal);
    }
}
