using System.Text.Json;
using CcCleanupStorage;
using CcDirector.Reclaim.Reporting;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The tool end to end, against a tree the test builds and an index folder of its own. Nothing here
/// touches the machine's real saved scans, and nothing here removes anything, because phase 1 holds
/// no code that could.
/// </summary>
public class RunnerTests
{
    [Fact]
    public void Run_ScanOfTheFixtureTree_SavesTheIndexAndEndsOnNought()
    {
        using var tree = StandardFixture.Build(nameof(Run_ScanOfTheFixtureTree_SavesTheIndexAndEndsOnNought));
        using var home = new FixtureTree("index-home");

        var answer = Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));

        Assert.Equal(ExitCodes.Ok, answer.ExitCode);
        var json = Assert.IsType<ReportJson>(answer.JsonPayload);
        Assert.True(json.Ok);
        Assert.Equal("ok", json.Verdict);
        Assert.Equal(StandardFixture.ExpectedBytesSeen, json.BytesSeen);
        Assert.NotNull(json.IndexPath);
        Assert.True(File.Exists(json.IndexPath));
    }

    /// <summary>
    /// A broken instrument is never saved. The saved scan is what a screen shows later without walking
    /// anything, so a measurement the tool has just called broken must not become that answer.
    /// </summary>
    [Fact]
    public void Run_ScanOfAnEmptyFolder_EndsOnOneAndSavesNothing()
    {
        using var tree = new FixtureTree(nameof(Run_ScanOfAnEmptyFolder_EndsOnOneAndSavesNothing));
        using var home = new FixtureTree("index-home");

        var answer = Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));

        Assert.Equal(ExitCodes.Failed, answer.ExitCode);
        var json = Assert.IsType<ReportJson>(answer.JsonPayload);
        Assert.False(json.Ok);
        Assert.Equal("broken", json.Verdict);
        Assert.Null(json.IndexPath);
        Assert.Empty(Directory.GetFiles(home.Root));
        Assert.Contains(
            answer.TextLines,
            line => line.StartsWith("index: not written", StringComparison.Ordinal));
    }

    /// <summary>
    /// Critical rule 7: the engine writes the sentences and nothing that prints them works anything
    /// out again. A report read from a saved scan must therefore say exactly what the scan said.
    /// </summary>
    [Fact]
    public void Run_ReportAfterAScan_PrintsWordForWordWhatTheScanPrinted()
    {
        using var tree = StandardFixture.Build(nameof(Run_ReportAfterAScan_PrintsWordForWordWhatTheScanPrinted));
        using var home = new FixtureTree("index-home");

        var scan = Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));
        var report = Runner.Run(Request(CommandName.Report, tree.Root, home.Root));

        Assert.Equal(ExitCodes.Ok, report.ExitCode);
        var fromScan = Assert.IsType<ReportJson>(scan.JsonPayload);
        var fromReport = Assert.IsType<ReportJson>(report.JsonPayload);
        Assert.Equal(fromScan.Lines, fromReport.Lines);
    }

    [Fact]
    public void Run_ReportWithNothingSaved_ThrowsSoTheEntryPointCanNameIt()
    {
        using var tree = StandardFixture.Build(nameof(Run_ReportWithNothingSaved_ThrowsSoTheEntryPointCanNameIt));
        using var home = new FixtureTree("index-home");

        var error = Assert.Throws<FileNotFoundException>(
            () => Runner.Run(Request(CommandName.Report, tree.Root, home.Root)));

        Assert.Contains("cc-cleanup-storage scan", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The review's first finding, end to end. On Windows the file system does not tell folders apart
    /// by letter case, so one folder spelled two ways is one folder: a report asked for with the
    /// drive letter in the other case must resolve the scan saved under the first spelling, must
    /// read the same saved file, and must print the same sentences. Without the case fold in the
    /// fingerprint this answers that there is no saved scan and sends the caller to walk the disk
    /// again for nothing. On every other platform the two spellings are two real folders, so the
    /// report rightly refuses and the refusal is what is asserted there.
    /// </summary>
    [Fact]
    public void Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn()
    {
        using var tree = StandardFixture.Build(nameof(Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn));
        using var home = new FixtureTree("index-home");
        var respelled = SpelledPath.WithFirstLetterCaseFlipped(tree.Root);

        var scan = Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));

        if (OperatingSystem.IsWindows())
        {
            var report = Runner.Run(Request(CommandName.Report, respelled, home.Root));

            Assert.Equal(ExitCodes.Ok, report.ExitCode);
            var fromScan = Assert.IsType<ReportJson>(scan.JsonPayload);
            var fromReport = Assert.IsType<ReportJson>(report.JsonPayload);
            Assert.Equal(fromScan.IndexPath, fromReport.IndexPath);
            Assert.Equal(fromScan.Lines, fromReport.Lines);
        }
        else
        {
            Assert.Throws<FileNotFoundException>(
                () => Runner.Run(Request(CommandName.Report, respelled, home.Root)));
        }
    }

    /// <summary>
    /// The review's second finding, through the same steps the entry point takes: read the command
    /// line, run the request, write the machine-readable payload. The machine-readable flag must
    /// reach the version in both orders - before it and after it - because a caller that asks for
    /// the machine-readable version and receives a prose line has been given a wrong answer.
    /// </summary>
    [Fact]
    public void Run_VersionWithTheMachineReadableFlagGivenBeforeIt_AnswersInMachineReadableForm()
    {
        var answer = VersionAnswer(["--json", "--version"]);
        Assert.True(answer);
    }

    [Fact]
    public void Run_VersionWithTheMachineReadableFlagGivenAfterIt_AnswersInMachineReadableForm()
    {
        var answer = VersionAnswer(["--version", "--json"]);
        Assert.True(answer);
    }

    [Fact]
    public void Run_SavedScansWithNothingSaved_SaysCountNoughtRatherThanPrintingNothing()
    {
        using var home = new FixtureTree(nameof(Run_SavedScansWithNothingSaved_SaysCountNoughtRatherThanPrintingNothing));

        var answer = Runner.Run(Request(CommandName.SavedScans, null, home.Root));

        Assert.Equal(ExitCodes.Ok, answer.ExitCode);
        Assert.Equal("count: 0", answer.TextLines[0]);
        Assert.Contains("saved-scans[0]{root,scanned,bytes,files}:", answer.TextLines);
        var json = Assert.IsType<SavedScansJson>(answer.JsonPayload);
        Assert.Equal(0, json.Count);
    }

    [Fact]
    public void Run_SavedScansAfterAScan_NamesTheFolderThatWasScanned()
    {
        using var tree = StandardFixture.Build(nameof(Run_SavedScansAfterAScan_NamesTheFolderThatWasScanned));
        using var home = new FixtureTree("index-home");
        Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));

        var answer = Runner.Run(Request(CommandName.SavedScans, null, home.Root));

        Assert.Equal("count: 1", answer.TextLines[0]);
        var json = Assert.IsType<SavedScansJson>(answer.JsonPayload);
        Assert.Equal(tree.Root, Assert.Single(json.Scans).RootPath);
    }

    /// <summary>
    /// The recoverability test the AXI standard asks for: every record must be readable back out of
    /// the ordinary output, whole. A folder with a comma in its name is the case that catches a tool
    /// which writes its rows plainly, and there are folders like that on any real disk.
    /// </summary>
    [Fact]
    public void Run_SavedScansForAFolderWithACommaInItsName_WritesARowThatReadsBackWhole()
    {
        using var tree = new FixtureTree(nameof(Run_SavedScansForAFolderWithACommaInItsName_WritesARowThatReadsBackWhole));
        using var home = new FixtureTree("index-home");
        var awkward = tree.Folder("one, two");
        tree.File(Path.Combine("one, two", "thing.bin"), 2048);
        Runner.Run(Request(CommandName.Scan, awkward, home.Root));

        var answer = Runner.Run(Request(CommandName.SavedScans, null, home.Root));

        var listAt = answer.TextLines.ToList().FindIndex(
            line => line.StartsWith("saved-scans[1]{", StringComparison.Ordinal));
        Assert.NotEqual(-1, listAt);
        var row = answer.TextLines[listAt + 1];

        Assert.StartsWith("  \"", row, StringComparison.Ordinal);
        Assert.Equal(awkward, ReadFirstValue(row.TrimStart()));
    }

    [Fact]
    public void Run_ScanAskedForFewerFoldersThanExist_NamesOnlyThatMany()
    {
        using var tree = StandardFixture.Build(nameof(Run_ScanAskedForFewerFoldersThanExist_NamesOnlyThatMany));
        using var home = new FixtureTree("index-home");

        var request = Request(CommandName.Scan, tree.Root, home.Root) with { LargestFolders = 2 };
        var answer = Runner.Run(request);

        var json = Assert.IsType<ReportJson>(answer.JsonPayload);
        Assert.Equal(2, json.LargestFolders.Count);
    }

    [Fact]
    public void Run_ScanJson_CarriesEveryFieldIncludingTheSentencesTheTextAnswerPrints()
    {
        using var tree = StandardFixture.Build(nameof(Run_ScanJson_CarriesEveryFieldIncludingTheSentencesTheTextAnswerPrints));
        using var home = new FixtureTree("index-home");

        var answer = Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));
        var json = Assert.IsType<ReportJson>(answer.JsonPayload);

        Assert.Equal(StandardFixture.ExpectedFilesSeen, json.FilesSeen);
        Assert.Equal(StandardFixture.ExpectedFoldersSeen, json.FoldersSeen);
        Assert.Equal(StandardFixture.ExpectedPlaceholderBytesInCloud, json.PlaceholderBytesInCloud);
        Assert.Single(json.Links);
        Assert.Equal("access denied", Assert.Single(json.RefusedFolders).Reason);
        Assert.NotNull(json.UnseenBytes);
        Assert.Contains(json.Lines, line => line.StartsWith("unseen: ", StringComparison.Ordinal));

        // The same shape has to survive being written out, because other code reads it that way.
        var written = JsonSerializer.Serialize(json, JsonShape.Options);
        using var read = JsonDocument.Parse(written);
        Assert.True(read.RootElement.TryGetProperty("unseenBytes", out _));
        Assert.True(read.RootElement.TryGetProperty("refusedFolders", out _));
        Assert.True(read.RootElement.TryGetProperty("lines", out _));
    }

    [Fact]
    public void Run_Help_NamesEveryCommandAndEveryExitCode()
    {
        var answer = Runner.Run(Request(CommandName.Help, null, "unused"));

        var text = string.Join("\n", answer.TextLines);
        Assert.Contains("cc-cleanup-storage scan", text, StringComparison.Ordinal);
        Assert.Contains("cc-cleanup-storage report", text, StringComparison.Ordinal);
        foreach (var code in new[] { "  0 ", "  1 ", "  2 " })
            Assert.Contains(code, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_Version_NamesTheToolAndItsNumber()
    {
        var answer = Runner.Run(Request(CommandName.Version, null, "unused"));

        Assert.StartsWith("cc-cleanup-storage ", Assert.Single(answer.TextLines), StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything this tool prints is plain ASCII, on every answer it can give, because a character
    /// outside it is what a Windows terminal turns into a question mark or an encoding failure.
    /// </summary>
    [Fact]
    public void Run_EveryAnswerThisToolCanGive_IsPlainAscii()
    {
        using var tree = StandardFixture.Build(nameof(Run_EveryAnswerThisToolCanGive_IsPlainAscii));
        using var empty = new FixtureTree("empty");
        using var home = new FixtureTree("index-home");

        Answer[] answers =
        [
            Runner.Run(Request(CommandName.Help, null, home.Root)),
            Runner.Run(Request(CommandName.Version, null, home.Root)),
            Runner.Run(Request(CommandName.Scan, tree.Root, home.Root)),
            Runner.Run(Request(CommandName.Scan, empty.Root, home.Root)),
            Runner.Run(Request(CommandName.Report, tree.Root, home.Root)),
            Runner.Run(Request(CommandName.SavedScans, null, home.Root)),
            Runner.Failure("scan", "usage", "there is no flag --nope", ExitCodes.Usage)
        ];

        foreach (var answer in answers)
        {
            foreach (var line in answer.TextLines)
                Assert.All(line, character => Assert.InRange(character, (char)0x20, (char)0x7E));

            Assert.All(
                JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options),
                character => Assert.True(
                    character is '\r' or '\n' or (>= (char)0x20 and <= (char)0x7E),
                    $"the machine-readable answer holds a character outside plain ASCII: {(int)character}"));
        }
    }

    [Fact]
    public void Run_EveryAnswer_OffersWhatToRunNext()
    {
        using var tree = StandardFixture.Build(nameof(Run_EveryAnswer_OffersWhatToRunNext));
        using var home = new FixtureTree("index-home");

        foreach (var answer in new[]
                 {
                     Runner.Run(Request(CommandName.Scan, tree.Root, home.Root)),
                     Runner.Run(Request(CommandName.Report, tree.Root, home.Root)),
                     Runner.Run(Request(CommandName.SavedScans, null, home.Root)),
                     Runner.Failure("scan", "usage", "something was wrong", ExitCodes.Usage)
                 })
        {
            Assert.Contains(answer.TextLines, line => line.StartsWith("help[", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Failure_AnyFailure_CarriesAWordAnAgentCanActOn()
    {
        var answer = Runner.Failure("report", "no-saved-scan", "there is nothing saved", ExitCodes.Failed);

        Assert.Equal(ExitCodes.Failed, answer.ExitCode);
        Assert.Equal("error: no-saved-scan", answer.TextLines[0]);
        var json = Assert.IsType<ErrorJson>(answer.JsonPayload);
        Assert.False(json.Ok);
        Assert.Equal("no-saved-scan", json.Code);
    }

    // The version command through the whole path the entry point takes it: read the command line,
    // run the request, write the machine-readable payload the caller asked for. True when every
    // step held - the flag survived the read, and the payload carries the version as a machine
    // reads it.
    private static bool VersionAnswer(string[] arguments)
    {
        var outcome = CommandLine.Parse(arguments, "unused-index-directory");

        Assert.Null(outcome.UsageError);
        var request = outcome.Request
            ?? throw new InvalidOperationException("The command line was read and produced no request.");

        Assert.Equal(CommandName.Version, request.Command);
        if (!request.Json) return false;

        var answer = Runner.Run(request);
        var written = JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options);
        using var read = JsonDocument.Parse(written);

        return read.RootElement.GetProperty("command").GetString() == "version" &&
               read.RootElement.GetProperty("ok").GetBoolean() &&
               read.RootElement.GetProperty("version").GetString() == Runner.VersionLine();
    }

    private static Request Request(CommandName command, string? folder, string indexDirectory) => new()
    {
        Command = command,
        FolderPath = folder,
        Json = false,
        IndexDirectory = indexDirectory,
        LargestFolders = ScanReportBuilder.DefaultLargestFolders,
        FolderDepth = 2,
        CommandWord = command.ToString().ToLowerInvariant()
    };

    // Reads one value back out of a rendered row, quotes, escapes and all. Small on purpose: the
    // point is that the row can be read back at all, by something that was not the writer.
    private static string ReadFirstValue(string row)
    {
        if (!row.StartsWith('"'))
        {
            var comma = row.IndexOf(',', StringComparison.Ordinal);
            return comma < 0 ? row : row[..comma];
        }

        var value = new System.Text.StringBuilder();
        for (var at = 1; at < row.Length; at++)
        {
            if (row[at] == '"') return value.ToString();
            if (row[at] != '\\') { value.Append(row[at]); continue; }

            at++;
            value.Append(row[at] switch
            {
                '\\' => '\\',
                '"' => '"',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => throw new FormatException($"The row holds an escape this reader does not know: {row[at]}")
            });
        }

        throw new FormatException($"The row ends inside a quoted value: {row}");
    }
}
