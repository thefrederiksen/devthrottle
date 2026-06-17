using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="ICronSessionStarter"/> (epic #479, #483). Resolves the job's target Director
/// from the <see cref="DirectorRegistry"/> and starts a session over the Gateway's existing
/// <see cref="DirectorEndpointClient"/> using the SAME session-create + seed-prompt path the Cockpit
/// and the work-list runner use (<see cref="NewSessionRequest.PrePrompt"/> is the seed channel). No
/// new Director surface is introduced; an unknown/offline target is reported as an error, not thrown.
/// </summary>
public sealed class DirectorCronSessionStarter : ICronSessionStarter
{
    private readonly DirectorRegistry _registry;
    private readonly DirectorEndpointClient _client;

    public DirectorCronSessionStarter(DirectorRegistry registry, DirectorEndpointClient client)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<(string? sessionId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        var directorId = job.Target.DirectorId;
        var director = _registry.Get(directorId);
        if (director is null)
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, no such director={directorId}");
            return (null, $"target director not registered: {directorId}");
        }

        var endpoint = director.ControlEndpoint.TrimEnd('/');
        FileLog.Write($"[DirectorCronSessionStarter] start: job={job.Id}, director={directorId}, endpoint={endpoint}, seed={job.Action.Seed}");

        var req = new NewSessionRequest
        {
            RepoPath = job.Action.RepoPath,
            Agent = "ClaudeCode",
            Type = "Implement",
            PrePrompt = job.Action.Seed,
        };

        var (ok, body, error) = await _client.CreateSessionAsync(endpoint, req, ct);
        if (!ok || body is null || string.IsNullOrEmpty(body.SessionId))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, error={error}");
            return (null, error ?? "director did not return a session id");
        }

        FileLog.Write($"[DirectorCronSessionStarter] started: job={job.Id}, sid={body.SessionId}");
        return (body.SessionId, null);
    }
}
