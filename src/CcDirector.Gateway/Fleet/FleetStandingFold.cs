using System.Globalization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// The owner's lessons and standing preferences, folded for the Fleet Manager page (issue #3559, part 4). Every label,
/// sentence and whether each action is offered is decided here; the page renders it as sent (CLAUDE.md rule 7).
///
/// WHO MAY DO WHAT is the routes' rule, mirrored here so the page never offers a button the Gateway would refuse:
/// the page is the owner's own device, which may confirm, edit and remove any row. Confirm is offered only on a
/// lesson that is not yet confirmed, and not while the account already holds the most confirmed lessons it may -
/// then the row says so, and how to make room, instead.
/// </summary>
internal static class FleetStandingFold
{
    /// <param name="fleetManagerRunning">Whether the account's marked Fleet Manager is running now - what the sentence
    /// after keeping a lesson may claim.</param>
    public static FleetStandingDto Fold(IReadOnlyList<FleetPreferenceDto> lessons, IReadOnlyList<FleetPreferenceDto> preferences,
        TimeZoneInfo tz, bool fleetManagerRunning)
    {
        ArgumentNullException.ThrowIfNull(lessons);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(tz);

        var confirmed = lessons.Count(l => l.ConfirmedByOwnerAtUtc is not null);
        var waiting = lessons.Count - confirmed;
        var full = confirmed >= FleetPreferenceStore.MaxConfirmedLessons;
        // Only a removal makes room: an edit keeps a lesson's confirmation.
        var fullNote = $"{FleetPreferenceStore.MaxConfirmedLessons} lessons are confirmed, the most an account may hold. "
                       + "Remove one below to make room.";
        var total = lessons.Count + preferences.Count;

        return new FleetStandingDto
        {
            Mistake = new FleetManagerActionDto
            {
                Offered = !full,
                Label = "That was a mistake",
                BusyLabel = "Keeping...",
                Note = full ? fullNote : null,
            },
            BoxTitle = "What should the Fleet Manager never do again?",
            BoxNote = "Type or paste your correction. It is kept in your words, exactly, with today's date. The Fleet "
                      + "Manager is told at once, and every Fleet Manager after it obeys it before anything else.",
            BoxPlaceholder = "For example: never message every session when none is stuck.",
            MaxLessonLength = FleetPreferenceStore.MaxLessonLength,
            KeepLabel = "Keep",
            KeepBusyLabel = "Keeping...",
            KeptSentence = fleetManagerRunning
                ? "Kept. The Fleet Manager is told at its next free moment, and every Fleet Manager after it obeys it."
                : "Kept. No Fleet Manager is running now; the next one is given it when it starts.",
            SaveLabel = "Save",
            SaveBusyLabel = "Saving...",
            CancelLabel = "Cancel",
            ShowLabel = $"Lessons and preferences ({total})",
            HideLabel = "Hide lessons and preferences",
            WaitingNote = waiting == 0 ? null : $"{Plural(waiting, "lesson")} {(waiting == 1 ? "waits" : "wait")} for you to confirm.",
            Lessons = new FleetStandingSectionDto
            {
                Title = "Lessons",
                Count = lessons.Count,
                Note = $"Your corrections. Only confirmed lessons are given to the Fleet Manager - {confirmed} of at most "
                       + $"{FleetPreferenceStore.MaxConfirmedLessons}.",
                EmptyText = lessons.Count == 0 ? "No lessons yet. When the Fleet Manager gets something wrong, press "
                                                 + "\"That was a mistake\" and say what it should do instead." : null,
                // Waiting for the owner first: they are the ones that need a press.
                Rows = lessons
                    .OrderBy(l => l.ConfirmedByOwnerAtUtc is null ? 0 : 1)
                    .ThenBy(l => l.CreatedAtUtc)
                    .Select(l => LessonRow(l, tz, full, fullNote))
                    .ToList(),
            },
            Preferences = new FleetStandingSectionDto
            {
                Title = "Standing preferences",
                Count = preferences.Count,
                Note = "How you want things done. The Fleet Manager reads them at the start of every conversation.",
                EmptyText = preferences.Count == 0 ? "No standing preferences. Tell the Fleet Manager how you want "
                                                     + "something done and it keeps it here." : null,
                Rows = preferences.OrderBy(p => p.CreatedAtUtc).Select(p => PreferenceRow(p, tz)).ToList(),
            },
        };
    }

    private static FleetStandingRowDto LessonRow(FleetPreferenceDto l, TimeZoneInfo tz, bool full, string fullNote)
    {
        var isConfirmed = l.ConfirmedByOwnerAtUtc is not null;
        return new FleetStandingRowDto
        {
            Id = l.Id,
            Kind = FleetPreferenceStore.KindLesson,
            Text = l.Text,
            MistakeLine = string.IsNullOrEmpty(l.Mistake) ? null : "What went wrong: " + l.Mistake,
            KeptLine = KeptLine(l, tz),
            MaxLength = FleetPreferenceStore.MaxLessonLength,
            RemoveAction = "remove this lesson",
            StatusLine = isConfirmed
                ? $"Confirmed {Day(l.ConfirmedByOwnerAtUtc!.Value, tz)} - every Fleet Manager obeys it"
                : "Kept by the Fleet Manager - confirm? Until you do, it is not given to a later Fleet Manager.",
            Tone = isConfirmed ? FleetStandingRowDto.ToneConfirmed : FleetStandingRowDto.ToneWaiting,
            Confirm = isConfirmed
                ? new FleetManagerActionDto()
                : new FleetManagerActionDto
                {
                    Offered = !full,
                    Label = "Confirm",
                    BusyLabel = "Confirming...",
                    Note = full ? fullNote : null,
                },
            Edit = new FleetManagerActionDto
            {
                Offered = true,
                Label = "Edit",
                BusyLabel = "Saving...",
                // An edit of a confirmed lesson reaches the running Fleet Manager as a fresh lesson event.
                Note = isConfirmed ? "The Fleet Manager is told the new words, and every Fleet Manager after it obeys them." : null,
            },
            Remove = new FleetManagerActionDto
            {
                Offered = true,
                Label = "Remove",
                BusyLabel = "Removing...",
                ConfirmTitle = "Remove this lesson?",
                ConfirmMessage = "No Fleet Manager is given it again. A Fleet Manager running now keeps what it was "
                                 + "already told until it starts again.",
            },
        };
    }

    private static FleetStandingRowDto PreferenceRow(FleetPreferenceDto p, TimeZoneInfo tz) => new()
    {
        Id = p.Id,
        Kind = FleetPreferenceStore.KindPreference,
        Text = p.Text,
        KeptLine = KeptLine(p, tz),
        MaxLength = FleetPreferenceStore.MaxTextLength,
        RemoveAction = "remove this preference",
        Tone = FleetStandingRowDto.TonePreference,
        Confirm = new FleetManagerActionDto(),
        Edit = new FleetManagerActionDto { Offered = true, Label = "Edit", BusyLabel = "Saving..." },
        Remove = new FleetManagerActionDto
        {
            Offered = true,
            Label = "Remove",
            BusyLabel = "Removing...",
            ConfirmTitle = "Remove this preference?",
            ConfirmMessage = "The Fleet Manager no longer reads it at the start of a conversation.",
        },
    };

    private static string KeptLine(FleetPreferenceDto row, TimeZoneInfo tz)
        => $"Kept {Day(row.CreatedAtUtc, tz)} by "
           + (row.CreatedBy == FleetOutcomeStore.OwnerCaller ? "you" : "the Fleet Manager");

    internal static string Day(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz)
            .ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
