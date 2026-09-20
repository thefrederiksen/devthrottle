using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// What Windows' own Disk Cleanup offers on one volume.
///
/// Windows ships its own tool for this, and it does the whole job itself: it owns the list of
/// categories, it is the only thing that knows what each category would take, and it shows the size
/// of every category before the owner tells it to go. So this rule does the one thing left that is
/// safe to do: it reads the category list out of Windows' own registry and prints the command. It
/// offers NOTHING for removal, ever, and that is deliberate. Sizing a category here would mean
/// measuring the folder its registry entry names and calling the result what the category would
/// clear, and that is an estimate presented as a fact: a category like Windows' own temporary files
/// handler takes only files older than a week out of a folder that holds everything. The number
/// this tool could print would overstate what Windows would free, and a reader is never told more
/// will be freed than actually will be.
///
/// The categories are read from the machine every time, never typed: a typed list goes stale
/// invisibly, because a Windows update that adds a category would leave a typed list saying nothing
/// about it. Which categories need an administrator is decided from the folders each category's own
/// entry names - the ones that point outside the account's own folders need one, and the ones that
/// name no folder at all are Windows' own to size and are counted as such.
/// </summary>
public sealed class DiskCleanupRule : IReclaimRule
{
    private readonly IDiskCleanupSource _source;
    private readonly string _volumeRootPath;
    private readonly string _volumeWord;

    /// <summary>
    /// Build the rule for one volume.
    /// </summary>
    /// <param name="source">Where the categories are read from.</param>
    /// <param name="volumeRootPath">The volume this rule is about, for example C:\.</param>
    public DiskCleanupRule(IDiskCleanupSource source, string volumeRootPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRootPath);

        _source = source;
        _volumeRootPath = volumeRootPath;
        _volumeWord = volumeRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public string Id => $"windows-disk-cleanup-on-{Letter}";

    /// <summary>The rule's name as a person reads it.</summary>
    public string Name => $"What Windows' own Disk Cleanup offers on the {_volumeWord} volume";

    private string Letter =>
        _volumeWord.Length > 0 && char.IsLetter(_volumeWord[0])
            ? char.ToLowerInvariant(_volumeWord[0]).ToString(CultureInfo.InvariantCulture)
            : _volumeWord;

    /// <summary>The tool that made the data has its own command to clear it.</summary>
    public ProofKind Proof => ProofKind.OwnersOwnCommand;

    /// <summary>What this rule removes.</summary>
    public string WhatItRemoves =>
        $"whatever Windows' own Disk Cleanup offers to clear on the {_volumeWord} volume: its categories " +
        "are read from the machine's own registry list, and this tool never decides for Windows which of them apply";

    /// <summary>Why removing it is safe.</summary>
    public string WhyItIsSafe =>
        "Windows itself ships the tool that names, sizes and clears each category, and it shows the size " +
        "of each one before the owner tells it to go: nothing here sizes or clears them in its place, " +
        "because only Windows' own handlers know what they would take";

    /// <summary>What is lost.</summary>
    public string WhatIsLost =>
        "what each of Windows' own categories holds, which this tool does not know and does not guess: " +
        "Windows says the size of each one when it runs. What Windows' own tool clears is not offered for " +
        "holding, because Windows' own command does not offer to put it back";

    /// <summary>How to get it back.</summary>
    public string HowToGetItBack =>
        "there is no way back that this tool offers: Windows' own tool clears for good, and a category " +
        "such as the recycle bin or a package cache comes back only by the same means as the rule that covers it";

    /// <summary>
    /// None. Windows' own tool decides what each of its categories takes when it runs, and no age
    /// this tool could read would make its decision safer.
    /// </summary>
    public int AgeGateDays => 0;

    /// <summary>
    /// True, because some of the categories on every real machine need one - the controls say how
    /// many - and Windows' own tool asks for an administrator for those when it runs.
    /// </summary>
    public bool NeedsAdministrator => true;

    /// <summary>Windows' own tool, opened on this volume.</summary>
    public string? CommandToRun => $"cleanmgr.exe /d {_volumeWord}";

    /// <summary>
    /// The volume this rule is about. The categories are registered for the machine, and each one
    /// acts on the volume Windows' own tool is opened against, so the volume is the place.
    /// </summary>
    public string LooksIn => _volumeRootPath;

    /// <summary>Read the machine's own category list and report. It reads; it changes nothing.</summary>
    /// <param name="context">What is being asked about.</param>
    public RuleAnswer Examine(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileLog.Write($"[DiskCleanupRule] Examine: volume={_volumeWord}");

        IReadOnlyList<DiskCleanupCategory> categories;
        try
        {
            categories = _source.Read();
        }
        catch (PlatformNotSupportedException ex)
        {
            return new RuleAnswer([], [], ex.Message);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var needAdministrator = 0;
        var accountOnly = 0;
        var windowsDecides = 0;

        foreach (var category in categories)
        {
            // A category that names no folder is one whose place Windows works out when it runs,
            // and this tool does not guess for it in either direction.
            if (category.Folders.Count == 0)
            {
                windowsDecides++;
                continue;
            }

            // A category that names any folder outside the account's own folders needs an
            // administrator, because cleaning a machine-wide place or a volume root is not
            // something an ordinary account may do. Leaning towards "needs an administrator" is the
            // safe direction for a claim about somebody else's folders.
            var outsideTheAccount = category.Folders.Any(folder =>
                !RuleSelection.IsInside(folder, userProfile));

            if (outsideTheAccount) needAdministrator++;
            else accountOnly++;
        }

        var controls = new List<RuleControl>
        {
            // The list Windows itself keeps. An empty list means the registry list could not be
            // found, which is a broken instrument rather than a machine where Windows offers
            // nothing, and it must never read as the latter.
            new("categories-read", categories.Count, MustNotBeEmpty: true),
            new("categories-that-need-an-administrator", needAdministrator, MustNotBeEmpty: false),
            new("categories-that-look-only-in-the-accounts-own-folders", accountOnly, MustNotBeEmpty: false),
            new("categories-whose-folders-windows-decides-when-it-runs", windowsDecides, MustNotBeEmpty: false)
        };

        // Deliberately nothing. There is no act this rule can describe honestly: the only command
        // is Windows' own, and only Windows knows what it would take. The finding carries the
        // command and the controls, and Windows' own tool says the sizes when the owner runs it.
        FileLog.Write(
            $"[DiskCleanupRule] Examine done: volume={_volumeWord}, categories={categories.Count}, " +
            $"needAdministrator={needAdministrator}, accountOnly={accountOnly}, windowsDecides={windowsDecides}");
        return new RuleAnswer(controls, []);
    }
}
