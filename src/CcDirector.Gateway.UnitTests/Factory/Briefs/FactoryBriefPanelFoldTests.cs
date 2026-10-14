using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Briefs;

/// <summary>
/// THE BRIEF PANEL'S WORDS (Factory Seats mission, phase 2b), proved without a server. The cases are the ones the
/// owner's screen is hard at: a seat with no brief on the Gateway, a seat with drafts and nothing published, a
/// published brief with a newer draft waiting, twenty drafts waiting, and which button each version of a mixed
/// history carries - checked against what the store itself accepts, so the screen never offers a button the store
/// refuses or hides one it would accept.
/// </summary>
[Trait("Category", "FactoryBrief")]
public sealed class FactoryBriefPanelFoldTests : IDisposable
{
    private const string Session = "73000000-0000-4000-8000-000000000001";
    private const string Owner = "device browser device-1";
    private static readonly TenantId A = new("acct-a");

    // 13 Oct 2026 15:00 UTC; the account is on UTC so "today" is plain.
    private static readonly DateTime Now = new(2026, 10, 13, 15, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly RegisteredFactorySeatDto Boss =
        new() { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml", Computer = "devlinux" };

    private static readonly RegisteredFactorySeatDto Scout =
        new() { Id = "scout", Name = "Scout", Role = "Scout", BriefFile = "agents/scout.yaml", Computer = "devlinux" };

    private static readonly RegisteredFactoryDto WarmForward = new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = "/home/soren/factories/warmforward",
        Computer = "devlinux",
        BossSeat = "boss",
        Seats = { Boss, Scout },
    };

    private static FactoryBriefVersionEntity V(int version, bool published, string text = "text", bool bySession = true,
        DateTime? written = null, DateTime? publishedAt = null) => new()
    {
        TenantId = A.Value,
        Factory = "warmforward",
        Seat = "scout",
        Version = version,
        Text = text,
        TextBytes = System.Text.Encoding.UTF8.GetByteCount(text),
        State = published ? FactoryBriefStates.Published : FactoryBriefStates.Draft,
        AuthorKind = bySession ? FactoryMemoryAuthorKinds.Session : FactoryMemoryAuthorKinds.Person,
        AuthorId = bySession ? Session : Owner,
        WrittenAtUtc = written ?? Now.AddHours(-2),
        PublishedAtUtc = published ? publishedAt ?? Now.AddHours(-1) : null,
        PublishedBy = published ? Owner : null,
    };

    private static FactoryBriefVersionSummary S(FactoryBriefVersionEntity v) =>
        new(v.Factory, v.Seat, v.Version, v.State, v.AuthorKind, v.AuthorId, v.WrittenAtUtc, v.PublishedAtUtc, v.PublishedBy, v.TextBytes);

    private static FactorySeatBriefDto Panel(FactoryBriefStanding standing, RegisteredFactorySeatDto? seat = null) =>
        FactoryBriefPanelFold.Panel(WarmForward, seat ?? Scout, standing, TimeZoneInfo.Utc, Now);

    // ---------- the panel on the seat page ----------

    [Fact]
    public void Panel_ASeatWithNoBriefOnTheGateway_KeepsTheFile_AndOffersToWriteTheBriefHere()
    {
        var panel = Panel(FactoryBriefStanding.None);

        Assert.Equal(FactorySeatBriefStates.File, panel.State);
        Assert.Equal("What this seat does", panel.Heading);
        Assert.Equal("Brief file", panel.PathLabel);
        Assert.Equal("/home/soren/factories/warmforward/agents/scout.yaml", panel.PathText);
        Assert.Equal("Computer", panel.ComputerLabel);
        Assert.Equal("devlinux", panel.ComputerText);
        Assert.Equal("The brief is a file on devlinux. It is not held on the Gateway yet, so it cannot be read or changed here. " +
                     "A talk and a scheduled run of this seat are told this path and read the file from that computer.", panel.Note);
        Assert.Equal("Write the brief here", panel.EditLabel);
        // Nothing to read, nothing waiting, and no history to open.
        Assert.Null(panel.Text);
        Assert.Null(panel.VersionText);
        Assert.Null(panel.DraftText);
        Assert.Null(panel.WaitingText);
        Assert.Null(panel.HistoryLabel);
    }

    [Fact]
    public void Panel_ASeatWithDraftsAndNothingPublished_SaysSo_KeepsTheFile_AndOffersTheNewestDraft()
    {
        var newest = V(2, published: false);

        var panel = Panel(new FactoryBriefStanding(newest, null));

        Assert.Equal(FactorySeatBriefStates.Drafts, panel.State);
        Assert.Equal($"This seat has 2 drafts on the Gateway and none is published. The newest is version 2, written by session {Session} today 13:00. " +
                     "Until you publish one, talks and runs of this seat are told the path of its brief file.", panel.DraftText);
        Assert.Equal("Open the newest draft", panel.EditLabel);
        Assert.Equal("History", panel.HistoryLabel);
        // A draft is not what a talk or a run is given, so the page still says where the file is and shows no text as the brief.
        Assert.Equal("/home/soren/factories/warmforward/agents/scout.yaml", panel.PathText);
        Assert.NotNull(panel.Note);
        Assert.Null(panel.Text);
        Assert.Null(panel.VersionText);
    }

    [Fact]
    public void Panel_APublishedBrief_ShowsItsText_ItsVersion_WhoPublishedIt_AndWhen()
    {
        var published = V(3, published: true, text: "You are the Scout of WarmForward.");

        var panel = Panel(new FactoryBriefStanding(published, published));

        Assert.Equal(FactorySeatBriefStates.Published, panel.State);
        Assert.Equal("You are the Scout of WarmForward.", panel.Text);
        Assert.Equal($"Version 3, published by a person ({Owner}) today 14:00.", panel.VersionText);
        Assert.Equal("Edit", panel.EditLabel);
        Assert.Equal("History", panel.HistoryLabel);
        Assert.Null(panel.DraftText);
        Assert.Null(panel.WaitingText);
        // The brief is on the Gateway: the file is not what the seat is told any more, so it is not shown.
        Assert.Null(panel.PathText);
        Assert.Null(panel.PathLabel);
        Assert.Null(panel.ComputerText);
        Assert.Null(panel.Note);
    }

    [Fact]
    public void Panel_APublishedBriefWithANewerDraft_ShowsThePublishedText_AndSaysTheDraftIsNotWhatRunsAreGiven()
    {
        var published = V(3, published: true, text: "published text");
        var draft = V(5, published: false, text: "a draft a session left", written: Now.AddDays(-3));

        var panel = Panel(new FactoryBriefStanding(draft, published));

        Assert.Equal("published text", panel.Text);
        Assert.Equal($"A newer draft, version 5, was written by session {Session} 10 Oct 15:00. " +
                     "Talks and runs of this seat are given version 3 until you publish a newer one.", panel.DraftText);
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(21, true)]
    public void Panel_TwentyDraftsWaiting_SaysSessionsAreRefused_AndNineteenSaysNothing(int waiting, bool said)
    {
        var published = V(3, published: true);
        var newest = V(3 + waiting, published: false);

        var panel = Panel(new FactoryBriefStanding(newest, published));

        if (!said)
        {
            Assert.Null(panel.WaitingText);
            return;
        }
        Assert.Equal($"{waiting} drafts are waiting that nobody has published (versions 4 to {3 + waiting}). " +
                     "A session of this factory is refused another draft while 20 or more are waiting. " +
                     "Publish one to make room. Every version is kept.", panel.WaitingText);
    }

    [Fact]
    public void Waiting_IsSaidExactlyWhenTheStoreRefusesASessionsDraft()
    {
        // The store is the one that refuses. The page must say "sessions are refused" when, and only when, it does.
        var db = _h.Open();
        var registry = new FactoryRegistryStore(db);
        registry.Register(A, new RegisterFactoryRequest
        {
            Factory = "warmforward", Title = "WarmForward", Folder = "/home/soren/factories/warmforward", Computer = "devlinux",
            Seats = { new FactorySeatManifest { Id = "scout", Name = "Scout", Role = "Scout", BriefFile = "agents/scout.yaml" } },
        }, "the owner (test)", Now);
        var store = new FactoryBriefStore(db, registry);

        for (var i = 0; i < FactoryBriefStore.MaxWaitingDrafts + 2; i++)
        {
            var before = store.Standing(A, "warmforward", "scout");
            var saidBefore = FactoryBriefPanelFold.Waiting(before) is not null;
            var write = store.SaveDraft(A, "warmforward", "scout", $"draft {i}", before.Newest?.Version ?? 0,
                FactoryMemoryAuthorKinds.Session, Session, Now);
            Assert.Equal(saidBefore, write.Outcome == FactoryBriefOutcome.TooManyDrafts);
        }
        Assert.Equal(FactoryBriefStore.MaxWaitingDrafts, store.Standing(A, "warmforward", "scout").Newest!.Version);
    }

    [Fact]
    public void Panel_TheBossSeat_IsCalledBoss_InEverySentenceThatNamesIt()
    {
        var boss = new RegisteredFactorySeatDto { Id = "boss", Name = "Mara Lind", Role = "CFO", BriefFile = "agents/boss.yaml", Computer = "devlinux" };
        var factory = new RegisteredFactoryDto { Factory = "warmforward", Title = "WarmForward", Folder = "/f", BossSeat = "boss", Seats = { boss } };

        var editor = FactoryBriefPanelFold.Editor(factory, boss, FactoryBriefStanding.None, TimeZoneInfo.Utc, Now);

        Assert.Equal("Write the brief of Boss", editor.Heading);
        Assert.Equal("Publish this brief for Boss?", editor.Publish.ConfirmTitle);
    }

    // ---------- the editor ----------

    [Fact]
    public void Editor_ASeatWithNoBrief_StartsEmpty_ExpectingVersionZero_AndSaysWhereTheFileIs()
    {
        var editor = FactoryBriefPanelFold.Editor(WarmForward, Scout, FactoryBriefStanding.None, TimeZoneInfo.Utc, Now);

        Assert.Equal("Write the brief of Scout", editor.Heading);
        Assert.Equal("Scout has no brief on the Gateway yet. Write it here. Until one is published, talks and runs of this seat are told " +
                     "the path of its brief file, /home/soren/factories/warmforward/agents/scout.yaml on devlinux.", editor.StartText);
        Assert.Equal("", editor.Text);
        Assert.Equal(0, editor.ExpectedVersion);
        Assert.Equal("A brief takes at most 65,536 bytes.", editor.LimitText);
        Assert.Equal("Save draft", editor.SaveLabel);
        Assert.Equal("Saved as a draft. Nothing is published yet, so talks and runs of this seat are still told the path of its brief file.", editor.SavedText);
        Assert.Equal("It is the first published brief of this seat. Until now its talks and runs were told the path of its brief file.",
            editor.Publish.ConfirmLines[^1]);
    }

    [Fact]
    public void Editor_APublishedBrief_StartsFromIt_AndThePublishConfirmSaysEveryLaterTalkAndRunIsGivenThisText()
    {
        var published = V(3, published: true, text: "the published brief");

        var editor = FactoryBriefPanelFold.Editor(WarmForward, Scout, new FactoryBriefStanding(published, published), TimeZoneInfo.Utc, Now);

        Assert.Equal("Edit the brief of Scout", editor.Heading);
        Assert.Equal("This starts from version 3, the published brief.", editor.StartText);
        Assert.Equal("the published brief", editor.Text);
        Assert.Equal(3, editor.ExpectedVersion);
        Assert.Equal("Saved as a draft. Talks and runs of this seat are still given version 3 until you publish.", editor.SavedText);
        Assert.Equal("Publish", editor.Publish.Label);
        Assert.Equal("Publish this brief for Scout?", editor.Publish.ConfirmTitle);
        Assert.Equal(new[]
        {
            "Every later talk and every later run of Scout will be given this text.",
            "A text you have changed is saved as a new version first, and that version is published.",
            "It replaces version 3, which is kept. History can bring it back.",
        }, editor.Publish.ConfirmLines);
        Assert.Equal("Published. Every later talk and every later run of Scout is given this text.", editor.Publish.DoneText);
    }

    [Fact]
    public void Editor_ANewerDraftOverAPublishedBrief_StartsFromTheDraft_AndSaysPlainlyItIsNotThePublishedOne()
    {
        var published = V(3, published: true, text: "published");
        var draft = V(5, published: false, text: "what a session proposes");

        var editor = FactoryBriefPanelFold.Editor(WarmForward, Scout, new FactoryBriefStanding(draft, published), TimeZoneInfo.Utc, Now);

        Assert.Equal($"This starts from version 5, the newest draft, written by session {Session} today 13:00. " +
                     "It is not the published brief: talks and runs of this seat are given version 3.", editor.StartText);
        Assert.Equal("what a session proposes", editor.Text);
        // A save must state the NEWEST version, which is the draft's - not the published one's.
        Assert.Equal(5, editor.ExpectedVersion);
        Assert.Equal(5, editor.Publish.Version);
    }

    [Fact]
    public void Editor_DraftsAndNothingPublished_StartsFromTheNewestDraft_AndSaysNothingIsPublished()
    {
        var draft = V(2, published: false, text: "second try", bySession: false);

        var editor = FactoryBriefPanelFold.Editor(WarmForward, Scout, new FactoryBriefStanding(draft, null), TimeZoneInfo.Utc, Now);

        Assert.Equal($"This starts from version 2, the newest draft, written by a person ({Owner}) today 13:00. Nothing is published yet.", editor.StartText);
        Assert.Equal(2, editor.ExpectedVersion);
    }

    [Fact]
    public void Editor_CarriesTheWordsOfARefusedSave_SoTheScreenNeverInventsThem()
    {
        var editor = FactoryBriefPanelFold.Editor(WarmForward, Scout, FactoryBriefStanding.None, TimeZoneInfo.Utc, Now);

        Assert.Equal("Nothing was saved. Your text is still in the box, and the newer version is shown beside it. " +
                     "Merge what you need into your text and save it after the newer version, or take the newer version.", editor.ConflictText);
        Assert.Equal("Your text", editor.YoursLabel);
        Assert.Equal("The newer version", editor.TheirsLabel);
        Assert.Equal("Save my text after the newer version", editor.SaveOverLabel);
        Assert.Equal("Take the newer version and drop my text", editor.TakeTheirsLabel);
        Assert.Equal("Close without saving", editor.CancelLabel);
    }

    // ---------- the history ----------

    // Version 1 published once, version 2 a draft nobody published, version 3 published now, versions 4 and 5 waiting.
    private static FactoryBriefVersionEntity[] Mixed() => new[]
    {
        V(5, published: false, text: new string('x', 2048)),
        V(4, published: false, bySession: false),
        V(3, published: true),
        V(2, published: false),
        V(1, published: true, text: "one"),
    };

    private static FactoryBriefHistoryViewDto MixedHistory()
    {
        var all = Mixed();
        var standing = new FactoryBriefStanding(all[0], all[2]);
        return FactoryBriefPanelFold.History(WarmForward, Scout, standing,
            new FactoryBriefHistoryPage(all.Select(S).ToList(), 5, null), firstPage: true, TimeZoneInfo.Utc, Now);
    }

    [Fact]
    public void History_EachVersion_SaysItsState_WhoWroteIt_When_AndItsSize()
    {
        var view = MixedHistory();

        Assert.Equal("History of the brief of Scout", view.Heading);
        Assert.Equal("5 versions. Version 3 is published.", view.SummaryText);
        Assert.Equal("Versions 5 to 1 of 5.", view.PageText);
        Assert.Equal(new[] { "Version", "State", "Written by", "When", "Size" }, view.Columns);
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, view.Rows.Select(r => r.Version).ToArray());
        Assert.Equal(new[] { "Draft", "Draft", "Published", "Draft, passed over", "Published earlier" }, view.Rows.Select(r => r.StateWord).ToArray());
        Assert.Equal(new[] { FactoryTone.Amber, FactoryTone.Amber, FactoryTone.Ok, FactoryTone.Grey, FactoryTone.Grey }, view.Rows.Select(r => r.StateTone).ToArray());
        Assert.Equal($"session {Session}", view.Rows[0].WhoText);
        Assert.Equal($"a person ({Owner})", view.Rows[1].WhoText);
        Assert.Equal("today 13:00", view.Rows[0].WhenText);
        Assert.Equal("2.0 KB", view.Rows[0].SizeText);
        Assert.Equal("3 bytes", view.Rows[4].SizeText);
        Assert.All(view.Rows, r => Assert.Equal("Open", r.OpenLabel));
        Assert.Null(view.EmptyText);
        Assert.Null(view.NextBefore);
        Assert.Null(view.OlderLabel);
        Assert.Null(view.NewestLabel);
        Assert.Equal("Back to the brief", view.CloseLabel);
    }

