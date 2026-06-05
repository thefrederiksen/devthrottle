using System.Net;
using Xunit;

namespace CcDirector.AgentBrain.Tests;

public class AgentBrainClientTests
{
    private const string Sid = "11111111-1111-1111-1111-111111111111";

    private static AgentBrainOptions FastOptions() => new()
    {
        DirectorUrl = "http://fake-director",
        RepoPath = @"D:\fake\repo",
        QuietSeconds = 2.0,
        QuietTimeoutSeconds = 5.0,
        CreateTimeoutSeconds = 5.0,
        AskTimeoutSeconds = 10.0,
        ClearTimeoutSeconds = 5.0,
        PollIntervalSeconds = 0.01,
        ReplyStableSeconds = 0.05,
        Log = _ => { },
    };

    private static string SessionJson(string state = "WaitingForInput", double idle = 5.0,
        string status = "Running") =>
        $"{{\"sessionId\":\"{Sid}\",\"status\":\"{status}\",\"activityState\":\"{state}\",\"idleSeconds\":{idle:F1},\"quietThresholdSeconds\":10}}";

    private static string TurnsJson(string claudeId, params (string Kind, string Content)[] widgets)
    {
        var items = string.Join(",", widgets.Select(w =>
            $"{{\"kind\":\"{w.Kind}\",\"content\":\"{w.Content}\"}}"));
        return $"{{\"sessionId\":\"{Sid}\",\"claudeSessionId\":\"{claudeId}\",\"status\":\"ok\",\"widgets\":[{items}]}}";
    }

    private static string UsageJson(long context, int msgs) =>
        $"{{\"sessionId\":\"{Sid}\",\"contextTokens\":{context},\"assistantMessageCount\":{msgs},\"inputTokens\":1,\"outputTokens\":2,\"cacheReadTokens\":3,\"cacheCreationTokens\":4,\"turns\":[]}}";

    private static async Task<AgentBrainClient> ConnectAsync(FakeDirectorHandler fake)
    {
        fake.OnJson("GET", "healthz", HttpStatusCode.OK, "{\"status\":\"ok\",\"version\":\"test\"}");
        return await AgentBrainClient.ConnectAsync(FastOptions(), fake);
    }

    // ------------------------------------------------------------ Connect

    [Fact]
    public async Task ConnectAsync_HealthzOk_Succeeds()
    {
        var fake = new FakeDirectorHandler();
        var client = await ConnectAsync(fake);
        Assert.Null(client.SessionId);
        Assert.Contains("GET healthz", fake.Requests);
    }

