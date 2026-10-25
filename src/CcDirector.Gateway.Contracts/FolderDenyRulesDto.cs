namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One settings file in the folder a session started in that holds deny rules (the owner's ruling of 24 October 2026:
/// the owner must always be able to approve). A fact the Director reads at launch and reports; the Gateway puts it in
/// words on the factory's page. The Director never changes the file.
/// </summary>
public sealed class FolderDenyRulesDto
{
    /// <summary>The settings file's full path on the session's machine.</summary>
    public string File { get; set; } = "";

    /// <summary>The file's deny rules, in file order. Empty when <see cref="Problem"/> is set.</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>Why the file's deny rules could not be read - it exists but is not readable, or not JSON - or null.</summary>
    public string? Problem { get; set; }
}
