using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Reclaim.Scanning;

namespace CcCleanupStorage;

/// <summary>A folder that refused its listing, with the plain words for why beside the code.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Reason">Plain words for why the listing was refused.</param>
/// <param name="Code">The number the file system gave.</param>
public sealed record RefusedFolderJson(string Path, string Reason, int Code);

/// <summary>What one folder holds.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Depth">How far below the scan root it sits.</param>
/// <param name="BytesSeen">Bytes seen in it and everything under it.</param>
/// <param name="FilesSeen">Files seen in it and everything under it.</param>
public sealed record FolderJson(string Path, int Depth, long BytesSeen, long FilesSeen);

/// <summary>
/// A whole report, as a machine reads it. Every field the report holds is here: this is the shape
/// other code parses, so it does not shrink to suit a screen and it does not change when a filter is
/// applied - the rows change, the shape does not.
/// </summary>
public sealed record ReportJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True when the command succeeded and the report can be believed.</summary>
    public required bool Ok { get; init; }

    /// <summary>Either "ok" or "broken".</summary>
    public required string Verdict { get; init; }

    /// <summary>Why the scan is a broken instrument, or null.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>The folder that was scanned.</summary>
    public required string RootPath { get; init; }

    /// <summary>True when that folder is the root of its own volume.</summary>
    public required bool RootIsVolumeRoot { get; init; }

    /// <summary>Where the scan is saved, or null when nothing was saved.</summary>
    public required string? IndexPath { get; init; }

    /// <summary>When the walk started.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>When the walk finished.</summary>
    public required DateTimeOffset FinishedUtc { get; init; }

    /// <summary>How long the walk took.</summary>
    public required double ElapsedSeconds { get; init; }

    /// <summary>Ordinary files seen.</summary>
    public required long FilesSeen { get; init; }

    /// <summary>Folders seen, not counting the scan root.</summary>
    public required long FoldersSeen { get; init; }

    /// <summary>Bytes of ordinary files seen.</summary>
    public required long BytesSeen { get; init; }

    /// <summary>
    /// The bytes the volume counts as used that the scan did not see, or null when the volume would
    /// not say how much of it is used.
    /// </summary>
    public required long? UnseenBytes { get; init; }

    /// <summary>What the volume says about itself.</summary>
    public required VolumeUsage Volume { get; init; }

    /// <summary>Cloud placeholder files seen.</summary>
    public required long PlaceholderFiles { get; init; }

    /// <summary>The bytes those placeholders hold in a cloud store, none of them on this disk.</summary>
    public required long PlaceholderBytesInCloud { get; init; }

    /// <summary>Every link and junction found, and none of them were followed.</summary>
    public required IReadOnlyList<FoundLink> Links { get; init; }

    /// <summary>Every folder that refused its listing.</summary>
    public required IReadOnlyList<RefusedFolderJson> RefusedFolders { get; init; }

    /// <summary>The largest folders, biggest first, as many as were asked for.</summary>
    public required IReadOnlyList<FolderJson> LargestFolders { get; init; }

    /// <summary>The report itself, in finished sentences, exactly as the text answer prints them.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>One saved scan, as the list of saved scans gives it.</summary>
/// <param name="RootPath">The folder that was scanned.</param>
/// <param name="ScannedUtc">When the scan was saved.</param>
/// <param name="BytesSeen">The bytes that scan saw.</param>
/// <param name="FilesSeen">The files that scan saw.</param>
/// <param name="IndexPath">The file it is saved in.</param>
public sealed record SavedScanJson(
    string RootPath,
    DateTimeOffset ScannedUtc,
    long BytesSeen,
    long FilesSeen,
    string IndexPath);

/// <summary>A file in the index folder that is not a saved scan.</summary>
/// <param name="IndexPath">The file.</param>
/// <param name="Reason">Why it could not be read.</param>
public sealed record UnreadableIndexJson(string IndexPath, string Reason);

/// <summary>The saved scans on this machine, as a machine reads them.</summary>
public sealed record SavedScansJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True: a listing with nothing in it is still a listing.</summary>
    public required bool Ok { get; init; }

    /// <summary>The folder saved scans live in.</summary>
    public required string IndexDirectory { get; init; }

    /// <summary>How many saved scans were read.</summary>
    public required int Count { get; init; }

    /// <summary>The saved scans.</summary>
    public required IReadOnlyList<SavedScanJson> Scans { get; init; }

    /// <summary>The files in the same folder that are not saved scans.</summary>
    public required IReadOnlyList<UnreadableIndexJson> Unreadable { get; init; }
}

