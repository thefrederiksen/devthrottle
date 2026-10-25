using System.Globalization;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The machinery that asks Windows about its component store, proven with a harmless command the
/// test supplies.
///
/// Nothing in this file runs Windows' own analysis command. The command that runs is one the test
/// builds itself - a shell command that prints a report the test wrote to a file in its own
/// fixture tree - so what is proven here is the machinery itself: starting the command, waiting for
/// it, reading its report, and turning the two sizes it prints into bytes. The real command is
/// proven only by the read-only run on the machine, in the phase proof.
///
/// The elevation question is answered by a test that supplies its own answer, so no test ever
/// starts the real command whatever this process happens to be running as.
/// </summary>
public class DismComponentStoreAnalysisTests
{

    /// <summary>
    /// The explorer-reported size is NOT the store's size, and must not be taken for it: it is the
    /// number a directory walk would produce, counting hard links again for every link that points
    /// at the same bytes.
    /// </summary>
    [Fact]
    public void Parse_AReportWhoseExplorerSizeDiffersFromTheActualSize_TakesOnlyTheActualSize()
    {
        var question = DismComponentStoreAnalysis.ParseReport(
            "Windows Explorer Reported Size of Component Store: 9.20 GB\r\n" +
            "Actual Size of Component Store: 8.00 GB\r\n" +
            "Backups and Disabled Features: 1.50 GB\r\n");

        Assert.NotNull(question);
        Assert.Equal((long)(8.00 * 1024 * 1024 * 1024), question!.ActualSizeBytes);
    }

    [Fact]
    public void Parse_AReportWithoutTheTwoSizes_IsNotAnsweredRatherThanGuessed()
    {
        Assert.Null(DismComponentStoreAnalysis.ParseReport(
            "Deployment Image Servicing and Management tool\r\n" +
            "Some other line: 12.00 GB\r\n"));
    }

    /// <summary>
    /// Windows localises its own report. A machine speaking a language this parser does not
    /// recognise gets an honest "not known" rather than a number read out of the wrong line.
    /// </summary>
    [Fact]
    public void Parse_AReportInWordsThisParserDoesNotRecognise_IsNotAnswered()
    {
        Assert.Null(DismComponentStoreAnalysis.ParseReport(
            "Tatsachliche Groesse des Komponentenspeichers: 8,65 GB\r\n" +
            "Sicherungen und deaktivierte Funktionen: 545 MB\r\n"));
    }

    [Fact]
    public void Parse_AReportWhoseSizesAreNotNumbers_IsNotAnswered()
    {
        Assert.Null(DismComponentStoreAnalysis.ParseReport(
            "Actual Size of Component Store: unknown\r\n" +
            "Backups and Disabled Features: unknown\r\n"));
    }

    /// <summary>
    /// The real command needs an administrator, and the machinery must not start it when this
    /// process is not running as one. The test supplies the answer, so this proves the decision
    /// without ever starting anything.
    /// </summary>
    [Fact]
    public void Ask_WhenTheProcessIsNotRunningAsAnAdministrator_DoesNotStartTheCommand()
    {
        var analysis = new DismComponentStoreAnalysis(
            executable: "cmd.exe",
            arguments: "/c exit 9",
            requiresAdministrator: true,
            runningAsAdministrator: () => false,
            runningOnWindows: () => true);

        var question = analysis.Ask();

        Assert.False(question.Answered);
        Assert.Contains("administrator", question.ReasonNotAnswered!, StringComparison.Ordinal);
        Assert.Contains("never raises itself", question.ReasonNotAnswered!, StringComparison.Ordinal);

        // Exit code 9 is what the command would have ended with, and it appears nowhere: the
        // command was never started.
        Assert.DoesNotContain("9", question.ReasonNotAnswered!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Off Windows the analysis says so before anything else, and starts nothing: the component store
    /// rule turns this into a BROKEN verdict that names the reason.
    /// </summary>
    [Fact]
    public void Ask_OnAMachineThatIsNotWindows_SaysSoAndStartsNothing()
    {
        var analysis = new DismComponentStoreAnalysis(
            executable: "cmd.exe",
            arguments: "/c exit 9",
            requiresAdministrator: false,
            runningOnWindows: () => false);

        var refusal = Assert.Throws<PlatformNotSupportedException>(() => analysis.Ask());

        Assert.Contains("not running Windows", refusal.Message, StringComparison.Ordinal);
    }

    private static DismComponentStoreAnalysis AnalysisThrough(
        string executable, string arguments, bool requiresAdministrator) =>
        new(executable, arguments, requiresAdministrator, runningAsAdministrator: () => true,
            timeLimit: TimeSpan.FromMinutes(2));
}
