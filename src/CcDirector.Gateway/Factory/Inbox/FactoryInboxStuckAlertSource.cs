using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>One reason a factory's inbox is stuck, in words the owner reads, and since when.</summary>
public sealed record FactoryInboxStuckReason(string Cause, string Sentence, int Count, DateTime SinceUtc);

/// <summary>The detail of a stuck alert: the factory, its title, and every reason it is stuck, oldest first.</summary>
public sealed record FactoryInboxStuckDetail(string Factory, string Title, IReadOnlyList<FactoryInboxStuckReason> Reasons);

/// <summary>
/// THE FACTORY INBOX IS STUCK (Factory Inbox mission, phase 2, task D; MISSION section 5 "Nobody picks it up"). A message
/// the owner leaves a factory is meant to be received by its Secretary within minutes and decided by its Boss within hours.
/// When that does not happen the owner is told, here, once per factory, with the reason in words:
/// <list type="bullet">
/// <item>a message not received within <see cref="ReceiveWithin"/> - because no Director is running on the Boss's computer,
///   or, when one is, because no Secretary started;</item>
/// <item>a received message not handed on within <see cref="ReceiveWithin"/> whose Secretary has ended, so nobody holds it;</item>
/// <item>a bundle handed to the Boss and not decided within <see cref="DecideWithin"/> - read by a Boss or never read.</item>
/// </list>
///
/// COMPUTED FROM THE STORE, NO NEW TABLE. Like every alert source this is a pure mapping at read time: the inbox's own
/// messages and bundles, the registry, whether a session has ended, and whether a Director runs on a computer. The alert
/// exists only while the factory is stuck; once every message is received and every bundle decided it is gone, and the
/// thread itself is the record of what happened. Every sentence is written here, on the Gateway (CLAUDE.md rule 7).
/// </summary>
public sealed class FactoryInboxStuckAlertSource : IAlertSource
{
    /// <summary>A message not received by a Secretary within this long is stuck (MISSION section 5).</summary>
    public static readonly TimeSpan ReceiveWithin = TimeSpan.FromMinutes(15);

    /// <summary>A bundle not decided by the Boss within this long is stuck (MISSION section 5).</summary>
    public static readonly TimeSpan DecideWithin = TimeSpan.FromHours(2);

    /// <summary>The machine words for each reason, carried on the detail.</summary>
    public static class Causes
    {
        public const string NoDirector = "no-director";
        public const string NoSecretary = "no-secretary";
        public const string SecretaryEnded = "secretary-ended";
        public const string BossDidNotDecide = "boss-did-not-decide";
    }

    private readonly GatewayDatabase _db;
    private readonly Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> _factories;
    private readonly Func<TenantId, string, bool> _isEnded;
    private readonly Func<TenantId, string, bool> _directorRunsOn;

