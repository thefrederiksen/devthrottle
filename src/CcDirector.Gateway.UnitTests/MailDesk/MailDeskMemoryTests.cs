using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.MailDesk;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskHandOffRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE MEMORY THE GATEWAY HANDS THE CHECK (Mail Desk phase 3 contract, section 4; task F), and the end-to-end run
/// through the real report route. The memory is MADE UP for the machine being handed the start, from the active channel
/// rows that name it; what comes back is merged by channel id; a channel not handed keeps its kept fields untouched; a
/// removed channel's fields stay 30 days. Channels are added and removed through the real routes
/// (<c>POST /gateway/mail-desk/channels</c> and <c>/channels/remove</c>, what <c>maildesk channel add|remove</c> call),
/// assignments are pulled and reports posted through the Director's routes, all on an in-memory test server.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskMemoryTests : IAsyncLifetime
{
    private const string BoxA = "inbox@example.com";
    private const string BoxB = "support@example.org";
    private const string BoxC = "billing@example.net";
    private MailDeskHandOffRig _rig = null!;

    public Task InitializeAsync()
    {
        _rig = new MailDeskHandOffRig();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private static DateTime At(int minutes) => new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    private static StandInMail Mail(string key, int minute) => new(key, "someone@example.net", "A question", At(minute));

    private static JsonArray HandedChannels(string? memory) => JsonNode.Parse(memory!)!["channels"]!.AsArray();

    private async Task<TriggerRunEntity?> PostRun(StandInCheck.Plan plan, string director = DirectorA)
    {
        var handed = await _rig.PullAsync(director);
        var (status, body, run) = await _rig.PostReportAsync(StandInCheck.Run(handed, plan, _rig.Now), director);
        Assert.True(status == 202, $"expected 202, got {status}: {body}");
        _rig.Now = _rig.Now.AddMinutes(5);
        return run;
    }

    private async Task AddThroughTheRoute(string address, params string[] machines)
    {
        var (status, body) = await _rig.PersonPostAsync(P + "/channels",
            new { address, provider = "outlook", machines = machines.Length == 0 ? new[] { MachineA } : machines, why = "a mailbox for the test" });
        Assert.True(status == 201, $"expected 201, got {status}: {body}");
    }

    // ---------- what is handed ----------

    [Fact]
    public async Task Assignment_AFirstChannel_IsHandedWithTheGatewaysFieldsOnlySoItIsAFirstRun()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        await AddThroughTheRoute(BoxA);
        var channel = _rig.Channels.List(MailDeskHandOffRig.Account).Single();

        // Act
        var handed = await _rig.PullAsync();

        // Assert
        var entry = Assert.Single(HandedChannels(handed))!.AsObject();
        Assert.Equal(new[] { "address", "id", "mode", "provider" }, entry.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(channel.Id.ToString("D"), entry["id"]!.GetValue<string>());
        Assert.Equal(BoxA, entry["address"]!.GetValue<string>());
        Assert.Equal("outlook", entry["provider"]!.GetValue<string>());
        Assert.Equal(MailDeskWords.Modes.Observe, entry["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Assignment_AChannelAddedThroughTheRoute_IsInTheVeryNextAssignmentAsAFirstRunWhileTheFirstKeepsItsCursor()
    {
        // Arrange: one mailbox has been read and has a cursor.
        _rig.AddMailDeskTrigger();
        await AddThroughTheRoute(BoxA);
        await PostRun(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1) })));
        var a = _rig.Channels.List(MailDeskHandOffRig.Account).Single();
        var cursorBefore = Entry(_rig.KeptMemory(), a.Id)!["cursor"]!.ToJsonString();

        // Act: the owner adds a second mailbox through POST /gateway/mail-desk/channels.
        await AddThroughTheRoute(BoxB);
        var handed = await _rig.PullAsync();

        // Assert
        var b = _rig.Channels.List(MailDeskHandOffRig.Account).Single(c => c.Address == BoxB);
        Assert.Equal(2, HandedChannels(handed).Count);
        Assert.Equal(cursorBefore, Entry(handed, a.Id)!["cursor"]!.ToJsonString());
        var added = Entry(handed, b.Id)!;
        Assert.Null(added["cursor"]);                                // read as a first run
        Assert.Null(added["keys"]);

        // And the run that follows reads it and keeps both.
        await PostRun(StandInCheck.Reads((BoxB, new[] { Mail("b-1", 2) })));
        Assert.NotNull(Entry(_rig.KeptMemory(), b.Id)!["cursor"]);
        Assert.Equal(cursorBefore, Entry(_rig.KeptMemory(), a.Id)!["cursor"]!.ToJsonString());
    }

    [Fact]
    public async Task Assignment_AChannelRemovedThroughTheRoute_IsMissingFromTheNextAssignmentAndItsDecisionsAreKept()
    {
        // Arrange: two mailboxes read once each.
        _rig.AddMailDeskTrigger();
        await AddThroughTheRoute(BoxA);
        await AddThroughTheRoute(BoxB);
        await PostRun(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1) }), (BoxB, new[] { Mail("b-1", 1) })));
        var b = _rig.Channels.List(MailDeskHandOffRig.Account).Single(c => c.Address == BoxB);

        // Act: the owner removes one through POST /gateway/mail-desk/channels/remove.
        var (status, body) = await _rig.PersonPostAsync(P + "/channels/remove", new { address = BoxB, why = "no longer used" });
        var handed = await _rig.PullAsync();

        // Assert: not handed, so it stops being read; its decision is kept; its kept fields stay in the stored memory.
        Assert.True(status == 200, $"expected 200, got {status}: {body}");
        Assert.Null(Entry(handed, b.Id));
        Assert.Single(HandedChannels(handed));
        Assert.Contains(_rig.DecisionRows(), d => d.ChannelId == b.Id && d.MessageKey == "b-1");
        await PostRun(StandInCheck.Reads());
        Assert.NotNull(Entry(_rig.KeptMemory(), b.Id));              // kept for 30 days
    }

    [Fact]
    public async Task Merge_ARemovedChannelsFields_AreDroppedThirtyDaysAfterItWasRemoved()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        await AddThroughTheRoute(BoxA);
        await AddThroughTheRoute(BoxB);
        await PostRun(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1) }), (BoxB, new[] { Mail("b-1", 1) })));
        var b = _rig.Channels.List(MailDeskHandOffRig.Account).Single(c => c.Address == BoxB);
        await _rig.PersonPostAsync(P + "/channels/remove", new { address = BoxB, why = "no longer used" });

        // Act: a run 29 days later keeps it; one 30 days later drops it.
        _rig.Now = _rig.Now.AddDays(29);
        await PostRun(StandInCheck.Reads());
        var at29 = Entry(_rig.KeptMemory(), b.Id);
        _rig.Now = _rig.Now.AddDays(1);
        await PostRun(StandInCheck.Reads());

        // Assert
        Assert.NotNull(at29);
        Assert.Null(Entry(_rig.KeptMemory(), b.Id));
    }

    [Fact]
    public async Task Assignment_TwoMachinesEachHandedTheirOwnChannels_EachReportLeavesTheOthersCursorByteForByte()
    {
        // Arrange: machine-a reads A; machine-b (the backup) reads B.
        _rig.AddMailDeskTrigger(backup: true);
        await AddThroughTheRoute(BoxA, MachineA);
        await AddThroughTheRoute(BoxB, MachineB);
        var a = _rig.Channels.List(MailDeskHandOffRig.Account).Single(c => c.Address == BoxA);
        var b = _rig.Channels.List(MailDeskHandOffRig.Account).Single(c => c.Address == BoxB);
        await PostRun(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1) })));
        var aEntry = Entry(_rig.KeptMemory(), a.Id)!.ToJsonString();

        // Act: machine-a goes quiet; the backup takes the lapsed claim and is handed only B.
        _rig.Now = _rig.Now.Add(TriggerStore.ClaimLapse(300)).AddSeconds(1);
        var handedToB = await _rig.PullAsync(DirectorB);
        await PostRun(StandInCheck.Reads((BoxB, new[] { Mail("b-1", 2) })), DirectorB);

        // Assert: B was handed only B, and A's kept entry is byte for byte what machine-a left.
        Assert.Equal(new[] { b.Id.ToString("D") }, HandedChannels(handedToB).Select(e => e!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal(aEntry, Entry(_rig.KeptMemory(), a.Id)!.ToJsonString());
        var bEntry = Entry(_rig.KeptMemory(), b.Id)!.ToJsonString();

        // Act again: machine-a takes its check back, is handed only A with its cursor, and reports.
        var handedToA = await _rig.PullAsync(DirectorA);
        await PostRun(StandInCheck.Reads((BoxA, new[] { Mail("a-2", 3) })));

        // Assert: B's entry is byte for byte what the backup left.
        Assert.Equal(new[] { a.Id.ToString("D") }, HandedChannels(handedToA).Select(e => e!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal(Entry(handedToA, a.Id)!["cursor"]!.ToJsonString(), JsonNode.Parse(aEntry)!["cursor"]!.ToJsonString());
        Assert.Equal(bEntry, Entry(_rig.KeptMemory(), b.Id)!.ToJsonString());
    }

    [Fact]
    public async Task Assignment_NoChannelNamesThisMachine_HandsAnEmptyListAndTheFailedCheckSaysTheFix()
    {
        // Arrange: the only mailbox names another machine.
        _rig.AddMailDeskTrigger();
        await AddThroughTheRoute(BoxA, MachineB);

        // Act
        var handed = await _rig.PullAsync();
        var (_, _, _) = await _rig.PostReportAsync(StandInCheck.Failed(_rig.Now, handed!));

        // Assert
        Assert.Empty(HandedChannels(handed));
        var run = Assert.Single(_rig.Runs());
        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        var sentence = $"the Mail Desk start runs on {MachineA}, but no channel names it: add it with maildesk channel add <address> --provider outlook --machine {MachineA} --why \"<text>\"";
        Assert.Equal(sentence, run.Reason);
        var problem = Assert.Single(_rig.OpenProblems());
        Assert.Equal(CronRunProblems.CheckFailed, problem.Kind);
        Assert.Contains("no channel names it", problem.Sentence);
    }

    [Fact]
    public async Task Merge_AMemoryOverTheCap_IsNotWrittenAndTheRunFailsNamingTheSizes()
    {
        // Arrange: each machine's own memory is under the cap - a Director never sends more - but machine-b's kept
        // entry and machine-a's together are over 65,536 bytes.
        _rig.AddMailDeskTrigger(backup: true);
        await AddThroughTheRoute(BoxA, MachineA);
        await AddThroughTheRoute(BoxB, MachineB);
        await PostRun(StandInCheck.Reads());
        _rig.Now = _rig.Now.Add(TriggerStore.ClaimLapse(300)).AddSeconds(1);
        await _rig.PostReportAsync(Padded(StandInCheck.Run(await _rig.PullAsync(DirectorB), StandInCheck.Reads(), _rig.Now), 180), DirectorB);
        var keptBefore = _rig.KeptMemory();
        _rig.Now = _rig.Now.AddMinutes(5);
        var handedToA = await _rig.PullAsync(DirectorA);
        var report = Padded(StandInCheck.Run(handedToA, StandInCheck.Reads(), _rig.Now), 180);
        Assert.True(report.State!.Length < MailDeskMemory.MaxBytes);

        // Act
        var (_, _, run) = await _rig.PostReportAsync(report);

        // Assert
        Assert.Equal(TriggerRunOutcome.Failed, run!.Outcome);
        Assert.Matches(@"the Mail Desk memory would be \d+ bytes, over the 65536 the check can hold, so it was not written", run.Reason);
        Assert.Equal(keptBefore, _rig.KeptMemory());
        var problem = Assert.Single(_rig.OpenProblems());
        Assert.Contains("65536", problem.Sentence);
    }

    /// <summary>The report with <paramref name="keys"/> made-up keys of 152-character ids added to its one channel.</summary>
    private static TriggerCheckReport Padded(TriggerCheckReport report, int keys)
    {
        var memory = JsonNode.Parse(report.State!)!.AsObject();
        var entry = memory["channels"]![0]!.AsObject();
        var table = new JsonObject();
        for (var i = 0; i < keys; i++)
            table[$"{i:x16}"] = new JsonObject { ["id"] = new string('x', 152), ["reportedUtc"] = "2026-10-18T09:00:00Z" };
        entry["keys"] = table;
        report.State = memory.ToJsonString();
        return report;
    }

    [Fact]
    public async Task Merge_ACheckThatDropsAHandedChannel_IsRefusedAndNothingIsScreenedOrKept()
    {
        // Arrange: the check returns a memory that left mailbox B out.
        _rig.AddMailDeskTrigger();
        await AddThroughTheRoute(BoxA);
        await AddThroughTheRoute(BoxB);
        var report = StandInCheck.Run(await _rig.PullAsync(), StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1) })), _rig.Now);
        var memory = JsonNode.Parse(report.State!)!.AsObject();
        memory["channels"]!.AsArray().RemoveAt(1);
        report.State = memory.ToJsonString();

        // Act
        var (_, _, run) = await _rig.PostReportAsync(report);

        // Assert
        Assert.Equal(TriggerRunOutcome.Failed, run!.Outcome);
        Assert.Contains("left channel", run.Reason);
        Assert.Empty(_rig.DecisionRows());
        Assert.Equal(0, _rig.KeptMemoryRows());
    }

    // ---------- end to end, through the real report route ----------

    [Fact]
    public async Task ReportRoute_TwoMailboxesOneFailing_DecidesTheOtherRaisesTheProblemFoldsSignedInAndStartsNothing()
    {
        // Arrange: a stand-in Director on machine-a; two mailboxes, the second not signed in.
        _rig.AddMailDeskTrigger();
        _rig.SaveRules(MailDeskHandOffRig.Document());
        await AddThroughTheRoute(BoxA);
        await AddThroughTheRoute(BoxC);
        var a = _rig.Channels.List(MailDeskHandOffRig.Account).Single(c => c.Address == BoxA);
        var c = _rig.Channels.List(MailDeskHandOffRig.Account).Single(x => x.Address == BoxC);
        var handed = await _rig.PullAsync();
        var failing = $"{BoxC} is not signed in on {MachineA}: run cc-outlook -a billing auth";
        var report = StandInCheck.Run(handed,
            StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1), Mail("a-2", 2) })).With(BoxC, failing), _rig.Now);
        Assert.Equal(2, JsonDocument.Parse(JsonDocument.Parse(report.Output!).RootElement.GetProperty("context").GetString()!).RootElement.GetProperty("version").GetInt32());

        // Act
        var (status, _, run) = await _rig.PostReportAsync(report);

        // Assert: answered at once, screened off the request.
        Assert.Equal(202, status);
        Assert.Equal(TriggerRunOutcome.Screened, run!.Outcome);
        // Decisions for the mailbox that was read, none for the failing one.
        Assert.Equal(new[] { "a-1", "a-2" }, _rig.DecisionRows().Where(d => d.ChannelId == a.Id).Select(d => d.MessageKey).ToArray());
        Assert.DoesNotContain(_rig.DecisionRows(), d => d.ChannelId == c.Id);
        // The failing mailbox's problem on the mail-desk factory's Open problems card.
        var problem = Assert.Single(_rig.OpenProblems(MailDeskWords.DefaultFactory));
        Assert.Equal(CronRunProblems.MailboxFailed, problem.Kind);
        Assert.Equal($"{BoxC} could not be checked on {MachineA}: {failing}", problem.Sentence);
        // signedIn: failing for it, yes for the other - as the channel list route answers.
        var channels = (await _rig.PersonGetAsync(P + "/channels")).GetProperty("channels").EnumerateArray()
            .ToDictionary(x => x.GetProperty("address").GetString()!, x => x.GetProperty("signedIn"));
        Assert.Equal("yes", channels[BoxA].GetProperty("state").GetString());
        Assert.Equal("failing", channels[BoxC].GetProperty("state").GetString());
        Assert.Contains(failing, channels[BoxC].GetProperty("label").GetString());
        // No session started.
        Assert.Empty(_rig.Starts);
        // The memory: the failing mailbox's entry exactly as handed, the other's cursor moved.
        var kept = _rig.KeptMemory();
        Assert.Equal(Entry(handed, c.Id)!.ToJsonString(), Entry(kept, c.Id)!.ToJsonString());
        Assert.Equal("2026-10-18T08:02:00Z", Entry(kept, a.Id)!["cursor"]!["newest"]!.GetValue<string>());
    }
}