    [Fact]
    public void History_Publish_IsOnADraftAboveThePublishedVersion_AndBringBack_OnEveryVersionBelowIt()
    {
        var rows = MixedHistory().Rows.ToDictionary(r => r.Version);

        Assert.NotNull(rows[5].Publish);
        Assert.NotNull(rows[4].Publish);
        Assert.Null(rows[3].Publish);
        Assert.Null(rows[2].Publish);
        Assert.Null(rows[1].Publish);

        Assert.Null(rows[5].BringBack);
        Assert.Null(rows[4].BringBack);
        Assert.Null(rows[3].BringBack);
        Assert.NotNull(rows[2].BringBack);
        Assert.NotNull(rows[1].BringBack);
    }

    [Fact]
    public void History_EveryButtonItOffers_TheStoreAccepts_AndWhereItOffersNoPublish_TheStoreRefusesOne()
    {
        // The same history, written into a real store once for every press, so each press meets it untouched.
        var view = MixedHistory();
        foreach (var row in view.Rows)
        {
            // Publish is offered exactly where the store publishes.
            Assert.Equal(row.Publish is not null, Press(store => store.Publish(A, "warmforward", "scout", row.Version,
                FactoryMemoryAuthorKinds.Person, Owner, Now)).Ok);
            // Bring back is offered only below the published version, and the store accepts every one offered. (The
            // store would also copy a waiting draft forward; the page offers Publish for that draft instead.)
            if (row.BringBack is not null)
                Assert.True(Press(store => store.BringBack(A, "warmforward", "scout", row.Version,
                    FactoryMemoryAuthorKinds.Person, Owner, Now, expectedVersion: row.BringBack.ExpectedVersion)).Ok);
        }
        // And the published version itself has nothing to bring back, which the store says too.
        Assert.False(Press(store => store.BringBack(A, "warmforward", "scout", 3, FactoryMemoryAuthorKinds.Person, Owner, Now)).Ok);
    }

