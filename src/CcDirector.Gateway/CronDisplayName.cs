namespace CcDirector.Gateway;

/// <summary>
/// The short name a schedule shows under its factory's header on the Schedule page (the owner's layout A,
/// 2026-10-09). Factory schedules are named "&lt;factory&gt; - &lt;seat's run&gt;" by convention, for example
/// "ClickFunnels Factory - Builder" or "M-Studio - AI Spend Watch - nightly"; under a header that already says the
/// factory, the row reads "Builder". Folded on the Gateway so no client parses a name.
///
/// It strips ONLY a leading segment that names the factory - its registered title, its id, or a segment ending in
/// "Factory" - and only on a schedule the record already puts in that factory. It never decides which factory a
/// schedule belongs to: that is the schedule's own factory field, and a schedule without one keeps its full name.
/// </summary>
public static class CronDisplayName
{
    private const string Separator = " - ";

    /// <summary>The schedule's name without its leading factory segment, or the full name when there is none.</summary>
    public static string ShortName(string name, string? factory, string? factoryTitle)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(factory))
            return name;

        var cut = name.IndexOf(Separator, StringComparison.Ordinal);
        if (cut <= 0)
            return name;
        var first = name[..cut].Trim();
        var rest = name[(cut + Separator.Length)..].Trim();
        if (rest.Length == 0)
            return name;

        var namesTheFactory =
            first.EndsWith("Factory", StringComparison.OrdinalIgnoreCase)
            // The id written out: "mindzie AI Reports" names mindzie-ai-reports, whose registered title is "M-AI Reports".
            || Letters(first) == Letters(factory)
            || (!string.IsNullOrWhiteSpace(factoryTitle)
                && first.StartsWith(factoryTitle.Trim(), StringComparison.OrdinalIgnoreCase));
        return namesTheFactory ? rest : name;
    }

    // Letters and digits only, lower case, so "mindzie AI Reports" and "mindzie-ai-reports" compare equal.
    private static string Letters(string text) =>
        new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
