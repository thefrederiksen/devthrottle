using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The WIRE FORM of the restart-request contract - issue #2725. Enums on this Gateway's API serialise as
/// integers unless the type says otherwise, and a request a person reads on a phone cannot render
/// <c>state: 3</c>. These pin the JSON text, not an object round trip: a round trip through the same
/// serialiser proves nothing when both ends share the same default.
/// </summary>
public sealed class DirectorRestartRequestWireFormTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(DirectorRestartRequestState.Pending, "Pending")]
    [InlineData(DirectorRestartRequestState.Accepted, "Accepted")]
    [InlineData(DirectorRestartRequestState.Declined, "Declined")]
    [InlineData(DirectorRestartRequestState.Expired, "Expired")]
    [InlineData(DirectorRestartRequestState.Abandoned, "Abandoned")]
    [InlineData(DirectorRestartRequestState.Completed, "Completed")]
    public void The_request_state_travels_as_its_name(DirectorRestartRequestState state, string expected)
    {
        var json = JsonSerializer.Serialize(new DirectorRestartRequestDto { State = state }, Web);
        Assert.Contains($"\"state\":\"{expected}\"", json);
        Assert.DoesNotContain($"\"state\":{(int)state}", json);
    }

    [Fact]
    public void The_capability_inside_the_request_travels_as_names_too()
    {
        var dto = new DirectorRestartRequestDto
        {
            State = DirectorRestartRequestState.Pending,
            Capability = new MachineRestartCapabilityDto
            {
                Verdict = RestartVerdict.CannotRestart,
                Reach = LauncherReach.NotStreamCapable,
                GuardedRestart = CapabilityState.Unknown,
                Declaration = LauncherDeclarationState.DeclaredNothing,
                RestartSignal = RestartSignalState.NotListening,
            },
        };
        var json = JsonSerializer.Serialize(dto, Web);
        Assert.Contains("\"verdict\":\"CannotRestart\"", json);
        Assert.Contains("\"reach\":\"NotStreamCapable\"", json);
        Assert.Contains("\"guardedRestart\":\"Unknown\"", json);
        Assert.Contains("\"declaration\":\"DeclaredNothing\"", json);
        Assert.Contains("\"restartSignal\":\"NotListening\"", json);
    }

    [Fact]
    public void A_progress_report_is_read_from_its_name_and_refused_from_a_name_this_build_does_not_know()
    {
        var report = JsonSerializer.Deserialize<DirectorRestartProgressReport>("{\"state\":\"Abandoned\",\"progress\":\"stopped\"}", Web)!;
        Assert.Equal(DirectorRestartRequestState.Abandoned, report.State);

        // A state written by a newer build is not silently the first enum value.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DirectorRestartProgressReport>("{\"state\":\"Paused\",\"progress\":\"x\"}", Web));
    }

    [Fact]
    public void The_list_envelope_names_its_array()
    {
        var json = JsonSerializer.Serialize(new DirectorRestartRequestListDto(), Web);
        Assert.Equal("{\"requests\":[]}", json);
    }
}