    private FactoryBriefWrite Press(Func<FactoryBriefStore, FactoryBriefWrite> press)
    {
        using var h = new GatewayDbTestHarness();
        var db = h.Open();
        var registry = new FactoryRegistryStore(db);
        registry.Register(A, new RegisterFactoryRequest
        {
            Factory = "warmforward", Title = "WarmForward", Folder = "/home/soren/factories/warmforward", Computer = "devlinux",
            Seats = { new FactorySeatManifest { Id = "scout", Name = "Scout", Role = "Scout", BriefFile = "agents/scout.yaml" } },
        }, "the owner (test)", Now);
        var store = new FactoryBriefStore(db, registry);
        // Version 1 published, 2 a draft, 3 published, 4 and 5 drafts: the shape Mixed() describes.
        Assert.True(store.SaveDraft(A, "warmforward", "scout", "one", 0, FactoryMemoryAuthorKinds.Session, Session, Now).Ok);
        Assert.True(store.Publish(A, "warmforward", "scout", 1, FactoryMemoryAuthorKinds.Person, Owner, Now).Ok);
        Assert.True(store.SaveDraft(A, "warmforward", "scout", "two", 1, FactoryMemoryAuthorKinds.Session, Session, Now).Ok);
        Assert.True(store.SaveDraft(A, "warmforward", "scout", "three", 2, FactoryMemoryAuthorKinds.Session, Session, Now).Ok);
        Assert.True(store.Publish(A, "warmforward", "scout", 3, FactoryMemoryAuthorKinds.Person, Owner, Now).Ok);
        Assert.True(store.SaveDraft(A, "warmforward", "scout", "four", 3, FactoryMemoryAuthorKinds.Person, Owner, Now).Ok);
        Assert.True(store.SaveDraft(A, "warmforward", "scout", "five", 4, FactoryMemoryAuthorKinds.Session, Session, Now).Ok);
        return press(store);
    }

