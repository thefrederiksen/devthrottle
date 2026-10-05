using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// The person on a team session's history row, and who may set its description line (devthrottle_internal#2305,
/// review of part 3). The session id on a pushed prompt is the client's own, so in a team a push may only describe a
/// session that is the pushing person's; and the person is write-once, so the recorder asks for it only until it knows.
/// </summary>
public sealed class SessionHistoryPersonTests : IDisposable
{
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private (SessionHistoryRecorder Recorder, SessionHistoryStore Store) New(Func<TenantId, string, string?> personOf)
    {
        var store = new SessionHistoryStore(_harness.Open());
        return (new SessionHistoryRecorder(store, personOf: personOf), store);
    }

    private static SessionDto Session(string id, string activityState = "Working") => new()
    {
        SessionId = id,
        Name = null,
        Number = 7,
        RepoPath = @"D:\repos\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "ClaudeCode",
        MachineName = "",
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        ActivityState = activityState,
        Status = "Running",
    };

    private static PromptRecord Prompt(string sessionId, string text, string? person) => new()
    {
        TsUtc = DateTime.UtcNow, SessionId = sessionId, Role = "user", TimestampFromAgent = true,
        CharCount = text.Length, WordCount = text.Split(' ').Length, Text = text, PersonSubject = person,
    };

    private static WorkHistorySessionDto Row(SessionHistoryStore store, string id)
        => store.ReadRange(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)).Single(r => r.SessionId == id);

    private static string? OwnerOfDirector(TenantId _, string directorId) => directorId switch
    {
        "dir-alice" => Alice,
        "dir-bob" => Bob,
        _ => null,
    };

    [Fact]
    public void A_push_naming_another_members_session_leaves_that_rows_description_alone()
    {
        var (recorder, store) = New(OwnerOfDirector);
        recorder.Observe(TenantId.Local, "dir-bob", Session("s-bob"));

        // Alice's key, Bob's session id: the Gateway stamped the record with Alice.
        recorder.ObservePrompts(TenantId.Local, new[] { Prompt("s-bob", "Alice writes into Bob's session", Alice) });

        Assert.NotEqual("Alice writes into Bob's session", Row(store, "s-bob").DescriptionLine);
    }

    [Fact]
    public void After_a_refused_push_the_sessions_own_person_still_sets_its_description()
    {
        var (recorder, store) = New(OwnerOfDirector);
        recorder.Observe(TenantId.Local, "dir-bob", Session("s-bob"));

        recorder.ObservePrompts(TenantId.Local, new[] { Prompt("s-bob", "Alice writes into Bob's session", Alice) });
        recorder.ObservePrompts(TenantId.Local, new[] { Prompt("s-bob", "Bob builds the parser", Bob) });

        Assert.Equal("Bob builds the parser", Row(store, "s-bob").DescriptionLine);
    }

    [Fact]
    public void A_team_push_onto_a_row_whose_person_is_not_known_yet_leaves_it_alone()
    {
        var (recorder, store) = New(OwnerOfDirector);
        recorder.Observe(TenantId.Local, "dir-nobody", Session("s-unknown"));

        recorder.ObservePrompts(TenantId.Local, new[] { Prompt("s-unknown", "Alice claims this one", Alice) });

        Assert.NotEqual("Alice claims this one", Row(store, "s-unknown").DescriptionLine);
    }

    [Fact]
    public void A_personal_push_carries_no_person_and_sets_the_description_as_before()
    {
        var (recorder, store) = New((_, _) => null);
        recorder.Observe(TenantId.Local, "dir-1", Session("s1"));

        recorder.ObservePrompts(TenantId.Local, new[] { Prompt("s1", "Build the History page", null) });

        Assert.Equal("Build the History page", Row(store, "s1").DescriptionLine);
    }

    [Fact]
    public void The_person_is_asked_for_once_per_session_then_never_again()
    {
        var asked = 0;
        var (recorder, _) = New((tenant, directorId) => { asked++; return OwnerOfDirector(tenant, directorId); });

        recorder.Observe(TenantId.Local, "dir-alice", Session("s-alice", "Working"));
        recorder.Observe(TenantId.Local, "dir-alice", Session("s-alice", "WaitingForInput"));
        recorder.Observe(TenantId.Local, "dir-alice", Session("s-alice", "Idle"));
        recorder.Observe(TenantId.Local, "dir-alice", Session("s-alice", "Working"));

        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_session_whose_person_is_not_known_is_asked_again_until_it_is()
    {
        var asked = 0;
        string? answer = null;
        var (recorder, _) = New((_, _) => { asked++; return answer; });

        recorder.Observe(TenantId.Local, "dir-alice", Session("s-alice", "Working"));
        answer = Alice;
        recorder.Observe(TenantId.Local, "dir-alice", Session("s-alice", "Idle"));
        recorder.Observe(TenantId.Local, "dir-alice", Session("s-alice", "Working"));

        Assert.Equal(2, asked);
    }
}
