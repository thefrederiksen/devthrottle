using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// HOW THE GATEWAY KNOWS A CHECK REPORT IS A MAIL DESK REPORT (the Mail Desk mission, phase 1 contract, section 1):
/// the reported trigger is a DEFINITION trigger of the factory <see cref="Factory"/> whose check path is
/// <see cref="CheckPath"/>.
///
/// It is read from the trigger alone, never from whether a channel row exists, so a published Mail Desk factory with
/// its channels removed still cannot fall back to "a count above zero starts the seat". Phase 2's template uses exactly
/// these two names; phase 3 uses the same predicate for its hand-off.
/// </summary>
public static class MailDeskStart
{
    /// <summary>The factory a Mail Desk start belongs to.</summary>
    public const string Factory = "mail-desk";

    /// <summary>The check, in the factory's definition, that screens the mailbox.</summary>
    public const string CheckPath = "checks/screen.py";

    /// <summary>
    /// True when <paramref name="trigger"/> is the Mail Desk screening check: a definition trigger of the factory
    /// <see cref="Factory"/> (folded the way every factory name is, <see cref="FactoryNames.TryFactory"/>) whose check
    /// path is exactly <see cref="CheckPath"/>. A command trigger is never a Mail Desk start, whatever its factory.
    /// </summary>
    public static bool Is(TriggerEntity trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!trigger.IsDefinitionCheck) return false;
        if (!FactoryNames.TryFactory(trigger.Factory, out var factory, out _)) return false;
        return string.Equals(factory, Factory, StringComparison.Ordinal)
               && string.Equals(trigger.CheckPath, CheckPath, StringComparison.Ordinal);
    }
}
