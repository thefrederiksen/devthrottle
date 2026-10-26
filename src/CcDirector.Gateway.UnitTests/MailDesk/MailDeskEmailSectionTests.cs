using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE 08:00 EMAIL'S MAIL DESK SECTION (Mail Desk mission, phase 3 contract, section 9): folded on the Gateway by
/// <see cref="MailDeskEmailFold"/> over the real stores, laid out by <see cref="FactoryEmailComposer"/>. Each part has
/// its test: the counts with zeros, every junked email, every email kept for the owner, every email that could not be
/// screened, every list change with who and why, the spend, the observe line, the escaping of every sender and subject,
/// and the section absent - the email exactly as before - when the switch is off or the account has no mailbox.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskEmailSectionTests : IDisposable
{
    private const string Mailbox = "email@devthrottle.com";
    private const string OtherMailbox = "support@example.com";
    private const string Machine = "devlinux";
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
    private static readonly TenantId Account = new("acct-a");

    // The 08:00 email of Monday 19 October 2026 in Toronto covers the 24 hours from 08:00 on the 18th.
    private static readonly DateTime From = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 18, 8, 0, 0), Toronto);
    private static readonly DateTime To = From.AddHours(24);
    private static readonly DateTime Inside = From.AddHours(3);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly MailDeskSwitch _switch;
    private readonly MailDeskChannelStore _channels;
    private readonly MailDeskDecisionStore _decisions;
    private readonly MailDeskRulesStore _rules;
    private MailDeskSpend _spend = new(12_300, MailDeskSpend.DefaultLimitMicros, null);
    private int _key;

    public MailDeskEmailSectionTests()
    {
        _db = _h.Open();
        _switch = new MailDeskSwitch(new TenantSettingsStore(_db));
        _switch.Set(Account, true, "email@devthrottle.com", "the owner's account", From.AddDays(-1));
        _channels = new MailDeskChannelStore(_db);
        _decisions = new MailDeskDecisionStore(_db);
        _rules = new MailDeskRulesStore(_db);
    }

    public void Dispose() => _h.Dispose();

    // ---------- the rig ----------

    private MailDeskEmailFold Fold() => new(_switch.IsOn, _channels, _decisions, _rules, _ => _spend);

    private MailDeskEmailSection? Section() => Fold().Section(Account, From, To, Toronto);

    // Added the day before the window, so the add itself is not one of the window's changes.
    private MailDeskChannelEntity Channel(string address = Mailbox)
        => _channels.Add(Account, address, "outlook", new[] { Machine }, MailDeskWho.Person("seed"), "seeded", From.AddDays(-1)).Channel!;

    private void Decide(MailDeskChannelEntity channel, string decision, DateTime at, string sender = "news@example.org",
        string subject = "A newsletter", string layer = MailDeskWords.Layers.Allow, string reason = "allow news@example.org (a newsletter)")
        => _decisions.Insert(Account, new MailDeskDecisionInput(channel.Id, $"m{++_key}", at, sender, subject, layer, decision,
            decision == MailDeskWords.Decisions.Route ? "books" : null, decision == MailDeskWords.Decisions.Route ? "clerk" : null,
            reason, null, null, MailDeskWords.Modes.Observe, null, null, at));

    private static FactoryEmail Compose(MailDeskEmailSection? section)
    {
        var slot = FactoryEmailSlots.DueAt(To.AddMinutes(5), Toronto)!;
        return FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, Array.Empty<CronRunProblemDto>(),
            new[] { new CronFactoryDayDto { Factory = "mail-desk", Done = 3 } },
            new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase), BaseUrl: null, section));
    }

    // ---------- absent ----------

    [Fact]
    public void Section_SwitchOff_IsAbsent()
    {
        Channel();
        _switch.Set(Account, false, "email@devthrottle.com", "off", From);

        Assert.Null(Section());
    }

    [Fact]
    public void Section_NoMailbox_IsAbsent()
    {
        Assert.Null(Section());
    }

    [Fact]
    public void Section_OnlyARemovedMailbox_IsAbsent()
    {
        var channel = Channel();
        Decide(channel, MailDeskWords.Decisions.Junk, Inside);
        _channels.Remove(Account, Mailbox, MailDeskWho.Person("owner"), "not screened any more", Inside.AddHours(1));

        Assert.Null(Section());
    }

    [Fact]
    public void Compose_NoSection_IsByteForByteTheEmailWithoutTheMailDesk()
    {
        // Arrange - the fold the sweep is wired to, with the switch off
        Channel();
        _switch.Set(Account, false, "email@devthrottle.com", "off", From);

        // Act
        var withTheFold = Compose(Section());
        var slot = FactoryEmailSlots.DueAt(To.AddMinutes(5), Toronto)!;
        var asBefore = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, Array.Empty<CronRunProblemDto>(),
            new[] { new CronFactoryDayDto { Factory = "mail-desk", Done = 3 } },
            new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase), BaseUrl: null));

        // Assert
        Assert.Equal(asBefore.Subject, withTheFold.Subject);
        Assert.Equal(asBefore.Html, withTheFold.Html);
        Assert.Equal(asBefore.Text, withTheFold.Text);
        Assert.DoesNotContain(MailDeskEmailFold.Heading, withTheFold.Html);
    }

    [Fact]
    public void Compose_NoSection_MatchesTheEmailComposedBeforeTheMailDeskSectionExisted()
    {
        // The same input composed by the composer as it stood on the phase branch before this change, its HTML and
        // text hashed there (see MailDeskEmailGolden). A change to the email without the section fails here.
        var email = Compose(null);

        Assert.Equal(MailDeskEmailGolden.HtmlSha256, MailDeskEmailGolden.Sha256(email.Html));
        // The plain text is built with AppendLine, so its line endings are the build machine's: line feeds where the
        // golden was hashed and where the Gateway runs (Linux), carriage return plus line feed on Windows. The claim is
        // about the words, so the text is hashed with line feeds on every machine.
        Assert.Equal(MailDeskEmailGolden.TextSha256, MailDeskEmailGolden.Sha256(email.Text.Replace("\r\n", "\n")));
    }

    // ---------- 1. the counts ----------

    [Fact]
    public void Section_Counts_ListEveryDecisionWithZerosPerMailboxAndInTotal()
    {
        // Arrange
        var first = Channel();
        var second = Channel(OtherMailbox);
        Decide(first, MailDeskWords.Decisions.Junk, Inside, layer: MailDeskWords.Layers.Block, reason: "block spam@example.net (spam)");
        Decide(first, MailDeskWords.Decisions.Junk, Inside, layer: MailDeskWords.Layers.Block, reason: "block spam@example.net (spam)");
        Decide(first, MailDeskWords.Decisions.Archive, Inside);
        Decide(second, MailDeskWords.Decisions.Route, Inside, layer: MailDeskWords.Layers.Rule, reason: "rule invoices");
        Decide(first, MailDeskWords.Decisions.Archive, From.AddMinutes(-1));   // before the window
        Decide(first, MailDeskWords.Decisions.Archive, To);                    // after it

        // Act
        var section = Section()!;

        // Assert - every decision listed, in the words' own order, zeros included
        Assert.Equal(new[] { Mailbox, OtherMailbox }, section.Mailboxes.Select(m => m.Address));
        Assert.All(section.Mailboxes.Append(new MailDeskEmailMailbox("total", section.Total)),
            m => Assert.Equal(MailDeskWords.Decisions.All, m.Counts.ByDecision.Select(c => c.Decision)));
        var firstCounts = section.Mailboxes[0].Counts.ByDecision.ToDictionary(c => c.Decision, c => c.Count);
        Assert.Equal(3, section.Mailboxes[0].Counts.Total);
        Assert.Equal(2, firstCounts[MailDeskWords.Decisions.Junk]);
        Assert.Equal(1, firstCounts[MailDeskWords.Decisions.Archive]);
        Assert.Equal(0, firstCounts[MailDeskWords.Decisions.Route]);
        Assert.Equal(0, firstCounts[MailDeskWords.Decisions.Failed]);
        Assert.Equal(1, section.Mailboxes[1].Counts.Total);
        Assert.Equal(4, section.Total.Total);

        // and laid out with the zeros
        var email = Compose(section);
        Assert.Contains("Junked 2 - Kept for you 0 - For the Mail Desk seat 0 - Could not be screened 0", email.Html);
        Assert.Contains("All mailboxes: 4", email.Html);
        Assert.Contains($"{Mailbox}: 3 (Routed to a seat 0 - Archived 1 - Junked 2 - Kept for you 0 - For the Mail Desk seat 0 - Could not be screened 0)", email.Text);
    }

    [Fact]
    public void Section_NothingDecided_CountsAreAllZeros()
    {
        Channel();

        var section = Section()!;

        Assert.Equal(0, section.Total.Total);
        Assert.All(section.Total.ByDecision, c => Assert.Equal(0, c.Count));
        Assert.Contains("Junked (0)", Compose(section).Html);
    }

    [Fact]
    public void Section_ARemovedMailboxThatDecidedInTheWindow_IsCountedAndNamedRemoved()
    {
        Channel();
        var old = Channel(OtherMailbox);
        Decide(old, MailDeskWords.Decisions.Archive, Inside);
        _channels.Remove(Account, OtherMailbox, MailDeskWho.Person("owner"), "moved", Inside.AddHours(1));

        var section = Section()!;

        Assert.Contains(section.Mailboxes, m => m.Address == $"{OtherMailbox} (removed)" && m.Counts.Total == 1);
    }

    // ---------- 2. every junked email ----------

    [Fact]
    public void Section_EveryJunkedEmail_IsListedWithMailboxSenderSubjectLayerAndReasonNeverASample()
    {
        // Arrange - forty junked emails
        var channel = Channel();
        for (var i = 0; i < 40; i++)
            Decide(channel, MailDeskWords.Decisions.Junk, Inside.AddMinutes(i), sender: $"offer{i}@example.net", subject: $"Offer number {i}",
                layer: MailDeskWords.Layers.SenderScreen, reason: "sender-screen: spam at 0.97 (threshold 0.95)");

        // Act
        var section = Section()!;
        var email = Compose(section);

        // Assert
        Assert.Equal(40, section.Junked.Count);
        Assert.Contains("Junked (40)", email.Html);
        for (var i = 0; i < 40; i++)
        {
            Assert.Contains($"offer{i}@example.net - Offer number {i}", email.Html);
            Assert.Contains($"offer{i}@example.net - Offer number {i}", email.Text);
        }
        Assert.Contains($"{Mailbox} - sender-screen: sender-screen: spam at 0.97 (threshold 0.95)", email.Html);
    }

    // ---------- 3 and 4. kept for the owner, and could not be screened ----------

    [Fact]
    public void Section_KeptForTheOwner_ListsEveryOneBySenderAndSubject()
    {
        var channel = Channel();
        Decide(channel, MailDeskWords.Decisions.KeepForOwner, Inside, sender: "friend@example.com", subject: "Dinner on Friday",
            reason: "allow friend@example.com (a friend)");
        Decide(channel, MailDeskWords.Decisions.KeepForOwner, Inside.AddMinutes(1), sender: "bank@example.org", subject: "Your statement",
            reason: "allow bank@example.org (the bank)");

        var section = Section()!;
        var email = Compose(section);

        Assert.Equal(new[] { "Dinner on Friday", "Your statement" }, section.KeptForOwner.Select(m => m.Subject));
        Assert.Contains("Kept for you (2)", email.Html);
        Assert.Contains("friend@example.com - Dinner on Friday", email.Html);
        Assert.Contains("bank@example.org - Your statement", email.Text);
    }

    [Fact]
    public void Section_CouldNotBeScreened_ListsEveryFailedEmailAndEveryEmailKeptAfterThreeFailuresWithTheReason()
    {
        // Arrange
        var channel = Channel();
        Decide(channel, MailDeskWords.Decisions.Failed, Inside, sender: "a@example.com", subject: "First",
            layer: MailDeskWords.Layers.SenderScreen, reason: "the Mail Desk spend limit of $5.00 is reached; the model was not asked");
        Decide(channel, MailDeskWords.Decisions.Failed, Inside.AddMinutes(1), sender: "b@example.com", subject: "Second",
            layer: MailDeskWords.Layers.RouteModel, reason: "the model could not be asked (timeout)");
        Decide(channel, MailDeskWords.Decisions.KeepForOwner, Inside.AddMinutes(2), sender: "c@example.com", subject: "Third",
            layer: MailDeskWords.Layers.RouteModel, reason: MailDeskReasons.KeptAfterFailures("http-500"));

        // Act
        var section = Section()!;
        var email = Compose(section);

        // Assert - the third is listed here with its reason, and not again among the emails kept for the owner
        Assert.Equal(new[] { "First", "Second", "Third" }, section.NotScreened.Select(m => m.Subject));
        Assert.Empty(section.KeptForOwner);
        Assert.Contains("Could not be screened (3)", email.Html);
        Assert.Contains($"{Mailbox} - screening failed 3 times (http-500); kept for the owner", email.Html);
        Assert.Contains("the Mail Desk spend limit of $5.00 is reached; the model was not asked", email.Text);
    }

    [Fact]
    public void KeptAfterFailures_TheReasonItWrites_IsRecognisedAndAnOrdinaryReasonIsNot()
    {
        Assert.Equal("screening failed 3 times (timeout); kept for the owner", MailDeskReasons.KeptAfterFailures("timeout"));
        Assert.True(MailDeskReasons.IsKeptAfterFailures(MailDeskReasons.KeptAfterFailures("timeout")));
        Assert.False(MailDeskReasons.IsKeptAfterFailures("allow friend@example.com (a friend)"));
        Assert.False(MailDeskReasons.IsKeptAfterFailures("screening failed 3 times (); kept for the owner"));
    }

    // ---------- 5. the list changes ----------

    [Fact]
    public void Section_ListChanges_ListsEveryChangeInTheWindowWithWhatWhoAndWhy()
    {
        // Arrange
        var channel = Channel();
        var document = _rules.Newest(Account).Document.WithBlock(new MailDeskBlockEntry { From = "*@spam.example", Why = "spam" });
        _rules.Save(Account, 0, document, new MailDeskChange(MailDeskWords.ChangeKinds.Block, MailDeskWords.ChangeActions.Add,
            "*@spam.example", null, MailDeskWho.Session("11111111-2222-4333-8444-555555555555", "Mail Desk - Agent - triage"),
            "three spam emails from it today"), Inside);
        _channels.SetMode(Account, Mailbox, MailDeskWords.Modes.Live, MailDeskWho.Person("owner-browser-1"), "ready to act", Inside.AddHours(1));

        // Act
        var section = Section()!;
        var email = Compose(section);

        // Assert - the add the day before is not in the window
        Assert.Equal(2, section.Changes.Count);
        Assert.Equal("Block list entry added: *@spam.example", section.Changes[0].What);
        Assert.Equal("session 11111111-2222-4333-8444-555555555555 (Mail Desk - Agent - triage)", section.Changes[0].Who);
        Assert.Equal("three spam emails from it today", section.Changes[0].Why);
        Assert.Equal($"Mode of {Mailbox} set: observe to live", section.Changes[1].What);
        Assert.Equal("a person (owner-browser-1)", section.Changes[1].Who);
        Assert.Contains("Changes to the Mail Desk (2)", email.Html);
        Assert.Contains("by a person (owner-browser-1): ready to act", email.Text);
    }

    [Fact]
    public void WhatInWords_EveryKindAndAction_HasWords()
    {
        foreach (var kind in MailDeskWords.ChangeKinds.All)
        foreach (var action in MailDeskWords.ChangeActions.All)
        {
            var change = new MailDeskListChangeEntity
            {
                Kind = kind, Action = action, Subject = "x@example.com",
                Detail = kind switch
                {
                    MailDeskWords.ChangeKinds.Mode => "{\"from\":\"observe\",\"to\":\"live\"}",
                    MailDeskWords.ChangeKinds.Setting => "{\"from\":0.90,\"to\":0.85}",
                    _ => "{}",
                },
            };
            Assert.False(string.IsNullOrWhiteSpace(MailDeskEmailFold.WhatInWords(change)));
        }
    }

    [Fact]
    public void WhatInWords_SettingChange_SaysTheSettingAndItsNumbersAsStored()
    {
        // Arrange: the row MailDeskSettingsStore writes.
        var change = new MailDeskListChangeEntity
        {
            Kind = MailDeskWords.ChangeKinds.Setting, Action = MailDeskWords.ChangeActions.Set,
            Subject = MailDeskSettingNames.RouteThreshold, Detail = "{\"from\":0.90,\"to\":0.85}",
        };

        // Act
        var words = MailDeskEmailFold.WhatInWords(change);

        // Assert
        Assert.Equal("Setting route-threshold set: 0.90 to 0.85", words);
    }

    // ---------- 6. the spend ----------

    [Fact]
    public void Section_Spend_SaysSpentAndTheLimit()
    {
        Channel();
        _spend = new MailDeskSpend(12_300, 5_000_000, null);

        var section = Section()!;
        var email = Compose(section);

        Assert.Equal("Spent $0.0123 of the $5.00 limit.", section.SpendLine);
        Assert.Null(section.SpendStoppedSentence);
        Assert.Contains("Spent $0.0123 of the $5.00 limit.", email.Html);
        Assert.DoesNotContain("stopped asking the model", email.Html);
    }

    [Fact]
    public void Section_SpendLimitStoppedTheModel_SaysTheSpendSentence()
    {
        Channel();
        _spend = new MailDeskSpend(5_000_300, 5_000_000, Inside);

        var section = Section()!;
        var email = Compose(section);

        Assert.Equal(_spend.StoppedSentence, section.SpendStoppedSentence);
        Assert.Contains(FactoryEmailComposer.E(_spend.StoppedSentence), email.Html);
        Assert.Contains(_spend.StoppedSentence, email.Text);
    }

    // ---------- 7. the observe line ----------

    [Fact]
    public void Section_EveryMailboxInObserveMode_SaysNothingWasMoved()
    {
        Channel();
        Channel(OtherMailbox);

        var section = Section()!;

        Assert.Equal(MailDeskEmailFold.ObserveLine, section.ObserveLine);
        Assert.Contains(MailDeskEmailFold.ObserveLine, Compose(section).Html);
    }

    [Fact]
    public void Section_OneMailboxLive_HasNoObserveLine()
    {
        Channel();
        Channel(OtherMailbox);
        _channels.SetMode(Account, OtherMailbox, MailDeskWords.Modes.Live, MailDeskWho.Person("owner"), "ready", Inside);

        var section = Section()!;

        Assert.Null(section.ObserveLine);
        Assert.DoesNotContain(MailDeskEmailFold.ObserveLine, Compose(section).Html);
    }

    // ---------- escaping ----------

    [Fact]
    public void Compose_ASenderAndSubjectWithMarkup_AreEscapedAndTheBodyStaysAscii()
    {
        // Arrange
        var channel = Channel();
        Decide(channel, MailDeskWords.Decisions.Junk, Inside, sender: "x\"onmouseover=\"y@example.net",
            subject: "<script>alert('hi')</script> & café ☃\nSecond line", layer: MailDeskWords.Layers.Block, reason: "block <b>x</b>");

        // Act
        var email = Compose(Section());

        // Assert
        Assert.DoesNotContain("<script>", email.Html);
        Assert.DoesNotContain("<b>x</b>", email.Html);
        Assert.Contains("&lt;script&gt;alert(&#39;hi&#39;)&lt;/script&gt; &amp; caf&#233; &#9731;", email.Html);
        Assert.Contains("x&quot;onmouseover=&quot;y@example.net", email.Html);
        Assert.All(email.Html, c => Assert.InRange(c, '\0', '\x7f'));
        // In the plain text a line break in a subject cannot start a line of its own.
        Assert.Contains("café ☃ Second line", email.Text);
    }

    // ---------- the section in the email ----------

    [Fact]
    public void Compose_WithTheSection_PutsItAfterTheFactoriesAndNamesTheFactoryAsCovered()
    {
        Channel();

        var email = Compose(Section());

        var factories = email.Html.IndexOf("Last 24 hours", StringComparison.Ordinal);
        var mailDesk = email.Html.IndexOf(">\n" + MailDeskEmailFold.Heading + "\n<", StringComparison.Ordinal);
        Assert.True(factories > 0 && mailDesk > factories, "the Mail Desk section comes after the factories' table");
        Assert.Contains("MAIL DESK", email.Text);
        Assert.Contains(MailDeskWords.DefaultFactory, email.Factories);
    }

    [Fact]
    public void Compose_TheSectionAndNoFactoryOrProblem_IsStillSent()
    {
        Channel();
        var slot = FactoryEmailSlots.DueAt(To.AddMinutes(5), Toronto)!;

        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, Array.Empty<CronRunProblemDto>(),
            Array.Empty<CronFactoryDayDto>(), new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase), null, Section()));

        Assert.True(email.Send);
        Assert.Contains(MailDeskEmailFold.Heading, email.Html);
    }
}

/// <summary>
/// The 08:00 email of <see cref="MailDeskEmailSectionTests"/>'s fixed input as the composer made it BEFORE the Mail
/// Desk section existed: composed on <c>origin/mail-desk-phase-3</c> at 1f99231b9 and hashed there, so the test proves
/// the email without the section is byte for byte what it was.
/// </summary>
internal static class MailDeskEmailGolden
{
    // Re-pinned on 26 October 2026 for the one daily email the owner approved that day (Factories Run Alone): the 08:00
    // email's day table became a line per factory saying what its runs did, so the email without the section changed
    // on purpose. The section itself is untouched.
    public const string HtmlSha256 = "6AD814FB6FEC3182F7598C57D5A47D99E3C9D79BD30DEFF87C2F81A204A562F9";
    public const string TextSha256 = "3965C6F2D70279686BBED71CE2FCD1C6F424A9EE15C3AE96F27B932FE62F376E";

    public static string Sha256(string text)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