/// <summary>
/// One command this tool offers, as the help page gives it.
///
/// The entry carries how the command is called, so nothing has to be pieced together from two lists
/// that happen to be in the same order, and a name that is not a word anybody can type cannot be
/// mistaken for one.
/// </summary>
/// <param name="Name">
/// The name this command answers under: the value the command field carries in every answer it
/// gives. It is not always something that can be typed - the tool run with no command word answers
/// under the name saved-scans, and there is no command word saved-scans. Word is what is typed.
/// </param>
/// <param name="Word">
/// The command word to type, or empty when this command is the tool run with no command word.
/// </param>
/// <param name="Invocation">How the command is called, as a whole line, flags included.</param>
/// <param name="Purpose">What the command does, in one line.</param>
/// <param name="Flags">Every flag the command takes, exactly as the command line reader knows them.</param>
public sealed record HelpCommandJson(
    string Name,
    string Word,
    string Invocation,
    string Purpose,
    IReadOnlyList<string> Flags);

/// <summary>One flag, as the help page gives it.</summary>
/// <param name="Name">The flag as it is typed, with the value it takes after it.</param>
/// <param name="Purpose">What the flag does, in one line.</param>
public sealed record HelpFlagJson(string Name, string Purpose);

/// <summary>One exit code, as the help page gives it.</summary>
/// <param name="Code">The number.</param>
/// <param name="Purpose">What it means, in one line.</param>
public sealed record ExitCodeJson(int Code, string Purpose);

/// <summary>
/// The help page, as a machine reads it. Every section the text page prints is here as its own
/// field, so asking for the page in machine-readable form loses nothing.
/// </summary>
public sealed record HelpJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True: the help page is always an answer.</summary>
    public required bool Ok { get; init; }

    /// <summary>Every way of calling the tool, one line each.</summary>
    public required IReadOnlyList<string> Usage { get; init; }

    /// <summary>The commands, and the flags each one takes.</summary>
    public required IReadOnlyList<HelpCommandJson> Commands { get; init; }

    /// <summary>Every flag, and what each one does.</summary>
    public required IReadOnlyList<HelpFlagJson> Flags { get; init; }

    /// <summary>Every exit code, and what each one means.</summary>
    public required IReadOnlyList<ExitCodeJson> ExitCodes { get; init; }

    /// <summary>
    /// The rest of the page: what a report always says, and what this tool does not do.
    /// </summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>Anything that went wrong, as a machine reads it.</summary>
public sealed record ErrorJson
{
    /// <summary>The command that was asked for.</summary>
    public required string Command { get; init; }

    /// <summary>Always false.</summary>
    public required bool Ok { get; init; }

    /// <summary>A short word an agent can branch on: usage, folder-not-found, no-saved-scan, and so on.</summary>
    public required string Code { get; init; }

    /// <summary>What went wrong, in one line, and what to do about it.</summary>
    public required string Message { get; init; }
}

/// <summary>The one place this tool decides how machine-readable text is shaped.</summary>
public static class JsonShape
{
    /// <summary>
    /// Indented, with names in the usual style for this kind of output, and every enumeration written
    /// as its name rather than as a number - a number in a saved answer means nothing to the reader
    /// and changes meaning the day somebody adds a value in the middle.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>One control on a rule's answer, as a machine reads it.</summary>
/// <param name="Name">What was counted.</param>
/// <param name="Count">How many.</param>
/// <param name="MustNotBeEmpty">
/// True when a count of nought means the rule could not do its work rather than that there is
/// nothing to remove.
/// </param>
public sealed record RuleControlJson(string Name, long Count, bool MustNotBeEmpty);

/// <summary>One thing a rule proved disposable, as a machine reads it.</summary>
/// <param name="Path">The full path of the item.</param>
/// <param name="Bytes">The bytes it occupies on this disk.</param>
/// <param name="SizeInWords">The same size in the words a report prints.</param>
/// <param name="LastWrittenUtc">When it was last written.</param>
/// <param name="Why">Why this particular item passed the rule.</param>
public sealed record ReclaimCandidateJson(
    string Path,
    long Bytes,
    string SizeInWords,
    DateTimeOffset LastWrittenUtc,
    string Why);

/// <summary>
/// What one rule found, as a machine reads it.
///
/// Every part of the answer is its own field, because a recommendation that says only how many bytes
/// could be freed is not one. A caller deciding whether to act needs the proof, what is lost and how
/// to get it back as much as it needs the number, and it must never have to read them out of a
/// sentence.
/// </summary>
public sealed record RuleFindingJson
{
    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public required string Rule { get; init; }

