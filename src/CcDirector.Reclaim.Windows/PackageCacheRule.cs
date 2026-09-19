using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// A package cache belonging to a tool that ships its own command to clear it.
///
/// This is the second proof kind, and the whole point of it is that we do not delete inside somebody
/// else's store. Only the tool that wrote a cache knows what its own store still needs - which
/// entries are hard links, which are being written right now, which index files must be rewritten
/// when an entry goes. So the rule names the command and the command does the work. What this rule
/// contributes is the measurement and the explanation: how much is there, what is lost, and how long
/// it takes to come back.
///
/// Nothing here runs the command. This phase holds no removal code of any kind, and even when it
/// does, running somebody else's command is an act the reader asks for.
/// </summary>
public sealed class PackageCacheRule : IReclaimRule
{
    private readonly string _cacheFolderPath;

    /// <summary>
    /// Build the rule.
    /// </summary>
    /// <param name="id">The rule's identifier, in lower case with hyphens.</param>
    /// <param name="name">The rule's name as a person reads it.</param>
    /// <param name="cacheFolderPath">The cache folder this rule measures.</param>
    /// <param name="commandToRun">The command the owning tool ships to clear it.</param>
    /// <param name="whatIsLost">What is lost, in one line, including how long it takes to come back.</param>
    public PackageCacheRule(
        string id,
        string name,
        string cacheFolderPath,
        string commandToRun,
        string whatIsLost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandToRun);
        ArgumentException.ThrowIfNullOrWhiteSpace(whatIsLost);

        Id = id;
        Name = name;
        _cacheFolderPath = cacheFolderPath;
        CommandToRun = commandToRun;
        WhatIsLost = whatIsLost;
    }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public string Id { get; }

    /// <summary>The rule's name as a person reads it.</summary>
    public string Name { get; }

    /// <summary>The tool that made the data has its own command to clear it.</summary>
    public ProofKind Proof => ProofKind.OwnersOwnCommand;

    /// <summary>What this rule removes.</summary>
    public string WhatItRemoves => $"the contents of {_cacheFolderPath}, by running the command its own tool ships";

    /// <summary>Why removing it is safe.</summary>
    public string WhyItIsSafe =>
        $"the tool that wrote this cache ships a command to clear it, and that command is what runs: " +
        "nothing here deletes inside somebody else's store, because only that tool knows what its own store still needs";

    /// <summary>What is lost.</summary>
    public string WhatIsLost { get; }

    /// <summary>How to get it back.</summary>
    public string HowToGetItBack =>
        "it comes back on its own: the next build or install that needs an entry downloads it again. " +
        "Nothing is held for this rule, because the command that clears the cache does not offer to put it back";

    /// <summary>
    /// None. A cache entry the owning tool is willing to clear on demand is one it is willing to
    /// lose at any moment, so there is no age at which it becomes safer than it already is.
    /// </summary>
    public int AgeGateDays => 0;

    /// <summary>A cache in the account's own folders needs no administrator.</summary>
    public bool NeedsAdministrator => false;

    /// <summary>The command the owning tool ships to clear its own cache.</summary>
    public string? CommandToRun { get; }

    /// <summary>The cache folder this rule looks in.</summary>
    public string LooksIn => _cacheFolderPath;

    /// <summary>Look, measure, and report. It reads; it changes nothing.</summary>
    /// <param name="context">What is being asked about.</param>
    public RuleAnswer Examine(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileLog.Write($"[PackageCacheRule] Examine: rule={Id}, folder={_cacheFolderPath}");

        // The cache not being there is a real answer, not a failure: the tool that would have made it
        // is not installed on this machine, or has never cached anything. The control below says so,
        // and it does not claim the rule was broken.
        if (!Directory.Exists(_cacheFolderPath))
        {
            FileLog.Write($"[PackageCacheRule] Examine done: rule={Id}, the cache folder is not on this machine");
            return new RuleAnswer(
                [
                    new RuleControl("cache-folders-looked-for", 1, MustNotBeEmpty: true),
                    new RuleControl("cache-folders-found", 0, MustNotBeEmpty: false),
                    new RuleControl("files-measured", 0, MustNotBeEmpty: false)
                ],
                []);
        }

        var measured = Measure(_cacheFolderPath);

        // A folder inside the cache that would not be listed means the size below is short by an
        // unknown amount. A number that is short by an unknown amount is not a measurement, and this
        // tool reports measured bytes rather than an estimate presented as a fact, so the rule says
        // it could not do its work instead of printing a number it cannot stand behind.
        if (measured.UnreadableFolders > 0)
        {
            FileLog.Write($"[PackageCacheRule] Examine done: rule={Id}, unreadable={measured.UnreadableFolders}");
            return new RuleAnswer([], [],
                $"{measured.UnreadableFolders.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"folders inside {_cacheFolderPath} would not be listed, so the size of this cache is short " +
                "by an unknown amount and cannot be reported as a measurement");
        }

        var controls = new List<RuleControl>
        {
            new("cache-folders-looked-for", 1, MustNotBeEmpty: true),
            new("cache-folders-found", 1, MustNotBeEmpty: false),
            new("files-measured", measured.Files, MustNotBeEmpty: false)
        };

        // The whole cache is one thing to act on, because the command clears the whole cache. Offering
        // the files inside it one at a time would describe an act nobody can perform.
        var candidates = measured.Files == 0
            ? new List<ReclaimCandidate>()
            :
            [
                new ReclaimCandidate(
                    _cacheFolderPath,
                    measured.Bytes,
                    measured.NewestWriteUtc,
                    $"the whole cache, cleared by its own tool with: {CommandToRun}")
            ];

        FileLog.Write(
            $"[PackageCacheRule] Examine done: rule={Id}, files={measured.Files}, bytes={measured.Bytes}");
        return new RuleAnswer(controls, candidates);
    }

    // Walks the cache to measure it. Links are not followed, for the same reason the scanner does not
    // follow them: a link out of a cache leads somewhere this rule has said nothing about. A folder
    // that will not be listed is counted rather than passed over, because a walk that quietly skips
    // what it cannot read reports a smaller cache and calls it a measurement.
    private static MeasuredCache Measure(string folder)
    {
        long files = 0;
        long bytes = 0;
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

                files++;
                bytes += ((FileInfo)entry).Length;
                var written = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                if (written > newest) newest = written;
            }
        }

        return new MeasuredCache(
            files, bytes, unreadable, newest == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch : newest);
    }

    private sealed record MeasuredCache(long Files, long Bytes, long UnreadableFolders, DateTimeOffset NewestWriteUtc);
}
