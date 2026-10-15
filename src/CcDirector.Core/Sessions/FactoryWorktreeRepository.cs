using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>Which folder a seat's repository name means on this machine, or why it means none.</summary>
public sealed record FactoryWorktreeResolution(string? Path, string? Refusal)
{
    public bool Ok => Refusal is null;
}

/// <summary>
/// THE REPOSITORY A SEAT WORKS IN, RESOLVED ON THE MACHINE THAT STARTS IT (Factory Definition mission, phase 5,
/// design section 2). A definition names a seat's repository by NAME, never by folder: the folder is this machine's to
/// know, through its own registered repositories. A name that is not registered here refuses the start in the design's
/// words; nothing is started in a checkout instead. The seat then takes a fresh pooled slot of that repository
/// whatever the machine's pooled-worktree setting says, so the kit sits beside the worktree and never in it.
/// </summary>
public static class FactoryWorktreeRepository
{
    /// <summary>The refusal for a repository this machine has not registered.</summary>
    public static string NotRegistered(string seat, string repository, string machine) =>
        $"The seat {seat} works in the repository '{repository}', which is not registered on {machine}, so no session " +
        "was started. Register it there, or change the seat's repository in the definition.";

    /// <summary>
    /// The folder of the registered repository named <paramref name="repository"/> (its name, compared without regard
    /// to case, as a person reads it), or the refusal. Two registered repositories with that one name are refused too:
    /// choosing one of them would be a guess.
    /// </summary>
    public static FactoryWorktreeResolution Resolve(IReadOnlyList<RepositoryConfig> registered, string seat,
        string repository, string machine)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var matches = registered
            .Where(r => string.Equals(r.Name?.Trim(), repository.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        FileLog.Write($"[FactoryWorktreeRepository] Resolve: seat={seat}, repository={repository}, machine={machine}, matches={matches.Count}");
        if (matches.Count == 0)
            return new FactoryWorktreeResolution(null, NotRegistered(seat, repository, machine));
        if (matches.Count > 1)
            return new FactoryWorktreeResolution(null,
                $"The seat {seat} works in the repository '{repository}', and {machine} has {matches.Count} repositories " +
                $"registered under that name ({string.Join(", ", matches.Select(m => m.Path))}), so no session was started. " +
                "Rename all but one of them on that machine.");
        var path = matches[0].Path;
        if (!Directory.Exists(path))
            return new FactoryWorktreeResolution(null,
                $"The seat {seat} works in the repository '{repository}', which is registered on {machine} at {path}, " +
                "but that folder does not exist, so no session was started. Fix the registration on that machine.");
        return new FactoryWorktreeResolution(path, null);
    }
}
