using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// THE NAME CHECK (Factory Control, step 5; the owner's ruling "Boss, no names": "A factory has a boss, not a CEO ...
/// there are no names that are used anywhere"). Folded once here; the command line, the Factory Manager and the
/// Factories screen show its words as given.
///
/// THE RULE, which needs no list of first names. A seat is flagged when:
///   - CEO: its id, name or role contains the word "CEO" (a whole word, any case); or
///   - NOT A ROLE WORD: its name is not a role word of its factory. A factory's role words are the registered roles of
///     its seats, and "Boss" for its boss seat. So "Savings Engineer" with role "Savings Engineer" passes, "Boss" on the
///     boss seat passes, and "Nora Hale" with role "Boss" is flagged.
///
/// What it cannot see: a role that is itself a person's name ("Nora" named "Nora") passes - the rule checks that names
/// are role words, and cannot tell a word from a name.
/// </summary>
public static class FactoryNameCheck
{
    public const string KindCeo = "ceo";
    public const string KindNotARoleWord = "not-a-role-word";

    public const string RuleText =
        "A seat is flagged when its id, name or role says CEO (a factory has a boss, not a CEO), or when its name is not "
        + "a role word of its factory - one of the roles its seats are registered with, or Boss for the boss seat. Seats "
        + "are role words only, never a person's name.";

    private static readonly Regex Ceo = new(@"(^|[^A-Za-z0-9])ceo([^A-Za-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The check over every factory given.</summary>
    public static FactoryNameCheckDto Check(IReadOnlyList<RegisteredFactoryDto> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        var findings = factories.SelectMany(FindingsOf).ToList();
        FileLog.Write($"[FactoryNameCheck] Check: factories={factories.Count}, findings={findings.Count}");
        return new FactoryNameCheckDto
        {
            Count = findings.Count,
            FactoriesChecked = factories.Count,
            RuleText = RuleText,
            PassText = findings.Count == 0 ? "Every factory passes." : null,
            Findings = findings,
        };
    }

    /// <summary>One factory's findings, in its seats' order.</summary>
    public static IEnumerable<FactoryNameFindingDto> FindingsOf(RegisteredFactoryDto factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var roles = factory.Seats.Select(s => s.Role.Trim()).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in factory.Seats)
        {
            var isBoss = factory.BossSeat is not null && string.Equals(seat.Id.Trim(), factory.BossSeat.Trim(), StringComparison.OrdinalIgnoreCase);
            if (SaysCeo(seat.Id) || SaysCeo(seat.Name) || SaysCeo(seat.Role))
            {
                yield return Finding(factory, seat, KindCeo,
                    $"{factory.Title}: the seat '{seat.Id}' (named '{seat.Name}', role '{seat.Role}') says CEO. A factory has a boss, not a CEO.");
                continue;
            }
            var name = seat.Name.Trim();
            var isRoleWord = roles.Contains(name)
                             || (isBoss && string.Equals(name, FactoriesScreenFold.BossRoleWord, StringComparison.OrdinalIgnoreCase));
            if (!isRoleWord)
                yield return Finding(factory, seat, KindNotARoleWord,
                    $"{factory.Title}: the seat '{seat.Id}' is named '{seat.Name}', which is not a role word of this factory "
                    + $"(its role is {(seat.Role.Trim().Length == 0 ? "not set" : seat.Role.Trim())}). Name it by its role.");
        }
    }

    /// <summary>Whether the text holds the word CEO, in any case.</summary>
    public static bool SaysCeo(string? text) => !string.IsNullOrEmpty(text) && Ceo.IsMatch(text);

    private static FactoryNameFindingDto Finding(RegisteredFactoryDto f, RegisteredFactorySeatDto seat, string kind, string text) => new()
    {
        Factory = f.Factory,
        FactoryTitle = f.Title,
        SeatId = seat.Id,
        Kind = kind,
        Text = text,
    };
}
