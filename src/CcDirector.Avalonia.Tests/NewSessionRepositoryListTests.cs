using CcDirector.Avalonia;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The rules behind the desktop New Session dialog's repository list (the one-repository-list mission,
/// phase 6), as pure functions with no window in the room.
///
/// WHAT THESE EXIST TO CATCH is a second opinion about recency being computed on the desktop. The
/// Gateway serves the one list already ordered and the screen renders it; the moment anything here
/// compares two last-used dates for a served list, the three screens can disagree again, which is the
/// complaint the whole mission started from.
///
/// The fixtures are deliberately orders NO client sort would produce - never-opened rows interleaved
/// where a sort would never leave them, names out of alphabetical order. A fixture that happens to be
/// sorted lets a client that sorts pass by agreeing with the Gateway by luck.
/// </summary>
public class NewSessionRepositoryListTests
{
    private static KnownRepositoryDto Row(string name, string path, DateTime? lastUsed) => new()
    {
        Name = name,
        Path = path,
        LastUsed = lastUsed,
        NeverOpened = lastUsed is null,
    };

    /// <summary>
    /// An order no rule over the last-used time produces: a never-opened row sits BETWEEN two used ones,
    /// and the names run backwards. Any client that sorted - ascending, descending, never-opened-to-the-
    /// bottom, by name - would move at least one of these.
    /// </summary>
    private static List<KnownRepositoryDto> AnOrderNoClientSortWouldProduce() => new()
    {
        Row("zephyr", "/code/zephyr", new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc)),
        Row("beacon", "/code/beacon", null),
        Row("atlas", "/code/atlas", new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc)),
        Row("cinder", "/code/cinder", null),
    };

    [Fact]
    public void FromGateway_RendersTheServedOrderVerbatim_EvenAnOrderNoClientSortWouldProduce()
    {
        var rows = NewSessionRepositoryList.FromGateway(AnOrderNoClientSortWouldProduce());

        Assert.Equal(
            new[] { "zephyr", "beacon", "atlas", "cinder" },
            rows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void FromGateway_CarriesTheGatewaysNeverOpenedVerdict_RatherThanReReadingTheDate()
    {
        var rows = NewSessionRepositoryList.FromGateway(new List<KnownRepositoryDto>
        {
            Row("atlas", "/code/atlas", new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc)),
            Row("beacon", "/code/beacon", null),
        });

        Assert.False(rows[0].IsDiscovered);
        Assert.True(rows[1].IsDiscovered);
        Assert.Equal(new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc), rows[0].LastUsed);
        Assert.Null(rows[1].LastUsed);
    }

    /// <summary>
    /// Remove is off for every row on the Gateway's list, used or not. It only ever removed from this
    /// Director's own registry, and the catalogue is not that registry - the row would be back on the
    /// next read, which is a button that undoes itself.
    /// </summary>
    [Fact]
    public void FromGateway_OffersRemoveOnNothing()
    {
        var rows = NewSessionRepositoryList.FromGateway(AnOrderNoClientSortWouldProduce());

        Assert.All(rows, r => Assert.True(r.IsFromTheGatewayList));
        Assert.All(rows, r => Assert.False(r.CanRemove));
    }

    /// <summary>
    /// A blank name stays blank. The name is a fact about the repository and the Gateway owns it; a
    /// folder name invented here would be one screen disagreeing with the other two about what a
    /// repository is called, which is the shape of defect this mission exists to end.
    /// </summary>
    [Fact]
    public void FromGateway_DoesNotInventANameTheGatewayDidNotSend()
    {
        var rows = NewSessionRepositoryList.FromGateway(new List<KnownRepositoryDto>
        {
            Row("", "/code/atlas", new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc)),
        });

        Assert.Equal("", rows[0].Name);
    }

    /// <summary>
    /// THE ONE THAT MATTERS. The default heading is Last Used descending, and over the Gateway's list
    /// that renders the served order untouched. If this ever starts sorting, the desktop has a second
    /// opinion about recency and the mission is undone.
    /// </summary>
    [Fact]
    public void Order_OnTheGatewaysList_LastUsedDescending_IsTheServedOrderUntouched()
    {
        var served = NewSessionRepositoryList.FromGateway(AnOrderNoClientSortWouldProduce());

        var shown = NewSessionRepositoryList.Order(
            served, sourceIsTheGatewaysOrder: true, NewSessionRepositoryList.LastUsedColumn, ascending: false);

        Assert.Equal(
            new[] { "zephyr", "beacon", "atlas", "cinder" },
            shown.Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// Ascending is the same ruling read bottom-up - the exact inverse of the served order, not a second
    /// sort with its own idea about where a never-opened row belongs.
    /// </summary>
    [Fact]
    public void Order_OnTheGatewaysList_LastUsedAscending_IsTheServedOrderReversed()
    {
        var served = NewSessionRepositoryList.FromGateway(AnOrderNoClientSortWouldProduce());

        var shown = NewSessionRepositoryList.Order(
            served, sourceIsTheGatewaysOrder: true, NewSessionRepositoryList.LastUsedColumn, ascending: true);

        Assert.Equal(
            new[] { "cinder", "atlas", "beacon", "zephyr" },
            shown.Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// Pressing Name is a person asking for a different view, not a client deciding what the data means,
    /// so it sorts - and pressing Last Used again comes back to the Gateway's order intact, because the
    /// served list was never re-ordered in place.
    /// </summary>
    [Fact]
    public void Order_ThroughTheNameHeadingAndBack_ReturnsTheGatewaysOrderIntact()
    {
        var served = NewSessionRepositoryList.FromGateway(AnOrderNoClientSortWouldProduce());

        var byName = NewSessionRepositoryList.Order(
            served, sourceIsTheGatewaysOrder: true, NewSessionRepositoryList.NameColumn, ascending: true);
        Assert.Equal(new[] { "atlas", "beacon", "cinder", "zephyr" }, byName.Select(r => r.Name).ToArray());

        var back = NewSessionRepositoryList.Order(
            served, sourceIsTheGatewaysOrder: true, NewSessionRepositoryList.LastUsedColumn, ascending: false);
        Assert.Equal(new[] { "zephyr", "beacon", "atlas", "cinder" }, back.Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// The machine's own list has no served order to read, so the local last-used order is computed -
    /// most recently used first, never-opened beneath, which is the order this dialog has always shown
    /// and the order the Gateway serves. The fallback keeps working exactly as it did.
    /// </summary>
    [Fact]
    public void Order_OnTheMachinesOwnList_ComputesTheLocalLastUsedOrder()
    {
        var local = new List<RepositoryConfig>
        {
            new() { Name = "beacon", Path = "/code/beacon", LastUsed = null, IsDiscovered = true },
            new() { Name = "atlas", Path = "/code/atlas", LastUsed = new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc) },
            new() { Name = "zephyr", Path = "/code/zephyr", LastUsed = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc) },
        };

        var shown = NewSessionRepositoryList.Order(
            local, sourceIsTheGatewaysOrder: false, NewSessionRepositoryList.LastUsedColumn, ascending: false);

        Assert.Equal(new[] { "zephyr", "atlas", "beacon" }, shown.Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// The machine's own fallback list names a scanned repository after its folder when the scan named
    /// none - and it reads the folder out of the PATH, not out of the host. A Windows path read on this
    /// macOS machine has no separator that <c>Path.GetFileName</c> recognises, so that call would put the
    /// whole path in the Name column. This is the path-comparison defect this mission has now met six
    /// times, and the line it guards is inside the function phase 6 rewrote.
    /// </summary>
    [Theory]
    [InlineData(@"D:\ReposFred\devthrottle", "devthrottle")]
    [InlineData(@"D:\ReposFred\devthrottle\", "devthrottle")]
    [InlineData("/home/soren/code/atlas", "atlas")]
    [InlineData("/home/soren/code/atlas/", "atlas")]
    public void NameForScannedRepository_WithNoScannedName_ReadsTheFolderOutOfThePath(string path, string expected)
    {
        Assert.Equal(expected, NewSessionRepositoryList.NameForScannedRepository(null, path));
        Assert.Equal(expected, NewSessionRepositoryList.NameForScannedRepository("   ", path));
    }

    /// <summary>A name the scan did compute is kept - the folder name is the answer to a missing one.</summary>
    [Fact]
    public void NameForScannedRepository_KeepsTheNameTheScanComputed()
    {
        Assert.Equal("devthrottle_internal",
            NewSessionRepositoryList.NameForScannedRepository("devthrottle_internal", @"D:\ReposFred\somewhere-else"));
    }

    /// <summary>Served means there is nothing to say - the ordinary state wears no banner.</summary>
    [Fact]
    public void FallbackNotice_WhenTheListWasServed_IsNothing()
    {
        Assert.Null(NewSessionRepositoryList.FallbackNotice(KnownRepositoryListOutcome.Served, null));
    }

    /// <summary>
    /// Every sentence ends, including one built around words the Gateway supplied. The Cockpit shipped
    /// "Director not connected Try again." - a phrase run into the advice after it - and this is the
    /// guard against the same wording arriving here.
    /// </summary>
    [Theory]
    [InlineData(KnownRepositoryListOutcome.NotConfigured, null)]
    [InlineData(KnownRepositoryListOutcome.Unreachable, "No such host is known")]
    [InlineData(KnownRepositoryListOutcome.Refused, "director not found")]
    [InlineData(KnownRepositoryListOutcome.Refused, "The Director has not reported a machine name.")]
    [InlineData(KnownRepositoryListOutcome.Refused, null)]
    public void FallbackNotice_IsAlwaysTerminatedSentences(KnownRepositoryListOutcome outcome, string? reason)
    {
        var notice = NewSessionRepositoryList.FallbackNotice(outcome, reason);

        Assert.NotNull(notice);
        Assert.EndsWith(".", notice);
        Assert.DoesNotContain("..", notice);
        // No sentence starts immediately after a lower-case word with no stop between them.
        foreach (var fragment in notice!.Split(". ", StringSplitOptions.RemoveEmptyEntries))
            Assert.True(char.IsUpper(fragment.TrimStart()[0]) || fragment.TrimStart()[0] == '"',
                $"a sentence starts mid-phrase in: {notice}");
    }

    /// <summary>
    /// The three fallbacks are told apart on screen, because "nothing to ask" and "asked and could not
    /// be reached" and "asked and refused" are three different problems with three different fixes.
    /// </summary>
    [Fact]
    public void FallbackNotice_SaysWhichOfTheThreeHappened()
    {
        Assert.Contains("No Gateway is connected",
            NewSessionRepositoryList.FallbackNotice(KnownRepositoryListOutcome.NotConfigured, null));
        Assert.Contains("could not be reached",
            NewSessionRepositoryList.FallbackNotice(KnownRepositoryListOutcome.Unreachable, "no route"));
        Assert.Contains("director not found",
            NewSessionRepositoryList.FallbackNotice(KnownRepositoryListOutcome.Refused, "director not found"));
    }

    /// <summary>Every fallback says the list on screen is the local one, whichever fallback it is.</summary>
    [Theory]
    [InlineData(KnownRepositoryListOutcome.NotConfigured)]
    [InlineData(KnownRepositoryListOutcome.Unreachable)]
    [InlineData(KnownRepositoryListOutcome.Refused)]
    public void FallbackNotice_AlwaysSaysTheListIsThisMachinesOwn(KnownRepositoryListOutcome outcome)
    {
        Assert.Contains("this machine's own list",
            NewSessionRepositoryList.FallbackNotice(outcome, "something"));
    }
}
