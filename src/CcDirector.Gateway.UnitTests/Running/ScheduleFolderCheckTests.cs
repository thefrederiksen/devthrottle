using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// Issue #4421: a schedule's folder is checked on its machine when it is saved. The words are pure functions; the check
/// runs against a fake tunnel and a fixed Director list, so no Director, port or clock is involved.
/// </summary>
public sealed class ScheduleFolderCheckTests
{
    private static readonly TenantId A = new("acct-a");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The path from the issue: a Windows-shaped path saved for a Mac.
    private const string CustomersPath = @"\\documents\\devthrottle";

    // When a stopped Director said goodbye: a fixed time, read from no clock.
    private static readonly DateTime Stopped = new(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc);

    private static DirectorDto Director(string id, string machine, string os, string version = "2.30.0", DateTime? stopped = null)
        => new() { DirectorId = id, MachineName = machine, Os = os, Version = version, StoppedAtUtc = stopped, LastSeen = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc) };

    private sealed class Tunnel
    {
        public List<(string DirectorId, DirectorCommand Command)> Sent { get; } = new();
        public Func<DirectorCommand, DirectorCommandResult?> Answer { get; set; } = _ => Answered(exists: true, isDirectory: true);

        public Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
        {
            Sent.Add((directorId, command));
            return Task.FromResult(Answer(command));
        }
    }

    private static DirectorCommandResult Answered(bool exists, bool isDirectory, string os = DirectorOs.MacOs)
        => DirectorCommandResult.Success(JsonSerializer.Serialize(new FolderCheckAnswer { Exists = exists, IsDirectory = isDirectory, Os = os }, Json));

    private static ScheduleFolderCheck Check(Tunnel tunnel, IReadOnlyCollection<DirectorDto> directors, ISet<string> connected)
        => new(_ => directors, (_, id) => connected.Contains(id), tunnel.SendAsync);

    // ---------- the words ----------

    [Theory]
    [InlineData(@"C:\repos\project", true)]
    [InlineData(@"c:/repos/project", true)]
    [InlineData(CustomersPath, true)]
    [InlineData(@"documents\project", true)]
    [InlineData("/Users/pat/project", false)]
    [InlineData("~/project", false)]
    [InlineData("project", false)]
    public void IsWindowsShaped_ReadsTheDriveLetterAndTheBackslash(string path, bool windows)
    {
        Assert.Equal(windows, ScheduleFolderWords.IsWindowsShaped(path));
    }

    [Fact]
    public void WrongShape_AWindowsPathForAMac_NamesTheMachineAndItsSystem_AndKeepsTheFolderName()
    {
        var sentence = ScheduleFolderWords.WrongShape(CustomersPath, "STUDIO_MAC", DirectorOs.MacOs);

        Assert.Equal($"'{CustomersPath}' is a Windows path, but STUDIO_MAC is a Mac. Use a path like /Users/<you>/Documents/devthrottle.", sentence);
    }

    [Fact]
    public void WrongShape_AWindowsPathForLinux_UsesTheLinuxExample()
    {
        var sentence = ScheduleFolderWords.WrongShape(@"C:\repos\project", "BUILD_BOX", DirectorOs.Linux);

        Assert.Equal(@"'C:\repos\project' is a Windows path, but BUILD_BOX is a Linux computer. Use a path like /home/<you>/project.", sentence);
    }

    [Theory]
    [InlineData(@"C:\repos\project", DirectorOs.Windows)]
    [InlineData("/Users/pat/project", DirectorOs.MacOs)]
    [InlineData(@"C:\repos\project", null)]
    // A Mac path for Windows is NOT refused from the shape: /Users/pat is read against the current drive and may exist.
    [InlineData("/Users/pat/project", DirectorOs.Windows)]
    public void WrongShape_APathThatFitsOrAnUnknownSystem_IsNotRefusedFromTheShape(string path, string? os)
    {
        Assert.Null(ScheduleFolderWords.WrongShape(path, "M", os));
    }

    [Fact]
    public void Missing_ARightShapedFolderThatIsNotThere_NamesTheMachineAndItsSystem()
    {
        var sentence = ScheduleFolderWords.Missing("/Users/pat/Documents/project", "STUDIO_MAC", DirectorOs.MacOs);

        Assert.Equal("The folder '/Users/pat/Documents/project' does not exist on STUDIO_MAC (a Mac). Choose a folder that exists on STUDIO_MAC.", sentence);
    }

    [Fact]
    public void Missing_AMacPathOnWindows_SaysItLooksLikeAMacPath()
    {
        var sentence = ScheduleFolderWords.Missing("/Users/pat/Documents/project", "OFFICE_PC", DirectorOs.Windows);

        Assert.Equal("The folder '/Users/pat/Documents/project' does not exist on OFFICE_PC (a Windows computer). It looks like a Mac or Linux path, " +
                     @"but OFFICE_PC is a Windows computer. Use a path like C:\Users\<you>\Documents\project. Choose a folder that exists on OFFICE_PC.", sentence);
    }

    [Theory]
    [InlineData(DirectorOs.MacOs, "/Users/<you>/Documents/my-project")]
    [InlineData(DirectorOs.Linux, "/home/<you>/my-project")]
    [InlineData(DirectorOs.Windows, @"C:\Users\<you>\Documents\my-project")]
    [InlineData(null, ScheduleFolderWords.UnknownPlaceholder)]
    public void Placeholder_IsInTheMachinesOwnShape(string? os, string expected)
    {
        Assert.Equal(expected, ScheduleFolderWords.Placeholder(os));
    }

    [Fact]
    public void DidNotRunText_TheDirectorsMissingFolderRefusal_ReadsAsThePlainSentence()
    {
        // The reason a run of the customer's schedule was recorded with.
        var reason = $"it did not start a session: director returned BadRequest: repoPath does not exist: {CustomersPath}";

        var text = ScheduleFolderWords.DidNotRunText(reason, "STUDIO_MAC", DirectorOs.MacOs);

        Assert.Equal($"did not start a session: '{CustomersPath}' is a Windows path, but STUDIO_MAC is a Mac. " +
                     "Use a path like /Users/<you>/Documents/devthrottle. Then edit the schedule.", text);
        Assert.DoesNotContain("BadRequest", text);
    }

    [Fact]
    public void DidNotRunText_WithTheSystemUnknown_StillNamesTheFolderAndTheMachine()
    {
        var text = ScheduleFolderWords.DidNotRunText("it did not start a session: director returned BadRequest: repoPath does not exist: /srv/x", "BOX", null);

        Assert.Equal("did not start a session: The folder '/srv/x' does not exist on BOX. Choose a folder that exists on BOX. Then edit the schedule.", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("it did not start a session: no Director is connected for machine BOX")]
    public void DidNotRunText_AnyOtherReason_IsNotRewritten(string? reason)
    {
        Assert.Null(ScheduleFolderWords.DidNotRunText(reason, "BOX", DirectorOs.Linux));
    }

    [Fact]
    public void Stamp_ARunThatDidNotStartBecauseItsFolderWasMissing_ReadsPlainlyInTheRunTable()
    {
        // The run as the customer's four Run-now presses recorded it. The run table used to read "did not start a session"
        // and nothing else: the reason never reached the Cockpit or `schedule runs`.
        var fired = new DateTime(2026, 10, 23, 18, 5, 0, DateTimeKind.Utc);
        var run = new CronRunRecord
        {
            FiredUtc = fired, ScheduledUtc = fired, Machine = "STUDIO_MAC", InfraStatus = "not-started", TaskStatus = "unknown",
            Result = CronRunResults.Problem, Problem = CronRunProblems.DidNotRun,
            ResultReason = $"it did not start a session: director returned BadRequest: repoPath does not exist: {CustomersPath}",
        };

        CronRunEndingFold.Stamp(new[] { run }, new Dictionary<string, CcDirector.Gateway.History.SessionEndingFact>(), fired.AddMinutes(1),
            machine => machine == "STUDIO_MAC" ? DirectorOs.MacOs : null);

        Assert.Equal(CronRunEndings.NoSession, run.Ending);
        Assert.Equal($"did not start a session: '{CustomersPath}' is a Windows path, but STUDIO_MAC is a Mac. " +
                     "Use a path like /Users/<you>/Documents/devthrottle. Then edit the schedule.", run.EndingText);
    }

    [Fact]
    public void Stamp_ARunThatDidNotStartForAnotherReason_KeepsItsWords()
    {
        var fired = new DateTime(2026, 10, 23, 18, 5, 0, DateTimeKind.Utc);
        var run = new CronRunRecord
        {
            FiredUtc = fired, ScheduledUtc = fired, Machine = "STUDIO_MAC", InfraStatus = "not-started", TaskStatus = "unknown",
            ResultReason = "it did not start a session: no Director is connected for machine STUDIO_MAC",
        };

        CronRunEndingFold.Stamp(new[] { run }, new Dictionary<string, CcDirector.Gateway.History.SessionEndingFact>(), fired.AddMinutes(1));

        Assert.Equal("did not start a session", run.EndingText);
    }

    // ---------- the check ----------

    [Fact]
    public async Task CheckAsync_AWindowsPathForAMacThatIsOffline_IsRefusedFromTheShape_AndNothingIsSent()
    {
        var tunnel = new Tunnel();
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs) }, new HashSet<string>());

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", CustomersPath, CancellationToken.None);

        Assert.StartsWith($"'{CustomersPath}' is a Windows path, but STUDIO_MAC is a Mac.", verdict.Refusal);
        Assert.Null(verdict.Warning);
        Assert.Empty(tunnel.Sent);
    }

    [Fact]
    public async Task CheckAsync_AFolderTheMachineHas_IsFine_AndTheFolderIsAskedOfTheConnectedDirector()
    {
        var tunnel = new Tunnel();
        var check = Check(tunnel, new[] { Director("d-old", "STUDIO_MAC", DirectorOs.MacOs), Director("d-mac", "studio_mac", DirectorOs.MacOs) },
            new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/project", CancellationToken.None);

        Assert.Equal(ScheduleFolderVerdict.Fine, verdict);
        var (directorId, command) = Assert.Single(tunnel.Sent);
        Assert.Equal("d-mac", directorId);
        Assert.Equal(ScheduleFolderCheckContract.Verb, command.Verb);
        Assert.Equal("/Users/pat/project", JsonSerializer.Deserialize<FolderCheckRequest>(command.PayloadJson, Json)!.Path);
    }

    [Fact]
    public async Task CheckAsync_AFolderTheMachineDoesNotHave_IsRefusedNamingTheMachineAndItsSystem()
    {
        var tunnel = new Tunnel { Answer = _ => Answered(exists: false, isDirectory: false) };
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs) }, new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/Documents/project", CancellationToken.None);

        Assert.Equal("The folder '/Users/pat/Documents/project' does not exist on STUDIO_MAC (a Mac). Choose a folder that exists on STUDIO_MAC.", verdict.Refusal);
    }

    [Fact]
    public async Task CheckAsync_AWindowsPathForAMacWhoseSystemWasNeverReported_IsRefusedWithTheSystemTheDirectorAnswers()
    {
        // An older Director said no system on Hello; the check's own answer names it.
        var tunnel = new Tunnel { Answer = _ => Answered(exists: false, isDirectory: false, os: DirectorOs.MacOs) };
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", os: "") }, new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", CustomersPath, CancellationToken.None);

        Assert.StartsWith($"'{CustomersPath}' is a Windows path, but STUDIO_MAC is a Mac.", verdict.Refusal);
    }

    [Fact]
    public async Task CheckAsync_AFile_IsRefused()
    {
        var tunnel = new Tunnel { Answer = _ => Answered(exists: true, isDirectory: false) };
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs) }, new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/notes.txt", CancellationToken.None);

        Assert.Equal("'/Users/pat/notes.txt' on STUDIO_MAC is a file, not a folder. Choose the folder the session should open in.", verdict.Refusal);
    }

    [Fact]
    public async Task CheckAsync_TheMachineIsOffline_IsAWarning_AndNothingIsSent()
    {
        var tunnel = new Tunnel();
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs) }, new HashSet<string>());

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/project", CancellationToken.None);

        Assert.Null(verdict.Refusal);
        Assert.Equal("STUDIO_MAC is offline, so the folder '/Users/pat/project' could not be checked. It is checked again when the schedule first runs.", verdict.Warning);
        Assert.Empty(tunnel.Sent);
    }

    [Fact]
    public async Task CheckAsync_AStoppedDirector_IsNotAsked_EvenWhenItsConnectionLingers()
    {
        var tunnel = new Tunnel();
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs, stopped: Stopped) }, new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/project", CancellationToken.None);

        Assert.StartsWith("STUDIO_MAC is offline", verdict.Warning);
        Assert.Empty(tunnel.Sent);
    }

    [Fact]
    public async Task CheckAsync_AMachineTheGatewayHasNeverSeen_IsAWarning()
    {
        var check = Check(new Tunnel(), Array.Empty<DirectorDto>(), new HashSet<string>());

        var verdict = await check.CheckAsync(A, "NEW_LAPTOP", "/Users/pat/project", CancellationToken.None);

        Assert.Null(verdict.Refusal);
        Assert.StartsWith("NEW_LAPTOP is offline", verdict.Warning);
    }

    [Fact]
    public async Task CheckAsync_ADirectorOlderThanTheCheck_IsAWarningNamingItsVersion()
    {
        var tunnel = new Tunnel { Answer = c => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown verb '{c.Verb}'") };
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs, version: "2.29.0") }, new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/project", CancellationToken.None);

        Assert.Null(verdict.Refusal);
        Assert.Equal("The Director on STUDIO_MAC is version 2.29.0, which is too old to check the folder '/Users/pat/project', so it was not checked. " +
                     "Update it. The folder is checked again when the schedule first runs.", verdict.Warning);
    }

    [Fact]
    public async Task CheckAsync_NoAnswerInTime_IsAWarning()
    {
        var tunnel = new Tunnel { Answer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer") };
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs) }, new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/project", CancellationToken.None);

        Assert.Null(verdict.Refusal);
        Assert.Contains("did not answer within 10 seconds", verdict.Warning);
    }

    [Fact]
    public async Task CheckAsync_AnAnswerItCannotRead_IsAWarning_NeverARefusal()
    {
        var tunnel = new Tunnel { Answer = _ => DirectorCommandResult.Success("not json") };
        var check = Check(tunnel, new[] { Director("d-mac", "STUDIO_MAC", DirectorOs.MacOs) }, new HashSet<string> { "d-mac" });

        var verdict = await check.CheckAsync(A, "STUDIO_MAC", "/Users/pat/project", CancellationToken.None);

        Assert.Null(verdict.Refusal);
        Assert.Contains("a shape the Gateway does not read", verdict.Warning);
    }

    [Fact]
    public void OsOf_PrefersARunningDirectorsReport()
    {
        var check = Check(new Tunnel(), new[]
        {
            Director("d-gone", "BOX", DirectorOs.Windows, stopped: Stopped),
            Director("d-now", "BOX", DirectorOs.Linux),
            Director("d-other", "ELSEWHERE", DirectorOs.MacOs),
        }, new HashSet<string>());

        Assert.Equal(DirectorOs.Linux, check.OsOf(A, "box"));
        Assert.Null(check.OsOf(A, "NOWHERE"));
    }
}
