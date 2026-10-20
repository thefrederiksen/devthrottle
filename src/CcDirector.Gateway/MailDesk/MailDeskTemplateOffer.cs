using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Factory.Definition;

namespace CcDirector.Gateway.MailDesk;

/// <summary>What one offer of the Mail Desk template came to.</summary>
public enum MailDeskTemplateOfferOutcome
{
    /// <summary>The template was stored as a new draft of the account's <c>mail-desk</c> factory.</summary>
    Offered,

    /// <summary>This template version was offered before, or the newest version already holds it: nothing was stored.</summary>
    AlreadyOffered,

    /// <summary>The account's Mail Desk switch is off: nothing was looked at and nothing was stored.</summary>
    SwitchOff,

    /// <summary>Another writer saved a version of <c>mail-desk</c> between this offer's read and its save, and that
    /// version is not this template: nothing was stored. Not a defect - a race already resolved; the next offer reads
    /// the new newest version and decides again.</summary>
    LostRace,

    /// <summary>The definition check or the store refused the draft: nothing was stored. A defect to look at, said in
    /// <see cref="MailDeskTemplateOfferResult.Sentence"/> and the findings.</summary>
    Refused,
}

/// <summary>
/// One offer's answer. <paramref name="Version"/> is the definition version the offer stored, or the one that already
/// holds this template; null when the switch is off or the offer was refused. <paramref name="Findings"/> are the
/// check's findings (its notices on a stored draft, its errors on a refusal).
/// </summary>
public sealed record MailDeskTemplateOfferResult(
    MailDeskTemplateOfferOutcome Outcome,
    int? Version,
    string TemplateVersion,
    string Sentence,
    IReadOnlyList<DefinitionFinding> Findings);

/// <summary>
/// THE OFFER OF THE BUILT-IN MAIL DESK FACTORY (Mail Desk mission, design 5.3): when an account's Mail Desk switch is
/// on, the Gateway offers the embedded template (<see cref="MailDeskTemplate"/>) as a DRAFT of that account's
/// <c>mail-desk</c> factory. The owner publishes it - himself, or through a session in no factory that he tells to.
/// Nothing runs before that publish.
///
///   - No <c>mail-desk</c> definition yet: the template is its first draft, version 1.
///   - Any version of <c>mail-desk</c> already holds this template - offered by the Gateway or imported by a person -
///     nothing. The owner may have changed the draft since; it is never overwritten and the same template is never
///     offered twice.
///   - A newer template than any offered: a NEW draft above the newest version. Versions are only ever appended, so
///     whatever the owner wrote stays in the history and stays the published version until he publishes another.
///
/// It goes through THE SAME CHECKED SAVE a person's import does (<see cref="FactoryDefinitionEndpoints.CheckAndSave"/>):
/// the whole-definition check with the account's skills and machines, then the store's draft, stated against the
/// newest version as an import is. It is recorded as written by the Gateway
/// (<see cref="FactoryDefinitionStore.GatewayAuthorKind"/>, author "Mail Desk template"), with a note naming the
/// template version. It NEVER publishes.
///
/// Offering twice is harmless: the second finds the first. Two offers at once - the switch turned on while the
/// Gateway's start offers to every switched-on account - are kept apart by the store, which refuses the second as
/// stale. The loser then reads again: when the winner stored this template the answer is
/// <see cref="MailDeskTemplateOfferOutcome.AlreadyOffered"/>, otherwise <see cref="MailDeskTemplateOfferOutcome.LostRace"/>.
/// Neither is a defect, so neither is logged or answered as one.
/// </summary>
internal sealed class MailDeskTemplateOffer
{
    /// <summary>Who an offered draft is recorded as written by.</summary>
    public const string AuthorId = "Mail Desk template";

    /// <summary>How many versions one history page reads while looking for an earlier offer.</summary>
    private const int HistoryPage = FactoryDefinitionStore.MaxHistoryPage;

    private readonly FactoryDefinitionStore _store;
    private readonly FactoryDefinitionEndpoints.Account _account;
    private readonly Func<TenantId, bool> _mailDeskOn;
    private readonly Func<DateTime> _nowUtc;
    private readonly IReadOnlyDictionary<string, string> _template;
    private readonly string _templateVersion;