    [Fact]
    public async Task ConnectAsync_DirectorDown_Throws()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("GET", "healthz", HttpStatusCode.ServiceUnavailable, "{}");
        await Assert.ThrowsAsync<AgentBrainException>(
            () => AgentBrainClient.ConnectAsync(FastOptions(), fake));
    }

    // ------------------------------------------------------ CreateSession

    [Fact]
    public async Task CreateSessionAsync_WaitsUntilQuiet()
    {
        var fake = new FakeDirectorHandler();
        var polls = 0;
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson("Starting", 0));
        fake.On("GET", $"sessions/{Sid}", _ =>
        {
            polls++;
            // Busy for the first 3 polls, then idle long enough to pass the quiet gate.
            return (HttpStatusCode.OK, polls < 3 ? SessionJson("Working", 0.1) : SessionJson("WaitingForInput", 5));
        });

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();

        Assert.Equal(Sid, client.SessionId);
        Assert.True(polls >= 3);
    }

    [Fact]
    public async Task CreateSessionAsync_SessionExits_Throws()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson("Starting", 0));
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson("Exited", 0, "Exited"));

        var client = await ConnectAsync(fake);
        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => client.CreateSessionAsync());
        Assert.Contains("dead", ex.Message);
    }

    // ---------------------------------------------------------------- Ask

    [Fact]
    public async Task AskAsync_ReturnsFullTextFromTurns()
    {
        var fake = new FakeDirectorHandler();
        var prompted = false;
        // Long answer (beyond /summary's 2000-char cap) to prove the /turns path.
        var longAnswer = new string('x', 5000);

        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}/usage", HttpStatusCode.OK, UsageJson(62000, 2));
        fake.On("GET", $"sessions/{Sid}/turns", _ => (HttpStatusCode.OK, prompted
            ? TurnsJson("c1", ("UserMessage", "q"), ("Text", longAnswer))
            : TurnsJson("c1")));
        fake.On("POST", $"sessions/{Sid}/prompt", _ => { prompted = true; return (HttpStatusCode.OK, "{\"accepted\":true}"); });
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson());

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        var result = await client.AskAsync("q");

        Assert.Equal(longAnswer, result.Text);
        Assert.Equal(62000, result.ContextTokens);
        Assert.Contains(fake.Requests, r => r.StartsWith($"POST sessions/{Sid}/prompt"));
    }

    [Fact]
    public async Task AskAsync_WaitsForTranscriptStability_ReturnsLastTextWidget()
    {
        var fake = new FakeDirectorHandler();
        var prompted = false;
        var turnsPolls = 0;

        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}/usage", HttpStatusCode.OK, UsageJson(100, 2));
        fake.On("GET", $"sessions/{Sid}/turns", _ =>
        {
            if (!prompted) return (HttpStatusCode.OK, TurnsJson("c1"));
            turnsPolls++;
            // First a partial reply, then the transcript grows to its final shape.
            return (HttpStatusCode.OK, turnsPolls < 3
                ? TurnsJson("c1", ("UserMessage", "q"), ("Text", "partial"))
                : TurnsJson("c1", ("UserMessage", "q"), ("Text", "partial"), ("Text", "final answer")));
        });
        fake.On("POST", $"sessions/{Sid}/prompt", _ => { prompted = true; return (HttpStatusCode.OK, "{}"); });
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson());

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        var result = await client.AskAsync("q");

        Assert.Equal("final answer", result.Text);
    }

    [Fact]
    public async Task AskAsync_SessionBusyBeyondTimeout_Throws()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        var created = false;
        fake.On("GET", $"sessions/{Sid}", _ =>
        {
            // Ready once for CreateSession's quiet gate, then permanently busy.
            if (!created) { created = true; return (HttpStatusCode.OK, SessionJson()); }
            return (HttpStatusCode.OK, SessionJson("Working", 0.1));
        });

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => client.AskAsync("q"));
        Assert.Contains("not quiet", ex.Message);
    }

    [Fact]
    public async Task AskAsync_NoSession_Throws()
    {
        var fake = new FakeDirectorHandler();
        var client = await ConnectAsync(fake);
        await Assert.ThrowsAsync<AgentBrainException>(() => client.AskAsync("q"));
    }

    // -------------------------------------------------------------- Clear

    [Fact]
    public async Task ClearAsync_RelinksToTheRecentNewTranscript()
    {
        var fake = new FakeDirectorHandler();
        var cleared = false;
        var now = DateTime.UtcNow.ToString("O");
        var lastWeek = DateTime.UtcNow.AddDays(-7).ToString("O");

        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}/turns", HttpStatusCode.OK, TurnsJson("old-id", ("Text", "hi")));
        fake.On("POST", $"sessions/{Sid}/prompt", _ => { cleared = true; return (HttpStatusCode.OK, "{}"); });
        // The listing also contains a STALE old transcript that must NOT be picked.
        fake.On("GET", "claude-transcripts", _ => (HttpStatusCode.OK, cleared
            ? $"[{{\"claudeSessionId\":\"new-id\",\"lastWriteUtc\":\"{now}\"}}," +
              $"{{\"claudeSessionId\":\"old-id\",\"lastWriteUtc\":\"{now}\"}}," +
              $"{{\"claudeSessionId\":\"stale-id\",\"lastWriteUtc\":\"{lastWeek}\"}}]"
            : $"[{{\"claudeSessionId\":\"old-id\",\"lastWriteUtc\":\"{now}\"}}]"));
        fake.OnJson("POST", $"sessions/{Sid}/relink", HttpStatusCode.OK, "{\"accepted\":true}");

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        var result = await client.ClearAsync();

        Assert.Equal("old-id", result.OldClaudeSessionId);
        Assert.Equal("new-id", result.NewClaudeSessionId);
        Assert.Contains(fake.Requests, r => r.StartsWith($"POST sessions/{Sid}/relink"));
    }

    [Fact]
    public async Task ClearAsync_StaleTranscriptOnly_TimesOutAndThrows()
    {
        var fake = new FakeDirectorHandler();
        var lastWeek = DateTime.UtcNow.AddDays(-7).ToString("O");

        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}/turns", HttpStatusCode.OK, TurnsJson("old-id"));
        fake.OnJson("POST", $"sessions/{Sid}/prompt", HttpStatusCode.OK, "{}");
        // Only a week-old transcript shows up - recency filter must reject it.
        fake.OnJson("GET", "claude-transcripts", HttpStatusCode.OK,
            $"[{{\"claudeSessionId\":\"ancient\",\"lastWriteUtc\":\"{lastWeek}\"}}]");

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => client.ClearAsync());
        Assert.Contains("no new claude session id", ex.Message);
    }

    // ------------------------------------------------------ Restart / Kill

    [Fact]
    public async Task RestartAsync_KillsThenCreatesFreshSession()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson());
        fake.OnJson("DELETE", $"sessions/{Sid}", HttpStatusCode.OK, "{}");

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        await client.RestartAsync();

        Assert.Equal(Sid, client.SessionId);
        Assert.Contains($"DELETE sessions/{Sid}", fake.Requests);
        Assert.Equal(2, fake.Requests.Count(r => r == "POST sessions"));
    }

    [Fact]
    public async Task RestartAsync_DeadSessionAlreadyGone_StillCreatesFresh()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson());
        fake.OnJson("DELETE", $"sessions/{Sid}", HttpStatusCode.NotFound, "{\"error\":\"gone\"}");

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        await client.RestartAsync();   // must not throw on the 404

        Assert.Equal(Sid, client.SessionId);
    }

    [Fact]
    public async Task KillAsync_DeletesAndForgetsSession()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson());
        fake.OnJson("DELETE", $"sessions/{Sid}", HttpStatusCode.OK, "{}");

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        await client.KillAsync();

        Assert.Null(client.SessionId);
    }

    // -------------------------------------------------------------- Health

    [Fact]
    public async Task GetHealthAsync_AliveSession_MapsFields()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        fake.OnJson("GET", $"sessions/{Sid}/usage", HttpStatusCode.OK, UsageJson(61973, 4));
        fake.OnJson("GET", $"sessions/{Sid}", HttpStatusCode.OK, SessionJson("WaitingForInput", 20.5));

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        var health = await client.GetHealthAsync();

        Assert.True(health.IsAlive);
        Assert.Equal("WaitingForInput", health.ActivityState);
        Assert.Equal(20.5, health.IdleSeconds, 1);
        Assert.Equal(61973, health.ContextTokens);
    }

    [Fact]
    public async Task GetHealthAsync_ExitedSession_NotAlive()
    {
        var fake = new FakeDirectorHandler();
        fake.OnJson("POST", "sessions", HttpStatusCode.Created, SessionJson());
        var created = false;
        fake.On("GET", $"sessions/{Sid}", _ =>
        {
            if (!created) { created = true; return (HttpStatusCode.OK, SessionJson()); }
            return (HttpStatusCode.OK, SessionJson("Exited", 99, "Exited"));
        });
        fake.OnJson("GET", $"sessions/{Sid}/usage", HttpStatusCode.NotFound, "{}");

        var client = await ConnectAsync(fake);
        await client.CreateSessionAsync();
        var health = await client.GetHealthAsync();

        Assert.False(health.IsAlive);
        Assert.Equal("Exited", health.Status);
    }
}
