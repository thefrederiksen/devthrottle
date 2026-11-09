using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Whether a Director can RECEIVE a command, and the sentence that says so (issue #4490).
///
/// On 26 October 2026 the licence switcher sent one command to devthrottle-mac-mini four times, waited
/// sixty seconds each time, and said "the Director on devthrottle-mac-mini did not answer" four times. Every
/// word of that was true and none of it was the fact: that Director had not answered ANYTHING sent down its
/// connection since 22:02 the night before, while its reports went up all night and every screen called it
/// healthy. The Gateway had the evidence - an answer or an expiry for every command it sent - and kept none
/// of it.
/// </summary>
public sealed class DirectorReachabilityTests
{
    private static DirectorDownlinkFacts.Facts Facts(int silences, DateTime? lastAnswered = null) =>
        new(lastAnswered, lastAnswered is null ? null : lastAnswered.Value.AddMinutes(1), silences);

    [Fact]
    public void WithNoStream_ThereIsNothingToSendDown()
    {
        // A different problem with a different fix: start a Director. Saying "not reachable" here would send
        // the reader to look at a connection that was never dialled.
        Assert.Equal(DirectorReach.NoStream, DirectorReachability.Classify(streamConnected: false, Facts(silences: 9)));
    }

    [Fact]
    public void OneExpiryIsASlowVerb_NotADeafMachine()
    {
        // The wait has an override for the language-model verbs precisely because a verb can outrun it.
        // Calling the first expiry a deaf machine would cry wolf on the first slow recap.
        Assert.Equal(DirectorReach.Reachable, DirectorReachability.Classify(streamConnected: true, Facts(silences: 1)));
        Assert.Null(DirectorReachability.Describe(DirectorReach.Reachable, Facts(silences: 1)));
    }

    [Fact]
    public void TwoExpiriesWithNothingAnsweredBetweenThem_IsAMachineThatIsNotReceiving()
    {
        var facts = Facts(silences: 2, lastAnswered: new DateTime(2026, 10, 25, 22, 2, 0, DateTimeKind.Utc));

        var reach = DirectorReachability.Classify(streamConnected: true, facts);

        Assert.Equal(DirectorReach.ReportingButNotReachable, reach);

        // THE SENTENCE THAT WAS MISSING. It names when the downlink last worked, because that time is what
        // tells the reader this is not their command's fault.
        var said = DirectorReachability.Describe(reach, facts);
        Assert.NotNull(said);
        Assert.Contains("has not answered anything sent down its connection since 22:02 UTC", said);
        Assert.Contains("the connection was open each time", said);
        Assert.Contains("2 commands expired with no answer", said);
        // IT DOES NOT CLAIM THE MACHINE IS ALIVE. This classifier never looked at the reporting side, and a
        // machine that lost power produces exactly these facts.
        Assert.DoesNotContain("reports are arriving", said);
        Assert.Contains("this cannot tell which", said);
    }

    [Fact]
    public void AMachineThatHasNeverAnswered_SaysThatRatherThanNamingATime()
    {
        var facts = Facts(silences: 4);

        var said = DirectorReachability.Describe(DirectorReach.ReportingButNotReachable, facts);

        Assert.NotNull(said);
        Assert.Contains("at any point since this Gateway started", said);
    }
}

/// <summary>
/// The facts themselves: kept from sends that already happened, with no probe and no extra traffic.
/// </summary>
public sealed class DirectorDownlinkFactsTests
{
    private static string NewDirector() => "dir-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void BeforeAnythingIsSent_NothingIsClaimed()
    {
        var facts = DirectorDownlinkFacts.Read(NewDirector());

        Assert.Equal(0, facts.SilencesSinceAnAnswer);
        Assert.Null(facts.LastAnsweredUtc);
    }

    [Fact]
    public void AnAnswerClearsTheSilences_BecauseTheDownlinkIsDemonstrablyCarryingAgain()
    {
        var director = NewDirector();
        var at = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

        DirectorDownlinkFacts.DidNotAnswer(director, at);
        DirectorDownlinkFacts.DidNotAnswer(director, at.AddSeconds(30));
        Assert.Equal(2, DirectorDownlinkFacts.Read(director).SilencesSinceAnAnswer);

        DirectorDownlinkFacts.Answered(director, at.AddMinutes(1));

        var facts = DirectorDownlinkFacts.Read(director);
        Assert.Equal(0, facts.SilencesSinceAnAnswer);
        Assert.Equal(at.AddMinutes(1), facts.LastAnsweredUtc);
        Assert.Equal(DirectorReach.Reachable, DirectorReachability.Classify(streamConnected: true, facts));
    }

    [Fact]
    public void TheEvidenceGoesWithTheConnection()
    {
        // A Director that was restarted to clear exactly this must not inherit the silence count of the
        // connection that would not answer, or it reads as deaf until its first answer comes back.
        var director = NewDirector();
        var at = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
        DirectorDownlinkFacts.DidNotAnswer(director, at);
        DirectorDownlinkFacts.DidNotAnswer(director, at);

        DirectorDownlinkFacts.Forget(director);

        Assert.Equal(0, DirectorDownlinkFacts.Read(director).SilencesSinceAnAnswer);
    }
}

/// <summary>
/// The failure message the caller actually reads, with and without the evidence.
/// </summary>
public sealed class DirectorCommandRouterDownlinkWordingTests
{
    [Fact]
    public void WithoutTheEvidence_TheMessageIsExactlyWhatItAlwaysWas()
    {
        var said = DirectorCommandRouter.DescribeTimeout("devthrottle-mac-mini", TimeSpan.FromSeconds(60));

        Assert.Equal("The Director on devthrottle-mac-mini did not answer within 60 seconds. "
                     + "It is not known whether the command was carried out.", said);
    }

    [Fact]
    public void WithTheEvidence_ItCarriesWhatTheGatewayKnows_AfterWhatItObserved()
    {
        var facts = new DirectorDownlinkFacts.Facts(
            LastAnsweredUtc: new DateTime(2026, 10, 25, 22, 2, 0, DateTimeKind.Utc),
            LastSilentUtc: new DateTime(2026, 10, 26, 15, 52, 0, DateTimeKind.Utc),
            SilencesSinceAnAnswer: 4);
        var evidence = DirectorReachability.Describe(DirectorReach.ReportingButNotReachable, facts);

        var said = DirectorCommandRouter.DescribeTimeout("devthrottle-mac-mini", TimeSpan.FromSeconds(60), evidence);

        Assert.StartsWith("The Director on devthrottle-mac-mini did not answer within 60 seconds.", said);
        Assert.Contains("since 22:02 UTC", said);
        Assert.Contains("4 commands expired with no answer", said);
    }
}