    /// <param name="store">The account's factory definitions.</param>
    /// <param name="account">The account's skills and machines, exactly as the definition routes check with.</param>
    /// <param name="mailDeskOn">The account's Mail Desk switch (mission 5.10; phase 1's <c>MailDeskSwitch.IsOn</c>).</param>
    /// <param name="nowUtc">The clock a stored draft is stamped with.</param>
    /// <param name="template">The template's files; null means the one embedded in this binary.</param>
    public MailDeskTemplateOffer(FactoryDefinitionStore store, FactoryDefinitionEndpoints.Account account,
        Func<TenantId, bool> mailDeskOn, Func<DateTime> nowUtc, IReadOnlyDictionary<string, string>? template = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _mailDeskOn = mailDeskOn ?? throw new ArgumentNullException(nameof(mailDeskOn));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _template = template ?? MailDeskTemplate.Files;
        _templateVersion = MailDeskTemplate.VersionOf(_template);
    }

    /// <summary>The template version this offer offers.</summary>
    public string TemplateVersion => _templateVersion;

    /// <summary>Offer the template to <paramref name="tenant"/> as a draft of its <c>mail-desk</c> factory, if its
    /// switch is on and this template version was not offered before.</summary>
    public MailDeskTemplateOfferResult Offer(TenantId tenant)
    {
        var shortVersion = MailDeskTemplate.Short(_templateVersion);
        FileLog.Write($"[MailDeskTemplateOffer] Offer: account={tenant.ToLogString()}, template={shortVersion}");
        try
        {
            if (!_mailDeskOn(tenant))
            {
                FileLog.Write($"[MailDeskTemplateOffer] Offer: account={tenant.ToLogString()} - the Mail Desk switch is off, nothing offered");
                return new MailDeskTemplateOfferResult(MailDeskTemplateOfferOutcome.SwitchOff, null, _templateVersion,
                    "The Mail Desk is not switched on for this account, so the Mail Desk factory was not offered.",
                    Array.Empty<DefinitionFinding>());
            }

            var standing = _store.Standing(tenant, MailDeskTemplate.Factory);
            if (VersionHoldingThisTemplate(tenant, standing) is { } held)
            {
                FileLog.Write($"[MailDeskTemplateOffer] Offer: account={tenant.ToLogString()} - template {shortVersion} is already in version {held}, nothing offered");
                return new MailDeskTemplateOfferResult(MailDeskTemplateOfferOutcome.AlreadyOffered, held, _templateVersion,
                    $"Version {held} of the {MailDeskTemplate.Factory} factory already holds Mail Desk template {shortVersion}, so it was not offered again.",
                    Array.Empty<DefinitionFinding>());
            }

            var after = standing.Newest?.Version ?? 0;
            var note = $"Offered by the Gateway: the Mail Desk template, version {shortVersion}. A draft; nothing runs until it is published.";
            var (check, write) = FactoryDefinitionEndpoints.CheckAndSave(_store, tenant, MailDeskTemplate.Factory, _template,
                after, FactoryDefinitionStore.GatewayAuthorKind, AuthorId, note, _account, _nowUtc);

            if (write is null)
            {
                var errors = check.Findings.Where(f => f.IsError).ToList();
                FileLog.Write($"[MailDeskTemplateOffer] Offer REFUSED by the check: account={tenant.ToLogString()}, template={shortVersion}, errors={errors.Count}: " +
                              string.Join(" | ", errors.Select(f => $"{f.File}:{f.Line} {f.Message}")));
                return new MailDeskTemplateOfferResult(MailDeskTemplateOfferOutcome.Refused, null, _templateVersion,
                    $"The Mail Desk template {shortVersion} was not offered: the definition check found {errors.Count} " +
                    $"{(errors.Count == 1 ? "error" : "errors")} in it.",
                    check.Findings);
            }
            if (write.Outcome == FactoryDefinitionOutcome.Stale)
                return AfterALostRace(tenant, shortVersion);
            if (!write.Ok)
            {
                FileLog.Write($"[MailDeskTemplateOffer] Offer REFUSED by the store: account={tenant.ToLogString()}, template={shortVersion}, outcome={write.Outcome}: {write.Refusal}");
                return new MailDeskTemplateOfferResult(MailDeskTemplateOfferOutcome.Refused, null, _templateVersion,
                    $"The Mail Desk template {shortVersion} was not offered: {write.Refusal}", check.Findings);
            }

            var stored = write.Version!.Version;
            FileLog.Write($"[MailDeskTemplateOffer] Offer: account={tenant.ToLogString()} - template {shortVersion} stored as draft version {stored} (after {after}), notices={check.Findings.Count}");
            return new MailDeskTemplateOfferResult(MailDeskTemplateOfferOutcome.Offered, stored, _templateVersion,
                $"The Mail Desk template {shortVersion} was offered as draft version {stored} of the {MailDeskTemplate.Factory} factory. " +
                "Nothing runs until it is published.",
                check.Findings);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MailDeskTemplateOffer] Offer FAILED: account={tenant.ToLogString()}, template={shortVersion}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// THE GATEWAY START'S OFFER (phase 3, section 10): offer the template to every account whose switch is on, so a
    /// newer template after a deploy is offered as a draft; an account whose switch is off is not looked at. One
    /// account's failure is logged and does not stop the others. Answers each switched-on account's result.
    /// </summary>
    /// <param name="accounts">Every account of the Gateway.</param>
    /// <param name="enterScope">Enters one account's scope for its offer (the tenant boundary); none when null.</param>
    public IReadOnlyList<(TenantId Tenant, MailDeskTemplateOfferResult? Result)> OfferToEverySwitchedOnAccount(IEnumerable<TenantId> accounts,
        Func<TenantId, IDisposable>? enterScope = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var results = new List<(TenantId, MailDeskTemplateOfferResult?)>();
        foreach (var tenant in accounts.Where(_mailDeskOn))
        {
            try
            {
                using var scope = enterScope?.Invoke(tenant);
                results.Add((tenant, Offer(tenant)));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MailDeskTemplateOffer] OfferToEverySwitchedOnAccount: account={tenant.ToLogString()} FAILED ({ex.GetType().Name}): {ex.Message}");
                results.Add((tenant, null));
            }
        }
        FileLog.Write($"[MailDeskTemplateOffer] OfferToEverySwitchedOnAccount: {results.Count} switched-on accounts, offered to {results.Count(r => r.Item2?.Outcome == MailDeskTemplateOfferOutcome.Offered)}");
        return results;
    }

