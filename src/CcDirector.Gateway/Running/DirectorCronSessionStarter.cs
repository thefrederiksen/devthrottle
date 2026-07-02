using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="ICronSessionStarter"/> (epic #479, #483, #503). Resolves the job's target
/// MACHINE to a Director via <see cref="IDirectorTargetResolver"/> (launching one if none is running)
/// and starts a session over the Gateway's existing <see cref="DirectorEndpointClient"/> using the
/// SAME session-create + seed-prompt path the Cockpit and the work-list runner use. No new Director
/// surface is introduced; an unresolvable target is reported as an error, not thrown.
/// </summary>
public sealed class DirectorCronSessionStarter : ICronSessionStarter
{
    private readonly DirectorEndpointClient _client;
    private readonly IDirectorTargetResolver _resolver;

    public DirectorCronSessionStarter(DirectorEndpointClient client, IDirectorTargetResolver resolver)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        var target = await _resolver.ResolveAsync(job.Target.Machine, ct);
        if (string.IsNullOrEmpty(target.Endpoint))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, machine={job.Target.Machine}, {target.Error}");
            return (null, target.DirectorId, target.Error ?? "could not resolve a director on the target machine");
        }

        FileLog.Write($"[DirectorCronSessionStarter] start: job={job.Id}, machine={job.Target.Machine}, director={target.DirectorId}, endpoint={target.Endpoint}, seed={job.Action.Seed}");

        var req = new NewSessionRequest
        {
            RepoPath = job.Action.RepoPath,
            Agent = "ClaudeCode",
            PrePrompt = job.Action.Seed,
        };

        var (ok, body, error) = await _client.CreateSessionAsync(target.Endpoint, req, ct);
        if (!ok || body is null || string.IsNullOrEmpty(body.SessionId))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, error={error}");
            return (null, target.DirectorId, error ?? "director did not return a session id");
        }

        FileLog.Write($"[DirectorCronSessionStarter] started: job={job.Id}, sid={body.SessionId}, director={target.DirectorId}");
        return (body.SessionId, target.DirectorId, null);
    }
}
