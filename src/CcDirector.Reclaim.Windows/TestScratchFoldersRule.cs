using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// Scratch folders DevThrottle's own test suites leave in the temporary folder.
///
/// This is the third proof kind, and it is the narrowest of the three on purpose. The proof is not
/// "this looks like a temporary folder" - the temporary folder is full of other programs' work and
/// none of it is ours to judge. The proof is that DevThrottle's own code creates a folder by an
/// exact name, that the name is created nowhere but in a test project, that the folder is older than
/// the age gate, and that nothing in it is open.
///
/// Every name in <see cref="ScratchFolderNames"/> was found by reading where it is created in this
/// repository, and every one of them is created only inside a project whose name ends in Tests. The
/// bare name cc-director is deliberately NOT in the list although it appears in the temporary folder:
/// product code creates it too, so it fails the proof.
///
/// 90,479 of these folders existed on the measured machine before the hand cleanup. The real fix is
/// for the suites to clean up after themselves, which the mission raises separately; this rule deals
/// with what is already there.
/// </summary>
public sealed class TestScratchFoldersRule : IReclaimRule
{
    /// <summary>How old a scratch folder must be before this rule will offer it.</summary>
    public const int DefaultAgeGateDays = 7;

    /// <summary>
    /// The exact names DevThrottle's test suites create in the temporary folder. A name ending in a
    /// hyphen is a prefix followed by something that changes every run; a name without one is matched
    /// whole. Each was read out of the code that creates it, and each is created only from a test
    /// project.
    /// </summary>
    public static IReadOnlyList<string> ScratchFolderNames { get; } =
    [
        "cc-conc-",
        "cc-director-agent-tool-config-tests",
        "cc-director-harness-tests",
        "cc-director-test-",
        "cc-director-tests",
        "cc-exe-resolve-",
        "cc-hook-test-",
        "cc-instances-",
        "cc-layout-test",
        "cc-machine-",
        "cc-orphan-",
        "cc-plog-",
        "cc-voice-limits-",
        "codex-sessions-",
        "wmvfb-",
        "wmvs-"
    ];

    private readonly string _temporaryFolderPath;

    /// <summary>
    /// Build the rule.
    /// </summary>
    /// <param name="temporaryFolderPath">The temporary folder to look in.</param>
    /// <param name="ageGateDays">How old a folder must be before it is offered. Seven days by default.</param>
    public TestScratchFoldersRule(string temporaryFolderPath, int ageGateDays = DefaultAgeGateDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryFolderPath);
        ArgumentOutOfRangeException.ThrowIfNegative(ageGateDays);