    /// <summary>
    /// Another writer took the version this offer meant to store (review note B). Read again: if the winner stored this
    /// template, it was offered; if not, nothing is stored now and the next offer decides on the new newest version.
    /// </summary>
    private MailDeskTemplateOfferResult AfterALostRace(TenantId tenant, string shortVersion)
    {
        var standing = _store.Standing(tenant, MailDeskTemplate.Factory);
        if (VersionHoldingThisTemplate(tenant, standing) is { } held)
        {
            FileLog.Write($"[MailDeskTemplateOffer] Offer: account={tenant.ToLogString()} - another writer stored template {shortVersion} first, as version {held}; nothing offered");
            return new MailDeskTemplateOfferResult(MailDeskTemplateOfferOutcome.AlreadyOffered, held, _templateVersion,
                $"Version {held} of the {MailDeskTemplate.Factory} factory already holds Mail Desk template {shortVersion}, so it was not offered again.",
                Array.Empty<DefinitionFinding>());
        }
        FileLog.Write($"[MailDeskTemplateOffer] Offer: account={tenant.ToLogString()} - another writer saved version {standing.Newest?.Version} first and it is not template {shortVersion}; nothing stored, the next offer decides again");
        return new MailDeskTemplateOfferResult(MailDeskTemplateOfferOutcome.LostRace, null, _templateVersion,
            $"Another version of the {MailDeskTemplate.Factory} factory was saved while the Mail Desk template {shortVersion} was being offered, " +
            "so nothing was stored; the next offer will decide again.",
            Array.Empty<DefinitionFinding>());
    }

    /// <summary>
    /// The newest version whose files are this template, or null. Every version counts, whoever wrote it (review note
    /// A): a template the owner imported by hand is as offered as one the Gateway stored.
    /// </summary>
    private int? VersionHoldingThisTemplate(TenantId tenant, FactoryDefinitionStanding standing)
    {
        if (standing.Newest is null) return null;

        int? before = null;
        do
        {
            var page = _store.History(tenant, MailDeskTemplate.Factory, before, HistoryPage);
            foreach (var version in page.Versions)
                if (IsThisTemplate(tenant, version.Version)) return version.Version;
            before = page.NextBefore;
        } while (before is not null);
        return null;
    }

    private bool IsThisTemplate(TenantId tenant, int version)
    {
        var stored = _store.Find(tenant, MailDeskTemplate.Factory, version)
            ?? throw new InvalidOperationException(
                $"Version {version} of the {MailDeskTemplate.Factory} definition is listed but cannot be read.");
        return MailDeskTemplate.VersionOf(stored.Files) == _templateVersion;
    }
}
