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
