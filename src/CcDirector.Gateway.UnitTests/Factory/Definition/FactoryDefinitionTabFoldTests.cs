using System.IO.Compression;
using System.Text;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// The Definition tab of a factory's page (Factory Definition mission, phase 6): every sentence and every button is the
/// fold's, so each state the owner can see is proven here, and the zip the tab's Export and Import move is proven to be
/// the folder, byte for byte.
/// </summary>
public sealed class FactoryDefinitionTabFoldTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private const string F = DefinitionFixture.FactoryId;
    private const string Title = "DevThrottle";

    private static FactoryDefinitionVersionSummary Summary(int version, string state, string? note = null, int? restoredFrom = null,
        string authorKind = "person", string? authorId = "device browser abc") =>
        new(F, version, state, authorKind, authorId, note, restoredFrom, Now.AddHours(-version), state == FactoryDefinitionStates.Published ? Now.AddHours(-version).AddMinutes(5) : null,
            state == FactoryDefinitionStates.Published ? "device browser abc" : null, 1234, 6);

    private static FactoryDefinitionVersion Version(int version, string state, IReadOnlyDictionary<string, string>? files = null) =>
        new(Summary(version, state), files ?? DefinitionFixture.Valid());

    private static FactoryDefinitionPlanDto NoPlan(FactoryDefinitionVersion v) =>
        new() { Version = v.Summary.Version, Heading = $"What publishing version {v.Summary.Version} would do to the live factory now:", Steps = new() { "Registry: seat boss kept." } };

    private static FactoryDefinitionTabViewDto Tab(FactoryDefinitionVersion? published, FactoryDefinitionVersion? newest,
        IReadOnlyList<(string, string)>? leavesOut = null) =>
        FactoryDefinitionTabFold.Tab(F, Title, new FactoryDefinitionStanding(newest?.Summary, published?.Summary), published, newest,
            NoPlan, leavesOut ?? Array.Empty<(string, string)>(), Zone, Now);

    [Fact]
    public void Tab_NoDefinition_SaysSo_OffersExportFromTodayWithWhatItLeavesOut_AndImport_AndNoPublishOrHistory()
    {
        var view = Tab(null, null, new[] { ("factory.yaml", "The goal is held in a file.") });

        Assert.Equal("DevThrottle has no definition on the Gateway.", view.StateText);
        Assert.Contains("keeps working exactly that way", view.EmptyText);
        Assert.Null(view.Published);
        Assert.Null(view.Draft);
        Assert.Null(view.Publish);
        Assert.Null(view.HistoryLabel);
        Assert.Equal("/gateway/factories/devthrottle/definition/export", view.Export.Href);
        Assert.Contains("written from what DevThrottle has today", view.Export.NoteText);
        Assert.Contains("1 thing it has cannot go in the folder", view.Export.NoteText);
        Assert.Equal(new[] { "factory.yaml: The goal is held in a file." }, view.Export.LeavesOut);
        Assert.Equal("/gateway/factories/devthrottle/definition/import", view.Import.Href);
        Assert.Contains("never published", view.Import.NoteText);
    }

    [Fact]
    public void Tab_FirstDraft_IsADraftWithWhatChangesAsAFirstPublish_AndAPublishThatSaysItIsTheFirst()
    {
        var draft = Version(1, FactoryDefinitionStates.Draft);
        var view = Tab(null, draft);

        Assert.Equal("Nothing is published yet. Version 1, a draft, is the only version.", view.StateText);
        Assert.Null(view.Published);
        Assert.Equal("Draft: version 1", view.Draft!.Title);
        Assert.Equal(FactoryTone.Amber, view.Draft.StateTone);
        Assert.True(view.Changes!.Changes!.FirstPublish);
        Assert.Equal("What changes when version 1 is published", view.Changes.Heading);
        Assert.Equal(new[] { "Registry: seat boss kept." }, view.Changes.Plan!.Steps);
        Assert.Equal("Publish version 1", view.Publish!.Label);
        Assert.Null(view.Publish.Replaces);
        Assert.Contains(view.Publish.ConfirmLines, l => l.StartsWith("It is the first published version."));
        Assert.Contains(view.Publish.ConfirmLines, l => l.StartsWith("What changes: First publish:"));
        Assert.Equal("/gateway/factories/devthrottle/definition/export?version=1", view.Export.Href);
        Assert.Contains("the newest draft, as nothing is published", view.Export.NoteText);
    }

    [Fact]
    public void Tab_PublishedAndNewest_ShowsThePublishedCardOnly_AndNoPublish()
    {
        var v3 = Version(3, FactoryDefinitionStates.Published);
        var view = Tab(v3, v3);

        Assert.Equal("Version 3 is published, and it is the newest version.", view.StateText);
        Assert.Equal("Published: version 3", view.Published!.Title);
        Assert.StartsWith("Published by a person (device browser abc)", view.Published.LineText);
        Assert.EndsWith("6 files, 1.2 KB.", view.Published.LineText);
        Assert.Null(view.Draft);
        Assert.Null(view.Changes);
        Assert.Null(view.Publish);
        Assert.Equal("History", view.HistoryLabel);
        Assert.Equal("/gateway/factories/devthrottle/definition/export?version=3", view.Export.Href);
    }

    [Fact]
    public void PublishAction_SeveralDraftsBetween_NamesThemAsPassedOver_AndAHistoryRowSaysNothingOfADifferenceItDoesNotShow()
    {
        var row = FactoryDefinitionTabFold.PublishAction("QA Outreach", 7, published: 3, newest: 7, changesSummary: null);
        var first = FactoryDefinitionTabFold.PublishAction("QA Outreach", 3, published: null, newest: 3, changesSummary: "3 seats added.");
        var next = FactoryDefinitionTabFold.PublishAction("QA Outreach", 4, published: 3, newest: 4, changesSummary: "1 seat changed.");

        Assert.Contains("Version 7 goes live exactly as it is. Versions 4 to 6 are passed over: they stay in the history, and History " +
                        "can restore one.", row.ConfirmLines);
        Assert.Contains("Version 3 goes live exactly as it is. Versions 1 to 2 are passed over: they stay in the history, and History " +
                        "can restore one.", first.ConfirmLines);
        Assert.DoesNotContain(next.ConfirmLines, l => l.Contains("passed over", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_TwoDraftsAboveThePublished_PublishConfirmSaysTheNewestGoesLiveAsItIs_AndTheOneBetweenIsPassedOver()
    {
        var v3 = Version(3, FactoryDefinitionStates.Published);
        var v5 = Version(5, FactoryDefinitionStates.Draft,
            DefinitionFixture.Edit("seats/boss.yaml", "Run the factory.", "Run the factory well."));
        var view = Tab(v3, v5);

        Assert.Equal("Version 3 is published. 2 drafts saved after it; the newest is version 5.", view.StateText);
        Assert.Equal(5, view.Publish!.Version);
        Assert.Equal(3, view.Publish.Replaces);
        Assert.Equal(5, view.Publish.ExpectedVersion);
        // Never that version 5 holds version 4's change: an import replaces the whole definition (review of #4028).
        Assert.Contains("Version 5 goes live exactly as it is. Version 4 is passed over: it stays in the history, and History can " +
                        "restore it. What changes, above, is the whole difference between version 3 and version 5.", view.Publish.ConfirmLines);
        Assert.DoesNotContain(view.Publish.ConfirmLines, l => l.Contains("holds every change", StringComparison.Ordinal));
        Assert.Contains("Version 3, published now, is kept. History can restore it.", view.Publish.ConfirmLines);
        Assert.Contains(view.Changes!.Changes!.Seats, s => s.Seat == "boss" && s.BriefDiff is not null);
    }

    [Fact]
    public void Tab_ADraftThatNoLongerParses_SaysWhereAndComparesNothing()
    {
        var v1 = Version(1, FactoryDefinitionStates.Published);
        var v2 = Version(2, FactoryDefinitionStates.Draft, DefinitionFixture.With("factory.yaml", "factory: [not, text"));
        var view = Tab(v1, v2);

        Assert.Null(view.Changes!.Changes);
        Assert.Null(view.Changes.Plan);
        Assert.StartsWith("Version 2 no longer passes the format check at factory.yaml", view.Changes.CannotCompareText);
    }

    [Fact]
    public void History_RowsCarryTheStoresRules_PublishAboveRestoreBelow_NoteOrRestoredFrom()
    {
        var page = new FactoryDefinitionHistoryPage(new[]
        {
            Summary(4, FactoryDefinitionStates.Draft, note: "the second definition"),
            Summary(3, FactoryDefinitionStates.Published, restoredFrom: 1),
            Summary(2, FactoryDefinitionStates.Published),
            Summary(1, FactoryDefinitionStates.Draft, authorKind: "session", authorId: "1f3a"),
        }, 4, null);
        var standing = new FactoryDefinitionStanding(page.Versions[0], page.Versions[1]);

        var view = FactoryDefinitionTabFold.History(F, Title, standing, page, firstPage: true, Zone, Now);

        Assert.Equal("4 versions. Version 3 is published.", view.SummaryText);
        Assert.Equal("Versions 4 to 1 of 4.", view.PageText);
        Assert.Null(view.NewestLabel);
        Assert.Null(view.OlderLabel);
        var (v4, v3, v2, v1) = (view.Rows[0], view.Rows[1], view.Rows[2], view.Rows[3]);
        Assert.Equal(("Draft", "the second definition"), (v4.StateWord, v4.NoteText));
        Assert.NotNull(v4.Publish);
        Assert.Null(v4.Restore);
        Assert.Equal(("Published", "Restored from version 1."), (v3.StateWord, v3.NoteText));
        Assert.Null(v3.Publish);
        Assert.Null(v3.Restore);
        Assert.Equal("Published earlier", v2.StateWord);
        Assert.Equal(2, v2.Restore!.Version);
        Assert.Equal((4, (int?)3), (v2.Restore.ExpectedVersion, v2.Restore.Replaces));
        Assert.Contains("A copy of version 2 becomes version 5, and it is published.", v2.Restore.ConfirmLines[0]);
        Assert.Equal("Version 4, a draft, is then older than the published version. It is kept and can be restored.", v2.Restore.ConfirmLines[2]);
        Assert.Equal(("Draft, passed over", "Written by session 1f3a"), (v1.StateWord, v1.WhoText));
        Assert.NotNull(v1.Restore);
        Assert.Equal("/gateway/factories/devthrottle/definition/export?version=1", v1.ExportHref);
    }

    [Fact]
    public void Version_ListsEveryFileInPathOrder_WithItsText()
    {
        var v2 = Version(2, FactoryDefinitionStates.Published);
        var view = FactoryDefinitionTabFold.Version(F, v2, new FactoryDefinitionStanding(v2.Summary, v2.Summary), Zone, Now);

        Assert.Equal("Version 2", view.Title);
        Assert.StartsWith("Published. Published by", view.LineText);
        Assert.Equal(v2.Files.Keys.OrderBy(k => k, StringComparer.Ordinal), view.Files.Select(f => f.Path));
        Assert.Equal(v2.Files["factory.yaml"], view.Files.Single(f => f.Path == "factory.yaml").Text);
    }

    [Fact]
    public void ImportedText_SaysItIsADraftAndNothingIsPublished()
    {
        Assert.Equal("Imported 6 files as version 1, a draft. It is the first version of the definition of devthrottle. Nothing is published: read what changes, then publish it.",
            FactoryDefinitionTabFold.ImportedText(F, 1, 6, created: true));
        Assert.Equal("Imported 1 file as version 7, a draft. Nothing is published: read what changes, then publish it.",
            FactoryDefinitionTabFold.ImportedText(F, 7, 1, created: false));
    }

    // ---------- the zip ----------

    [Fact]
    public void Zip_TwoExportsOfTheSameFiles_AreTheSameBytes_AndUnzipGivesTheFilesBack()
    {
        var files = DefinitionFixture.Valid();
        var first = FactoryDefinitionTabEndpoints.Zip(files);
        var second = FactoryDefinitionTabEndpoints.Zip(new Dictionary<string, string>(files.Reverse()));

        Assert.Equal(first, second);
        Assert.True(FactoryDefinitionTabEndpoints.TryUnzip(first, out var back, out var refusal), refusal);
        Assert.Equal(files.OrderBy(f => f.Key, StringComparer.Ordinal), back.OrderBy(f => f.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Unzip_AFolderZippedWithItsOwnFolder_TakesThatFolderOffEveryPath()
    {
        var zip = ZipOf(("qa-outreach/factory.yaml", "factory: x"), ("qa-outreach/seats/boss.yaml", "seat: boss"), ("qa-outreach/", null));

        Assert.True(FactoryDefinitionTabEndpoints.TryUnzip(zip, out var content, out _));
        Assert.Equal(new[] { "factory.yaml", "seats/boss.yaml" }, content.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Unzip_RefusesAFileThatIsNotUtf8_ByName()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = archive.CreateEntry("files/logo.png").Open())
            s.Write(new byte[] { 0x89, 0x50, 0xFF, 0xFE, 0x00 });

        Assert.False(FactoryDefinitionTabEndpoints.TryUnzip(buffer.ToArray(), out _, out var refusal));
        Assert.Equal("The file files/logo.png in the zip is not UTF-8 text. Every file in a definition is text. Nothing was imported.", refusal);
    }

    [Fact]
    public void Unzip_RefusesWhatIsNotAZip_AndAnEmptyZip()
    {
        Assert.False(FactoryDefinitionTabEndpoints.TryUnzip(Encoding.UTF8.GetBytes("factory: devthrottle"), out _, out var notZip));
        Assert.StartsWith("The file is not a zip that can be read", notZip);
        Assert.False(FactoryDefinitionTabEndpoints.TryUnzip(ZipOf(("folder/", null)), out _, out var empty));
        Assert.Equal("The zip holds no files, so there is no definition to import. Nothing was imported.", empty);
    }

    private static byte[] ZipOf(params (string Path, string? Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in entries)
            {
                var entry = archive.CreateEntry(path);
                if (text is null) continue;
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        return buffer.ToArray();
    }
}
