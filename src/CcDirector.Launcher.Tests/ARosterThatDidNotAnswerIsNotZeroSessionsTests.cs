using CcDirector.Core.Instances;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// A live roster document that carries NO session list must read as "unknown", never as zero sessions.
///
/// FOUND BY AN INDEPENDENT REVIEWER ON ANOTHER PULL REQUEST, and routed here because the defect is in
/// this file rather than in the consumer that tripped over it.
///
/// <c>DirectorCrashJournalData.Sessions</c> has an empty-list initializer, so a document that never
/// mentioned sessions - <c>{}</c>, a half-written file, or one written by a newer build that renamed the
/// property - deserializes to a NON-NULL roster whose count is ZERO. <c>ReadSessionCount</c> then returns
/// a confident zero derived from a document that said nothing at all, and a guard standing on that number
/// permits a restart over a busy Director.
///
/// IT IS THE SAME FOLD AS THE REST OF THIS BRANCH, ONE LAYER LOWER. <c>ReadSessionCount</c>'s own summary
/// says the absence of a roster beside a live Director "is not zero sessions, it is no answer" - a true
/// sentence, defeated at deserialization, where ABSENT and EXPLICITLY-EMPTY became one in-memory value.
/// No caller can tell them apart once that has happened, which is why the check has to be where the
/// document is read.
///
/// THE THREE ANSWERS, and each one is asserted below: the roster says a number; the roster says an
/// explicit none; the roster did not answer.
/// </summary>
[Collection(StorageRootCollection.Name)]
public sealed class ARosterThatDidNotAnswerIsNotZeroSessionsTests : IDisposable
{
    private readonly string _dir;

    public ARosterThatDidNotAnswerIsNotZeroSessionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Id = "aaaa1111-0000-0000-0000-000000000001";

    private void WriteRoster(string json) => File.WriteAllText(Path.Combine(_dir, Id + ".json"), json);

    /// <summary>THE KNOWN-BAD INPUT. An empty object is a readable document that answers nothing.</summary>
    [Fact]
    public void An_empty_object_is_no_answer_and_not_zero_sessions()
    {
        WriteRoster("{}");

        Assert.Null(DirectorCrashJournal.ReadLiveRoster(Id, _dir));
    }

    /// <summary>A document naming the Director but no sessions is the same: it did not answer the
    /// question that was asked, and the fields it does carry do not make it an answer.</summary>
    [Fact]
    public void A_document_with_every_field_except_the_session_list_is_still_no_answer()
    {
        WriteRoster($$"""{"directorId":"{{Id}}","pid":1234,"machineName":"SOREN_NORTH"}""");

        Assert.Null(DirectorCrashJournal.ReadLiveRoster(Id, _dir));
    }

    /// <summary>A newer build that renamed the property is the realistic version of the same thing, and
    /// it is the one a version check would never see coming.</summary>
    [Fact]
    public void A_document_whose_session_list_is_under_another_name_is_no_answer()
    {
        WriteRoster($$"""{"directorId":"{{Id}}","liveSessions":[{"sessionId":"s1"}]}""");

        Assert.Null(DirectorCrashJournal.ReadLiveRoster(Id, _dir));
    }

    /// <summary>A session list of the wrong SHAPE is not a session list either.</summary>
    [Fact]
    public void A_session_property_that_is_not_a_list_is_no_answer()
    {
        WriteRoster($$"""{"directorId":"{{Id}}","sessions":"none"}""");

        Assert.Null(DirectorCrashJournal.ReadLiveRoster(Id, _dir));
    }

    /// <summary>
    /// THE CONTROL, and the test above means nothing without it. An EXPLICIT empty list is a real answer -
    /// the Director says it holds none - and must still read as zero. Otherwise the fix would simply have
    /// made every roster unknown, which passes every test above and stops any Director ever being
    /// restarted.
    /// </summary>
    [Fact]
    public void An_explicit_empty_session_list_IS_an_answer_of_zero()
    {
        WriteRoster($$"""{"directorId":"{{Id}}","sessions":[]}""");

        var roster = DirectorCrashJournal.ReadLiveRoster(Id, _dir);

        Assert.NotNull(roster);
        Assert.Empty(roster!.Sessions);
    }

    /// <summary>And the ordinary case: a roster with sessions in it reads as that number.</summary>
    [Fact]
    public void A_roster_with_sessions_reads_as_that_many()
    {
        WriteRoster($$"""
            {"directorId":"{{Id}}","sessions":[
              {"sessionId":"s1","repoPath":"D:/repo","agent":"ClaudeCode"},
              {"sessionId":"s2","repoPath":"D:/repo","agent":"ClaudeCode"}]}
            """);

        var roster = DirectorCrashJournal.ReadLiveRoster(Id, _dir);

        Assert.NotNull(roster);
        Assert.Equal(2, roster!.Sessions.Count);
    }

    /// <summary>A file that is not readable JavaScript Object Notation at all was already refused, and
    /// still is - asserted so the new check cannot be mistaken for the only thing standing there.</summary>
    [Fact]
    public void A_truncated_document_is_no_answer()
    {
        WriteRoster("{\"directorId\":\"" + Id + "\",\"sessions\":[");

        Assert.Null(DirectorCrashJournal.ReadLiveRoster(Id, _dir));
    }

    /// <summary>And an absent file, which is the case the original sentence was written about.</summary>
    [Fact]
    public void An_absent_roster_is_no_answer()
    {
        Assert.Null(DirectorCrashJournal.ReadLiveRoster(Id, _dir));
    }

    /// <summary>
    /// The whole point, at the consumer: the locator turns "no answer" into null rather than into a
    /// number. Driven through the production <see cref="DirectorInstanceLocator.ReadSessionCount"/> so
    /// the seam between the two is covered, not just each half.
    /// </summary>
    [Fact]
    public void The_locator_reports_an_unanswered_roster_as_unknown_rather_than_idle()
    {
        var home = Path.Combine(_dir, "home");
        var journal = Path.Combine(home, "config", "director", "crash-journal");
        Directory.CreateDirectory(journal);
        File.WriteAllText(Path.Combine(journal, Id + ".json"), "{}");

        var locator = new DirectorInstanceLocator(home);
        var director = new SupervisedDirector(Id, Environment.ProcessId, home, "2.0.6", DateTime.UtcNow, "");

        Assert.Null(locator.ReadSessionCount(director));
    }

    /// <summary>The control at the same seam: an explicit empty list reaches the locator as zero.</summary>
    [Fact]
    public void The_locator_reports_an_explicitly_empty_roster_as_zero()
    {
        var home = Path.Combine(_dir, "home2");
        var journal = Path.Combine(home, "config", "director", "crash-journal");
        Directory.CreateDirectory(journal);
        File.WriteAllText(Path.Combine(journal, Id + ".json"), $$"""{"directorId":"{{Id}}","sessions":[]}""");

        var locator = new DirectorInstanceLocator(home);
        var director = new SupervisedDirector(Id, Environment.ProcessId, home, "2.0.6", DateTime.UtcNow, "");

        Assert.Equal(0, locator.ReadSessionCount(director));
    }
}
