using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// ONE CHANGE TO BE RECORDED in <c>mail_desk_list_changes</c> (contract section 2): what kind of thing changed, what
/// was done, to what, the detail as JSON, who and why. A change is checked when it is made - an unknown word or a
/// blank reason throws, because every route refuses those first and a store reached without that check is a defect.
/// </summary>
public sealed record MailDeskChange
{
    public string Kind { get; }
    public string Action { get; }
    public string Subject { get; }
    public string Detail { get; }
    public MailDeskWho Who { get; }
    public string Why { get; }

    /// <summary>
    /// A change. <paramref name="kind"/> is one of <see cref="MailDeskWords.ChangeKinds"/>, <paramref name="action"/>
    /// one of <see cref="MailDeskWords.ChangeActions"/>, <paramref name="detail"/> a JSON text (<c>{}</c> when there is
    /// nothing more to say), and <paramref name="why"/> is required and never blank.
    /// </summary>
    public MailDeskChange(string kind, string action, string subject, string? detail, MailDeskWho who, string? why)
    {
        if (!MailDeskWords.IsOneOf(kind, MailDeskWords.ChangeKinds.All))
            throw new ArgumentException($"'{kind}' is not a kind of Mail Desk change; it is one of {MailDeskWords.InWords(MailDeskWords.ChangeKinds.All)}.", nameof(kind));
        if (!MailDeskWords.IsOneOf(action, MailDeskWords.ChangeActions.All))
            throw new ArgumentException($"'{action}' is not a Mail Desk change action; it is one of {MailDeskWords.InWords(MailDeskWords.ChangeActions.All)}.", nameof(action));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("A Mail Desk change names what it was made to: the subject may not be blank.", nameof(subject));
        Kind = kind;
        Action = action;
        Subject = subject.Trim();
        Detail = string.IsNullOrWhiteSpace(detail) ? "{}" : detail;
        Who = who ?? throw new ArgumentNullException(nameof(who));
        Why = MailDeskWho.RequireWhy(why);
    }

    /// <summary>Whether this change is to the rules document, and so carries a version before and after.</summary>
    public bool IsRulesChange => MailDeskWords.IsOneOf(Kind, MailDeskWords.ChangeKinds.Rules);

    internal MailDeskListChangeEntity ToEntity(string tenant, DateTime nowUtc, int? versionBefore, int? versionAfter) => new()
    {
        TenantId = tenant,
        AtUtc = nowUtc,
        Kind = Kind,
        Action = Action,
        Subject = Subject,
        Detail = Detail,
        VersionBefore = versionBefore,
        VersionAfter = versionAfter,
        WhoKind = Who.Kind,
        WhoId = Who.Id,
        WhoName = Who.Name,
        Why = Why,
    };
}
