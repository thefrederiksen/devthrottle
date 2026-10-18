using CcDirector.TestInfrastructure;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The colour a Wingman trace records is the colour the display push shows (the Wingman inspector, phase 2, inspection
/// round 1).
///
/// THE DEFECT. The trace fold named its own inputs and passed no voice-waiting clock. A voice-mode session whose wait for
/// voice had given up was pushed red "Voice did not arrive" and recorded yellow. The fix is structural - both folds take
/// every input from <see cref="DisplayFold"/> - and these tests hold it: one proves the two answers agree on the case that
/// differed, through the real clock and the real fold; the other proves no second call site has grown back.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class DisplayFoldTraceMatchesPushTests
{
    private static readonly TenantId Account = TenantId.Local;
    private const string Sid = "33333333-3333-3333-3333-333333333333";

    private sealed class NoVerdicts : ITurnVerdictRowSource
    {
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => new Dictionary<string, TurnVerdictDto>();
        public bool IsReading(TenantId tenant, string sessionId) => false;
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }

    private static SessionDto VoiceRow() => new()
    {
        SessionId = Sid,
        Name = Sid,
        ActivityState = "WaitingForInput",
        LastActivityAt = DateTime.UtcNow,
        VoiceMode = true,
    };

    [Fact]
    public void A_voice_session_whose_wait_has_given_up_is_recorded_with_the_colour_and_label_the_push_shows()
    {
        // The wait began four minutes ago - past the give-up - and no voice arrived.
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, "d", "c");
        Assert.True(pushed.ApplySnapshot(Account, "d", "c", 1, new List<SessionDto> { VoiceRow() }));
        var fold = new DisplayFold(
            voice: () => null,
            needsYou: new NeedsYouClock(),
            voiceWaiting: new VoiceWaitingClock(utcNow: () => DateTime.UtcNow.AddMinutes(-4)),
            snoozes: null,
            handRaises: new HandRaiseRegistry(),
            snoozeExpiry: () => null,
            pushed: pushed,
            enterScope: _ => new NoScope());
        var verdicts = new NoVerdicts();

        var pushRows = new List<SessionDto> { VoiceRow() };
        fold.Push(Account, pushRows, verdicts);
        var push = pushRows.Single();

        var trace = new TurnVerdictTraceRowStamp(t => pushed.SnapshotConnected(t), verdicts, fold.Record)
            .Stamp(Account, new TurnVerdictTrace
            {
                TraceId = "t1",
                SessionId = Sid,
                RecordedAtUtc = DateTime.UtcNow,
                TurnEndObservedAtUtc = DateTime.UtcNow,
                Trigger = "clock",
                Outcome = TurnVerdictTraceOutcomes.Skipped,
                ColourEnabled = true,
            });

        // THE CONTROL: the push really did give up, so an agreement below is about the case that differed.
        Assert.Equal("red", push.EffectiveColor);
        Assert.StartsWith("Voice did not arrive", push.StateLabel);

        Assert.Equal(push.EffectiveColor, trace.RowColour);
        Assert.Equal(push.StateLabel, trace.RowLabel);
    }

    /// <summary>
    /// THE PUSH CARRIES THE API-MODE VERDICT (mission #4070, phase 4): the desktop draws the Terminal tab's wheel button
    /// from what the push stamps, so the push must stamp it - the same stamp the roster runs, so every surface shows the
    /// same button. With no facts given, no row gets a verdict: one folded from nothing would be a confident wrong one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_push_stamps_the_wheel_button_the_roster_shows(bool withFacts)
    {
        // Arrange - the user holds the wheel of an API-mode session, so the roster shows Hand back.
        var pushed = new PushedSessionStore();
        var facts = new CcDirector.Gateway.ApiModeDisplay.ApiModeDisplayFacts(
            new CcDirector.Gateway.ApiModeEvents.ApiModeEventBuffer(),
            (_, ids) => ids.ToDictionary(id => id, _ => (IReadOnlyList<CcDirector.Gateway.ApiModeDisplay.ApiModeVerdictReceipt>)Array.Empty<CcDirector.Gateway.ApiModeDisplay.ApiModeVerdictReceipt>()));
        var fold = new DisplayFold(
            voice: () => null,
            needsYou: new NeedsYouClock(),
            voiceWaiting: new VoiceWaitingClock(),
            snoozes: null,
            handRaises: new HandRaiseRegistry(),
            snoozeExpiry: () => null,
            pushed: pushed,
            enterScope: _ => new NoScope(),
            apiModeFacts: withFacts ? facts : null);
        var row = new SessionDto
        {
            SessionId = Sid,
            Name = Sid,
            ActivityState = "WaitingForInput",
            LastActivityAt = DateTime.UtcNow,
            ConnectionMode = ConnectionModes.Api,
            WheelHolder = WheelHolders.User,
        };

        // Act
        fold.Push(Account, new List<SessionDto> { row }, new NoVerdicts());

        // Assert
        if (!withFacts)
        {
            Assert.Null(row.ApiMode);
            return;
        }
        var wheel = row.ApiMode?.WheelAction;
        Assert.NotNull(wheel);
        Assert.Equal(ApiModeWheelVerbs.HandBack, wheel!.Verb);
        Assert.Equal("Hand back", wheel.Label);
        Assert.True(wheel.Enabled);
    }

    [Fact]
    public void Only_the_display_fold_calls_the_push_fold_so_no_caller_can_name_its_own_inputs()
    {
        var gateway = Path.Combine(RepositorySourceIndex.Root, "src", "CcDirector.Gateway");
        var callers = RepositorySourceIndex.Under(gateway, ".cs")
            .SelectMany(f => File.ReadLines(f)
                .Where(line => Regex.IsMatch(line, @"\bEnrichVoiceThenFoldForPush\(")
                            && !line.TrimStart().StartsWith("///")
                            && !line.Contains("static void EnrichVoiceThenFoldForPush("))
                .Select(_ => Path.GetRelativePath(gateway, f)))
            .ToList();

        Assert.Equal(new[] { Path.Combine("Fleet", "DisplayFold.cs") }, callers);
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/Wingman/DisplayFoldTraceMatchesPushTests.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        var marker = Path.Combine(root, "src", "CcDirector.Gateway", "Fleet", "DisplayFold.cs");
        Assert.True(File.Exists(marker), $"Resolved the repository root to {root}, but it has no {marker}. Run the suite from a checkout.");
        return root;
    }
}