        _temporaryFolderPath = temporaryFolderPath;
        AgeGateDays = ageGateDays;
    }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public string Id => "devthrottle-test-scratch-folders";

    /// <summary>The rule's name as a person reads it.</summary>
    public string Name => "DevThrottle test scratch folders in the temporary folder";

    /// <summary>We made it, by an exact name, and it is old and closed.</summary>
    public ProofKind Proof => ProofKind.WeMadeIt;

    /// <summary>What this rule removes.</summary>
    public string WhatItRemoves =>
        $"folders in {_temporaryFolderPath} created by DevThrottle's own test suites, by one of " +
        $"{ScratchFolderNames.Count.ToString(CultureInfo.InvariantCulture)} exact names, with nothing in them open";

    /// <summary>Why removing it is safe.</summary>
    public string WhyItIsSafe =>
        "each name is created by DevThrottle's own code and by nothing else, and only from a test project: " +
        "the folder held one test run's scratch work and that run ended";

    /// <summary>What is lost.</summary>
    public string WhatIsLost =>
        "the scratch files of test runs that have already finished. No test reads them again and no " +
        "product reads them at all";

    /// <summary>How to get it back.</summary>
    public string HowToGetItBack =>
        "from the holding folder until it is purged. After that, by running the suite again, which " +
        "builds its own scratch tree from nothing every time";

    /// <summary>How old a folder must be before this rule will offer it.</summary>
    public int AgeGateDays { get; }

    /// <summary>The account's own temporary folder needs no administrator.</summary>
    public bool NeedsAdministrator => false;

    /// <summary>There is no command to print: this rule's items are moved to holding.</summary>
    public string? CommandToRun => null;

    /// <summary>The temporary folder this rule looks in.</summary>
    public string LooksIn => _temporaryFolderPath;

    /// <summary>Look, count, and report. It reads; it changes nothing.</summary>
    /// <param name="context">What is being asked about, and the moment the age gate is judged against.</param>
    public RuleAnswer Examine(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileLog.Write($"[TestScratchFoldersRule] Examine: folder={_temporaryFolderPath}");

        if (!Directory.Exists(_temporaryFolderPath))
        {
            return new RuleAnswer([], [],
                $"the temporary folder {_temporaryFolderPath} is not there, so this rule has nothing to look at");
        }

        var folders = new DirectoryInfo(_temporaryFolderPath)
            .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
            .ToList();

        var oldEnough = context.NowUtc.AddDays(-AgeGateDays);
        var candidates = new List<ReclaimCandidate>();
        var matched = 0;
        var tooYoung = 0;
        var stillOpen = 0;
        var unreadable = 0;

        foreach (var folder in folders)
        {
            // A link that happens to be named like one of ours leads somewhere else entirely, and
            // this rule has said nothing about wherever that is.
            if (folder.LinkTarget is not null) continue;
            if (!IsOneOfOurs(folder.Name)) continue;

            matched++;

            var measured = Measure(folder.FullName);
            if (measured.UnreadableFolders > 0)
            {
                unreadable++;
                continue;
            }

            // The newest write anywhere inside it, not the folder's own stamp: a folder whose own
            // stamp is old can still hold a file written a minute ago.
            var newest = measured.NewestWriteUtc > new DateTimeOffset(folder.LastWriteTimeUtc, TimeSpan.Zero)
                ? measured.NewestWriteUtc
                : new DateTimeOffset(folder.LastWriteTimeUtc, TimeSpan.Zero);

            if (newest > oldEnough)
            {
                tooYoung++;
                continue;
            }

            if (measured.OpenFiles > 0)
            {
                stillOpen++;
                continue;
            }

            candidates.Add(new ReclaimCandidate(
                folder.FullName,
                measured.Bytes,
                newest,
                "created by one of DevThrottle's own test suites, older than the age gate, with nothing in it open"));
        }

        var controls = new List<RuleControl>
        {
            // The names this rule knows. An empty list would match nothing on every machine and read
            // exactly like a machine with no leftovers, so it is the one control that must not be nought.
            new("names-looked-for", ScratchFolderNames.Count, MustNotBeEmpty: true),
            new("folders-examined", folders.Count, MustNotBeEmpty: false),
            new("folders-matching-one-of-our-names", matched, MustNotBeEmpty: false),
            new("matches-too-young-to-offer", tooYoung, MustNotBeEmpty: false),
            new("matches-with-a-file-still-open", stillOpen, MustNotBeEmpty: false),
            new("matches-that-would-not-be-listed", unreadable, MustNotBeEmpty: false)
        };

        FileLog.Write(
            $"[TestScratchFoldersRule] Examine done: examined={folders.Count}, matched={matched}, " +
            $"offered={candidates.Count}, tooYoung={tooYoung}, open={stillOpen}, unreadable={unreadable}");

        return new RuleAnswer(controls, candidates);
    }

    private static bool IsOneOfOurs(string folderName) =>
        ScratchFolderNames.Any(known => known.EndsWith('-')
            ? folderName.StartsWith(known, StringComparison.OrdinalIgnoreCase)
            : folderName.Equals(known, StringComparison.OrdinalIgnoreCase));

    // Measures the folder and asks, of every file in it, whether anything has it open. A folder with
    // a file open belongs to a run that has not finished, whatever its age says.
    private static MeasuredFolder Measure(string folder)
    {
        long bytes = 0;
        long open = 0;
        long unreadable = 0;
        var newest = DateTimeOffset.MinValue;

        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            var next = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(next).EnumerateFileSystemInfos().ToList();
            }
            catch (UnauthorizedAccessException)
            {
                unreadable++;
                continue;
            }
            catch (IOException)
            {
                unreadable++;
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null) continue;

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                bytes += ((FileInfo)entry).Length;
                var written = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                if (written > newest) newest = written;
                if (IsOpen(entry.FullName)) open++;
            }
        }

        return new MeasuredFolder(
            bytes, open, unreadable, newest == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : newest);
    }

    // Asks the file system whether anything else holds the file, by asking for it exclusively for an
    // instant and giving it straight back. Nothing is read from it and nothing is written to it.
    private static bool IsOpen(string path)
    {
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Something about this file cannot be answered, and a rule that cannot answer treats the
            // file as in use. Leaning towards keeping is the whole posture of this tool.
            return true;
        }
    }

    private sealed record MeasuredFolder(
        long Bytes, long OpenFiles, long UnreadableFolders, DateTimeOffset NewestWriteUtc);
}
