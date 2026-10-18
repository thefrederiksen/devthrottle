using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// HOW THE GATEWAY KNOWS A CHECK REPORT IS A MAIL DESK REPORT (the Mail Desk mission, phase 1 contract, section 1):
/// the reported trigger is a DEFINITION trigger of the template's factory (<see cref="MailDeskTemplate.Factory"/>)
/// whose check path is the template's screening check (<see cref="MailDeskTemplate.CheckPath"/>). The two names are
/// held once, in the template that defines the factory.
///
/// THE SAME RULE FOR BOTH KINDS OF START. A definition can put the check on an interval start (a trigger) or on a timed
/// start (a schedule with a check); both are reported on the same Director route, and both are Mail Desk starts when
/// their factory and check path match. Each overload hands the two strings to the one rule, <see cref="Names"/>.
///
/// It is read from the start alone, never from whether a channel row exists, so a published Mail Desk factory with
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
        return trigger.IsDefinitionCheck && Names(trigger.Factory, trigger.CheckPath);
    }

    /// <summary>
    /// True when <paramref name="job"/> is a timed start of the Mail Desk screening check: a schedule with a check of the
    /// factory <see cref="MailDeskTemplate.Factory"/> whose check path is exactly <see cref="MailDeskTemplate.CheckPath"/>.
    /// A schedule with no check is never a Mail Desk start.
    /// </summary>
    public static bool Is(CronJobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.HasCheck && Names(job.Factory, job.CheckPath);
    }

    /// <summary>The one rule: the factory, folded the way every factory name is, and the check path, exactly.</summary>
    internal static bool Names(string? factory, string? checkPath)
    {
        if (!FactoryNames.TryFactory(factory, out var folded, out _)) return false;
        return string.Equals(folded, MailDeskTemplate.Factory, StringComparison.Ordinal)
               && string.Equals(checkPath, MailDeskTemplate.CheckPath, StringComparison.Ordinal);
    }
}