    [Fact]
    public void History_ThePublishConfirm_SaysWhatEveryLaterRunIsGiven_WhatItReplaces_AndWhatStaysADraft()
    {
        var publish = MixedHistory().Rows.Single(r => r.Version == 4).Publish!;

        Assert.Equal(4, publish.Version);
        Assert.Equal("Publish", publish.Label);
        Assert.Equal("Publish version 4 of the brief of Scout?", publish.ConfirmTitle);
        Assert.Equal(new[]
        {
            "Every later talk and every later run of Scout will be given the text of version 4.",
            "It replaces version 3, which is kept. History can bring it back.",
            "Version 5 stays a draft.",
        }, publish.ConfirmLines);
        Assert.Equal("Publish", publish.ConfirmLabel);
        Assert.Equal("Publishing...", publish.BusyLabel);
        Assert.Equal("Version 4 is published. Every later talk and every later run of Scout is given it.", publish.DoneText);

        // The newest draft leaves nothing behind it, so the confirm does not say a version stays a draft.
        Assert.Equal(2, MixedHistory().Rows.Single(r => r.Version == 5).Publish!.ConfirmLines.Count);
    }

    [Fact]
    public void History_ThePublishConfirmOfAFirstBrief_SaysTheSeatWasToldAFilePathUntilNow()
    {
        var draft = V(1, published: false);
        var view = FactoryBriefPanelFold.History(WarmForward, Scout, new FactoryBriefStanding(draft, null),
            new FactoryBriefHistoryPage(new[] { S(draft) }, 1, null), firstPage: true, TimeZoneInfo.Utc, Now);

        Assert.Equal("1 version. None is published.", view.SummaryText);
        Assert.Equal("Version 1 of 1.", view.PageText);
        Assert.Equal(new[]
        {
            "Every later talk and every later run of Scout will be given the text of version 1.",
            "It is the first published brief of this seat. Until now its talks and runs were told the path of its brief file.",
        }, view.Rows[0].Publish!.ConfirmLines);
    }

