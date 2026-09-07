using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The one definition of what a workspace id is, and the one way to make one from a display name.
///
/// It lives in the contracts assembly because BOTH ends need it and they must agree exactly: the Gateway
/// refuses an id that does not match, and the desktop mints an id from whatever the user typed. Two copies
/// of that rule would drift the moment either end changed, and the failure would be a name the user can
/// type and the server will not take.
/// </summary>
public static class WorkspaceSlug
{
    /// <summary>
    /// Lowercase slug ids, the same shape the workflow and skill catalogs use: letters, digits and
    /// dashes, starting with a letter or digit, 2 to 64 characters.
    ///
    /// The end anchor is \z and NOT $ deliberately. In .NET, $ also matches before a FINAL NEWLINE, so
    /// an id ending in a line break passes a $-anchored pattern - and that id would then reach a primary
    /// key and every log line that prints it. \z means the actual end of the string.
    /// </summary>
    public static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]{1,63}\z", RegexOptions.Compiled);

    /// <summary>The longest a workspace id may be.</summary>
    public const int MaxLength = 64;

    /// <summary>True when this is a valid workspace id.</summary>
    /// <param name="id">The candidate id.</param>
    public static bool IsValid(string? id) => !string.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id);

    /// <summary>
    /// Turn a display name into a valid workspace id. ALWAYS returns something
    /// <see cref="IsValid"/> accepts - a name made only of punctuation, or a single letter, still has to
    /// produce a usable id rather than a refusal the user cannot act on.
    /// </summary>
    /// <param name="name">The display name, as typed.</param>
    public static string From(string? name)
    {
        var slug = (name ?? "").Trim().ToLowerInvariant();
        slug = Regex.Replace(slug, "[^a-z0-9\\s-]", "");
        slug = Regex.Replace(slug, "\\s+", "-");
        slug = Regex.Replace(slug, "-+", "-");
        slug = slug.Trim('-');

        // A name with nothing usable in it ("!!!") and a one-character name ("a") both fail the pattern,
        // so both get the same prefix rather than being rejected back at somebody who typed a real name.
        if (slug.Length < 2)
            slug = string.IsNullOrEmpty(slug) ? "workspace" : "workspace-" + slug;

        if (slug.Length > MaxLength)
            slug = slug[..MaxLength].TrimEnd('-');

        return slug;
    }
}
