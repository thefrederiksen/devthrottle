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

    [Fact]
    public void Only_the_display_fold_calls_the_push_fold_so_no_caller_can_name_its_own_inputs()
    {
        var gateway = Path.Combine(RepoRoot(), "src", "CcDirector.Gateway");
        var callers = Directory.EnumerateFiles(gateway, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
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