    /// <summary>The rule's name as a person reads it.</summary>
    public required string Name { get; init; }

    /// <summary>Which of the three proofs this rule holds.</summary>
    public required string Proof { get; init; }

    /// <summary>The proof in the words a report prints.</summary>
    public required string ProofInWords { get; init; }

    /// <summary>Either "ok" or "broken".</summary>
    public required string Verdict { get; init; }

    /// <summary>True when the rule did its work and its answer can be acted on.</summary>
    public required bool Ok { get; init; }

    /// <summary>Why the rule is a broken instrument, or null.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>What this rule removes.</summary>
    public required string WhatItRemoves { get; init; }

    /// <summary>Why removing it is safe.</summary>
    public required string WhyItIsSafe { get; init; }

    /// <summary>What is lost by removing it.</summary>
    public required string WhatIsLost { get; init; }

    /// <summary>How to get it back.</summary>
    public required string HowToGetItBack { get; init; }

    /// <summary>How old an item must be before this rule will touch it, in days; nought for no gate.</summary>
    public required int AgeGateDays { get; init; }

    /// <summary>True when acting on this rule needs an administrator.</summary>
    public required bool NeedsAdministrator { get; init; }

    /// <summary>The exact command the owner runs, or null when there is none.</summary>
    public required string? CommandToRun { get; init; }

    /// <summary>Everything the rule counted while reaching its answer.</summary>
    public required IReadOnlyList<RuleControlJson> Controls { get; init; }

    /// <summary>What the rule proved disposable. Always empty on a broken rule.</summary>
    public required IReadOnlyList<ReclaimCandidateJson> Candidates { get; init; }

    /// <summary>How many items this rule offers.</summary>
    public required int ItemsOffered { get; init; }

    /// <summary>The bytes those items hold.</summary>
    public required long Bytes { get; init; }

    /// <summary>The rule's own lines, exactly as the text answer prints them.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>A rule that was not run, and where it looks instead.</summary>
/// <param name="Rule">The rule's identifier.</param>
/// <param name="Name">The rule's name as a person reads it.</param>
/// <param name="LooksIn">The folder it looks in.</param>
public sealed record RuleNotRunJson(string Rule, string Name, string LooksIn);

/// <summary>A whole set of recommendations, as a machine reads it.</summary>
public sealed record RecommendJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True when the recommendations can be believed.</summary>
    public required bool Ok { get; init; }

    /// <summary>Either "ok" or "broken".</summary>
    public required string Verdict { get; init; }

    /// <summary>Why the recommendations are a broken instrument, or null.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>The folder they are about.</summary>
    public required string RootPath { get; init; }

    /// <summary>Where the saved scan they rest on lives.</summary>
    public required string IndexPath { get; init; }

    /// <summary>When that scan was saved.</summary>
    public required DateTimeOffset ScannedUtc { get; init; }

    /// <summary>How many rules were run.</summary>
    public required int RulesRun { get; init; }

    /// <summary>How many of those could not do their work and therefore offer nothing.</summary>
    public required int RulesBroken { get; init; }

    /// <summary>
    /// The machine's rules that were not run because they look outside the folder asked about. They
    /// are named rather than absent, so a smaller answer is never mistaken for a cleaner disk.
    /// </summary>
    public required IReadOnlyList<RuleNotRunJson> RulesNotRun { get; init; }

    /// <summary>
    /// True when the rules measured more bytes than the scan saw, which means the unclassified count
    /// below is a floor rather than a measurement and is not to be read as one.
    /// </summary>
    public required bool RulesSawMoreThanTheScan { get; init; }

    /// <summary>How many items are offered across every rule.</summary>
    public required long ItemsOffered { get; init; }

    /// <summary>The bytes every rule that did its work proved disposable, added together.</summary>
    public required long ReclaimableBytes { get; init; }

    /// <summary>
    /// The bytes the scan saw that no rule matched. Never offered for removal, whatever they are.
    /// </summary>
    public required long UnclassifiedBytes { get; init; }

