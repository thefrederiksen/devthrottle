using System.Text.Json;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// "CHECKED BY" AND "SIGN-IN HELD" ON A MAILBOX CARD (mission "Zoho Mail Skill", section 4). The main machine is the first
/// machine on the Mail Desk start's list that the channel names, never the first the channel lists; a mailbox with no
/// backup says so and how to add one; a machine the start never runs on is named as one that never checks it; and the
/// sign-in is said to be kept by the provider's own tool on every named machine, never on the Gateway.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskCheckedByTests
{

    private static MailDeskChannelEntity Mailbox(string provider, params string[] machines) => new()
    {
        Kind = MailDeskWords.ChannelKinds.Email,
        Provider = provider,
        Address = "email@devthrottle.com",
        MachinesJson = JsonSerializer.Serialize(machines),
        Mode = MailDeskWords.Modes.Observe,
    };

    private static TriggerEntity Start(string machine, params string[] backups) => new()
    {
        Name = "mail-desk check",
        Factory = MailDeskTemplate.Factory,
        Machine = machine,
        CheckPath = MailDeskTemplate.CheckPath,
        BackupMachinesJson = TriggerMachines.ToJson(backups),
    };

    [Fact]
    public void Of_OneMachineOnTheStart_IsMainWithTheNoBackupNote()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "devlinux"), Start("devlinux"));

        Assert.Equal("devlinux (main)", row.Label);
        Assert.Equal("No backup machine. Ask your agent to add one.", row.Note);
        Assert.Equal(MailDeskChannelFold.Tones.Attention, row.NoteTone);
    }

    [Fact]
    public void Of_TwoMachines_MainIsTheStartsFirstNotTheChannelsFirst()
    {
        // The channel lists SOREN_NORTH first, but the start runs on devlinux first, so devlinux checks it.
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "SOREN_NORTH", "devlinux"), Start("devlinux", "SOREN_NORTH"));

        Assert.Equal("devlinux (main), SOREN_NORTH (backup)", row.Label);
        Assert.Null(row.Note);
        Assert.Null(row.NoteTone);
    }

    [Fact]
    public void Of_MachineNamesDifferInCase_StillMatchesTheStart()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("gmail", "DEVLINUX"), Start("devlinux"));

        Assert.Equal("DEVLINUX (main)", row.Label);
    }

    [Fact]
    public void Of_AMachineTheStartNeverRunsOn_IsNamedAsNeverChecking()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "devlinux", "attic-box"), Start("devlinux"));

        Assert.Equal("devlinux (main)", row.Label);
        Assert.Equal("attic-box never checks it: the Mail Desk check does not run there. "
                     + "Ask your agent to add it to the check, or take it off this mailbox.", row.Note);
        Assert.Equal(MailDeskChannelFold.Tones.Bad, row.NoteTone);
    }

    [Fact]
    public void Of_OnlyTheStartsBackupNamed_IsABackupAndSaysTheMailboxIsUnreadWhileTheStartsOwnMachineRuns()
    {
        // devlinux takes the claim back whenever it is up, and is never handed this mailbox.
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "SOREN_NORTH"), Start("devlinux", "SOREN_NORTH"));

        Assert.Equal("SOREN_NORTH (backup)", row.Label);
        Assert.Equal("While devlinux runs the Mail Desk check, this mailbox is not read, because it does not name that machine. "
                     + "Ask your agent to add devlinux to this mailbox.", row.Note);
        Assert.Equal(MailDeskChannelFold.Tones.Bad, row.NoteTone);
    }

    [Fact]
    public void Of_AGapInTheStartsList_NamesTheMachineWhileWhichTheMailboxIsUnread()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "alpha", "gamma"), Start("alpha", "beta", "gamma"));

        Assert.Equal("alpha (main), gamma (backup)", row.Label);
        Assert.Equal("While beta runs the Mail Desk check, this mailbox is not read, because it does not name that machine. "
                     + "Ask your agent to add beta to this mailbox.", row.Note);
        Assert.Equal(MailDeskChannelFold.Tones.Bad, row.NoteTone);
    }

    [Fact]
    public void Of_TwoGaps_NamesBothInThePlural()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "gamma"), Start("alpha", "beta", "gamma"));

        Assert.Equal("gamma (backup)", row.Label);
        Assert.Equal("While alpha or beta run the Mail Desk check, this mailbox is not read, because it does not name those machines. "
                     + "Ask your agent to add alpha and beta to this mailbox.", row.Note);
    }

    [Fact]
    public void Of_AStartMachineAfterTheLastNamedOne_IsNoGap()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "alpha", "beta"), Start("alpha", "beta", "gamma"));

        Assert.Equal("alpha (main), beta (backup)", row.Label);
        Assert.Null(row.Note);
    }

    [Fact]
    public void Of_TwoMachinesTheStartNeverRunsOn_NamesThemInThePlural()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "devlinux", "attic-box", "cellar-box"), Start("devlinux"));

        Assert.Equal("attic-box and cellar-box never check it: the Mail Desk check does not run there. "
                     + "Ask your agent to add them to the check, or take them off this mailbox.", row.Note);
    }

    [Fact]
    public void Of_NoNamedMachineIsOnTheStart_SaysNoMachineChecksIt()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "attic-box"), Start("devlinux", "SOREN_NORTH"));

        Assert.Equal("No machine checks it", row.Label);
        Assert.Equal("This mailbox names attic-box, and the Mail Desk check runs only on devlinux and SOREN_NORTH. "
                     + "Ask your agent to name one of those machines on this mailbox.", row.Note);
        Assert.Equal(MailDeskChannelFold.Tones.Bad, row.NoteTone);
    }

    [Fact]
    public void Of_NoStart_NamesTheMachinesAndSaysNothingChecksItYet()
    {
        var row = MailDeskCheckedBy.Of(Mailbox("outlook", "devlinux", "SOREN_NORTH"), start: null);

        Assert.Equal("devlinux, SOREN_NORTH", row.Label);
        Assert.Equal("Nothing checks it yet: the mail-desk factory is not published.", row.Note);
        Assert.Equal(MailDeskChannelFold.Tones.Neutral, row.NoteTone);
    }

    [Fact]
    public void Of_Help_SaysTheGatewayDoesNotReadMailAndNamesTheProvidersKeeper()
    {
        var outlook = MailDeskCheckedBy.Of(Mailbox("outlook", "devlinux"), Start("devlinux")).Help;
        var gmail = MailDeskCheckedBy.Of(Mailbox("gmail", "devlinux"), Start("devlinux")).Help;

        Assert.StartsWith("The Gateway does not read your mail.", outlook);
        Assert.Contains("saved by cc-outlook on those machines, never on the Gateway.", outlook);
        Assert.Contains("saved by cc-gmail on those machines, never on the Gateway.", gmail);
    }

    [Fact]
    public void Of_ProviderWithNoKeeper_Throws()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => MailDeskCheckedBy.Of(Mailbox("carrier-pigeon", "devlinux"), Start("devlinux")));

        Assert.Contains("'carrier-pigeon'", thrown.Message);
    }

    [Fact]
    public void SignInHeldOf_OneMachine_NamesItAndTheKeeper()
    {
        Assert.Equal("Saved by cc-outlook on devlinux, not on the Gateway",
            MailDeskCheckedBy.SignInHeldOf(Mailbox("outlook", "devlinux")));
    }

    [Fact]
    public void SignInHeldOf_TwoMachines_NamesBoth()
    {
        Assert.Equal("Saved by cc-gmail on devlinux and SOREN_NORTH, not on the Gateway",
            MailDeskCheckedBy.SignInHeldOf(Mailbox("gmail", "devlinux", "SOREN_NORTH")));
    }

    [Fact]
    public void SignInKeepers_EveryProvider_HasOne()
    {
        Assert.Equal(MailDeskWords.Providers.All.OrderBy(p => p), MailDeskWords.Providers.SignInKeepers.Keys.OrderBy(p => p));
    }
}
