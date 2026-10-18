using System.Text.RegularExpressions;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// ONE SPELLING FOR AN ADDRESS AND FOR A SENDER PATTERN (Mail Desk mission, phase 1, contract section 3). Matching is
/// case-insensitive (design 5.6), so an address is held lower case: <c>Billing@Example.com</c> and
/// <c>billing@example.com</c> are one sender, and a list holding both would be a list holding one sender twice.
///
/// A PATTERN is an exact address or <c>*@domain</c>, and nothing else. Any other wildcard is refused rather than
/// guessed at, because phase 3 matches with plain code and a pattern it cannot read would silently match nothing.
/// </summary>
public static partial class MailDeskAddresses
{
    /// <summary>The longest an address may be (the limit email itself sets).</summary>
    public const int MaxLength = 320;

    // The part before the @: anything but space, @ and the wildcard. The domain: labels of letters, digits and hyphens
    // joined by dots, at least two labels.
    [GeneratedRegex(@"^[^\s@*]+@[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex Address();

    [GeneratedRegex(@"^\*@[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex Domain();

    /// <summary>
    /// Fold <paramref name="raw"/> to the one spelling of an exact address - a mailbox, never a pattern - and say
    /// whether it is one. <paramref name="field"/> names the field in the refusal.
    /// </summary>
    public static bool TryAddress(string? raw, string field, out string address, out string? refusal)
    {
        address = Fold(raw);
        refusal = null;
        if (address.Length == 0)
        {
            refusal = $"{field} is needed: give the mailbox's address, for example email@example.com";
            return false;
        }
        if (address.Length > MaxLength)
        {
            refusal = $"{field} takes at most {MaxLength} characters, and the one given is {address.Length}";
            return false;
        }
        if (!Address().IsMatch(address))
        {
            refusal = $"{field} '{raw}' is not an email address: give one address, such as email@example.com, with no wildcard";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Fold <paramref name="raw"/> to the one spelling of a sender pattern - an exact address or <c>*@domain</c> - and
    /// say whether it is one. <paramref name="field"/> names the field in the refusal.
    /// </summary>
    public static bool TryPattern(string? raw, string field, out string pattern, out string? refusal)
    {
        pattern = Fold(raw);
        refusal = null;
        if (pattern.Length == 0)
        {
            refusal = $"{field} is needed: give an address such as news@example.org, or a whole domain as *@example.org";
            return false;
        }
        if (pattern.Length > MaxLength)
        {
            refusal = $"{field} takes at most {MaxLength} characters, and the one given is {pattern.Length}";
            return false;
        }
        if (!Address().IsMatch(pattern) && !Domain().IsMatch(pattern))
        {
            refusal = $"{field} '{raw}' is not a sender pattern: give an exact address such as news@example.org, " +
                      "or a whole domain as *@example.org - no other wildcard is accepted";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="sender"/> matches <paramref name="pattern"/> (phase 3 contract, section 3): the exact
    /// address, or for <c>*@domain</c> an address whose domain is exactly that domain (not a subdomain of it), both
    /// without regard to case. An empty sender (the mailbox gave none) matches no pattern.
    /// </summary>
    public static bool Matches(string? pattern, string? sender)
    {
        var p = Fold(pattern);
        var s = Fold(sender);
        if (p.Length == 0 || s.Length == 0) return false;
        if (p.StartsWith("*@", StringComparison.Ordinal))
        {
            var at = s.LastIndexOf('@');
            return at > 0 && string.Equals(s[(at + 1)..], p[2..], StringComparison.Ordinal);
        }
        return string.Equals(p, s, StringComparison.Ordinal);
    }

    private static string Fold(string? raw) => (raw ?? "").Trim().ToLowerInvariant();
}
