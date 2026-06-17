using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Resolves a Director id to its control endpoint + machine name, behind an interface so the cron
/// work-list trigger (epic #479, #484) is unit-testable without populating a real
/// <see cref="DirectorRegistry"/>. Production is <see cref="RegistryDirectorResolver"/>.
/// </summary>
public interface ICronDirectorResolver
{
    /// <summary>
    /// Resolve <paramref name="directorId"/>. Returns (null, null) when no such Director is
    /// registered or it has no control endpoint.
    /// </summary>
    (string? endpoint, string? machineName) Resolve(string directorId);
}

/// <summary>Production resolver over the Gateway's <see cref="DirectorRegistry"/>.</summary>
public sealed class RegistryDirectorResolver : ICronDirectorResolver
{
    private readonly DirectorRegistry _registry;

    public RegistryDirectorResolver(DirectorRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public (string? endpoint, string? machineName) Resolve(string directorId)
    {
        var director = _registry.Get(directorId);
        if (director is null || string.IsNullOrEmpty(director.ControlEndpoint))
            return (null, null);
        return (director.ControlEndpoint.TrimEnd('/'), director.MachineName);
    }
}
