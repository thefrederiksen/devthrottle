using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// A throwaway, fully migrated Gateway database with one team in it, a prompt log in a temporary folder, and a FAKE
/// model: everything the Mentor's weekly writer reads, and nothing paid (devthrottle_internal#2305). The team's week
/// under test is <see cref="Week"/>, in UTC, and the clock stands two hours after it closed.
/// </summary>
internal sealed class MentorRig : IDisposable
{
    public const string Owner = "sub-owner";
    public const string Manager = "sub-manager";
    public const string Rob = "sub-rob";
    public const string Dana = "sub-dana";
    public const string Collaborator = "sub-collab";

    public static readonly MentorWeek Week = new(2026, 40);
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _promptDir = Path.Combine(Path.GetTempPath(), "mentor-rig-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTenantContext _ambient = new(TenantId.Local);

    public GatewayDatabase Db { get; }
    public TenantRegistry Tenants { get; }
    public TeamRegistry Teams { get; }
    public TeamMentorStore Store { get; }
    public SessionHistoryStore Sessions { get; }
    public GatewayPromptLog Prompts { get; }
    public FakeBrain Brain { get; } = new();
    public TenantId Team { get; }
    public DateTime Now { get; set; }

    /// <param name="ownerAndRobOnly">A team of two people - the Owner and Rob - for the proof; otherwise five.</param>
    public MentorRig(bool ownerAndRobOnly = false)
    {
        Db = _harness.Open(_ambient);
        Tenants = new TenantRegistry(Db);
        Teams = new TeamRegistry(Db, Tenants);
        Store = new TeamMentorStore(Db);
        Sessions = new SessionHistoryStore(Db);
        Prompts = new GatewayPromptLog(_promptDir);

        Tenants.MintOrLookupBySubject(Owner, "olivia.owner@example.com");
        Tenants.MintOrLookupBySubject(Manager, "priya.nair@example.com");
        Tenants.MintOrLookupBySubject(Rob, "rob.keller@example.com");
        Tenants.MintOrLookupBySubject(Dana, "dana@example.com");
        Tenants.MintOrLookupBySubject(Collaborator, "mike@example.com");
        Team = Teams.CreateTeam(Owner, "Team DevThrottle").Team!.Tenant;
        Teams.AddMember(Team.Value, Rob, TeamRole.Developer);
        if (!ownerAndRobOnly)
        {
            Teams.AddMember(Team.Value, Manager, TeamRole.Manager);
            Teams.AddMember(Team.Value, Dana, TeamRole.Developer);
            Teams.AddMember(Team.Value, Collaborator, TeamRole.Collaborator);
        }

        Now = Week.UtcBounds(Zone).ToUtc.AddHours(2);
    }

    public TeamMentorWriter Writer() =>
        new(Teams, Store, Sessions, Prompts, (_, _) => Task.FromResult<(IAgentBrain, string)>((Brain, "fake-model")), () => Now);

    /// <summary>A session of <paramref name="person"/> in the team, alive at <paramref name="atUtc"/>.</summary>
    public void SessionOf(string person, DateTime atUtc, string sessionId, TenantId? tenant = null)
    {
        _ambient.Current = tenant ?? Team;
        try
        {
            Sessions.UpsertLive("director-" + person, new SessionDto
            {
                SessionId = sessionId,
                Name = "work",
                CreatedAt = atUtc,
                LastActivityAt = atUtc,
                ActivityState = "Working",
                Status = "Running",
            }, atUtc, new DirectorFacts("MACHINE", "1.0", person));
        }
        finally
        {
            _ambient.Current = TenantId.Local;
        }
    }

    /// <summary>One prompt pushed into the team as <paramref name="person"/> would push it - stamped by the Gateway.
    /// Returns the stored record, with its Gateway id.</summary>
    public PromptRecord PromptOf(string person, DateTime atUtc, string text, string sessionId = "s-1", string role = "user")
    {
        var stamped = PromptStamp.ForTeam(new[] { Record(atUtc, text, sessionId, role) }, person);
        Prompts.Append(Team, stamped);
        return stamped[0];
    }

    public static PromptRecord Record(DateTime atUtc, string text, string sessionId = "s-1", string role = "user") => new()
    {
        TsUtc = atUtc,
        SessionId = sessionId,
        Role = role,
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
        Text = text,
    };

    /// <summary>A moment inside the week, <paramref name="day"/> days after its Monday.</summary>
    public static DateTime InWeek(int day, int hour = 10) => Week.UtcBounds(Zone).FromUtc.AddDays(day).AddHours(hour);

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_promptDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class MutableTenantContext : ITenantContext
    {
        public MutableTenantContext(TenantId tenant) => Current = tenant;
        public TenantId Current { get; set; }
    }
}

/// <summary>
/// The fake model: answers with whatever the test sets, records every prompt it was asked, and never reaches a
/// network. <see cref="Answer"/> gets the request text and returns the reply.
/// </summary>
internal sealed class FakeBrain : IAgentBrain
{
    public Func<string, string> Answer { get; set; } = _ => throw new InvalidOperationException("The test set no answer.");
    public List<string> Asked { get; } = new();

    public string? SessionId => null;

    public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
    {
        Asked.Add(prompt);
        return Task.FromResult(new AskResult { Text = Answer(prompt) });
    }

    public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<ClearResult> ClearAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => throw new NotSupportedException();

    // The writer disposes the brain after each call; the fake is reused across calls, so disposing does nothing.
    public void Dispose() { }

    /// <summary>A well-formed hard-week answer quoting <paramref name="labels"/>.</summary>
    public static string HardWeek(params string[] labels) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            tone = "hard",
            workedOn = "The new signup page and two bug fixes in the installer.",
            howItWent = (string?)null,
            wentBadlyAndWhy = "On Tuesday they restarted the same task four times. Their first instruction did not say which file to change, so the agent guessed differently each time.",
            quotes = labels,
            oneThingToTry = "Name the file and the result you expect in the first line, before asking for the change.",
        });

    /// <summary>A well-formed good-week answer, quoting nothing.</summary>
    public static string GoodWeek() =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            tone = "good",
            workedOn = "The release checklist skill and the October release.",
            howItWent = "Every session started from a written plan; none needed a restart.",
            wentBadlyAndWhy = (string?)null,
            quotes = Array.Empty<string>(),
            oneThingToTry = "Keep writing the plan first.",
        });
}
