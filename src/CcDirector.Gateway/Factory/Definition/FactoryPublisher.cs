using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// WHO PUBLISHED A VERSION OF A FACTORY'S DEFINITION (Factory Definition mission, phase 6c): a person, or a session in
/// no factory, with the session's name as it was at the moment it published.
///
/// The owner's ruling of 16 October 2026: "Factories are created by agents as long as they have user approval." The
/// owner creates and publishes a factory by telling a session what to make; the approval is his word in that session.
/// What stays forbidden is a factory's OWN session publishing - the rule of the teams-and-agents design, R7: "Factory
/// agents can be written by large language models, but it's the user driving it." So a publish is recorded with who
/// did it, and the history says it in words.
/// </summary>
/// <param name="Kind"><see cref="FactoryMemoryAuthorKinds.Person"/> or <see cref="FactoryMemoryAuthorKinds.Session"/>.</param>
/// <param name="Id">The person's identifier, or the session's id. Null when it is not known.</param>
/// <param name="Name">The session's name; null for a person and for a session with no name.</param>
public sealed record FactoryPublisher(string Kind, string? Id, string? Name = null)
{
    /// <summary>A person, by the identifier their device carries.</summary>
    public static FactoryPublisher Person(string? id) => new(FactoryMemoryAuthorKinds.Person, id);

    /// <summary>A session in no factory, by its id and its name.</summary>
    public static FactoryPublisher Session(string id, string? name) =>
        new(FactoryMemoryAuthorKinds.Session, id, string.IsNullOrWhiteSpace(name) ? null : name.Trim());

    /// <summary>The name a version records: the session's name, never a person's (a person has none here).</summary>
    public string? RecordedName => Kind == FactoryMemoryAuthorKinds.Session && !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : null;

    /// <summary>Who it was, in words, for a log line or the history: "a person (id)", "session id (name)".</summary>
    public string Text => Words(Kind, Id, Name);

    /// <summary>Who it was, as a registry row or a trigger records it ("definition v3 published by ..."): a person by
    /// their identifier, exactly as before phase 6c; a session by <see cref="Text"/>.</summary>
    public string RowText => Kind == FactoryMemoryAuthorKinds.Session ? Text : Id ?? "a person";

    /// <summary>
    /// Who published a version, in words, from what the version recorded. A version published before the kind was
    /// recorded has no kind, and every one of those was published by a person, because nobody else could. The
    /// identifier and the name are never cut short.
    /// </summary>
    public static string Words(string? kind, string? id, string? name)
    {
        var known = !string.IsNullOrWhiteSpace(id);
        if (kind == FactoryMemoryAuthorKinds.Session)
        {
            var session = known ? $"session {id}" : "a session";
            return string.IsNullOrWhiteSpace(name) ? session : $"{session} ({name})";
        }
        return known ? $"a person ({id})" : "a person";
    }
}
