using System;

namespace CcDirector.ControlApi;

/// <summary>
/// A create-only write found a workspace already under that id (issue #2722).
///
/// It has its OWN type because it is not a failure - it is an answer, and the two callers that make
/// create-only writes want to do different things with it. The legacy import leaves the Gateway's copy
/// alone; the Save dialog turns it into an explicit overwrite decision by the user. Both of those need
/// to tell "somebody already has this name" apart from "the Gateway could not be reached", and an
/// InvalidOperationException carrying a sentence cannot be told apart from anything.
/// </summary>
public sealed class WorkspaceAlreadyExistsException : Exception
{
    /// <param name="id">The workspace id that was already taken.</param>
    public WorkspaceAlreadyExistsException(string id)
        : base($"A workspace with the id '{id}' is already on the Gateway.")
    {
        Id = id;
    }

    /// <summary>The id that was already taken.</summary>
    public string Id { get; }
}
