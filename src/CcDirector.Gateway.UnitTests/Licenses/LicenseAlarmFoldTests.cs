using System.Text.Json;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Push;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE WORDS OF A LICENSE ALARM (License Switching phase 6), pinned: the red banner for no alarm, one and several; the
/// push on a locked screen, which never carries an account email address; the owner email; and the Licenses log's rows.
/// The clients render the banner's words verbatim, so a change here is a change the owner sees on every page.
/// </summary>
public sealed class LicenseAlarmFoldTests
{
    private static readonly LicenseAlarmKind NotReady = new("not-ready-at-80", "Not ready to switch");

    private static LicenseAlarmEntity Alarm(string machine, string account, string problem, string action, int minutes = 0) => new()
    {
        Kind = NotReady.Key,
        MachineId = machine.Length == 0 ? Guid.Empty : Guid.NewGuid(),
        MachineName = machine,
        AccountEmail = account,
        Problem = problem,
        Action = action,
        OpenedAtUtc = T0.AddMinutes(minutes),
    };

    [Fact]
    public void Banner_NoAlarm_IsEmpty_WithNoWords()
    {
        var banner = LicenseAlarmFold.Banner(Array.Empty<LicenseAlarmEntity>(), T0);

        Assert.Equal(0, banner.Count);
        Assert.Null(banner.Summary);
        Assert.Null(banner.LinkText);
        Assert.Empty(banner.Alarms);
    }

    [Fact]
    public void Banner_OneAlarm_IsOneLine_TheProblemThenTheOneThingToDo_AndNoSummary()
    {
        var banner = LicenseAlarmFold.Banner(new[]
        {
            Alarm("machine-a", "alice@example.com", "machine-a holds no spare that could take it off alice@example.com.",
                "Sign machine-a in to a second account."),
        }, T0);

        Assert.Equal(1, banner.Count);
        Assert.Null(banner.Summary);
        Assert.Equal("Open Licenses", banner.LinkText);
        var line = Assert.Single(banner.Alarms);
        Assert.Equal("machine-a holds no spare that could take it off alice@example.com. Sign machine-a in to a second account.", line.Text);
        Assert.Equal(("not-ready-at-80", "machine-a", "alice@example.com"), (line.Kind, line.MachineName, line.AccountEmail));
        Assert.Matches("^[0-9a-f]{32}$", line.AlarmId);
    }

    [Fact]
    public void Banner_SeveralAlarms_HasTheSummaryLine_AndOneLineEachInOrder()
    {
        var banner = LicenseAlarmFold.Banner(new[]
        {
            Alarm("machine-a", "alice@example.com", "First problem.", "First thing to do."),
            Alarm("", "bob@example.com", "Second problem.", "Second thing to do.", minutes: 1),
            Alarm("machine-b", "", "Third problem.", "Third thing to do.", minutes: 2),
        }, T0);

        Assert.Equal(3, banner.Count);
        Assert.Equal("3 license problems need you.", banner.Summary);
        Assert.Equal(new[] { "First problem. First thing to do.", "Second problem. Second thing to do.", "Third problem. Third thing to do." },
            banner.Alarms.Select(a => a.Text));
        // An alarm about no one machine, or no one account, says so with null - never an empty name.
        Assert.Null(banner.Alarms[1].MachineName);
        Assert.Null(banner.Alarms[2].AccountEmail);
    }

    // The push's words: the machine and the kind of problem only.
    [Fact]
    public void PushBody_NamesTheMachineAndTheKind_AndNothingElse()
    {
        Assert.Equal("machine-a: Not ready to switch. Open Licenses to see what to do.", LicenseAlarmFold.PushBody("machine-a", NotReady));
        Assert.Equal("Not ready to switch. Open Licenses to see what to do.", LicenseAlarmFold.PushBody("", NotReady));
    }

    // The whole push message, as the phone's service worker reads it, pinned - and an alarm whose own sentences name the
    // account puts no email address on the locked screen, because the push is built from the machine and the kind alone.
    [Fact]
    public void BuildPayload_IsPinned_AndCarriesNoAccountEmailAddress()
    {
        var alarmId = Guid.Parse("0123456789abcdef0123456789abcdef");

        var json = LicenseAlarmPushNotifier.BuildPayload(new LicenseAlarmOpened(alarmId, NotReady, "machine-a"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(new[] { "kind", "title", "body", "tag", "url" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("license-alarm", root.GetProperty("kind").GetString());
        Assert.Equal("License problem", root.GetProperty("title").GetString());
        Assert.Equal("machine-a: Not ready to switch. Open Licenses to see what to do.", root.GetProperty("body").GetString());
        Assert.Equal("devthrottle-license-alarm-0123456789abcdef0123456789abcdef", root.GetProperty("tag").GetString());
        Assert.Equal("/licenses", root.GetProperty("url").GetString());
        Assert.DoesNotContain("@", json);
    }

    [Fact]
    public void Email_OneAlarm_IsPinned()
    {
        var email = LicenseAlarmFold.Email(new[]
        {
            Alarm("machine-a", "alice@example.com", "machine-a holds no spare that could take it off alice@example.com.",
                "Sign machine-a in to a second account."),
        });

        Assert.Equal("DevThrottle license alarm: a problem on machine-a needs you", email.Subject);
        Assert.Equal(
            "machine-a holds no spare that could take it off alice@example.com.\r\n\r\n"
            + "Sign machine-a in to a second account.\r\n\r\n"
            + "Each problem is shown in red at the top of every page of the Cockpit until it is fixed, and on its Licenses page.\r\n"
            + "\r\nThis is the only email for this problem. Another is sent only if a problem is fixed and then comes back.",
            email.BodyText);
    }

    [Fact]
    public void Email_AlarmAboutNoMachine_SaysYourLicenses()
    {
        var email = LicenseAlarmFold.Email(new[] { Alarm("", "alice@example.com", "A problem.", "Do this.") });

        Assert.Equal("DevThrottle license alarm: a problem with your licenses needs you", email.Subject);
    }

    // The Licenses log shows an alarm's two rows with their own kind words, and the row's sentence as its summary.
    [Fact]
    public void LogEntries_AlarmRows_HaveTheirOwnKindWords()
    {
        var rows = new[]
        {
            new LicenseSwitchLogEntity
            {
                Kind = LicenseLogKinds.AlarmOpened, MachineName = "machine-a", ToAccountEmail = "alice@example.com",
                State = LicenseLogStates.Recorded, Message = LicenseAlarmFold.OpenedLogMessage("A problem.", "Do this."),
                CreatedAtUtc = T0, FinishedAtUtc = T0,
            },
            new LicenseSwitchLogEntity
            {
                Kind = LicenseLogKinds.AlarmClosed, MachineName = "machine-a", ToAccountEmail = "alice@example.com",
                State = LicenseLogStates.Recorded, Message = LicenseAlarmFold.ClosedLogMessage("A problem."),
                CreatedAtUtc = T0.AddMinutes(5), FinishedAtUtc = T0.AddMinutes(5),
            },
        };

        var entries = LicenseDisplayFold.LogEntries(rows, Array.Empty<(string, string)>());

        Assert.Equal(new[] { "Alarm opened", "Alarm closed" }, entries.Select(e => e.KindText));
        Assert.Equal(new[] { "Alarm opened. A problem. Do this.", "Alarm closed, because this is no longer true: A problem." },
            entries.Select(e => e.Summary));
        Assert.All(entries, e => Assert.False(e.IsOpen));
    }
}
