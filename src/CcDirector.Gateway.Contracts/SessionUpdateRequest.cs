namespace CcDirector.Gateway.Contracts;

/// <summary>
/// PATCH /sessions/{sid} request body. Currently only supports renaming via
/// <see cref="Name"/>. Empty / whitespace-only string clears the custom name and
/// falls back to the default (repo folder name).
/// </summary>
public sealed class SessionUpdateRequest
{
    /// <summary>New custom display name, or empty/null to clear.</summary>
    public string? Name { get; set; }
}
