using CcCleanupStorage;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Scanning;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The command line. Nearly every test here is about something the tool refuses, because a command
/// line that accepts a flag and does nothing with it has given a wrong answer, not an untidy one.
/// </summary>
public class CommandLineTests
{
    private const string AnyIndexDirectory = "index-directory";

    [Fact]
    public void Parse_NoArguments_AsksForTheSavedScansOnThisMachine()
    {
        var request = Parsed([]);

        Assert.Equal(CommandName.SavedScans, request.Command);
        Assert.Null(request.FolderPath);
        Assert.False(request.Json);
    }

    [Fact]
    public void Parse_ScanWithAFolder_AsksForAScanOfThatFolder()
    {
        var request = Parsed(["scan", "D:\\repos"]);

        Assert.Equal(CommandName.Scan, request.Command);
        Assert.Equal("D:\\repos", request.FolderPath);
        Assert.Equal(ScanOptions.DefaultFolderDepth, request.FolderDepth);
        Assert.Equal(ScanReportBuilder.DefaultLargestFolders, request.LargestFolders);
    }

    [Fact]
    public void Parse_ReportWithEveryFlagItTakes_ReadsThemAll()
    {
        var request = Parsed(["report", "D:\\repos", "--json", "--top", "5", "--index-directory", "D:\\saved"]);

        Assert.Equal(CommandName.Report, request.Command);
        Assert.True(request.Json);
        Assert.Equal(5, request.LargestFolders);
        Assert.Equal("D:\\saved", request.IndexDirectory);
    }