    /// <param name="db">The Gateway database holding the factory inbox tables.</param>
    /// <param name="factories">The account's registered factories, for each factory's title and its Boss's computer.</param>
    /// <param name="isEnded">Whether the Gateway has recorded a session as ended.</param>
    /// <param name="directorRunsOn">Whether a Director of the account is running on the named computer.</param>
    public FactoryInboxStuckAlertSource(GatewayDatabase db, Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> factories,
        Func<TenantId, string, bool> isEnded, Func<TenantId, string, bool> directorRunsOn)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _isEnded = isEnded ?? throw new ArgumentNullException(nameof(isEnded));
        _directorRunsOn = directorRunsOn ?? throw new ArgumentNullException(nameof(directorRunsOn));
    }

    public string Kind => AlertKinds.FactoryInboxStuck;

    public IReadOnlyList<AlertDto> List(TenantId tenant, DateTime nowUtc)
    {
        var receiveBy = nowUtc - ReceiveWithin;
        var decideBy = nowUtc - DecideWithin;
        var reasons = new Dictionary<string, List<FactoryInboxStuckReason>>(StringComparer.Ordinal);
        var registered = _factories(tenant);

        using (var ctx = _db.CreateContext(tenant))
        {
            // Not received in time, grouped per factory in the database: how many, and the oldest.
            var unreceived = ctx.FactoryInboxMessages.AsNoTracking()
                .Where(m => m.State == FactoryInboxWords.States.Sent && m.SentAtUtc <= receiveBy)
                .GroupBy(m => m.Factory)
                .Select(g => new { Factory = g.Key, Count = g.Count(), Oldest = g.Min(m => m.SentAtUtc) })
                .ToList();
            foreach (var u in unreceived)
            {
                var since = Utc(u.Oldest) + ReceiveWithin;
                var computer = BossComputer(registered.FirstOrDefault(f => Same(f.Factory, u.Factory)));
                Add(reasons, u.Factory, computer is null || !_directorRunsOn(tenant, computer)
                    ? new FactoryInboxStuckReason(Causes.NoDirector, NoDirectorSentence(u.Count, computer), u.Count, since)
                    : new FactoryInboxStuckReason(Causes.NoSecretary, NoSecretarySentence(u.Count), u.Count, since));
            }

            // Received, not handed on, and held by a Secretary that has ended: nobody holds them now.
            var held = ctx.FactoryInboxMessages.AsNoTracking()
                .Where(m => m.State == FactoryInboxWords.States.Received && m.BundleId == null && m.ClaimSessionId != null
                            && m.ReceivedAtUtc != null && m.ReceivedAtUtc <= receiveBy)
                .Select(m => new { m.Factory, Claimant = m.ClaimSessionId!, ReceivedAtUtc = m.ReceivedAtUtc!.Value })
                .ToList();
            var ended = held.Select(h => h.Claimant).Distinct(StringComparer.Ordinal)
                .Where(s => _isEnded(tenant, s)).ToHashSet(StringComparer.Ordinal);
            foreach (var g in held.Where(h => ended.Contains(h.Claimant)).GroupBy(h => h.Factory, StringComparer.Ordinal))
            {
                var count = g.Count();
                Add(reasons, g.Key, new FactoryInboxStuckReason(Causes.SecretaryEnded, SecretaryEndedSentence(count), count,
                    Utc(g.Min(h => h.ReceivedAtUtc)) + ReceiveWithin));
            }

            // Bundles with the Boss and not decided in time.
            var open = ctx.FactoryInboxBundles.AsNoTracking()
                .Where(b => b.ClosedAtUtc == null && b.HandedAtUtc <= decideBy)
                .Select(b => new { b.Factory, b.HandedAtUtc, Read = b.ReadBySessionId != null })
                .ToList();
            foreach (var g in open.GroupBy(b => b.Factory, StringComparer.Ordinal))
            {
                var count = g.Count();
                Add(reasons, g.Key, new FactoryInboxStuckReason(Causes.BossDidNotDecide,
                    BossDidNotDecideSentence(count, g.All(b => !b.Read)), count, Utc(g.Min(b => b.HandedAtUtc)) + DecideWithin));
            }
        }

        var alerts = reasons
            .Select(pair => Fold(pair.Key, registered.FirstOrDefault(f => Same(f.Factory, pair.Key))?.Title, pair.Value))
            .ToList();
        FileLog.Write($"[FactoryInboxStuckAlertSource] List: tenant={tenant.ToLogString()}, stuck factories={alerts.Count}");
        return alerts;
    }

    /// <summary>One stuck factory as the owner reads it. The summary is the oldest reason; the detail holds them all.</summary>
    internal static AlertDto Fold(string factory, string? title, IReadOnlyList<FactoryInboxStuckReason> reasons)
    {
        var ordered = reasons.OrderBy(r => r.SinceUtc).ThenBy(r => r.Cause, StringComparer.Ordinal).ToList();
        var name = string.IsNullOrWhiteSpace(title) ? factory : title.Trim();
        return new AlertDto
        {
            Id = AlertKinds.IdOf(AlertKinds.FactoryInboxStuck, factory),
            Kind = AlertKinds.FactoryInboxStuck,
            NeedsAnswer = true,
            Title = $"The Factory inbox of {name} is stuck",
            Summary = ordered[0].Sentence,
            Status = "Stuck",
            Tone = AlertTones.Bad,
            // It became stuck at the oldest reason, and it last changed when the newest reason began: a new reason makes
            // the alert new again for the owner's seen mark.
            CreatedAtUtc = ordered[0].SinceUtc,
            UpdatedAtUtc = ordered.Max(r => r.SinceUtc),
            ExpiresAtUtc = null,
            Detail = new FactoryInboxStuckDetail(factory, name, ordered),
        };
    }

    /// <summary>The computer the factory's Boss runs on: the Boss seat's own computer, or the factory's when the seat names
    /// none - the registry's meaning of an empty seat computer (the same rule the definition export writes). Null when the
    /// factory is not registered or names no Boss seat or computer.</summary>
    internal static string? BossComputer(RegisteredFactoryDto? factory)
    {
        if (factory is null || string.IsNullOrWhiteSpace(factory.BossSeat)) return null;
        var seat = factory.Seats.FirstOrDefault(s => Same(s.Id, factory.BossSeat.Trim()));
        var computer = string.IsNullOrWhiteSpace(seat?.Computer) ? factory.Computer : seat!.Computer;
        return string.IsNullOrWhiteSpace(computer) ? null : computer.Trim();
    }

    internal static string NoDirectorSentence(int count, string? computer)
        => computer is null
            ? $"{Messages(count)} not received in 15 minutes: the Gateway cannot tell which computer the Boss runs on, so no Secretary can start. Check the factory's Boss seat and its computer."
            : $"{Messages(count)} not received in 15 minutes: no Director is running on {computer}, the Boss's computer, so no Secretary can start. Start the Director on {computer}.";

    internal static string NoSecretarySentence(int count)
        => $"{Messages(count)} not received in 15 minutes: a Director is running on the Boss's computer, but no Secretary started to receive {(count == 1 ? "it" : "them")}.";

    internal static string SecretaryEndedSentence(int count)
        => $"{Messages(count)} received but not handed to the Boss: the Secretary that held {(count == 1 ? "it" : "them")} ended, and no Secretary has taken {(count == 1 ? "it" : "them")} since.";

    internal static string BossDidNotDecideSentence(int bundles, bool neverRead)
        => $"{(bundles == 1 ? "A bundle was" : bundles.ToString(CultureInfo.InvariantCulture) + " bundles were")} handed to the Boss more than 2 hours ago and not decided" +
           (neverRead ? ": no Boss has read " + (bundles == 1 ? "it." : "them.") : ": the Boss read " + (bundles == 1 ? "it" : "them") + " but has not decided every message.");

    private static string Messages(int count)
        => count == 1 ? "A message was" : count.ToString(CultureInfo.InvariantCulture) + " messages were";

    private static void Add(Dictionary<string, List<FactoryInboxStuckReason>> reasons, string factory, FactoryInboxStuckReason reason)
    {
        if (!reasons.TryGetValue(factory, out var list)) reasons[factory] = list = new List<FactoryInboxStuckReason>();
        list.Add(reason);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
