using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// ONE SOURCE OF TRUTH: THE ROUTE REFUSALS (Factory Definition mission, phase 4; mission section 5.3). Once a factory
/// has a published definition, the definition is what the factory is - its registration, its schedules and its
/// triggers are written by a publish. Every other route that would define it another way (register it, set its
/// purpose, add, edit, delete, link or unlink a schedule of it, add, edit or delete a trigger of it) is refused with
/// the ONE sentence below, so a person or an agent is sent to the definition instead of making a factory the
/// definition does not describe.
///
/// What OPERATES a factory is never refused here: switching a schedule on or off, pausing and resuming a trigger,
/// running a schedule now, archiving and restoring the factory, and posting goal numbers.
///
/// The routes ask <see cref="FactoryDefinitionStore.IsDefined"/>; this class holds only the words, the outcome and
/// the one rule that tells an edit from a switch.
/// </summary>
public static class DefinedByDefinition
{
    /// <summary>The refusal, exactly as every route answers it and the command line prints it.</summary>
    public const string Sentence = "This factory is defined by its definition; edit the definition.";

    /// <summary>The outcome the refusal carries beside the sentence, so a caller can tell it from any other 409.</summary>
    public const string Outcome = "DefinedByDefinition";

    /// <summary>The 409 a route answers when its write touches a defined factory.</summary>
    public static IResult Refusal(string route, string factory)
    {
        FileLog.Write($"[DefinedByDefinition] {route}: REFUSED - {factory} is defined by its published definition");
        return Results.Json(new { error = Sentence, outcome = Outcome }, statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// True when <paramref name="incoming"/> differs from <paramref name="stored"/> in nothing a schedule definition
    /// holds except <see cref="CronJobDto.Enabled"/>: that PUT is a switch on or off, which is how
    /// <c>cc-devthrottle schedule enable|disable</c> and the Cockpit's switch work - they re-send the stored job with
    /// the one field changed - and switching is operating the factory, never defining it.
    ///
    /// Every field the store writes from a body (<c>CronJobStore.ApplyDefinition</c>) is compared. A blank factory or
    /// seat in the body means "said nothing" on this route and keeps the stored one, so it is the same value here.
    /// Fields the Gateway only stamps for display (schedule text, labels, shifts) are not part of a definition and are
    /// not compared. <paramref name="incoming"/>'s factory must already be settled by the factory-naming gate.
    /// </summary>
    public static bool DiffersOnlyInEnabled(CronJobDto stored, CronJobDto incoming)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(incoming);
        var factory = string.IsNullOrWhiteSpace(incoming.Factory) ? stored.Factory : incoming.Factory;
        var seat = string.IsNullOrWhiteSpace(incoming.Seat) ? stored.Seat : incoming.Seat;
        return Same(stored.Name, incoming.Name)
            && Same(stored.ScheduleKind, incoming.ScheduleKind)
            && Same(stored.CronExpression, incoming.CronExpression)
            && Same(stored.RunAt, incoming.RunAt)
            && Same(stored.TimeZoneId, incoming.TimeZoneId)
            && Same(stored.Factory, factory)
            && SameSeat(stored.Seat, seat)
            && Same(stored.Target?.Machine, incoming.Target?.Machine)
            && Same(stored.Action?.RepoPath, incoming.Action?.RepoPath)
            && Same(stored.Action?.Seed, incoming.Action?.Seed)
            && Same(stored.Action?.WorkListName, incoming.Action?.WorkListName)
            && stored.Action?.AutoDismiss == incoming.Action?.AutoDismiss
            && stored.PreventOverlap == incoming.PreventOverlap
            && CronNotify.Normalize(stored.NotifyOn) == CronNotify.Normalize(incoming.NotifyOn)
            && Same(stored.NotifyWebhookUrl, incoming.NotifyWebhookUrl);
    }

    /// <summary>Ordinal, with null and empty the same: the JSON a client re-sends may spell an absent text either way.</summary>
    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    /// <summary>A seat is the same when it folds to the stored one, the rule the schedule's own seat check uses.</summary>
    private static bool SameSeat(string? stored, string? incoming)
        => Same(stored, incoming) || (FactoryNames.TrySeat(incoming, out var folded, out _) && folded == stored);
}
