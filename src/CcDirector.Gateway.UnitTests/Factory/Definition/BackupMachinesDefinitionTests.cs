using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// BACKUP MACHINES IN A DEFINITION (Mail Desk, phase 5, contract section 2, tests 6, 7 and 9): the check refuses a
/// list that cannot mean anything, the publish asks every machine on the list, maps the list onto the trigger, and
/// keeps or releases the claim by whether its holder is still on the list. The fixture's outreach seat runs on
/// SOREN_SOUTH, with an interval start whose check is <c>checks/new_mail.py</c>; its prompt is line 15 of
/// <c>seats/outreach.yaml</c>, so a <c>backupMachines</c> line put after it is line 16.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class BackupMachinesDefinitionTests : IDisposable
{
    private const string Seat = "seats/outreach.yaml";
    private const string Prompt = "    prompt: \"New mail: {count}. {context}\"\n";
    private const int BackupLine = 16;

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>The valid definition with <paramref name="line"/> added after the interval start's prompt.</summary>
    private static Dictionary<string, string> WithBackups(string line)
        => DefinitionFixture.Edit(Seat, Prompt, Prompt + "    " + line + "\n");

    // ---------- 6. the check ----------

    [Fact]
    public void Check_BackupMachinesOnAnIntervalStartWithACheck_IsAccepted_AndReadInOrder()
    {
        var result = DefinitionFixture.Check(WithBackups("backupMachines: [machine-b, machine-c]"));

        Assert.DoesNotContain(result.Findings, f => f.IsError);
        var start = result.Definition!.Seats.Single(s => s.Seat == "outreach").Starts.Single();
        Assert.Equal(new[] { "machine-b", "machine-c" }, start.BackupMachines);
    }

    [Fact]
    public void Check_BackupMachinesRepeatingTheSeatsMachine_IgnoringCase_IsRefused()
    {
        var result = DefinitionFixture.Check(WithBackups("backupMachines: [soren_south]"));

        DefinitionFixture.AssertOneError(result, Seat, BackupLine, "'soren_south' is the seat's own machine");
    }

    [Fact]
    public void Check_BackupMachinesNamingOneMachineTwice_IgnoringCase_IsRefused()
    {
        var result = DefinitionFixture.Check(WithBackups("backupMachines: [machine-b, Machine-B]"));

        DefinitionFixture.AssertOneError(result, Seat, BackupLine, "'Machine-B' is named twice", "name each machine once");
    }

    [Fact]
    public void Check_BackupMachinesOnACronStart_IsRefused()
    {
        // A time start may carry a check; it still runs where its seat runs, so a backup list is refused.
        var content = DefinitionFixture.Edit(Seat, "  - every: 10m\n", "  - cron: \"0 8 * * 1-5\"\n    timeZone: Europe/Copenhagen\n");
        content[Seat] = content[Seat].Replace(Prompt, Prompt + "    backupMachines: [machine-b]\n", StringComparison.Ordinal);

        var result = DefinitionFixture.Check(content);

        DefinitionFixture.AssertOneError(result, Seat, BackupLine + 1, "backupMachines is only allowed on a start with every");
    }

    [Fact]
    public void Check_BackupMachinesOnAStartWithNoCheck_IsRefused()
    {
        // An interval with no check is itself refused; the backup list is refused beside it, on its own line.
        var content = WithBackups("backupMachines: [machine-b]");
        content[Seat] = content[Seat].Replace("    check: checks/new_mail.py\n    secrets: [outreach-imap-password]\n", "", StringComparison.Ordinal);

        var result = DefinitionFixture.Check(content);

        Assert.Contains(result.Findings, f => f.IsError && f.File == Seat && f.Line == BackupLine - 2
                                              && f.Message.Contains("this start has no check", StringComparison.Ordinal));
        Assert.Null(result.Definition);
    }

    [Theory]
    [InlineData("backupMachines: [\" \"]")]
    [InlineData("backupMachines: []")]
    public void Check_ABlankBackupMachineOrAnEmptyList_IsRefused(string line)
    {
        var result = DefinitionFixture.Check(WithBackups(line));

        var error = Assert.Single(result.Findings, f => f.IsError);
        Assert.Equal(Seat, error.File);
        Assert.Contains("backupMachines", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Definition);
    }

    // ---------- 7. the machine questions ----------

    [Fact]
    public void QuestionsFor_AStartWithBackups_AsksEveryBackupForTheChecksSecretsAndPython_AndNotTheSeatsOwnNeeds()
    {
        var content = WithBackups("backupMachines: [machine-b, machine-c]");

        var questions = FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(content).Definition, content);

        Assert.Equal(new[] { "SOREN_SOUTH", "machine-b", "machine-c" }, questions.Select(q => q.Machine));
        foreach (var backup in questions.Skip(1))
        {
            Assert.Equal(new[] { "outreach-imap-password" }, backup.Secrets);
            Assert.True(backup.AskPython);
            Assert.Empty(backup.Repositories);
        }
    }

    [Fact]
    public void Check_APublishWhereABackupHasNoPython_SaysSoOnTheBackupsLine()
    {
        var content = WithBackups("backupMachines: [machine-b]");
        var replies = new Dictionary<string, MachineReply>(DefinitionFixture.NotConnected(content), StringComparer.OrdinalIgnoreCase)
        {
            ["machine-b"] = MachineReply.Answered(new CcDirector.Gateway.Contracts.FactoryMachineAnswer
            {
                Machine = "machine-b",
                Secrets = new() { new() { Name = "outreach-imap-password", Held = true, AllowedForRun = true } },
                Repositories = new(),
                Python = new() { LookedFor = "python3", Found = false },
            }),
        };

        var result = FactoryDefinitionCheck.Check(content, DefinitionFixture.FactoryId, DefinitionFixture.FleetSkills,
            DefinitionFixture.Machines, DefinitionCheckPurpose.Publish, replies);

        DefinitionFixture.AssertNotice(result, Seat, BackupLine, "machine-b has no Python");
    }

    // ---------- 7. the publish ----------

    [Fact]
    public void Publish_AStartWithBackups_WritesThemOnTheTrigger_AndNamesThemInTheStep()
    {
        var s = new FactoryPublishScenario(_h.Open(), TenantId.Local);

        var result = s.SaveAndPublish(WithBackups("backupMachines: [machine-b]"));

        var t = Assert.Single(s.Triggers(FactoryPublishScenario.Factory));
        Assert.Equal(new[] { "machine-b" }, TriggerMachines.BackupsOf(t));
        Assert.Contains(result.Applied, line => line.EndsWith(
            "on SOREN_SOUTH, then machine-b if SOREN_SOUTH is away.", StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_ARepublishRemovingTheHoldersMachine_ReleasesTheClaim()
    {
        var s = new FactoryPublishScenario(_h.Open(), TenantId.Local);
        s.SaveAndPublish(WithBackups("backupMachines: [machine-b, machine-c]"));
        var id = Claim(s, "dir-b", "machine-b");

        var result = s.SaveAndPublish(WithBackups("backupMachines: [machine-c]"));

        var t = s.Triggers(FactoryPublishScenario.Factory).Single();
        Assert.Equal(id, t.Id);
        Assert.Equal(("", "", null), (t.ClaimedByDirectorId, t.ClaimedByMachine, t.ClaimedUtc));
        Assert.Contains(result.Applied, line => line.Contains("backup machines machine-b, machine-c -> machine-c", StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_ARepublishKeepingTheHoldersMachine_KeepsTheClaim()
    {
        var s = new FactoryPublishScenario(_h.Open(), TenantId.Local);
        s.SaveAndPublish(WithBackups("backupMachines: [machine-b, machine-c]"));
        Claim(s, "dir-c", "machine-c");

        s.SaveAndPublish(WithBackups("backupMachines: [machine-c]"));

        var t = s.Triggers(FactoryPublishScenario.Factory).Single();
        Assert.Equal(("dir-c", "machine-c"), (t.ClaimedByDirectorId, t.ClaimedByMachine));
        Assert.Equal(new[] { "machine-c" }, TriggerMachines.BackupsOf(t));
    }

    [Fact]
    public void Publish_ARepublishDroppingEveryBackup_ReleasesABackupsClaim_ButNotTheSeatMachines()
    {
        var s = new FactoryPublishScenario(_h.Open(), TenantId.Local);
        s.SaveAndPublish(WithBackups("backupMachines: [machine-b]"));
        Claim(s, "dir-south", "SOREN_SOUTH");

        s.SaveAndPublish(DefinitionFixture.Valid());

        var t = s.Triggers(FactoryPublishScenario.Factory).Single();
        Assert.Equal(("dir-south", "SOREN_SOUTH"), (t.ClaimedByDirectorId, t.ClaimedByMachine));
        Assert.Empty(TriggerMachines.BackupsOf(t));
    }

    /// <summary>A Director on <paramref name="machine"/> claims the factory's one trigger, as its assignment poll does,
    /// after any claim before it has lapsed.</summary>
    private static Guid Claim(FactoryPublishScenario s, string director, string machine)
    {
        var store = new TriggerStore(s.Db);
        var claimed = Assert.Single(store.ClaimForDirector(s.Tenant, director, machine, FactoryPublishScenario.Now.AddDays(1),
            runsDefinitionChecks: true));
        return claimed.Id;
    }

    // ---------- what changes ----------

    [Fact]
    public void Changes_ABackupListChanged_IsADetailOfTheStart()
    {
        var before = FactoryDefinitionCheck.Read(WithBackups("backupMachines: [machine-b]")).Definition!;
        var after = FactoryDefinitionCheck.Read(WithBackups("backupMachines: [machine-c, machine-b]")).Definition!;

        Assert.False(FactoryDefinitionChanges.SameStart(before.Seats.Single(x => x.Seat == "outreach").Starts[0],
            after.Seats.Single(x => x.Seat == "outreach").Starts[0]));
    }
}
