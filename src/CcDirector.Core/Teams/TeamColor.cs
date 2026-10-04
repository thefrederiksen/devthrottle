using System.Text;

namespace CcDirector.Core.Teams;

/// <summary>One chip colour: its fill, its text and its outline, as #RRGGBB.</summary>
public sealed record TeamChipColor(string Background, string Foreground, string Border);

/// <summary>
/// The colour of a team's chip in the title bar (screen D2). The Gateway stores no colour for a team, so the
/// colour is DERIVED from the team id over a fixed palette: the same team gets the same colour on every
/// Director, on every computer, in every run, with nothing to store or keep in sync.
///
/// The hash is FNV-1a over the id's UTF-8 bytes, written out here rather than <see cref="string.GetHashCode()"/>,
/// which .NET randomises per process - that would give one team a different colour on each Director.
/// The personal account uses <see cref="Default"/>, which is not in the palette, so no team can look personal.
/// </summary>
public static class TeamColor
{
    /// <summary>The personal account's chip: the neutral grey of the Director's own controls.</summary>
    public static readonly TeamChipColor Default = new("#2D2D30", "#CCCCCC", "#3F3F46");

    /// <summary>The team palette: eight dark fills with light text, readable on the Director's dark toolbar.</summary>
    public static readonly IReadOnlyList<TeamChipColor> Palette = new[]
    {
        new TeamChipColor("#1D3357", "#9CC2FF", "#2C5391"), // blue
        new TeamChipColor("#2A2140", "#D7C6FF", "#4B3A78"), // purple
        new TeamChipColor("#132A1C", "#6EE79B", "#1F4A30"), // green
        new TeamChipColor("#2E2410", "#F5C76B", "#5A4517"), // amber
        new TeamChipColor("#33161F", "#F4A6BC", "#6A2A3D"), // rose
        new TeamChipColor("#0F2B2E", "#7FDCD9", "#1E5458"), // teal
        new TeamChipColor("#33200F", "#FDBA74", "#6B3F17"), // orange
        new TeamChipColor("#1C1F45", "#A5B4FC", "#343A80"), // indigo
    };

    /// <summary>The chip colour for a team id; null (the personal account) gets <see cref="Default"/>.</summary>
    public static TeamChipColor For(string? teamId)
    {
        if (teamId is null)
            return Default;
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id cannot be blank; use null for the personal account.", nameof(teamId));
        return Palette[(int)(Fnv1a(teamId.Trim().ToLowerInvariant()) % (uint)Palette.Count)];
    }

    private static uint Fnv1a(string text)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