    [Fact]
    public void History_TheBringBackConfirm_SaysACopyIsPublishedAsTheNewestVersion_AndNothingIsRemoved()
    {
        var back = MixedHistory().Rows.Single(r => r.Version == 1).BringBack!;

        Assert.Equal(1, back.Version);
        // The newest version the person is looking at goes with the press, so a draft written since is not buried.
        Assert.Equal(5, back.ExpectedVersion);
        Assert.Equal("Bring back", back.Label);
        Assert.Equal("Bring version 1 of the brief of Scout back?", back.ConfirmTitle);
        Assert.Equal(new[]
        {
            "A copy of version 1 becomes version 6, and it is published. Every later talk and every later run of Scout will be given that text.",
            "Nothing is removed. Version 3, which is published now, and every other version are kept.",
            "The 2 drafts above version 3 are then older than the published version. They are kept and each can be brought back.",
        }, back.ConfirmLines);
        Assert.Equal("Bring it back", back.ConfirmLabel);
        Assert.Equal("Version 1 was brought back as the newest version, and it is published. Every later talk and every later run of Scout is given it.", back.DoneText);
    }

    [Fact]
    public void History_AnOlderPage_OffersTheWayBackToTheNewest_AndTheWayOnToOlderVersions()
    {
        var published = V(41, published: true);
        var newest = V(43, published: false);
        var page = Enumerable.Range(4, 20).Reverse().Select(n => S(V(n, published: false))).ToList();

        var view = FactoryBriefPanelFold.History(WarmForward, Scout, new FactoryBriefStanding(newest, published),
            new FactoryBriefHistoryPage(page, 43, 4), firstPage: false, TimeZoneInfo.Utc, Now);

        Assert.Equal("43 versions. Version 41 is published.", view.SummaryText);
        Assert.Equal("Versions 23 to 4 of 43.", view.PageText);
        Assert.Equal(4, view.NextBefore);
        Assert.Equal("Older versions", view.OlderLabel);
        Assert.Equal("Newest versions", view.NewestLabel);
        // Every version on this page is older than the published one: none can be published, each can be brought back.
        Assert.All(view.Rows, r => { Assert.Null(r.Publish); Assert.NotNull(r.BringBack); Assert.Equal(43, r.BringBack!.ExpectedVersion); });
    }

