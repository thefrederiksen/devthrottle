using System.Globalization;
using System.Text.RegularExpressions;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Factory.Records;

/// <summary>
/// THE RULES EVERY FACTORY RECORD'S NAMES FOLLOW (Factory Definition mission, phase 2): a working document's name, an
/// attachment's name, a journal's day, the one-time upload's names, and who may write.
///
/// A RECORD'S NAME IS A RELATIVE PATH THAT CAN BE A FILE ON EVERY MACHINE. A working document and an attachment are
/// downloaded into a seat's kit when the seat starts (section 5.5), one file each, under the name stored here. So a
/// name is refused here, once, if it could not become that file: a segment Windows keeps for a device (<c>aux</c>,
/// <c>nul.txt</c>), a segment that climbs out of the folder (<c>..</c>), or a character a file name cannot hold. Case
/// is FOLDED, as factory memory folds it: <c>Ledger.json</c> beside <c>ledger.json</c> is two documents to the
/// database and one file on a Windows disk, and a seat would start a run silently missing one of them.
/// </summary>
public static partial class FactoryRecordNames
{
    /// <summary>The longest a record's name may be, all of its segments together.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The most folders deep a record's name may go.</summary>
    public const int MaxSegments = 8;

    /// <summary>The longest the one-time upload's own name for a file may be.</summary>
    public const int MaxImportKeyLength = 400;

    /// <summary>A day, as a journal entry states it.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    [GeneratedRegex("^[a-z0-9._-]+$")]
    private static partial Regex Segment();

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// Fold a record's name to its one spelling - lower case, forward slashes - and say whether it can be a file.
    /// <paramref name="what"/> is the word the refusal uses (<c>document name</c>, <c>attachment name</c>).
    /// </summary>
    public static bool TryName(string? raw, string what, out string name, out string? refusal)
    {
        name = (raw ?? "").Trim().Replace('\\', '/').ToLowerInvariant();
        refusal = null;
        if (name.Length == 0)
        {
            refusal = $"a {what} is needed";
            return false;
        }
        if (name.Length > MaxNameLength)
        {
            refusal = $"a {what} takes at most {MaxNameLength} characters, and this one is {name.Length}";
            return false;
        }
        var segments = name.Split('/');
        if (segments.Length > MaxSegments)
        {
            refusal = $"a {what} goes at most {MaxSegments} folders deep, and '{name}' goes {segments.Length}";
            return false;
        }
        foreach (var segment in segments)
        {
            // A part may start with a dot - a site's .gitignore is a real record - but may not BE a dot or two, which
            // name a folder rather than a file, and may not end with one, which Windows drops.
            if (!Segment().IsMatch(segment) || segment.EndsWith('.'))
            {
                refusal = $"a {what} is a relative path whose parts are letters, digits, dots, hyphens and underscores, " +
                          $"none of them '.' or '..' and none ending with a dot, so '{raw}' cannot be one: it becomes a file in a seat's kit";
                return false;
            }
            var stem = segment.Split('.')[0];
            if (Reserved.Contains(stem))
            {
                refusal = $"'{segment}' starts with a name Windows keeps for a device, so '{raw}' cannot become a file " +
                          "in a seat's kit; call it something else";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The seat a record belongs to, folded, or null for one kept by the factory as a whole. An empty or absent seat
    /// is the factory's; anything else must be one spelling of a seat id.
    /// </summary>
    public static bool TryOptionalSeat(string? raw, out string? seat, out string? refusal)
    {
        seat = null;
        refusal = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!FactoryNames.TrySeat(raw, out var folded, out refusal)) return false;
        seat = folded;
        return true;
    }

    /// <summary>A journal entry's day: <c>yyyy-MM-dd</c>, a real date.</summary>
    public static bool TryDate(string? raw, out string date, out string? refusal)
    {
        date = (raw ?? "").Trim();
        refusal = null;
        if (DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return true;
        refusal = $"a journal entry's day is written {DateFormat}, such as 2026-10-14, so '{raw}' cannot be one";
        return false;
    }

    /// <summary>
    /// WHO MAY WRITE, AND WHEN AN UPLOAD'S NAME GOES WITH IT. A record is written by a session of the factory, by a
    /// person, or by the one-time upload. Only the upload names its rows (<c>importKey</c>), and the upload always
    /// does: an upload row with no name could be stored twice, and an ordinary write with one could block the upload
    /// from storing the file that name belongs to. Anything else is a caller this store was never meant to be reached
    /// by, which is a defect in the route rather than a refusal to word.
    /// </summary>
    public static void RequireAWriter(string authorKind, string? importKey)
    {
        if (authorKind is not (FactoryRecordAuthorKinds.Person or FactoryRecordAuthorKinds.Session or FactoryRecordAuthorKinds.Upload))
            throw new ArgumentException($"'{authorKind}' is not a kind of writer: a factory record is written by a person, a session or the upload.", nameof(authorKind));
        var named = !string.IsNullOrWhiteSpace(importKey);
        if (authorKind == FactoryRecordAuthorKinds.Upload && !named)
            throw new ArgumentException("the upload names every row it stores; this one carried no import key.", nameof(importKey));
        if (authorKind != FactoryRecordAuthorKinds.Upload && named)
            throw new ArgumentException("only the upload names the rows it stores; a person or a session writes without an import key.", nameof(importKey));
    }

    /// <summary>An upload's name for a file: present, and short enough for its column.</summary>
    public static bool TryImportKey(string? raw, out string? importKey, out string? refusal)
    {
        importKey = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        refusal = null;
        if (importKey is not null && importKey.Length > MaxImportKeyLength)
        {
            refusal = $"an import key takes at most {MaxImportKeyLength} characters, and this one is {importKey.Length}";
            return false;
        }
        return true;
    }

    /// <summary>The innermost reason, which is the one a database driver puts the real message on.</summary>
    internal static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }
}

/// <summary>What a write to a factory's records did, or why it was refused. Shared by the three stores so the routes
/// map each one to one status, in one place.</summary>
public enum FactoryRecordOutcome
{
    /// <summary>The record was stored.</summary>
    Written,
    /// <summary>The upload's file was already stored, with the same content: nothing was added, nothing is lost.</summary>
    AlreadyStored,
    /// <summary>The upload's file was stored before with DIFFERENT content. Nothing was changed.</summary>
    ImportConflict,
    /// <summary>A working document's writer stated a version that is not its newest - two writers, or a slow reader.</summary>
    Stale,
    /// <summary>The text is empty.</summary>
    Empty,
    /// <summary>One record is over its cap.</summary>
    TooLarge,
    /// <summary>The factory's attachments together would be over their cap.</summary>
    FactoryFull,
    /// <summary>A journal entry names too many attachments.</summary>
    TooManyAttachments,
    /// <summary>A journal entry names an attachment the factory does not hold.</summary>
    NoSuchAttachment,
    /// <summary>The factory id is not one spelling of a factory id.</summary>
    BadFactory,
    /// <summary>The seat id is not one spelling of a seat id.</summary>
    BadSeat,
    /// <summary>The name, the day, the import key or the content type cannot be stored as given.</summary>
    BadName,
    /// <summary>The write failed for its own reason, and no second writer holds what it wanted.</summary>
    WriteFailed,
}
