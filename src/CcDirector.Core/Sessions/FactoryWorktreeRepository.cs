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
/// know, through its ONE repository list (<see cref="RepositoryRegistry"/>, which the root-folder scan and Browse both
/// feed - devthrottle#4301). A name this machine does not know refuses the start; nothing is started in a checkout
/// instead. The seat then takes a fresh pooled slot of that repository whatever the machine's pooled-worktree setting
/// says, so the kit sits beside the worktree and never in it.
/// </summary>
public static class FactoryWorktreeRepository
{
    /// <summary>The refusal for a repository name this machine does not know.</summary>
    public static string NotKnown(string seat, string repository, string machine) =>
        $"The seat {seat} works in the repository '{repository}', but {machine} does not know a repository of that " +
        $"name, so no session was started. Browsing to its folder once in the Director on {machine} adds it; or change " +
        "the seat's repository in the definition.";

    /// <summary>
    /// The folder of the repository named <paramref name="repository"/> in this machine's one list (its name, compared
    /// without regard to case, as a person reads it), or the refusal.
    ///
    /// <para>Two entries with that one name (the owner's ruling, devthrottle#4301): when exactly one of them was added
    /// by hand with Browse, that is the one meant - a person chose it, the scan merely found the other. Otherwise the
    /// start is refused and every folder is named: choosing among them would be a guess.</para>
    /// </summary>
    public static FactoryWorktreeResolution Resolve(IReadOnlyList<RepositoryConfig> known, string seat,
        string repository, string machine)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var matches = known
            .Where(r => string.Equals(r.Name?.Trim(), repository.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        FileLog.Write($"[FactoryWorktreeRepository] Resolve: seat={seat}, repository={repository}, machine={machine}, matches={matches.Count}");
        if (matches.Count == 0)
            return new FactoryWorktreeResolution(null, NotKnown(seat, repository, machine));
        if (matches.Count > 1)
        {
            var byHand = matches.Where(m => !m.FoundByScan).ToList();
            if (byHand.Count != 1)
                return new FactoryWorktreeResolution(null,
                    $"The seat {seat} works in the repository '{repository}', and {machine} knows {matches.Count} " +
                    $"repositories by that name ({string.Join(", ", matches.Select(m => m.Path))}), " +
                    (byHand.Count == 0 ? "none of them added by hand" : $"{byHand.Count} of them added by hand") +
                    ", so no session was started. Rename all but one of them on that machine.");
            FileLog.Write($"[FactoryWorktreeRepository] Resolve: {matches.Count} share the name; the one added by hand wins: {byHand[0].Path}");
            matches = byHand;
        }
        var path = matches[0].Path;
        if (!Directory.Exists(path))
            return new FactoryWorktreeResolution(null,
                $"The seat {seat} works in the repository '{repository}', which {machine} knows at {path}, " +
                "but that folder does not exist, so no session was started. Browse to the repository's folder in the " +
                "Director on that machine, or remove the entry there.");
        return new FactoryWorktreeResolution(path, null);
    }
}