    /// <summary>
    /// The bytes the volume counts as used that the scan did not see, or null when the volume would
    /// not say. This is the unseen gap, and it is carried here because recommendations made on a
    /// scan that could not see a third of the disk are not a complete answer.
    /// </summary>
    public required long? UnseenBytes { get; init; }

    /// <summary>What the volume says about itself.</summary>
    public required VolumeUsage Volume { get; init; }

    /// <summary>The scan report's own words about how far it reached.</summary>
    public required IReadOnlyList<string> ReachLines { get; init; }

    /// <summary>What every rule found, in the order the rules were run.</summary>
    public required IReadOnlyList<RuleFindingJson> Rules { get; init; }

    /// <summary>The whole answer in finished sentences, exactly as the text answer prints them.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>One refusal check's outcome about one item, as a machine reads it.</summary>
/// <param name="Check">The check's number, one to ten.</param>
/// <param name="Name">The check's plain name.</param>
/// <param name="Outcome">"passed", "refused" or "not-reached".</param>
/// <param name="Reason">Why it refused, when it did, or null.</param>
public sealed record CheckResultJson(int Check, string Name, string Outcome, string? Reason);

/// <summary>One item's whole answer: the gate's ten outcomes and what the run did about it.</summary>
public sealed record ReclaimItemJson
{
    /// <summary>The item's path, as the recommendation spelled it.</summary>
    public required string Path { get; init; }

    /// <summary>The rule that proved it disposable.</summary>
    public required string RuleId { get; init; }

    /// <summary>Which of the three proofs the rule holds.</summary>
    public required string Proof { get; init; }

    /// <summary>The bytes the recommendation measured.</summary>
    public required long RecommendedBytes { get; init; }

    /// <summary>True when every item-level check passed, whatever the run's apply state.</summary>
    public required bool Eligible { get; init; }

    /// <summary>Which refusal fired, by its number, or null when none did.</summary>
    public required int? FiredCheck { get; init; }

    /// <summary>The fired refusal's reason, or null.</summary>
    public required string? Reason { get; init; }

    /// <summary>A refusal that is none of the ten checks - a broken holding configuration - or null.</summary>
    public required string? ConfigurationReason { get; init; }

    /// <summary>All ten checks, in the mandate's numbered order, so a refusal that was never reached is never read as one that passed.</summary>
    public required IReadOnlyList<CheckResultJson> Checks { get; init; }

    /// <summary>True when the item moved into holding. Never true in a dry run.</summary>
    public required bool Moved { get; init; }

    /// <summary>The holding entry the item moved into, when it moved.</summary>
    public required string? HoldingEntryId { get; init; }

    /// <summary>True when the owner's own cleanup command ran for this item.</summary>
    public required bool OwnersCommandRan { get; init; }

    /// <summary>The owner's command's exit code, when it ran.</summary>
    public required int? OwnersCommandExitCode { get; init; }

    /// <summary>Why the item did not move even though the gate passed it, or the incomplete-entry warning, or null.</summary>
    public required string? OutcomeReason { get; init; }
}

/// <summary>A whole reclaim answer, as a machine reads it.</summary>
public sealed record ReclaimJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True when the command succeeded.</summary>
    public required bool Ok { get; init; }

    /// <summary>True only for an apply run. A dry run is the default.</summary>
    public required bool Apply { get; init; }

    /// <summary>The folder the run was asked about.</summary>
    public required string RootPath { get; init; }

    /// <summary>The holding root the run moves into.</summary>
    public required string HoldingRootPath { get; init; }

    /// <summary>The one rule the run was narrowed to, or null.</summary>
    public required string? RuleId { get; init; }

    /// <summary>Why the whole answer means nothing, or null.</summary>
    public required string? BrokenReason { get; init; }

    /// <summary>What every rule found at recommendation time.</summary>
    public required IReadOnlyList<RuleFindingJson> Rules { get; init; }

    /// <summary>The rules that were not run, each with the folder it looks in.</summary>
    public required IReadOnlyList<RuleNotRunJson> RulesNotRun { get; init; }

    /// <summary>Every item the rules offered, with the gate's answer for each.</summary>
    public required IReadOnlyList<ReclaimItemJson> Items { get; init; }

    /// <summary>The candidate bytes measured before anything happened.</summary>
    public required long CandidateBytesBefore { get; init; }

    /// <summary>The candidate bytes measured after everything happened.</summary>
    public required long CandidateBytesAfter { get; init; }

    /// <summary>The bytes of the items that actually moved into holding.</summary>
    public required long BytesMoved { get; init; }

