namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// WHO MADE A MAIL DESK CHANGE (contract section 4): a person by the identifier their device carries, or a session by
/// its id and its name at that moment - the shape <c>FactoryPublisher</c> records. The name is kept so the history
/// reads in words after the session is gone; it never goes into a log line.
/// </summary>
public sealed record MailDeskWho
{
    /// <summary>The longest a reason may be. Long enough for a sentence or two, short enough to read in a list.</summary>
    public const int MaxWhyLength = 2000;

    /// <summary><c>person</c> or <c>session</c>.</summary>
    public string Kind { get; }

    /// <summary>The person's identifier, or the session's id.</summary>
    public string Id { get; }

    /// <summary>The session's name at the moment of the change; always null for a person.</summary>
    public string? Name { get; }

    private MailDeskWho(string kind, string id, string? name)
    {
        Kind = kind;
        Id = id;
        Name = name;
    }

    /// <summary>A person, by the identifier their device carries.</summary>
    public static MailDeskWho Person(string id) => new(MailDeskWords.WhoKinds.Person, RequireId(id), null);

    /// <summary>A session, by its id and - when it has one - its name at this moment.</summary>
    public static MailDeskWho Session(string id, string? name)
        => new(MailDeskWords.WhoKinds.Session, RequireId(id), string.IsNullOrWhiteSpace(name) ? null : name);

    /// <summary>
    /// A change's reason, checked: every change carries a why that is not blank. A blank why reaching a store is a
    /// route that forgot to refuse it first (the route answers <c>why_required</c>), so it throws rather than records
    /// a change nobody can account for.
    /// </summary>
    public static string RequireWhy(string? why)
    {
        if (WhyRefusal(why) is { } refusal) throw new ArgumentException(refusal, nameof(why));
        return why!.Trim();
    }

    /// <summary>Why <paramref name="why"/> cannot be a change's reason, or null when it can - for a route to refuse
    /// before it reaches a store.</summary>
    public static string? WhyRefusal(string? why)
    {
        if (string.IsNullOrWhiteSpace(why))
            return "A Mail Desk change needs a reason: 'why' is required and may not be blank.";
        var length = why.Trim().Length;
        return length > MaxWhyLength
            ? $"A Mail Desk change's reason takes at most {MaxWhyLength} characters; this one is {length}. Say it shorter."
            : null;
    }

    private static string RequireId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A Mail Desk change needs to know who made it: the identifier may not be blank.", nameof(id));
        return id.Trim();
    }
}
