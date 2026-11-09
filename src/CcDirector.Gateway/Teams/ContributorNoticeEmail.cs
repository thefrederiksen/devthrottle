using System.Net;

namespace CcDirector.Gateway.Teams;

/// <summary>One email to a person, every word finished: the subject, the plain text and the HTML.</summary>
public sealed record PersonEmail(string Subject, string Text, string Html);

/// <summary>
/// THE WORDS OF A CONTRIBUTOR'S "SOMETHING IS WAITING" EMAIL (Team implementation piece 4.4, devthrottle_internal#2801;
/// security review Risk 8; design.html: "The email only says something is waiting").
///
/// THE EMAIL CARRIES NO CONTENT, AND THESE METHODS CANNOT BE HANDED ANY. They take the names of the teams something is
/// waiting in and the link to the person's Inbox - nothing else. No report, title, question, factory, sentence, sender,
/// count or address is a parameter, so none can be put in the email by any caller. What was sent stays on DevThrottle,
/// behind the person's own sign-in.
/// </summary>
public static class ContributorNoticeEmail
{
    /// <summary>The subject of every one of these emails: the same whatever arrived and however many.</summary>
    public const string Subject = "Something is waiting for you on DevThrottle";

    internal const string WhatStaysLine = "This email does not say what it is: what was sent to you stays on DevThrottle, where only you can open it once you are signed in.";
    internal const string ChoiceLine = "You get this email because of how you chose to be told. To change it - right away, once a day, or never - open Settings on DevThrottle.";

    /// <summary>The "right away" email: one thing is waiting in <paramref name="teamName"/>.</summary>
    public static PersonEmail RightAway(string teamName, string inboxLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxLink);
        var lead = $"Something new is waiting for you in the team {Quote(teamName)}.";
        return Build(lead, WebUtility.HtmlEncode(lead), inboxLink);
    }

    /// <summary>The daily email: something is waiting in each of <paramref name="teamNames"/>, which is not empty.</summary>
    public static PersonEmail Daily(IReadOnlyList<string> teamNames, string inboxLink)
    {
        ArgumentNullException.ThrowIfNull(teamNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxLink);
        var names = teamNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            throw new ArgumentException("The daily email names at least one team; with nothing waiting no email is sent.", nameof(teamNames));

        var quoted = names.Select(Quote).ToList();
        var lead = names.Count == 1
            ? $"Something new is waiting for you in the team {quoted[0]}."
            : $"Something new is waiting for you in these teams: {JoinNames(quoted)}.";
        var leadHtml = WebUtility.HtmlEncode(lead);
        return Build(lead, leadHtml, inboxLink);
    }

    private static PersonEmail Build(string lead, string leadHtml, string inboxLink)
    {
        var text = string.Join("\n", new[]
        {
            lead,
            "",
            $"Open your Inbox to see it: {inboxLink}",
            "",
            WhatStaysLine,
            "",
            ChoiceLine,
        });
        var link = WebUtility.HtmlEncode(inboxLink);
        var html = string.Join("\n", new[]
        {
            $"<p>{leadHtml}</p>",
            $"<p><a href=\"{link}\">Open your Inbox</a> to see it.</p>",
            $"<p>{WebUtility.HtmlEncode(WhatStaysLine)}</p>",
            $"<p>{WebUtility.HtmlEncode(ChoiceLine)}</p>",
        });
        return new PersonEmail(Subject, text, html);
    }

    private static string Quote(string name) => $"\"{name.Trim()}\"";

    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count == 2 ? $"{names[0]} and {names[1]}" : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
