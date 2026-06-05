using System.Net;
using System.Text;

namespace CcDirector.AgentBrain.Tests;

/// <summary>
/// Scripted fake of a Director's Control API for unit-testing AgentBrainClient
/// without a live Director. Routes are registered as (method, path-prefix) ->
/// response factory; factories can close over mutable state to simulate state
/// machines (busy -> idle, old transcript -> new transcript, ...).
/// </summary>
public sealed class FakeDirectorHandler : HttpMessageHandler
{
    private readonly List<(string Method, string Path, Func<HttpRequestMessage, (HttpStatusCode, string)> Respond)> _routes = new();

    /// <summary>Every request the client made, "METHOD path", in order.</summary>
    public List<string> Requests { get; } = new();

    public void On(string method, string pathPrefix, Func<HttpRequestMessage, (HttpStatusCode, string)> respond) =>
        _routes.Add((method, pathPrefix, respond));

    public void OnJson(string method, string pathPrefix, HttpStatusCode status, string json) =>
        On(method, pathPrefix, _ => (status, json));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is null)
            throw new InvalidOperationException("FakeDirectorHandler received a request without a RequestUri");
        var path = request.RequestUri.PathAndQuery.TrimStart('/');
        Requests.Add($"{request.Method.Method} {path}");

        // Longest matching prefix wins, so "sessions/{sid}/prompt" is not swallowed
        // by a route registered as just "sessions".
        var match = _routes
            .Where(r => r.Method == request.Method.Method && path.StartsWith(r.Path, StringComparison.Ordinal))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();
        if (match.Respond is not null)
        {
            var (status, json) = match.Respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"{{\"error\":\"no fake route for {request.Method.Method} {path}\"}}",
                Encoding.UTF8, "application/json"),
        });
    }
}
