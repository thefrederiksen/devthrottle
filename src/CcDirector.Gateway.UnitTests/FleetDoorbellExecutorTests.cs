using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director's <c>ring</c> verb (the Message Load mission, slice 2) through the real command dispatcher: the
/// verb is registered, its guards answer as the Gateway expects, and a session whose screen cannot be read is
/// deferred with NOTHING written to its terminal. The screen rules themselves are proven on real captures in
/// DoorbellSafetyTests; the typed line is proven end to end in the slice 2 evidence.
/// </summary>
[Collection("DirectorRoot")]
public sealed class FleetDoorbellExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DirectorCommand Ring(string sessionId, string? payload = null) => new()
    {
        CommandId = "ring-1",
        Verb = FleetDoorbellVerbs.Ring,
        SessionId = sessionId,
        PayloadJson = payload ?? JsonSerializer.Serialize(new FleetRingRequest { UnreadCount = 2 }, Json),
    };

    [Fact]
    public void The_verb_is_spelled_ring()
    {
        Assert.Equal("ring", FleetDoorbellVerbs.Ring);
        Assert.Equal("rung", FleetRingOutcomes.Rung);
        Assert.Equal("deferred", FleetRingOutcomes.Deferred);
    }

    [Fact]
    public async Task A_session_without_a_rendered_terminal_is_deferred_and_nothing_is_typed()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var backend = new ExecuteActionTestBackend();
            var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
            var before = backend.Buffer!.DumpAll().Length;

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Ring(session.Id.ToString()));

            Assert.True(result.Ok, result.Error);
            Assert.Equal("ring-1", result.CommandId);
            var answer = JsonSerializer.Deserialize<FleetRingResponse>(result.BodyJson!, Json)!;
            Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
            Assert.Equal(FleetRingDeferReasons.ScreenUnreadable, answer.Reason);
            Assert.Equal(before, backend.Buffer.DumpAll().Length);
            Assert.DoesNotContain("doorbell", Encoding.UTF8.GetString(backend.Buffer.DumpAll()));
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task An_exited_session_is_deferred_as_exited()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var backend = new ExecuteActionTestBackend();
            var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
            backend.RaiseProcessExited(1);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Ring(session.Id.ToString()));

            var answer = JsonSerializer.Deserialize<FleetRingResponse>(result.BodyJson!, Json)!;
            Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
            Assert.Equal(FleetRingDeferReasons.Exited, answer.Reason);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task An_unknown_session_is_not_found()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Ring(Guid.NewGuid().ToString()));

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Theory]
    [InlineData("not-a-guid", null)]
    [InlineData("11111111-1111-1111-1111-111111111111", "")]
    public async Task A_malformed_ring_is_a_bad_request(string sessionId, string? payload)
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", Ring(sessionId, payload));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }
}
