using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="IImplSessionDriver"/> over the Gateway's existing
/// <see cref="DirectorEndpointClient"/> (issue #274). It uses the SAME session-create + seed-prompt
/// path the Cockpit/Director already use (ASSUMPTION confirmed: <see cref="NewSessionRequest.PrePrompt"/>
/// is the seed channel - the Director dispatches it once the agent reaches Idle), and reads the
/// session transcript with <c>GET /sessions/{sid}/buffer</c>. No new Director surface is introduced.
/// </summary>
public sealed class DirectorImplSessionDriver : IImplSessionDriver
{
    private readonly DirectorEndpointClient _client;
    private readonly string _endpoint;
    private readonly string _repoPath;

    /// <param name="client">The Gateway's shared Director client.</param>
    /// <param name="endpoint">Control endpoint (base URL, no trailing slash) of the target Director.</param>
    /// <param name="repoPath">Absolute repo path the seeded implementation session opens in.</param>
    public DirectorImplSessionDriver(DirectorEndpointClient client, string endpoint, string repoPath)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("director endpoint is required", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("repo path is required", nameof(repoPath));
        _endpoint = endpoint.TrimEnd('/');
        _repoPath = repoPath;
    }

    public async Task<(string? sessionId, string? error)> StartImplementationSessionAsync(
        string itemId, string seedPrompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seedPrompt))
            throw new ArgumentException("seed prompt is required", nameof(seedPrompt));

        FileLog.Write($"[DirectorImplSessionDriver] start: endpoint={_endpoint}, item={itemId}, seed={seedPrompt}");

        var req = new NewSessionRequest
        {
            RepoPath = _repoPath,
            Agent = "ClaudeCode",
            Type = "Implement",
            // The seed: built per source by the item's ISourceAdapter (issue #300). The
            // implementation-loop skill drives the whole DEV->QA loop for this item in its source
            // mode and prints the IMPL-LOOP-TERMINAL sentinel when it terminates.
            PrePrompt = seedPrompt,
        };

        var (ok, body, error) = await _client.CreateSessionAsync(_endpoint, req, ct);
        if (!ok || body is null || string.IsNullOrEmpty(body.SessionId))
        {
            FileLog.Write($"[DirectorImplSessionDriver] start FAILED: item={itemId}, error={error}");
            return (null, error ?? "director did not return a session id");
        }

        FileLog.Write($"[DirectorImplSessionDriver] started: item={itemId}, sid={body.SessionId}");
        return (body.SessionId, null);
    }

    public async Task<string?> ReadTranscriptAsync(string sessionId, CancellationToken ct)
    {
        var buffer = await _client.GetBufferAsync(_endpoint, sessionId, ct: ct);
        return buffer?.Text;
    }
}