    [Fact]
    public void Parse_ACommandThatDoesNotExist_IsAUsageErrorNamingTheCommandsThatDo()
    {
        var error = Refused(["recommend", "C:\\"]);

        Assert.Contains("there is no command recommend", error, StringComparison.Ordinal);
        Assert.Contains("scan and report", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AFlagThatDoesNotExist_IsAUsageErrorNamingTheFlagsThatDo()
    {
        var error = Refused(["scan", "C:\\", "--everything"]);

        Assert.Contains("there is no flag --everything", error, StringComparison.Ordinal);
        Assert.Contains("--folder-depth", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A report renders a scan that has already happened, so it cannot change how deep that scan
    /// recorded folders. Taking the flag and ignoring it would answer a question nobody asked.
    /// </summary>
    [Fact]
    public void Parse_FolderDepthOnAReport_IsAUsageErrorBecauseAReportCannotChangeIt()
    {
        var error = Refused(["report", "C:\\", "--folder-depth", "3"]);

        Assert.Contains("there is no flag --folder-depth for report", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AFlagWithNoValueAfterIt_IsAUsageError()
    {
        Assert.Contains("needs a value after it", Refused(["scan", "C:\\", "--top"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ANumberThatIsNotANumber_IsAUsageError()
    {
        Assert.Contains("needs a whole number", Refused(["scan", "C:\\", "--top", "lots"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--top", "0")]
    [InlineData("--top", "1001")]
    [InlineData("--folder-depth", "0")]
    [InlineData("--folder-depth", "11")]
    public void Parse_ANumberOutsideWhatIsAllowed_IsAUsageErrorSayingTheRange(string flag, string value)
    {
        Assert.Contains("takes a number between", Refused(["scan", "C:\\", flag, value]), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ScanWithNoFolder_IsAUsageErrorShowingHowToGiveOne()
    {
        var error = Refused(["scan"]);

        Assert.Contains("needs a folder", error, StringComparison.Ordinal);
        Assert.Contains("cc-cleanup-storage scan", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TwoFolders_IsAUsageErrorNamingBoth()
    {
        var error = Refused(["scan", "C:\\", "D:\\"]);

        Assert.Contains("C:\\", error, StringComparison.Ordinal);
        Assert.Contains("D:\\", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AFolderGivenToTheSavedScansView_IsAUsageError()
    {
        Assert.Contains("takes no folder", Refused(["--json", "C:\\"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Help_AsksForTheHelpPage()
    {
        Assert.Equal(CommandName.Help, Parsed(["--help"]).Command);
        Assert.Equal(CommandName.Help, Parsed(["scan", "C:\\", "--help"]).Command);
        Assert.Equal(CommandName.Help, Parsed(["-h"]).Command);
    }

    [Fact]
    public void Parse_Version_AsksForTheVersion()
    {
        Assert.Equal(CommandName.Version, Parsed(["--version"]).Command);
    }

    /// <summary>
    /// The review's second finding: the machine-readable flag was read and then dropped by
    /// --version - given before it, it was switched back off in the request, and given after it, it
    /// was never reached at all. Both orders must keep the flag, because the caller that passes it
    /// is asking for the machine-readable answer and the AXI standard makes a flag that is silently
    /// ignored a defect.
    /// </summary>
    [Theory]
    [InlineData("--json", "--version")]
    [InlineData("--version", "--json")]
    public void Parse_VersionWithTheMachineReadableFlagInEitherOrder_KeepsTheFlag(string first, string second)
    {
        var request = Parsed([first, second]);

        Assert.Equal(CommandName.Version, request.Command);
        Assert.True(request.Json);
    }

    /// <summary>
    /// The same rule for the help page, which dropped the machine-readable flag the same way. The
    /// short form of the flag is covered too, because it reads the same request.
    /// </summary>
    [Theory]
    [InlineData("--json", "--help")]
    [InlineData("--help", "--json")]
    [InlineData("--json", "-h")]
    [InlineData("-h", "--json")]
    public void Parse_HelpWithTheMachineReadableFlagInEitherOrder_KeepsTheFlag(string first, string second)
    {
        var request = Parsed([first, second]);

        Assert.Equal(CommandName.Help, request.Command);
        Assert.True(request.Json);
    }

    /// <summary>
    /// Help and version now answer only after every flag has been read, so a flag that follows them
    /// is judged rather than quietly dropped. A caller that passes --top after --help is passing a
    /// flag --help does not take, and the honest answer is the usage error, not the help page with
    /// the flag ignored.
    /// </summary>
    [Fact]
    public void Parse_AFlagAfterHelpThatHelpDoesNotTake_IsAUsageErrorRatherThanQuietlyDropped()
    {
        var error = Refused(["--help", "--top", "5"]);

        Assert.Contains("there is no flag --top", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Help and version are answered before the missing-folder check, so asking for the help page of
    /// a command whose folder was not given still shows the help page, as it always did.
    /// </summary>
    [Fact]
    public void Parse_HelpForACommandGivenNoFolder_StillShowsTheHelpPage()
    {
        Assert.Equal(CommandName.Help, Parsed(["scan", "--help"]).Command);
    }

    /// <summary>
    /// The fix-round review's second finding: a word after --help was judged as a folder and turned
    /// the request for the help page into a usage error, where before the fix round it printed the
    /// page. The word is the command the caller wants the page about, and the page answers for every
    /// command, so the word is not judged at all. Version answers the same way, for the same reason.
    /// </summary>
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Parse_HelpFollowedByAWord_ShowsTheHelpPageRatherThanAUsageError(string helpFlag)
    {
        var request = Parsed([helpFlag, "scan"]);

        Assert.Equal(CommandName.Help, request.Command);
    }

    [Fact]
    public void Parse_VersionFollowedByAWord_AnswersTheVersionRatherThanAUsageError()
    {
        var request = Parsed(["--version", "scan"]);

        Assert.Equal(CommandName.Version, request.Command);
    }

    [Fact]
    public void Parse_NoIndexDirectoryGiven_UsesTheOneForThisMachine()
    {
        Assert.Equal(AnyIndexDirectory, Parsed(["scan", "C:\\"]).IndexDirectory);
    }

    private static Request Parsed(string[] arguments)
    {
        var outcome = CommandLine.Parse(arguments, AnyIndexDirectory);
        Assert.Null(outcome.UsageError);
        Assert.NotNull(outcome.Request);
        return outcome.Request;
    }

    private static string Refused(string[] arguments)
    {
        var outcome = CommandLine.Parse(arguments, AnyIndexDirectory);
        Assert.Null(outcome.Request);
        Assert.NotNull(outcome.UsageError);
        return outcome.UsageError;
    }
}
