using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// ONE SOURCE OF TRUTH, ITS TWO RULES (Factory Definition mission, phase 4): what makes a factory DEFINED
/// (<see cref="FactoryDefinitionStore.IsDefined"/>: a published version exists; drafts do not count), and what tells a
/// schedule edit from a switch on or off (<see cref="DefinedByDefinition.DiffersOnlyInEnabled"/>, every field the
/// store writes from a body compared one by one). The routes that use them are proven in
/// <see cref="DefinedByDefinitionRoutesThroughThePipelineTests"/>.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class DefinedByDefinitionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");

    private readonly GatewayDbTestHarness _h = new();
    private readonly FactoryDefinitionStore _store;

    public DefinedByDefinitionTests()
    {
        _store = new FactoryDefinitionStore(_h.Open());
    }

    public void Dispose() => _h.Dispose();

    private void SaveDraft(TenantId tenant, int expect) =>
        Assert.True(_store.SaveDraft(tenant, DefinitionFixture.FactoryId, DefinitionFixture.Valid(), expect,
            FactoryMemoryAuthorKinds.Person, "owner", null, Now).Ok);

    private void Publish(TenantId tenant, int version) =>
        Assert.True(_store.Publish(tenant, DefinitionFixture.FactoryId, version, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);

    // ---------- is this factory defined ----------

    [Fact]
    public void IsDefined_UNDEFINED_FACTORY_NoDefinitionAtAll_IsFalse()
    {
        Assert.False(_store.IsDefined(A, DefinitionFixture.FactoryId));
    }

    [Fact]
    public void IsDefined_UNDEFINED_FACTORY_OnlyDrafts_IsFalse()
    {
        SaveDraft(A, 0);
        SaveDraft(A, 1);

        Assert.False(_store.IsDefined(A, DefinitionFixture.FactoryId));
    }

    [Fact]
    public void IsDefined_APublishedVersion_IsTrue_AndStaysTrueWithANewerDraftAbove()
    {
        SaveDraft(A, 0);
        Publish(A, 1);
        SaveDraft(A, 1);

        Assert.True(_store.IsDefined(A, DefinitionFixture.FactoryId));
    }

    [Fact]
    public void IsDefined_ARestoredVersion_IsTrue()
    {
        SaveDraft(A, 0);
        Publish(A, 1);
        SaveDraft(A, 1);
        Publish(A, 2);
        Assert.True(_store.Restore(A, DefinitionFixture.FactoryId, 1, FactoryMemoryAuthorKinds.Person, "owner", null, Now).Ok);

        Assert.True(_store.IsDefined(A, DefinitionFixture.FactoryId));
    }

    [Fact]
    public void IsDefined_AnotherSpellingOfTheId_FoldsToTheSameFactory()
    {
        SaveDraft(A, 0);
        Publish(A, 1);

        Assert.True(_store.IsDefined(A, "DevThrottle"));
    }

    [Fact]
    public void IsDefined_AnotherAccountsPublishedFactory_IsNotThisAccounts()
    {
        SaveDraft(A, 0);
        Publish(A, 1);

        Assert.False(_store.IsDefined(B, DefinitionFixture.FactoryId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a factory id!")]
    [InlineData("warmforward")]
    public void IsDefined_ABlankMisspeltOrOtherFactory_IsFalse(string? factory)
    {
        SaveDraft(A, 0);
        Publish(A, 1);

        Assert.False(_store.IsDefined(A, factory));
    }

    // ---------- an edit, or a switch ----------

    private static CronJobDto Stored() => new()
    {
        Id = "cj_boss",
        Name = "DevThrottle - Boss",
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 8 * * 1-5",
        RunAt = null,
        TimeZoneId = "Europe/Copenhagen",
        Factory = "devthrottle",
        Seat = "boss",
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\factories\devthrottle", Seed = "Morning round.", WorkListName = null, AutoDismiss = true },
        PreventOverlap = true,
        NotifyOn = CronNotify.None,
        NotifyWebhookUrl = null,
        CreatedUtc = Now.AddDays(-3),
        NextRunUtc = Now.AddHours(1),
        ScheduleText = "Weekdays 08:00",
    };

    /// <summary>The stored job re-sent exactly as `schedule enable|disable` re-sends it, with one field changed by the test.</summary>
    private static CronJobDto Resent(Action<CronJobDto>? change = null)
    {
        var body = Stored();
        change?.Invoke(body);
        return body;
    }

    [Fact]
    public void DiffersOnlyInEnabled_TheStoredJobResentWithEnabledFlipped_IsASwitch()
    {
        Assert.True(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), Resent(b => b.Enabled = false)));
    }

    [Fact]
    public void DiffersOnlyInEnabled_TheStoredJobResentUnchanged_IsASwitch()
    {
        Assert.True(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), Resent()));
    }

    [Fact]
    public void DiffersOnlyInEnabled_DisplayFieldsTheGatewayStamps_AreNotPartOfTheDefinition()
    {
        var body = Resent(b =>
        {
            b.Enabled = false;
            b.ScheduleText = "something else";
            b.StateLabel = "Paused";
            b.NextRunUtc = null;
            b.LastStatus = "ok";
            b.Shifts = new List<string> { "day" };
        });

        Assert.True(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), body));
    }

    [Fact]
    public void DiffersOnlyInEnabled_ABlankFactoryAndSeat_KeepTheStoredOnes_SoTheyAreTheSame()
    {
        Assert.True(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), Resent(b => { b.Enabled = false; b.Factory = null; b.Seat = ""; })));
    }

    [Fact]
    public void DiffersOnlyInEnabled_ASeatSpelledAnotherWay_FoldsToTheStoredSeat()
    {
        Assert.True(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), Resent(b => b.Seat = "Boss")));
    }

    [Fact]
    public void DiffersOnlyInEnabled_NotifyOnSpelledAnotherWay_IsTheSamePolicy()
    {
        Assert.True(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), Resent(b => b.NotifyOn = "")));
    }

    /// <summary>Every field the store writes from a body, each changed on its own, with Enabled flipped too.</summary>
    public static IEnumerable<object[]> EveryDefinitionField() => new (string Field, Action<CronJobDto> Change)[]
    {
        ("Name", b => b.Name = "Renamed"),
        ("ScheduleKind", b => b.ScheduleKind = CronSchedule.KindOneOff),
        ("CronExpression", b => b.CronExpression = "0 9 * * 1-5"),
        ("RunAt", b => b.RunAt = "2026-11-01T08:00"),
        ("TimeZoneId", b => b.TimeZoneId = "UTC"),
        ("Factory", b => b.Factory = "warmforward"),
        ("Seat", b => b.Seat = "outreach"),
        ("Target.Machine", b => b.Target.Machine = "SOREN_SOUTH"),
        ("Action.RepoPath", b => b.Action.RepoPath = @"D:\elsewhere"),
        ("Action.Seed", b => b.Action.Seed = "Another prompt."),
        ("Action.WorkListName", b => b.Action.WorkListName = "inbox"),
        ("Action.AutoDismiss", b => b.Action.AutoDismiss = false),
        ("PreventOverlap", b => b.PreventOverlap = false),
        ("NotifyOn", b => b.NotifyOn = CronNotify.Failure),
        ("NotifyWebhookUrl", b => b.NotifyWebhookUrl = "https://example.com/hook"),
    }.Select(f => new object[] { f.Field, f.Change });

    [Theory]
    [MemberData(nameof(EveryDefinitionField))]
    public void DiffersOnlyInEnabled_AnyDefinitionFieldChanged_IsAnEdit(string field, Action<CronJobDto> change)
    {
        Assert.False(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), Resent(change)), field);
        Assert.False(DefinedByDefinition.DiffersOnlyInEnabled(Stored(), Resent(b => { change(b); b.Enabled = false; })), field);
    }

    [Fact]
    public void EveryDefinitionField_CoversEveryFieldTheStoreWritesFromABody()
    {
        // The store's own list (CronJobStore.ApplyDefinition): a field added there and not here would let an edit
        // through as a switch. Enabled is the one field a switch may change.
        var written = new[]
        {
            "Name", "ScheduleKind", "CronExpression", "RunAt", "TimeZoneId", "Factory", "Seat", "Target.Machine",
            "Action.RepoPath", "Action.Seed", "Action.WorkListName", "Action.AutoDismiss", "PreventOverlap", "NotifyOn",
            "NotifyWebhookUrl",
        };

        Assert.Equal(written, EveryDefinitionField().Select(row => (string)row[0]));
        Assert.Equal(new[] { "AutoDismiss", "RepoPath", "Seed", "WorkListName" },
            typeof(CronJobAction).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "Machine" }, typeof(CronJobTarget).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void Refusal_IsThe409WithTheSentenceAndTheOutcome()
    {
        Assert.Equal("This factory is defined by its definition; edit the definition.", DefinedByDefinition.Sentence);
        Assert.Equal("DefinedByDefinition", DefinedByDefinition.Outcome);
    }
}
