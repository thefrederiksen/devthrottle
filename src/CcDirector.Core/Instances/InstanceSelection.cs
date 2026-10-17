namespace CcDirector.Core.Instances;

/// <summary>
/// Which named instance a Director process runs as, decided from the <c>--instance</c> it was given and the
/// registry - or why it must not start at all (issue devthrottle_internal#2488).
///
/// WHY A REFUSAL AND NOT A FALLBACK. This used to fall back to the default instance whenever the name it was
/// given was not registered. A request for a NAMED instance is exactly the case where the caller has said it
/// does not want the default, so the fallback turned a typing mistake into running on the owner's real data:
/// on devlinux, 2026-10-17, a test build started as <c>--instance toolhealth2474</c> ran for three minutes on the
/// live default instance home, beside the live Director, and cleared its <c>sessions.json</c>. An unknown
/// explicit name now stops the process before it reads or writes any instance's data.
///
/// No <c>--instance</c> at all still means the default instance, as it always has.
/// </summary>
/// <param name="Slug">The instance to run as. Meaningless when <see cref="Refusal"/> is set.</param>
/// <param name="WasExplicit">True when the instance was named on the command line.</param>
/// <param name="DisplayName">The instance's label from the registry, when it has one.</param>
/// <param name="Refusal">Why the process must not start, or null when it may.</param>
public sealed record InstanceSelection(string Slug, bool WasExplicit, string? DisplayName, string? Refusal)
{
    /// <summary>True when the process may start as <see cref="Slug"/>.</summary>
    public bool MayStart => Refusal is null;

    /// <summary>
    /// Decide the instance. Pure: the registry is passed in, so the rule is testable without a disk.
    /// </summary>
    /// <param name="requestedSlug">The value of <c>--instance</c>, or null when none was given.</param>
    /// <param name="registered">Every instance in the registry.</param>
    public static InstanceSelection Choose(string? requestedSlug, IReadOnlyList<NamedInstance> registered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        var wasExplicit = requestedSlug is not null;
        var slug = InstanceContext.Normalize(requestedSlug);
        var match = registered.FirstOrDefault(i => string.Equals(i.Name, slug, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return new InstanceSelection(match.Name, wasExplicit, match.DisplayName, null);

        // The default instance always exists in meaning, even before its registry entry is written.
        if (string.Equals(slug, InstanceContext.DefaultSlug, StringComparison.OrdinalIgnoreCase))
            return new InstanceSelection(InstanceContext.DefaultSlug, wasExplicit, null, null);

        return new InstanceSelection(slug, wasExplicit, null, RefusalFor(requestedSlug!, registered));
    }

    /// <summary>The refusal for an instance list that could not be read while a named instance was asked
    /// for. Without the list nobody can say the name is real, so the process does not start.</summary>
    public static InstanceSelection Unreadable(string requestedSlug, string error) => new(
        InstanceContext.Normalize(requestedSlug), true, null,
        $"The Director was asked to start as the instance '{requestedSlug}', but the list of instances could not be read: {error}. "
        + "It did not start, so it cannot run on another instance's data.");

    private static string RefusalFor(string requestedSlug, IReadOnlyList<NamedInstance> registered)
    {
        var names = registered.Count == 0
            ? "none"
            : string.Join(", ", registered.Select(i => i.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return $"There is no Director instance named '{requestedSlug}'. Registered instances: {names}. "
               + "The Director did not start, so it cannot run on another instance's data. "
               + "Start it with one of those names, or create the instance first.";
    }
}
