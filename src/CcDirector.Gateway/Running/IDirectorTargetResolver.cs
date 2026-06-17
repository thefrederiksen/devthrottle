using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>The outcome of resolving a cron job's target machine to a runnable Director (#503).</summary>
/// <param name="Endpoint">The chosen Director's control endpoint (no trailing slash), or null if none.</param>
/// <param name="DirectorId">The chosen Director's id (for the run record), or null.</param>
/// <param name="Error">A human-readable reason when no Director could be resolved/launched, else null.</param>
public sealed record DirectorTargetResult(string? Endpoint, string? DirectorId, string? Error);

/// <summary>
/// Resolves a cron job's target MACHINE to a runnable Director (epic #479, #503): picks the first
/// reachable Director on that machine, and if none is running asks the launcher to start one and
/// waits (bounded) for it to register. Behind an interface so the firing engine is unit-testable
/// without a live registry/launcher. Production is <see cref="RegistryDirectorTargetResolver"/>.
/// </summary>
public interface IDirectorTargetResolver
{
    Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct);
}

/// <summary>
/// Production resolver. Reads the live Director list (the <see cref="DirectorRegistry"/>) and uses an
/// <see cref="IDirectorLauncher"/> to start a Director on demand. Uses wall-clock waits (small in
/// tests) for the launch poll, so it never depends on the engine's injected clock.
/// </summary>
public sealed class RegistryDirectorTargetResolver : IDirectorTargetResolver
{
    private readonly Func<IEnumerable<DirectorDto>> _listDirectors;
    private readonly IDirectorLauncher _launcher;
    private readonly TimeSpan _launchTimeout;
    private readonly TimeSpan _pollInterval;

    /// <param name="listDirectors">The live Director list (production: <c>registry.ListDirectors</c>).</param>
    /// <param name="launcher">Starts a Director on a machine when none is running.</param>
    public RegistryDirectorTargetResolver(
        Func<IEnumerable<DirectorDto>> listDirectors,
        IDirectorLauncher launcher,
        TimeSpan? launchTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _listDirectors = listDirectors ?? throw new ArgumentNullException(nameof(listDirectors));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _launchTimeout = launchTimeout ?? TimeSpan.FromSeconds(90);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    }

    public async Task<DirectorTargetResult> ResolveAsync(string machine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machine))
            return new DirectorTargetResult(null, null, "no target machine");

        var d = PickReachable(machine);
        if (d is not null)
            return new DirectorTargetResult(d.ControlEndpoint.TrimEnd('/'), d.DirectorId, null);

        // No Director running on the machine: ask its launcher to start one, then wait for it to register.
        FileLog.Write($"[RegistryDirectorTargetResolver] no Director on machine={machine}; asking launcher to start one");
        if (!await _launcher.StartAsync(machine, ct))
            return new DirectorTargetResult(null, null, $"no Director on '{machine}' and the launcher could not start one");

        var deadline = DateTime.UtcNow + _launchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_pollInterval, ct);
            d = PickReachable(machine);
            if (d is not null)
            {
                FileLog.Write($"[RegistryDirectorTargetResolver] launched Director registered on machine={machine}: {d.DirectorId}");
                return new DirectorTargetResult(d.ControlEndpoint.TrimEnd('/'), d.DirectorId, null);
            }
        }
        return new DirectorTargetResult(null, null, $"launched a Director on '{machine}' but none registered within {_launchTimeout.TotalSeconds:0}s");
    }

    /// <summary>First registered, reachable Director on the machine with a usable control endpoint.</summary>
    private DirectorDto? PickReachable(string machine) =>
        _listDirectors().FirstOrDefault(x =>
            string.Equals(x.MachineName, machine, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(x.ControlEndpoint) &&
            x.AdvertisedEndpointState != DirectorDto.EndpointStateUnreachableByName);
}