    [Fact]
    public void History_ASeatWithNoVersion_SaysItHasNoHistory()
    {
        var view = FactoryBriefPanelFold.History(WarmForward, Scout, FactoryBriefStanding.None, FactoryBriefHistoryPage.Empty,
            firstPage: true, TimeZoneInfo.Utc, Now);

        Assert.Empty(view.Rows);
        Assert.Equal("No version yet.", view.SummaryText);
        Assert.Null(view.PageText);
        Assert.Equal("This seat has no brief on the Gateway, so it has no history.", view.EmptyText);
    }

    // ---------- one version ----------

    [Fact]
    public void Version_SaysItsState_WhoWroteIt_WhoPublishedIt_AndItsSize_WithItsWholeText()
    {
        var all = Mixed();
        var standing = new FactoryBriefStanding(all[0], all[2]);

        var draft = FactoryBriefPanelFold.Version(all[0], standing, TimeZoneInfo.Utc, Now);
        Assert.Equal("Version 5", draft.Title);
        Assert.Equal($"Draft. Written by session {Session} today 13:00. 2.0 KB.", draft.LineText);
        Assert.Equal(new string('x', 2048), draft.Text);
        Assert.Equal("Close", draft.CloseLabel);

        var current = FactoryBriefPanelFold.Version(all[2], standing, TimeZoneInfo.Utc, Now);
        Assert.Equal($"Published. Written by session {Session} today 13:00. Published by a person ({Owner}) today 14:00. 4 bytes.", current.LineText);

        var earlier = FactoryBriefPanelFold.Version(all[4], standing, TimeZoneInfo.Utc, Now);
        Assert.StartsWith("Published earlier. ", earlier.LineText);
    }

    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(65536, "64.0 KB")]
    public void Size_IsInBytesBelowAKilobyte_AndInKilobytesFromThere(int bytes, string expected) =>
        Assert.Equal(expected, FactoryBriefPanelFold.Size(bytes));

    [Fact]
    public void Who_NeverCutsAnIdentifierShort_AndSaysWhenTheGatewayRecordedNone()
    {
        Assert.Equal($"session {Session}", FactoryBriefPanelFold.Who(FactoryMemoryAuthorKinds.Session, Session));
        Assert.Equal("a session", FactoryBriefPanelFold.Who(FactoryMemoryAuthorKinds.Session, null));
        Assert.Equal($"a person ({Owner})", FactoryBriefPanelFold.Who(FactoryMemoryAuthorKinds.Person, Owner));
        Assert.Equal("a person", FactoryBriefPanelFold.Who(FactoryMemoryAuthorKinds.Person, " "));
    }

    [Fact]
    public void Times_AreToldInTheAccountsZone()
    {
        var toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        var published = V(1, published: true, publishedAt: Now.AddHours(-1));

        var panel = FactoryBriefPanelFold.Panel(WarmForward, Scout, new FactoryBriefStanding(published, published), toronto, Now);

        // 14:00 UTC on 13 October is 10:00 in Toronto.
        Assert.EndsWith("today 10:00.", panel.VersionText);
    }
}
