using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>One numbered step of the welcome: its bold title and the sentence under it. <paramref name="Yours"/> marks the
/// step the contributor does themselves (drawn orange); <paramref name="Watch"/> marks the step that carries the small
/// "Watch your factory" drawing.</summary>
public sealed record ContributorWelcomeStep(int Number, string Title, string Text, bool Yours, bool Watch);

/// <summary>
/// THE CONTRIBUTOR'S WELCOME (Team implementation piece 4.2, devthrottle_internal#2799): the page a contributor lands on the
/// first time they sign in after joining a team.
///
/// THE WORDS LIVE HERE, ONCE, VERBATIM. They are the owner's approved words of 10 November 2026
/// (cc-consult writing/2026-11-09-contributor-welcome-page/shipped.md; "Yes, build it as shown", step two reading "It is
/// checked first."). Not one word, mark or sentence is changed or added; a test rebuilds shipped.md from these values and
/// compares it character for character. The client renders them as they are (rule 7).
///
/// SHOWN ONCE PER PERSON, NOT PER TEAM. The five steps describe the contributor's own app, which is the same on every team
/// and already shows every team they are a contributor on together (piece 4.1), so a second team would only repeat the
/// same words. So whether the person has seen it is one mark on their OWN account - a setting row in their personal tenant
/// (<see cref="TenantSettingKeys.ContributorWelcomeSeen"/>), which needs no new table. Until it is there, the contributor's
/// app opens on the welcome and lists it in the menu; once it is there, the app opens on Factories as before, and the
/// welcome is still reachable at its address (a mailed link) without being in the menu.
/// </summary>
public sealed class ContributorWelcome
{
    /// <summary>The welcome's address in the contributor's app.</summary>
    public const string PagePath = "/contributor/welcome";

    // ---------------------------------------------------------------- the approved words (shipped.md, verbatim)

    /// <summary>The page's heading.</summary>
    public const string Title = "Welcome to the team";

    /// <summary>The one line under the heading.</summary>
    public const string Lead = "A factory is a group of workers that builds something for you.";

    /// <summary>The five numbered steps, in order.</summary>
    public static readonly IReadOnlyList<ContributorWelcomeStep> Steps = new[]
    {
        new ContributorWelcomeStep(1, "You send a request.",
            "Say what you want in your own words, like \"Make me a website for my bakery.\"", Yours: true, Watch: false),
        new ContributorWelcomeStep(2, "It is checked first.",
            "The team's owner reads every request before any work starts.", Yours: false, Watch: false),
        new ContributorWelcomeStep(3, "The factory starts.",
            "You get an email to say so.", Yours: false, Watch: false),
        new ContributorWelcomeStep(4, "You can watch it work.",
            "Watch your factory shows the workers on your request as a drawing, live or as a replay.", Yours: false, Watch: true),
        new ContributorWelcomeStep(5, "You are told when it is finished.",
            "The result is waiting in your Inbox.", Yours: false, Watch: false),
    };

    /// <summary>The line below the steps. Pressing it asks this browser for permission to notify.</summary>
    public const string Notifications = "Allow notifications to hear on your phone or in your browser too.";

    /// <summary>The question above the button.</summary>
    public const string Question = "What should your factory build first?";

    /// <summary>The button's label.</summary>
    public const string ButtonLabel = "Send a request";

    /// <summary>The label beside step four's drawing, as the approved design shows it (welcome-mockup.html).</summary>
    public const string WatchLabel = "Watch your factory";

    /// <summary>What a person whose role gives them the whole app is told at the welcome's routes: it is not for them.</summary>
    internal const string NotForThisRole = "The welcome is only for a team's contributors.";

    private readonly TenantSettingsStore _settings;
    private readonly TenantRegistry _tenants;
    private readonly Func<DateTime> _utcNow;

    public ContributorWelcome(TenantSettingsStore settings, TenantRegistry tenants, Func<DateTime>? utcNow = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Whether the person has seen the welcome. A person with no account of their own on record cannot have.</summary>
    public bool HasSeen(string accountSubject)
    {
        if (_tenants.LookupBySubject(accountSubject) is not { } own)
            throw new InvalidOperationException("The welcome was asked about a person with no account of their own on record.");
        var seen = _settings.Get(own, TenantSettingKeys.ContributorWelcomeSeen) is { Length: > 0 };
        FileLog.Write($"[ContributorWelcome] HasSeen: tenant {own.ToLogString()} seen={seen}");
        return seen;
    }

    /// <summary>Marks the welcome seen on the person's own account. The first mark is kept; marking again changes nothing.</summary>
    public void MarkSeen(string accountSubject)
    {
        if (_tenants.LookupBySubject(accountSubject) is not { } own)
            throw new InvalidOperationException("The welcome was marked seen for a person with no account of their own on record.");
        var now = _utcNow();
        var stored = _settings.GetOrAdd(own, TenantSettingKeys.ContributorWelcomeSeen,
            () => DateTime.SpecifyKind(now, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture), now);
        FileLog.Write($"[ContributorWelcome] MarkSeen: tenant {own.ToLogString()} seen since {stored}");
    }

    /// <summary>Where "Send a request" opens: the contributor's Inbox with its write box on this team's Owner (piece 4.1).
    /// Piece 4.3 reworks that box; this address stays the way to it.</summary>
    public static string RequestHref(string teamId) => "/contributor/inbox?team=" + Uri.EscapeDataString(teamId);

    /// <summary>The page as the client draws it, for one team (its name is the label above the heading). Every field is
    /// listed, so nothing reaches the screen by being added elsewhere.</summary>
    public static object Page(string teamId, string teamLabel) => new
    {
        teamId,
        teamLabel,
        title = Title,
        lead = Lead,
        steps = Steps.Select(s => new { number = s.Number, title = s.Title, text = s.Text, yours = s.Yours, watch = s.Watch }).ToList(),
        watchLabel = WatchLabel,
        notifications = Notifications,
        question = Question,
        button = new { label = ButtonLabel, href = RequestHref(teamId) },
    };
}
