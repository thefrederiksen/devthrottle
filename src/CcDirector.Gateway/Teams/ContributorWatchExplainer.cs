using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// THE EXPLAINER CARD BEFORE A CONTRIBUTOR'S FIRST WATCH (Watch your factory, version one, piece 6), and the one record
/// of whether a person has seen it.
///
/// THE WORDS ARE THE OWNER'S, approved word for word on 10 November 2026 (report eight). They live here and nowhere
/// else: every client shows the card from <see cref="FactoryWatchExplainerDto"/> on the watch view, never from a literal of
/// its own (critical rule 7). Do not reword them - a change is a new approval by the owner.
///
/// WHETHER TO SHOW IT is decided here too: the card is on the view until the person has pressed its button once. The
/// record is kept PER PERSON, on the person's own account (<see cref="TenantSettingKeys.WatchExplainerSeenAt"/>), so it
/// holds across every team they are a contributor on and across the Cockpit and the phone - seeing it once anywhere is
/// seeing it.
/// </summary>
internal sealed class ContributorWatchExplainer
{
    /// <summary>The card's three paragraphs, in order, exactly as the owner approved them.</summary>
    internal static readonly IReadOnlyList<string> Paragraphs = new[]
    {
        "Your factory does real work, and we take that work seriously. We also think it is fun to watch. So we drew it.",
        "Each character is one of the workers in your factory. What it does comes from the factory's own record. Only the walking and the timing are drawn for the screen. An hour of work takes seconds.",
        "We thought a drawing was the best way to show what goes on inside. We hope it helps.",
    };

    /// <summary>The card's button, and the button on a contributor's Factories page that opens the watch view.</summary>
    internal const string WatchLabel = "Watch your factory";

    private readonly TenantSettingsStore _settings;
    private readonly TenantRegistry _tenants;
    private readonly Func<DateTime> _utcNow;

    public ContributorWatchExplainer(TenantSettingsStore settings, TenantRegistry tenants, Func<DateTime>? utcNow = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The card for this person, or null once they have seen it. A new object on every call, so no caller can
    /// change the words another caller is given.</summary>
    public FactoryWatchExplainerDto? For(string personSubject)
    {
        if (HasSeen(personSubject))
            return null;
        return new FactoryWatchExplainerDto { Paragraphs = Paragraphs.ToList(), ButtonText = WatchLabel };
    }

    /// <summary>Whether this person has pressed the card's button, on any team and any surface.</summary>
    public bool HasSeen(string personSubject) => _settings.Get(OwnAccount(personSubject), TenantSettingKeys.WatchExplainerSeenAt) is not null;

    /// <summary>Records that this person has seen the card. The first time is kept: a second press, from another tab or
    /// the phone, never moves it.</summary>
    public void MarkSeen(string personSubject)
    {
        var now = DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc);
        var tenant = OwnAccount(personSubject);
        var stored = _settings.GetOrAdd(tenant, TenantSettingKeys.WatchExplainerSeenAt,
            () => now.ToString("O", CultureInfo.InvariantCulture), now);
        FileLog.Write($"[ContributorWatchExplainer] MarkSeen: account {tenant.ToLogString()} seen since {stored}");
    }

    /// <summary>The person's own account. Every caller a team route admits was resolved FROM that account
    /// (<c>TeamEndpoints.ResolveCaller</c>), so a person with none here means the route was miswired.</summary>
    private Core.Tenancy.TenantId OwnAccount(string personSubject)
    {
        if (string.IsNullOrWhiteSpace(personSubject))
            throw new ArgumentException("A person is required to read or record the explainer card.", nameof(personSubject));
        return _tenants.LookupBySubject(personSubject)
               ?? throw new InvalidOperationException("The explainer card was asked about a person with no account of their own - the team gate was not run.");
    }
}
