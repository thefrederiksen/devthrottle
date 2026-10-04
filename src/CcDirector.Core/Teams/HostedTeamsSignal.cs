using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Teams;

/// <summary>
/// Whether a Gateway has Teams released (devthrottle_internal#2311, review finding F1). The Director reads this
/// ONE explicit signal before anything to do with teams, and never infers it from how another route answers: a
/// Gateway without Teams answers 401 on the teams route, which cannot be told apart from a refused sign-in.
/// </summary>
public interface IHostedTeamsSignal
{
    /// <summary>
    /// True when the Gateway at <paramref name="gatewayUrl"/> says it has Teams; false when it says it does not,
    /// or says nothing about Teams at all (a Gateway from before Teams). A failure means the Gateway could not be
    /// asked, so nobody can tell.
    /// </summary>
    Task<OperationResult<bool>> TeamsReleasedAsync(string gatewayUrl, CancellationToken ct);
}

/// <summary>
/// Reads the signal from the Gateway's anonymous health answer, <c>GET /healthz</c>, which needs no key and
/// which the Director already reads to test an address. A Gateway with Teams released carries
/// <c>"teams": true</c>. A Gateway from before Teams carries no <c>teams</c> field at all - that missing field
/// is what such a Gateway really sends, and it means "no teams", exactly as <c>false</c> does.
/// </summary>
public sealed class HealthzTeamsSignal : IHostedTeamsSignal
{
    /// <summary>The anonymous route the signal is read from.</summary>
    public const string Route = "healthz";

    /// <summary>The field of the health answer that carries the signal.</summary>
    public const string Field = "teams";

    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly TimeSpan _timeout;

    /// <param name="handlerFactory">The HTTP handler; null uses a real one.</param>
    /// <param name="timeout">The call's timeout; null uses 15 seconds.</param>
    public HealthzTeamsSignal(Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? timeout = null)
    {
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler());
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <inheritdoc />
    public async Task<OperationResult<bool>> TeamsReleasedAsync(string gatewayUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new ArgumentException("gatewayUrl is required", nameof(gatewayUrl));

        using var http = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = _timeout };
        http.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/");

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(Route, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Write($"[HealthzTeamsSignal] TeamsReleasedAsync: {gatewayUrl} unreachable: {ex.Message}");
            return OperationResult<bool>.Fail(
                $"Could not reach the Gateway at {gatewayUrl} to see whether it has teams. Please check your connection and try again.");
        }

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? body;
        try
        {
            body = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            body = null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            // A Gateway that is still starting answers 503 and, by its contract, already carries the same "teams"
            // answer: that is read. Any other refusal says nothing, and nothing is guessed from it.
            if (body is not null && body.TryGetPropertyValue(Field, out var starting)
                && starting is JsonValue sv && sv.TryGetValue<bool>(out var releasedWhileStarting))
            {
                FileLog.Write($"[HealthzTeamsSignal] TeamsReleasedAsync: {gatewayUrl} answered HTTP {(int)resp.StatusCode} with teams={releasedWhileStarting}");
                return OperationResult<bool>.Ok(releasedWhileStarting);
            }
            FileLog.Write($"[HealthzTeamsSignal] TeamsReleasedAsync: {gatewayUrl} health check answered HTTP {(int)resp.StatusCode}");
            return OperationResult<bool>.Fail(
                $"The Gateway at {gatewayUrl} did not answer its health check (HTTP {(int)resp.StatusCode}), so this Director cannot tell whether it has teams.");
        }

        if (body is null)
        {
            FileLog.Write($"[HealthzTeamsSignal] TeamsReleasedAsync: {gatewayUrl} health answer is not a JSON object");
            return OperationResult<bool>.Fail($"The Gateway at {gatewayUrl} sent a health answer this Director cannot read.");
        }

        if (!body.TryGetPropertyValue(Field, out var field) || field is null)
        {
            FileLog.Write($"[HealthzTeamsSignal] TeamsReleasedAsync: {gatewayUrl} says nothing about teams -> no teams");
            return OperationResult<bool>.Ok(false);
        }
        if (field is JsonValue v && v.TryGetValue<bool>(out var released))
        {
            FileLog.Write($"[HealthzTeamsSignal] TeamsReleasedAsync: {gatewayUrl} teams={released}");
            return OperationResult<bool>.Ok(released);
        }

        FileLog.Write($"[HealthzTeamsSignal] TeamsReleasedAsync: {gatewayUrl} health answer has a '{Field}' that is not true or false");
        return OperationResult<bool>.Fail($"The Gateway at {gatewayUrl} says something about teams this Director cannot read.");
    }
}

/// <summary>
/// The team a Director shows (screen D2) and offers to change (D3), from what it recorded and what its Gateway
/// says (review finding F2). A Director with no team file was enrolled before Teams existed, or by the setup
/// command line - both of which bind the PERSONAL account - so on a Gateway with Teams released, no file means
/// the personal account, and D3 is reachable from it.
/// </summary>
public static class DirectorTeamView
{
    /// <summary>
    /// The recorded team; otherwise, when the Director is connected to a Gateway that says it has Teams, the
    /// personal account; otherwise null (no chip). A failure means the Gateway could not be asked.
    /// </summary>
    /// <param name="recorded">The team this Director recorded, or null.</param>
    /// <param name="gatewayUrl">The Gateway this Director is connected to, or null/blank when it is connected to none.</param>
    /// <param name="signal">Asks that Gateway whether it has Teams.</param>
    /// <param name="ct">Cancels the question.</param>
    public static async Task<OperationResult<DirectorTeam?>> ResolveAsync(
        DirectorTeam? recorded, string? gatewayUrl, IHostedTeamsSignal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (recorded is not null)
            return OperationResult<DirectorTeam?>.Ok(recorded);
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return OperationResult<DirectorTeam?>.Ok(null);

        var released = await signal.TeamsReleasedAsync(gatewayUrl, ct).ConfigureAwait(false);
        if (!released.Success)
            return OperationResult<DirectorTeam?>.Fail(released.ErrorMessage!);

        FileLog.Write($"[DirectorTeamView] ResolveAsync: no team recorded; Gateway teams={released.Value} -> {(released.Value ? "personal account" : "no chip")}");
        return OperationResult<DirectorTeam?>.Ok(released.Value ? DirectorTeam.Personal : null);
    }
}
