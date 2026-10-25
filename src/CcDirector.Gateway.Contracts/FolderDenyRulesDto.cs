namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One settings file a session's agent reads that refused something outright when the session started (the owner's
/// rulings of 24 and 25 October 2026: the owner must always be able to approve, everywhere). Before the agent starts the
/// Director turns each such rule into the agent's ask form and records it here; a file it could not change - managed
/// settings, a file it cannot write, one a rewrite would damage - is recorded with why. The Gateway puts it in words on
/// the factory's page.
/// </summary>
public sealed class FolderDenyRulesDto
{
    /// <summary>The settings file's full path on the session's machine.</summary>
    public string File { get; set; } = "";

    /// <summary>The rules that refused outright, in file order: turned into ask rules when <see cref="Rewritten"/>, still
    /// refusing otherwise. Empty when the file could not even be read.</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>True when the Director turned <see cref="Rules"/> into ask rules in the file before the agent started.</summary>
    public bool Rewritten { get; set; }

    /// <summary>Why the file was left as it was - managed settings, unreadable, unwritable, or a rewrite would lose
    /// something - or null when it was rewritten.</summary>
    public string? Problem { get; set; }
}
