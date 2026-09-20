using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// The Windows component store: the folder of every version of every component Windows has
/// installed, most of whose files are hard links and whose true size is known only by asking
/// Windows.
///
/// This is the first rule that cannot measure its own subject. A directory walk of the store counts
/// the shared bytes again for every link that points at them, so a number read that way is not a
/// measurement but an estimate presented as a fact, which this tool never prints. The only honest
/// size is the one Windows' own analysis reports, and asking needs an administrator. So the rule
/// answers with what it can prove and says BROKEN when it cannot ask: a rule that cannot determine
/// the size reports a control counting nought and says it does not know, never nothing to remove.
/// On a machine where this tool is not running as an administrator - the usual case - this rule is
/// a broken instrument with the reason named, and that is the honest answer.
///
/// Nothing here runs the cleanup command. The command the rule prints is the owner's to run, and
/// the one thing this tool runs is the ANALYSIS command, which measures and removes nothing.
/// </summary>
public sealed class ComponentStoreRule : IReclaimRule
{
    /// <summary>Windows' own command that cleans the component store, printed for the owner to run.</summary>
    public const string OwnersOwnCleanupCommand = "Dism.exe /Online /Cleanup-Image /StartComponentCleanup";

    private readonly IComponentStoreAnalysis _analysis;
    private readonly string _componentStorePath;

    /// <summary>
    /// Build the rule.
    /// </summary>
    /// <param name="analysis">How Windows is asked about its store.</param>
    /// <param name="componentStorePath">The component store folder, normally C:\Windows\WinSxS.</param>
    public ComponentStoreRule(IComponentStoreAnalysis analysis, string componentStorePath)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentStorePath);

        _analysis = analysis;
        _componentStorePath = componentStorePath;
    }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public string Id => "windows-component-store";

    /// <summary>The rule's name as a person reads it.</summary>
    public string Name => "The Windows component store";

    /// <summary>The tool that made the data has its own command to clear it.</summary>
    public ProofKind Proof => ProofKind.OwnersOwnCommand;

    /// <summary>What this rule removes.</summary>
    public string WhatItRemoves =>
        $"superseded versions of components, and Windows' backups of older versions, in the component " +
        $"store at {_componentStorePath}, cleared by Windows' own command";

    /// <summary>Why removing it is safe.</summary>
    public string WhyItIsSafe =>
        "Windows ships the command that cleans its own component store, and that command is what runs: " +
        "nothing here deletes inside the store, because most of its files are hard links and only Windows " +
        "knows which bytes belong to the store alone";

    /// <summary>
    /// What is lost. It says plainly that nothing is held, because the mandate requires the "what is
    /// lost" of a rule whose proof is the owner's own command to say that the command does not offer
    /// to put anything back.
    /// </summary>
    public string WhatIsLost =>
        "superseded component versions and the backups of older ones, and with them the ability to " +
        "uninstall the Windows updates already installed. Nothing is held for this rule: Dism.exe does " +
        "not offer to put back what it clears, so once the command has run these items are gone for good";

    /// <summary>How to get it back.</summary>
    public string HowToGetItBack =>
        "there is no way back: the command clears for good and nothing is held. A removed component " +
        "version returns only if Windows Update delivers it again, which it does not";

    /// <summary>
    /// None. Windows' own command decides what is superseded when it runs, and no age this tool could
    /// read would make its decision safer.
    /// </summary>
    public int AgeGateDays => 0;

    /// <summary>Both asking Windows about the store and cleaning it need an administrator.</summary>
    public bool NeedsAdministrator => true;

    /// <summary>Windows' own cleanup command, printed for the owner to run.</summary>
    public string? CommandToRun => OwnersOwnCleanupCommand;

    /// <summary>The component store this rule is about.</summary>
    public string LooksIn => _componentStorePath;

    /// <summary>Ask Windows about its store and report. It reads; it changes nothing.</summary>
    /// <param name="context">What is being asked about, and the moment age gates are judged against.</param>
    public RuleAnswer Examine(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileLog.Write($"[ComponentStoreRule] Examine: folder={_componentStorePath}");

        if (!Directory.Exists(_componentStorePath))
        {
            return new RuleAnswer([], [],
                $"the component store {_componentStorePath} is not there, so this rule has nothing to ask Windows about");
        }

        ComponentStoreQuestion question;
        try
        {
            question = _analysis.Ask();
        }
        catch (PlatformNotSupportedException ex)
        {
            return new RuleAnswer([], [], ex.Message);
        }

        // The control the mandate names: when the size cannot be determined the control counts
        // nought, and the fold turns that into a broken instrument rather than an answer of nothing
        // to remove. The reason is carried as well, because a reader told only that a control
        // counted nought still deserves to be told why.
        if (!question.Answered)
        {
            FileLog.Write($"[ComponentStoreRule] Examine done: not answered, {question.ReasonNotAnswered}");
            return new RuleAnswer(
                [new RuleControl("component-store-size-known", 0, MustNotBeEmpty: true)],
                [],
                question.ReasonNotAnswered);
        }

        var controls = new List<RuleControl>
        {
            new("component-store-size-known", 1, MustNotBeEmpty: true)
        };

        var candidate = new ReclaimCandidate(
            _componentStorePath,
            question.BytesItsCommandCanClear,
            context.NowUtc,
            "Windows' own analysis measured this store and says its cleanup command can clear the " +
            "superseded versions and disabled features below");

        FileLog.Write(
            $"[ComponentStoreRule] Examine done: actualSize={question.ActualSizeBytes}, " +
            $"canClear={question.BytesItsCommandCanClear}");
        return new RuleAnswer(controls, [candidate]);
    }
}
