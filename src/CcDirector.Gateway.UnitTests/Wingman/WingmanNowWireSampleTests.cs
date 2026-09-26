using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Drivers;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE ROUTE'S OWN ANSWER, WRITTEN DOWN WHERE THE CLIENT CAN READ IT - the guard that stops the Now screen and the
/// Gateway drifting apart again.
///
/// WHY THIS EXISTS. The Cockpit's TypeScript type for this answer was written from the settled design document, and
/// the route that shipped diverged from that document on three fields. Every client test answered in the client's own
/// invented shape, so all 1,317 of them stayed green while the screen could not answer a stop at all: the verdict
/// identifier was read from inside <c>needs</c> where the route puts it at the root, the agent's decisive sentence
/// was read as <c>sentence</c> where the route sends <c>text</c>, and the working clock ignored <c>elapsedOnly</c>
/// and rendered "Working for 7:14 a.m.". A test that hands a view an object the CLIENT invented proves nothing about
/// the wire.
///
/// SO THE SAMPLE IS PRODUCED HERE, BY THE FOLD, and only read there. This test folds six real states, serializes
/// them exactly as <c>GET /sessions/{sid}/wingman-now</c> serializes its body, and writes the result to a file inside
/// the client package. <c>wingmanNowWireSample.test.tsx</c> renders the Now view from that file. Rename a field on
/// either side and one of the two goes red.
///
/// WHAT IT DOES NOT PROVE. It is the FOLD's answer, not an answer observed coming out of a running Gateway over
/// HTTP - the handler that gathers the fold's inputs is proven separately in <see cref="WingmanNowRouteTests"/>.
/// And it covers the six states below; a field that appears only in a seventh is not on the wire here.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class WingmanNowWireSampleTests
{
    /// <summary>Where the sample lands, relative to the repository root. It lives beside the client type it guards,
    /// because the test that reads it lives there and a sample nobody reads guards nothing.</summary>
    private static readonly string[] SamplePath =
        ["packages", "client-core", "src", "sessions", "wingmanNow.gatewaySample.json"];

    /// <summary>
    /// EXACTLY WHAT THE ROUTE SERIALIZES WITH. The handler answers <c>Results.Json(answer)</c> with no options of
    /// its own, and this Gateway configures no JSON options for its HTTP pipeline, so the body is written with the
    /// framework's web defaults: camelCase names, and nothing else changed.
    /// </summary>
    private static readonly JsonSerializerOptions RouteOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The same options with line breaks added, which is how the file is checked in. Indenting changes no
    /// name and no value - proven below rather than asserted in a comment - and it makes the file reviewable in a
    /// diff instead of one four-thousand-character line.</summary>
    private static readonly JsonSerializerOptions FileOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string Sid = "11111111-1111-1111-1111-111111111111";
    private const string NextSid = "44444444-4444-4444-4444-444444444444";
    private static readonly DateTime Stopped = new(2026, 9, 17, 11, 12, 0, DateTimeKind.Utc);
    private static readonly DateTime AnsweredAt = Stopped.AddMinutes(2);

    [Fact]
    public void The_sample_the_client_renders_is_the_answer_this_fold_produces()
    {
        var samples = new Dictionary<string, WingmanNowResponse>
        {
            ["needsYou"] = NeedsYou(),
            ["working"] = Working(),
            ["justAnswered"] = JustAnswered(),
            ["carryingOn"] = CarryingOn(),
            ["done"] = Done(),
            ["snoozed"] = Snoozed(),
        };

        var written = JsonSerializer.Serialize(samples, FileOptions);

        // THE INDENTATION IS THE ONLY DIFFERENCE, and this is the proof of it: read the file's text back as JSON and
        // write it out with the ROUTE'S options, and it is the route's own body for every state.
        var reparsed = JsonNode.Parse(written)!.AsObject();
        foreach (var (name, answer) in samples)
        {
            Assert.Equal(
                JsonSerializer.Serialize(answer, RouteOptions),
                reparsed[name]!.ToJsonString(RouteOptions));
        }

        var path = Path.Combine(RepoRoot(), Path.Combine(SamplePath));
        var onDisk = File.Exists(path) ? File.ReadAllText(path) : null;
        if (string.Equals(onDisk, written, StringComparison.Ordinal)) return;

        // REFRESHED AND FAILED, never refreshed quietly. Writing it and passing would let a contract change slip
        // through with nobody reading it; failing here puts the new wire shape in the diff where a reviewer sees it,
        // and the next run is green once it has been looked at.
        File.WriteAllText(path, written);
        Assert.Fail(
            $"The Gateway's answer has changed, so {Path.Combine(SamplePath)} has been rewritten. Read the diff, " +
            "check packages/client-core/src/sessions/wingmanNowRead.ts still matches it field for field, and " +
            "commit the new sample.");
    }

    /// <summary>Every state in the sample carries the session it is about, so the client test can prove it is
    /// reading one answer rather than a composed fixture.</summary>
    [Fact]
    public void Every_sampled_state_is_about_the_same_session()
    {
        foreach (var answer in new[] { NeedsYou(), Working(), JustAnswered(), CarryingOn(), Done(), Snoozed() })
            Assert.Equal(Sid, answer.SessionId);
    }

    // ---------------------------------------------------------------- the six states

    /// <summary>A stop that needs him, with two options to tap - the state the whole screen exists for. It carries
    /// the verdict identifier the answer route takes and the agent's own decisive sentence.</summary>
    private static WingmanNowResponse NeedsYou()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou, options: 2);
        return WingmanNowFold.Fold(new WingmanNowInputs(
            Sid, Row(verdict), [new AnsweredTurnVerdict(verdict, null)], Asked("Is the release ready to tag?"),
            NowUtc: Stopped.AddMinutes(8)));
    }

    /// <summary>A working session: the state a session is in for most of its life, whose timed sentence is the
    /// elapsed time alone, and the one that says what it was last asked. Nobody is named as the asker here, so the
    /// lead before the time is the Gateway's bare "at" - the words the view used to write for itself, in a
    /// capital.</summary>
    private static WingmanNowResponse Working()
    {
        var superseded = Verdict(TurnVerdictVocabulary.NeededYou);
        superseded.SupersededAtUtc = Stopped.AddMinutes(1);
        return WingmanNowFold.Fold(new WingmanNowInputs(
            Sid, WorkingRow(), [new AnsweredTurnVerdict(superseded, null)],
            Asked("Carry on with the next slice."), NowUtc: Stopped.AddMinutes(7)));
    }

    /// <summary>A session that said it would carry on by itself: the calm card in purple, and the deadline sentence
    /// with the instant in the middle of it.</summary>
    private static WingmanNowResponse CarryingOn()
    {
        var verdict = Verdict(TurnVerdictVocabulary.ContinuesAlone,
            label: "Waiting for its Worker to finish the test run");
        return WingmanNowFold.Fold(new WingmanNowInputs(
            Sid, Row(verdict, colour: "purple", hex: "#a855f7", label: "Carrying on"),
            [new AnsweredTurnVerdict(verdict, null)], null, NowUtc: Stopped.AddMinutes(3)));
    }

    /// <summary>The moment after he answered: what he answered, that the session took it, and the next session
    /// waiting on him.</summary>
    private static WingmanNowResponse JustAnswered()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        verdict.SupersededAtUtc = AnsweredAt.AddSeconds(2);
        var answered = new AnsweredTurnVerdict(verdict, AnsweredAt,
            new TurnVerdictStoredAnswer(verdict.VerdictId, verdict.TurnEndObservedAtUtc, [0], "Allow the merge"));

        var next = Row(Verdict(TurnVerdictVocabulary.NeededYou));
        next.SessionId = NextSid;
        next.Name = "Dev Reports - Architect";
        next.NeedsYouSince = Stopped.AddMinutes(-40);
        next.VerdictLabel = "Should I open an issue for the dev Gateway?";

        return WingmanNowFold.Fold(new WingmanNowInputs(
            Sid, WorkingRow(), [answered], null, AccountRoster: [next], NowUtc: AnsweredAt.AddSeconds(20)));
    }

    /// <summary>The work is complete: the calm card, and the colour the Gateway now names for it.</summary>
    private static WingmanNowResponse Done()
    {
        var verdict = Verdict(TurnVerdictVocabulary.Finished, finishedKind: "done",
            label: "Release v2.5.0 is tagged and published");
        return WingmanNowFold.Fold(new WingmanNowInputs(
            Sid, Row(verdict, colour: "cyan", hex: "#06b6d4", label: "Done"),
            [new AnsweredTurnVerdict(verdict, null)], null, NowUtc: Stopped.AddMinutes(3)));
    }

    /// <summary>A session the owner parked: when it comes back, and the stop he parked it over, kept rather
    /// than thrown away.</summary>
    private static WingmanNowResponse Snoozed()
    {
        var verdict = Verdict(TurnVerdictVocabulary.NeededYou);
        var row = Row(verdict, colour: "grey", hex: "#6b7280", label: "Snoozed");
        row.OnHold = true;
        row.SnoozeUntil = Stopped.AddHours(4);
        return WingmanNowFold.Fold(new WingmanNowInputs(
            Sid, row, [new AnsweredTurnVerdict(verdict, null)], null, NowUtc: Stopped.AddMinutes(30)));
    }

    // ---------------------------------------------------------------- the rows and verdicts behind them

    private static TurnVerdictDto Verdict(
        string word,
        string? finishedKind = null,
        string label = "Merge pull request 3002, or allow me to merge it",
        int options = 0) => new()
    {
        VerdictId = "verdict-1",
        JudgedAtUtc = Stopped.AddSeconds(4),
        TurnEndObservedAtUtc = Stopped,
        Verdict = word,
        Confidence = "high",
        FinishedKind = finishedKind,
        Label = label,
        Summary = "The release notes are pushed and the merge command was refused by a permission check.",
        Evidence = "Either merge 3002 yourself, or allow that command and I will do it.",
        Options = Enumerable.Range(0, options).Select(i => new TurnVerdictOptionDto
        {
            Key = "Option " + i,
            Note = "What option " + i + " does.",
            Send = i.ToString(),
            Recommended = i == 0,
        }).ToList(),
    };

    private static SessionDto Row(TurnVerdictDto? verdict, string colour = "red", string hex = "#ef4444",
        string label = "Needs you") => new()
    {
        SessionId = Sid,
        Number = 112,
        Name = "Wingman Inspector - Manager",
        AgentToolDisplay = "Claude Code",
        ActivityState = "WaitingForInput",
        WaitingSince = Stopped,
        EffectiveColor = colour,
        EffectiveColorHex = hex,
        StateLabel = label,
        VerdictState = verdict is null ? VerdictStates.None : VerdictStates.Judged,
        TurnVerdict = verdict,
    };

    private static SessionDto WorkingRow()
    {
        var row = Row(null, colour: "blue", hex: "#3b82f6", label: "Working");
        row.ActivityState = "Working";
        return row;
    }

    private static WingmanNowConversation Asked(string text) => new(true,
    [
        new HistoryMessageDto { Role = "Assistant", Parts = { new HistoryPartDto { Kind = "Text", Text = "Earlier reply." } } },
        new HistoryMessageDto
        {
            Role = "User",
            Parts = { new HistoryPartDto { Kind = "Text", Text = text } },
            Timestamp = new DateTimeOffset(Stopped.AddMinutes(-4), TimeSpan.Zero),
        },
    ]);

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/Wingman/WingmanNowWireSampleTests.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        var marker = Path.Combine(root, "packages", "client-core", "src", "sessions", "wingmanNowRead.ts");
        Assert.True(File.Exists(marker),
            $"Resolved the repository root to {root}, but it has no {marker}. Run the suite from a checkout.");
        return root;
    }
}