    /// <summary>What the volume said before anything happened.</summary>
    public required VolumeUsage VolumeBefore { get; init; }

    /// <summary>What the volume said after everything happened.</summary>
    public required VolumeUsage VolumeAfter { get; init; }

    /// <summary>The whole answer in finished sentences, exactly as the text answer prints them.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
}

/// <summary>One holding entry, as a machine reads it.</summary>
/// <param name="EntryId">The entry folder's name.</param>
/// <param name="OriginalPath">Where the item came from.</param>
/// <param name="Name">The item's own name.</param>
/// <param name="Bytes">The bytes measured at the move.</param>
/// <param name="Rule">The rule that proved it disposable.</param>
/// <param name="MovedAtUtc">When it moved.</param>
/// <param name="PurgeNotBeforeUtc">When it becomes purgeable.</param>
/// <param name="State">"held" or "moving".</param>
public sealed record HoldingEntryJson(
    string EntryId,
    string OriginalPath,
    string Name,
    long Bytes,
    string Rule,
    DateTimeOffset MovedAtUtc,
    DateTimeOffset PurgeNotBeforeUtc,
    string State);

/// <summary>An entry a purge kept, and why.</summary>
/// <param name="EntryId">The entry that was kept.</param>
/// <param name="Reason">Why it was kept and named rather than assumed.</param>
public sealed record KeptEntryJson(string EntryId, string Reason);

/// <summary>A whole holding listing, as a machine reads it.</summary>
public sealed record HoldingListJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True when the holding root could be read, or did not need to be.</summary>
    public required bool Ok { get; init; }

    /// <summary>True when the holding root exists on the disk. An empty holding is an honest answer.</summary>
    public required bool RootExists { get; init; }

    /// <summary>The holding root this listing is of.</summary>
    public required string HoldingRootPath { get; init; }

    /// <summary>Why the root could not be read, when it could not.</summary>
    public required string? UnreadableReason { get; init; }

    /// <summary>How many complete entries there are.</summary>
    public required int Count { get; init; }

    /// <summary>The entries whose records say the move completed.</summary>
    public required IReadOnlyList<HoldingEntryJson> Entries { get; init; }

    /// <summary>
    /// The entries whose records say the move never completed, listed separately and named, never
    /// silently counted among the held and never silently dropped.
    /// </summary>
    public required IReadOnlyList<HoldingEntryJson> Incomplete { get; init; }
}

/// <summary>A restore answer, as a machine reads it.</summary>
public sealed record HoldingRestoreJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True when the item was put back or was already home.</summary>
    public required bool Ok { get; init; }

    /// <summary>The entry that was asked for.</summary>
    public required string EntryId { get; init; }

    /// <summary>Where the record says the item came from, when the record could be read.</summary>
    public required string? OriginalPath { get; init; }

    /// <summary>True when the item was moved back.</summary>
    public required bool Restored { get; init; }

    /// <summary>True when the move never happened and the item was already home; the entry is cleared.</summary>
    public required bool AlreadyHome { get; init; }

    /// <summary>Why nothing was restored, or null. Restore never overwrites.</summary>
    public required string? RefusalReason { get; init; }
}

/// <summary>A purge answer, as a machine reads it.</summary>
public sealed record HoldingPurgeJson
{
    /// <summary>The command that produced this answer.</summary>
    public required string Command { get; init; }

    /// <summary>True when the purge call succeeded at what it was asked for.</summary>
    public required bool Ok { get; init; }

    /// <summary>True when entries were removed; false when this was the dry run.</summary>
    public required bool Applied { get; init; }

    /// <summary>The holding root.</summary>
    public required string HoldingRootPath { get; init; }

    /// <summary>Entries past their holding period, which an apply removes.</summary>
    public required IReadOnlyList<HoldingEntryJson> Purgeable { get; init; }

    /// <summary>Entries not yet past their holding period, which no apply touches.</summary>
    public required IReadOnlyList<HoldingEntryJson> NotYetPurgeable { get; init; }

    /// <summary>Entries whose records say the move never completed. Purge always refuses them.</summary>
    public required IReadOnlyList<HoldingEntryJson> Incomplete { get; init; }

    /// <summary>Entries that could not be answered and were kept, each with why.</summary>
    public required IReadOnlyList<KeptEntryJson> Kept { get; init; }

    /// <summary>How many entries this call removed, when it applied.</summary>
    public required int PurgedCount { get; init; }
}
