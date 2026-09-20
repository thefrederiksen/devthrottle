using System.Text.Json;
using CcCleanupStorage;
using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The tool end to end, against a tree the test builds and an index folder of its own. Nothing here
/// touches the machine's real saved scans, and nothing here removes anything it did not build on a
/// fixture tree itself: the reclaim answers are dry runs or applies over folders no rule looks
/// inside, and the holding answers work on a holding root inside the fixture.
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
    /// The review's first finding, end to end. On Windows and on a default macOS volume the file
    /// system does not tell folders apart by letter case, so one folder spelled two ways is one
    /// folder: a report asked for with the drive letter in the other case must resolve the scan
    /// saved under the first spelling, must read the same saved file, and must print the same
    /// sentences. Without the case fold in the fingerprint this answers that there is no saved scan
    /// and sends the caller to walk the disk again for nothing. On Linux the two spellings are two
    /// real folders, so the report rightly refuses and the refusal is what is asserted there.
    /// </summary>
    [Fact]
    public void Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn()
    {
        using var tree = StandardFixture.Build(nameof(Run_ReportAfterAScanWithThePathCaseFlipped_FollowsThePlatformItRunsOn));
        using var home = new FixtureTree("index-home");
        var respelled = SpelledPath.WithFirstLetterCaseFlipped(tree.Root);

        var scan = Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
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

    /// <summary>
    /// The fix-round review's first finding: the machine-readable help page carried no help - two
    /// fields and none of the page. The payload now carries the same content the text page prints,
    /// and this test reads it the way a machine does, out of the serialized answer: a flag name the
    /// scan command takes, a flag with its purpose, an exit code with its meaning, and the usage
    /// lines.
    /// </summary>
    [Fact]
    public void Run_HelpInMachineReadableForm_CarriesThePageAndAFlagNameCanBeReadOutOfIt()
    {
        var answer = Runner.Run(Request(CommandName.Help, null, "unused"));

        Assert.Equal(ExitCodes.Ok, answer.ExitCode);
        var written = JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options);
        using var read = JsonDocument.Parse(written);

        Assert.Equal("help", read.RootElement.GetProperty("command").GetString());
        Assert.True(read.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(read.RootElement.GetProperty("usage").EnumerateArray(), line =>
        {
            var text = line.GetString();
            return text is not null &&
                   text.StartsWith("cc-cleanup-storage scan", StringComparison.Ordinal);
        });

        var scan = read.RootElement.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "scan");
        Assert.Contains(scan.GetProperty("flags").EnumerateArray(), flag =>
        {
            var name = flag.GetString();
            return name is not null && name == "--folder-depth";
        });

        Assert.Contains(read.RootElement.GetProperty("flags").EnumerateArray(), flag =>
            flag.GetProperty("name").GetString() == "--json");

        var usage = read.RootElement.GetProperty("exitCodes").EnumerateArray()
            .Single(code => code.GetProperty("code").GetInt32() == 2);
        Assert.Contains("command line was wrong", usage.GetProperty("purpose").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The second fix round's first finding: the page named a command called saved-scans, which the
    /// tool refuses, beside two whose name IS the word that is typed. A machine reading the name
    /// field would type it and be turned away.
    ///
    /// Every command entry now carries how it is called, and this test takes the page at its word:
    /// for each entry it reads the word out of the payload and gives that word to the command line
    /// reader, which either takes it or is the thing that would have refused it. A command whose
    /// word is empty is the tool run with no command word, and its invocation must be the bare tool.
    /// </summary>
    [Fact]
    public void Run_HelpInMachineReadableForm_EveryCommandSaysHowItIsCalledAndTheReaderTakesThatWord()
    {
        using var home = new FixtureTree("index-home");
        var answer = Runner.Run(Request(CommandName.Help, null, "unused"));

        var written = JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options);
        using var read = JsonDocument.Parse(written);
        var commands = read.RootElement.GetProperty("commands").EnumerateArray().ToList();
        Assert.NotEmpty(commands);

        foreach (var command in commands)
        {
            var word = command.GetProperty("word").GetString();
            var invocation = command.GetProperty("invocation").GetString();
            Assert.NotNull(word);
            Assert.NotNull(invocation);
            Assert.StartsWith("cc-cleanup-storage", invocation, StringComparison.Ordinal);

            if (word.Length == 0)
            {
                Assert.Equal("cc-cleanup-storage", invocation);
                continue;
            }

            Assert.Contains(word, invocation, StringComparison.Ordinal);

            // The word the page hands a machine is given to the reader that would refuse it. A
            // command whose word is two words - "holding list" - is handed as the two words it is,
            // and the reader must take them and name the command by both.
            var outcome = CommandLine.Parse(WordsOf(word, home.Root), home.Root);
            Assert.Null(outcome.UsageError);
            Assert.NotNull(outcome.Request);
            Assert.Equal(word, outcome.Request.CommandWord);
        }
    }

    /// <summary>
    /// The second fix round's third finding: the usage lines and the command entries were two lists
    /// joined by nothing but their order, so a fourth command with no fourth usage line added beside
    /// it threw from the help page - the one answer that must never fail.
    ///
    /// The usage lines are now read off the commands themselves. This test says so: one line per
    /// command, in order, each equal to that command's own invocation. Two lists that could fall out
    /// of step cannot satisfy it.
    /// </summary>
    [Fact]
    public void Run_HelpInMachineReadableForm_TheUsageLinesAreTheCommandsOwnInvocationsOneForEach()
    {
        var answer = Runner.Run(Request(CommandName.Help, null, "unused"));

        var written = JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options);
        using var read = JsonDocument.Parse(written);

        var usage = read.RootElement.GetProperty("usage").EnumerateArray()
            .Select(line => line.GetString()).ToList();
        var invocations = read.RootElement.GetProperty("commands").EnumerateArray()
            .Select(command => command.GetProperty("invocation").GetString()).ToList();

        Assert.NotEmpty(invocations);
        Assert.Equal(invocations, usage);
    }

    /// <summary>
    /// The second fix round's second finding: the page missed two flags the reader takes. The short
    /// spelling of help was taken by every command and named by none, and the version flag was taken
    /// with no command word and left out of that command's list.
    ///
    /// This test does not read the lists and compare them with themselves, which would prove nothing.
    /// It asks the reader, one flag at a time, whether it takes that flag for that command, and then
    /// requires the page to name exactly the flags the reader took - no fewer, so a working flag is
    /// never hidden, and no more, so the page never sends a caller to a refusal.
    /// </summary>
    [Fact]
    public void Run_HelpInMachineReadableForm_EachCommandsFlagsAreExactlyTheOnesTheReaderTakes()
    {
        using var home = new FixtureTree("index-home");

        // Every flag spelling this tool has anywhere, with a value for the ones that need one.
        (string Flag, string? Value)[] everySpelling =
        [
            ("--json", null),
            ("--index-directory", home.Root),
            ("--top", "5"),
            ("--folder-depth", "2"),
            ("--rule", "some-rule"),
            ("--apply", null),
            ("--holding-root", home.Root),
            ("--days", "30"),
            ("--help", null),
            ("-h", null),
            ("--version", null)
        ];

        var answer = Runner.Run(Request(CommandName.Help, null, "unused"));
        var written = JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options);
        using var read = JsonDocument.Parse(written);

        foreach (var command in read.RootElement.GetProperty("commands").EnumerateArray())
        {
            var word = command.GetProperty("word").GetString();
            Assert.NotNull(word);
            var named = command.GetProperty("flags").EnumerateArray()
                .Select(flag => flag.GetString()).ToList();

            var taken = new List<string?>();
            foreach (var (flag, value) in everySpelling)
            {
                var arguments = new List<string>(WordsOf(word, home.Root));
                var at = word.Length == 0 ? 0 : word.Split(' ').Length;
                arguments.Insert(at, flag);
                if (value is not null) arguments.Insert(at + 1, value);

                // A request built at all means the reader took the flag; a flag it does not take is
                // the one thing that stops a command line this well formed from being understood.
                if (CommandLine.Parse(arguments, home.Root).Request is not null)
                    taken.Add(flag);
            }

            Assert.Equal(taken.Order(StringComparer.Ordinal), named.Order(StringComparer.Ordinal));
        }
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

        // This is the one test that hands the REAL rule set the apply flag, so where it would hold is
        // not left to which rules happen to match a fixture. The request names a holding root inside
        // the fixture tree, and both answers are made to say so before they are read for anything
        // else: a run that fell through to the default would name the root of a real volume here.
        var reclaimDryRun = Runner.Run(ReclaimRequest(tree.Root, apply: false));
        var reclaimApply = Runner.Run(ReclaimRequest(tree.Root, apply: true));
        foreach (var reclaim in new[] { reclaimDryRun, reclaimApply })
        {
            var holdingRoot = Assert.IsType<ReclaimJson>(reclaim.JsonPayload).HoldingRootPath;
            Assert.True(
                RuleSelection.IsInside(holdingRoot, tree.Root),
                $"the holding root {holdingRoot} is not inside the fixture tree {tree.Root}");
        }

        Answer[] answers =
        [
            Runner.Run(Request(CommandName.Help, null, home.Root)),
            Runner.Run(Request(CommandName.Version, null, home.Root)),
            Runner.Run(Request(CommandName.Scan, tree.Root, home.Root)),
            Runner.Run(Request(CommandName.Scan, empty.Root, home.Root)),
            Runner.Run(Request(CommandName.Report, tree.Root, home.Root)),
            Runner.Run(Request(CommandName.SavedScans, null, home.Root)),
            reclaimDryRun,
            reclaimApply,
            Runner.Run(HoldingRequest(CommandName.HoldingList, home.Root, folder: tree.Root)),
            Runner.Run(HoldingRequest(
                CommandName.HoldingRestore, home.Root, folder: tree.Root, entryId: "2026-09-19-3f2a1b9c")),
            Runner.Run(HoldingRequest(CommandName.HoldingPurge, home.Root, folder: tree.Root, apply: false)),
            Runner.Run(HoldingRequest(CommandName.HoldingPurge, home.Root, folder: tree.Root, apply: true)),
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

    /// <summary>
    /// A recommendation is made against a saved scan, never by walking the disk. A caller who asked
    /// what is safe to remove did not ask for a three minute walk, so with no saved scan the answer
    /// is the same refusal a report gives, naming the command that makes one.
    /// </summary>
    [Fact]
    public void Run_RecommendWithNoSavedScan_RefusesAndNeverWalksTheDisk()
    {
        using var tree = StandardFixture.Build(nameof(Run_RecommendWithNoSavedScan_RefusesAndNeverWalksTheDisk));
        using var home = new FixtureTree("index-home");

        var thrown = Assert.Throws<FileNotFoundException>(
            () => Runner.Run(Request(CommandName.Recommend, tree.Root, home.Root)));

        Assert.Contains("cc-cleanup-storage scan", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asked about a folder none of the machine's rules look inside, the answer is BROKEN with the
    /// rules named - never exit nought and "nothing to remove", which is the same answer a genuinely
    /// clean disk would give and is the failure this whole mission exists to prevent.
    /// </summary>
    [Fact]
    public void Run_RecommendForAFolderNoRuleLooksInside_ReportsBrokenAndNamesTheRulesItDidNotRun()
    {
        using var tree = StandardFixture.Build(nameof(Run_RecommendForAFolderNoRuleLooksInside_ReportsBrokenAndNamesTheRulesItDidNotRun));
        using var home = new FixtureTree("index-home");

        Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));
        var answer = Runner.Run(Request(CommandName.Recommend, tree.Root, home.Root));

        Assert.Equal(ExitCodes.Failed, answer.ExitCode);
        var json = Assert.IsType<RecommendJson>(answer.JsonPayload);
        Assert.False(json.Ok);
        Assert.Equal("broken", json.Verdict);
        Assert.Equal(0, json.RulesRun);
        Assert.Contains("no rule ran at all", json.BrokenReason!, StringComparison.Ordinal);

        // On Windows the machine has rules; they look elsewhere, and every one of them is named.
        if (OperatingSystem.IsWindows())
        {
            Assert.NotEmpty(json.RulesNotRun);
            Assert.All(json.RulesNotRun, rule => Assert.False(string.IsNullOrWhiteSpace(rule.LooksIn)));
        }
    }

    /// <summary>
    /// A recommendation that says only how many bytes could be freed is not one. Every part a caller
    /// needs in order to decide is its own field, read here the way a machine reads it.
    /// </summary>
    [Fact]
    public void Run_RecommendInMachineReadableForm_CarriesTheProofWhatIsLostAndHowToGetItBack()
    {
        using var tree = StandardFixture.Build(nameof(Run_RecommendInMachineReadableForm_CarriesTheProofWhatIsLostAndHowToGetItBack));
        using var home = new FixtureTree("index-home");

        Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));
        var answer = Runner.Run(Request(CommandName.Recommend, tree.Root, home.Root));

        var written = JsonSerializer.Serialize(answer.JsonPayload, JsonShape.Options);
        using var read = JsonDocument.Parse(written);
        var root = read.RootElement;

        Assert.Equal("recommend", root.GetProperty("command").GetString());
        foreach (var field in new[]
        {
            "ok", "verdict", "brokenReason", "rootPath", "indexPath", "scannedUtc", "rulesRun",
            "rulesBroken", "rulesNotRun", "rulesSawMoreThanTheScan", "itemsOffered", "reclaimableBytes",
            "unclassifiedBytes", "unseenBytes", "volume", "reachLines", "rules", "lines"
        })
        {
            Assert.True(root.TryGetProperty(field, out _), $"the machine-readable answer is missing {field}");
        }

        // The unseen gap travels with the recommendations, because recommendations made on a scan
        // that could not see a third of the disk are not a complete answer.
        Assert.NotEmpty(root.GetProperty("reachLines").EnumerateArray().ToList());
    }

    /// <summary>
    /// Every field the mission requires of a recommendation reaches a machine reader: the rule, what
    /// it removes, the proof, what is lost, how to get it back, and the rule's controls.
    /// </summary>
    [Fact]
    public void Run_RecommendInMachineReadableForm_GivesEachRuleItsProofAndItsControls()
    {
        using var tree = StandardFixture.Build(nameof(Run_RecommendInMachineReadableForm_GivesEachRuleItsProofAndItsControls));
        using var home = new FixtureTree("index-home");
        using var cache = new FixtureTree("a-cache");
        cache.File("one.tgz", 500);

        Runner.Run(Request(CommandName.Scan, tree.Root, home.Root));
        var scan = ScanIndexStore.Load(ScanIndexStore.PathFor(home.Root, tree.Root));

        // One rule, pointed at a cache inside the folder that was asked about, so the answer has a
        // rule in it on every platform this suite runs on.
        var rule = new PackageCacheRule(
            "a-cache", "A cache", cache.Root, "the command its own tool ships", "nothing but a download");
        var findings = new[] { RuleFold.Fold(rule, rule.Examine(new RuleContext
        {
            ScanRootPath = scan.Scan.RootPath,
            NowUtc = DateTimeOffset.UtcNow
        })) };

        var report = RecommendationBuilder.Build(
            ScanReportBuilder.Build(scan.Scan, 5), findings, []);

        var single = Assert.Single(report.Findings);
        Assert.Equal("the command its own tool ships", single.CommandToRun);
        Assert.Equal("nothing but a download", single.WhatIsLost);
        Assert.NotEmpty(single.HowToGetItBack);
        Assert.NotEmpty(single.WhyItIsSafe);
        Assert.NotEmpty(single.Controls);
    }


    /// <summary>
    /// The shortest well-formed command line that asks for one command's word, so the page's word
    /// is always handed to the reader that would refuse it. The holding commands whose word is two
    /// words are handed as the two words they are, and each command is given only what its own
    /// reader demands - a restore needs an entry and the holding folder, a purge needs the holding
    /// folder, and everything else takes the folder.
    /// </summary>
    private static string[] WordsOf(string word, string folder) => word switch
    {
        "" => [],
        "holding restore" => ["holding", "restore", "an-entry", "--holding-root", folder],
        "holding purge" => ["holding", "purge", "--holding-root", folder],
        _ => word.Split(' ').Concat([folder]).ToArray()
    };

    /// <summary>
    /// No rule on this machine looks inside a fixture folder, and the tool says so as a broken answer
    /// rather than as an empty one - exactly the posture the mission demands of an empty result.
    /// </summary>
    [Fact]
    public void Run_ReclaimOfAFolderNoRuleLooksInside_EndsOnOneAndNamesWhy()
    {
        using var tree = new FixtureTree(nameof(Run_ReclaimOfAFolderNoRuleLooksInside_EndsOnOneAndNamesWhy));

        var answer = Runner.Run(ReclaimRequest(tree.Root, apply: false));

        Assert.Equal(ExitCodes.Failed, answer.ExitCode);
        var json = Assert.IsType<ReclaimJson>(answer.JsonPayload);
        Assert.False(json.Ok);
        Assert.NotNull(json.BrokenReason);
        Assert.Contains(
            answer.TextLines,
            line => line.Contains("no rule on this machine looks inside", StringComparison.Ordinal));
    }

    /// <summary>
    /// A rule id that is not among the rules that look inside the folder asked about is an error
    /// naming the rules that do, never a quiet empty answer.
    /// </summary>
    [Fact]
    public void Run_ReclaimWithARuleThatDoesNotLookInside_IsAUsageErrorNamingIt()
    {
        using var tree = new FixtureTree(nameof(Run_ReclaimWithARuleThatDoesNotLookInside_IsAUsageErrorNamingIt));

        var answer = Runner.Run(ReclaimRequest(tree.Root, apply: false, ruleId: "devthrottle-test-scratch-folders"));

        Assert.Equal(ExitCodes.Usage, answer.ExitCode);
        var json = Assert.IsType<ErrorJson>(answer.JsonPayload);
        Assert.Equal("no-such-rule-here", json.Code);
        Assert.Contains("is not among the rules that look inside", json.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The machine-readable output carries all ten outcomes for a refused item, with the ones after
    /// the refusal marked not-reached, so an agent reading one item can see the reach of every
    /// refusal and never read an unchecked one as a passed one.
    /// </summary>
    [Fact]
    public void Run_ReclaimJsonForARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked()
    {
        using var tree = new FixtureTree(nameof(Run_ReclaimJsonForARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[64]);
        Directory.CreateDirectory(Path.Combine(temp, ".git"));

        // A real engine result on a fixture tree, mapped by the tool's own mapper - the same path the
        // command takes, with the rules the test built instead of this machine's.
        var result = CcDirector.Reclaim.Removal.ReclaimRunner.Run(new CcDirector.Reclaim.Removal.ReclaimRunRequest
        {
            Rules = [new CcDirector.Reclaim.Windows.TestScratchFoldersRule(temp)],
            RootPath = tree.Root,
            HoldingRootPath = Path.Combine(tree.Root, "holding"),
            ProtectedPaths = [],
            UserFolders = [],
            Apply = false,
            NowUtc = DateTimeOffset.UtcNow.AddDays(400)
        });

        var json = Runner.ToReclaimJson(result);
        var written = JsonSerializer.Serialize(json, JsonShape.Options);
        using var read = JsonDocument.Parse(written);

        var refused = read.RootElement.GetProperty("items")[0];
        Assert.False(refused.GetProperty("eligible").GetBoolean());
        Assert.Equal(2, refused.GetProperty("firedCheck").GetInt32());

        var checks = refused.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal(10, checks.Count);
        for (var number = 0; number < 10; number++)
        {
            var outcome = checks[number].GetProperty("outcome").GetString();
            if (number == 1)
                Assert.Equal("refused", outcome);
            else if (number > 1)
                Assert.Equal("not-reached", outcome);
        }

        Assert.Equal("inside a git working tree", checks[1].GetProperty("name").GetString());
        Assert.NotNull(checks[1].GetProperty("reason").GetString());
    }

    /// <summary>An honest empty answer: no holding root is count zero, not an error.</summary>
    [Fact]
    public void Run_HoldingListOfAHoldingRootThatIsNotThere_SaysCountZero()
    {
        using var tree = new FixtureTree(nameof(Run_HoldingListOfAHoldingRootThatIsNotThere_SaysCountZero));
        var holdingRoot = Path.Combine(tree.Root, "holding");

        var answer = Runner.Run(HoldingRequest(CommandName.HoldingList, holdingRoot, folder: tree.Root));

        Assert.Equal(ExitCodes.Ok, answer.ExitCode);
        var json = Assert.IsType<HoldingListJson>(answer.JsonPayload);
        Assert.True(json.Ok);
        Assert.False(json.RootExists);
        Assert.Equal(0, json.Count);
        Assert.Empty(json.Entries);
        Assert.Contains(
            answer.TextLines,
            line => line.StartsWith("count: 0", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole holding flow through the tool: the engine holds an item on a fixture tree, the tool
    /// lists it, restores it byte for byte, purges the re-held entry after its period, and the tree
    /// is checked after each step.
    /// </summary>
    [Fact]
    public void Run_TheWholeHoldingFlowThroughTheTool_ListsRestoresAndPurges()
    {
        using var tree = new FixtureTree(nameof(Run_TheWholeHoldingFlowThroughTheTool_ListsRestoresAndPurges));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);
        File.WriteAllBytes(Path.Combine(item, "scratch.txt"), new byte[512]);

        var holdingRoot = Path.Combine(tree.Root, "holding");
        var held = new CcDirector.Reclaim.Removal.HoldingStore(holdingRoot).Hold(
            new ReclaimCandidate(item, 512, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"),
            "devthrottle-test-scratch-folders",
            DateTimeOffset.UtcNow);
        Assert.True(held.Held, held.RefusalReason);

        // The list names the entry with its original path and bytes.
        var list = Runner.Run(HoldingRequest(CommandName.HoldingList, holdingRoot, folder: tree.Root));
        var listJson = Assert.IsType<HoldingListJson>(list.JsonPayload);
        Assert.Equal(ExitCodes.Ok, list.ExitCode);
        var entry = Assert.Single(listJson.Entries);
        Assert.Equal(held.EntryId, entry.EntryId);
        Assert.Equal(Path.GetFullPath(item), entry.OriginalPath);
        Assert.Equal(512, entry.Bytes);

        // The restore puts the item back, byte for byte, and clears the entry.
        Assert.NotNull(held.EntryId);
        var restore = Runner.Run(HoldingRequest(
            CommandName.HoldingRestore, holdingRoot, folder: tree.Root, entryId: held.EntryId));
        Assert.Equal(ExitCodes.Ok, restore.ExitCode);
        var restoreJson = Assert.IsType<HoldingRestoreJson>(restore.JsonPayload);
        Assert.True(restoreJson.Restored);
        Assert.True(File.Exists(Path.Combine(item, "scratch.txt")));
        Assert.Equal(512, new FileInfo(Path.Combine(item, "scratch.txt")).Length);

        // Held again, then purged: first the dry run that names it as not yet purgeable, then the
        // apply after the period, which is the only step that frees space.
        var heldAgain = new CcDirector.Reclaim.Removal.HoldingStore(holdingRoot).Hold(
            new ReclaimCandidate(item, 512, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"),
            "devthrottle-test-scratch-folders",
            DateTimeOffset.UtcNow);
        Assert.True(heldAgain.Held);

        Assert.NotNull(heldAgain.EntryId);
        var purgeDryRun = Runner.Run(HoldingRequest(
            CommandName.HoldingPurge, holdingRoot, folder: tree.Root, apply: false));
        var dryRunJson = Assert.IsType<HoldingPurgeJson>(purgeDryRun.JsonPayload);
        Assert.False(dryRunJson.Applied);
        Assert.Empty(dryRunJson.Purgeable);
        Assert.Single(dryRunJson.NotYetPurgeable);

        var purge = Runner.Run(HoldingRequest(
            CommandName.HoldingPurge, holdingRoot, folder: tree.Root, apply: true, days: 0));
        var purgeJson = Assert.IsType<HoldingPurgeJson>(purge.JsonPayload);
        Assert.True(purgeJson.Applied);
        Assert.Equal(1, purgeJson.PurgedCount);
        Assert.False(Directory.Exists(Path.Combine(holdingRoot, heldAgain.EntryId)));
        Assert.False(Directory.Exists(item));
    }

    /// <summary>A restore that is refused - something now stands at the original path - never overwrites.</summary>
    [Fact]
    public void Run_HoldingRestoreOntoAnOccupiedPath_IsRefusedAndOverwritesNothing()
    {
        using var tree = new FixtureTree(nameof(Run_HoldingRestoreOntoAnOccupiedPath_IsRefusedAndOverwritesNothing));
        var temp = tree.Folder("temp");
        var item = Path.Combine(temp, "cc-director-tests");
        Directory.CreateDirectory(item);

        var holdingRoot = Path.Combine(tree.Root, "holding");
        var held = new CcDirector.Reclaim.Removal.HoldingStore(holdingRoot).Hold(
            new ReclaimCandidate(item, 0, DateTimeOffset.UtcNow, "created by one of DevThrottle's own test suites"),
            "devthrottle-test-scratch-folders",
            DateTimeOffset.UtcNow);
        Assert.True(held.Held);

        var newcomer = tree.Folder("temp/cc-director-tests");
        File.WriteAllText(Path.Combine(newcomer, "somebody-else.txt"), "not ours");

        Assert.NotNull(held.EntryId);
        var answer = Runner.Run(HoldingRequest(
            CommandName.HoldingRestore, holdingRoot, folder: tree.Root, entryId: held.EntryId));

        Assert.Equal(ExitCodes.Failed, answer.ExitCode);
        var json = Assert.IsType<HoldingRestoreJson>(answer.JsonPayload);
        Assert.False(json.Restored);
        Assert.NotNull(json.RefusalReason);
        Assert.Contains("never overwrites", json.RefusalReason, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(newcomer, "somebody-else.txt")));
        Assert.True(Directory.Exists(Path.Combine(holdingRoot, held.EntryId)));
    }

    // The holding root is always named, and always inside the folder the test handed over, which is
    // a fixture tree. Left null the tool works out its per-volume default - a folder at the root of
    // the real volume - and these requests run the REAL rule set, sometimes with the apply flag. No
    // request built here can hold anywhere but inside the fixture, whatever the rules select.
    private static Request ReclaimRequest(string folder, bool apply, string? ruleId = null) => new()
    {
        Command = CommandName.Reclaim,
        FolderPath = folder,
        Json = false,
        IndexDirectory = "unused-index-directory",
        LargestFolders = ScanReportBuilder.DefaultLargestFolders,
        FolderDepth = 2,
        RuleId = ruleId,
        Apply = apply,
        HoldingRootPath = Path.Combine(folder, "holding"),
        EntryId = null,
        Days = null,
        CommandWord = "reclaim"
    };

    private static Request HoldingRequest(
        CommandName command, string holdingRoot, string folder, bool apply = false, string? entryId = null, int? days = null) => new()
    {
        Command = command,
        FolderPath = folder,
        Json = false,
        IndexDirectory = "unused-index-directory",
        LargestFolders = ScanReportBuilder.DefaultLargestFolders,
        FolderDepth = 2,
        RuleId = null,
        Apply = apply,
        HoldingRootPath = holdingRoot,
        EntryId = entryId,
        Days = days,
        CommandWord = command switch
        {
            CommandName.HoldingList => "holding list",
            CommandName.HoldingRestore => "holding restore",
            CommandName.HoldingPurge => "holding purge",
            _ => "holding"
        }
    };

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
