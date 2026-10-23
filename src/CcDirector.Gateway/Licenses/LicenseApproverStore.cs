using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Licenses;

/// <summary>Which machine and browser profile approve the sign-ins of one Claude account (contract section 8.3).</summary>
/// <param name="AccountEmail">The account, lower-cased.</param>
/// <param name="MachineName">The approving machine, as its Director reports it.</param>
/// <param name="OsUser">The operating system user on that machine whose Director approves.</param>
/// <param name="BrowserProfile">The browser profile there that is signed in to claude.ai as the account.</param>
public sealed record LicenseApprover(string AccountEmail, string MachineName, string OsUser, string BrowserProfile);

/// <summary>The answer to setting an approver: the approver as stored, or the word and the sentence of the refusal.</summary>
public sealed record LicenseApproverSet(LicenseApprover? Approver, string? RefusalWord, string? RefusalText)
{
    public bool Accepted => Approver is not null;
}

/// <summary>
/// WHO APPROVES THE SIGN-INS OF EACH ACCOUNT, per DevThrottle account (License Switching, contract section 8.3). It is
/// the OWNER'S setting, made on the Licenses page: a machine that cannot approve a sign-in in a browser of its own asks
/// the Gateway, and the Gateway asks the machine named here.
///
/// ONE VALUE PER ACCOUNT, in the per-tenant settings (<see cref="TenantSettingKeys.LicenseApprovers"/>), so there is
/// no table of its own. Every call names its tenant and reads and writes through <see cref="TenantSettingsStore"/>,
/// which a tenant can never read another tenant's row through.
///
/// WHAT IS KEPT: an account email, a machine name, an operating system user and the name of a browser profile. Nothing
/// from a sign-in, and never the address a sign-in is approved at or the approval itself - those are only ever in
/// memory while <see cref="LicenseApprovalRelay"/> passes them on.
/// </summary>
public sealed class LicenseApproverStore
{
    /// <summary>The most approvers one DevThrottle account keeps: one per Claude account, and an account's machines
    /// hold at most <see cref="LicenseUsageIngest.MaxHoldings"/> of those each.</summary>
    public const int MaxApprovers = LicenseUsageIngest.MaxHoldings;

    public const int MaxNameLength = 128;

    /// <summary>A browser profile's name: it is handed to a command on the approving machine, so it is letters,
    /// digits, spaces, dots, underscores and hyphens, starting with a letter or a digit.</summary>
    public static readonly Regex BrowserProfileShape = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly TenantSettingsStore _settings;

    public LicenseApproverStore(TenantSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Every approver of this account, in account email order.</summary>
    public IReadOnlyList<LicenseApprover> List(TenantId tenant)
    {
        var all = Read(tenant);
        FileLog.Write($"[LicenseApproverStore] List: tenant={tenant.ToLogString()}, count={all.Count}");
        return all;
    }

    /// <summary>The approver of one account; null when none is set.</summary>
    public LicenseApprover? Find(TenantId tenant, string? accountEmail)
    {
        var email = LicenseStore.NormalizeEmail(accountEmail);
        var found = Read(tenant).FirstOrDefault(a => a.AccountEmail == email);
        FileLog.Write($"[LicenseApproverStore] Find: tenant={tenant.ToLogString()}, account={email}, found={found is not null}");
        return found;
    }

    /// <summary>
    /// Set the approver of one account, replacing the one it had. Refused - and nothing stored - when a value is
    /// missing or too long, when the browser profile's name is not a profile name, or when the account would be the
    /// one past <see cref="MaxApprovers"/>. The machine need not have reported yet: the owner may set it first.
    /// </summary>
    public LicenseApproverSet Set(TenantId tenant, string? accountEmail, string? machineName, string? osUser, string? browserProfile,
        DateTime nowUtc)
    {
        var email = LicenseStore.NormalizeEmail(accountEmail);
        var machine = (machineName ?? "").Trim();
        var user = (osUser ?? "").Trim();
        var profile = (browserProfile ?? "").Trim();
        // Nothing the caller sent is written to the log before it has been checked: a line break in a name would
        // otherwise write what looks like a second line of the log.
        if (!IsAccountEmail(email))
            return Refuse("invalid_account", "accountEmail is the email of a Claude account.");
        if (machine.Length is 0 or > MaxNameLength || machine.Any(NotReadable))
            return Refuse("invalid_machine", $"machineName is the name of the approving machine, at most {MaxNameLength} characters, with no control character.");
        if (user.Length is 0 or > MaxNameLength || user.Any(NotReadable))
            return Refuse("invalid_machine", $"osUser is the operating system user on the approving machine, at most {MaxNameLength} characters, with no control character.");
        if (!BrowserProfileShape.IsMatch(profile))
            return Refuse("invalid_browser_profile",
                "browserProfile is the name of a browser profile on the approving machine: letters, digits, spaces, dots, underscores and hyphens, at most 64 characters.");

        FileLog.Write($"[LicenseApproverStore] Set: tenant={tenant.ToLogString()}, account={email}, machine={machine}, user={user}");
        var approver = new LicenseApprover(email, machine, user, profile);
        lock (_gate)
        {
            var all = Read(tenant).ToList();
            var at = all.FindIndex(a => a.AccountEmail == email);
            if (at < 0 && all.Count >= MaxApprovers)
                return Refuse("too_many_approvers", $"An account keeps at most {MaxApprovers} approvers.");
            if (at < 0) all.Add(approver);
            else all[at] = approver;
            Write(tenant, all, nowUtc);
        }
        FileLog.Write($"[LicenseApproverStore] Set: stored the approver of {email}");
        return new LicenseApproverSet(approver, null, null);
    }

    /// <summary>Remove the approver of one account. False when none was set.</summary>
    public bool Remove(TenantId tenant, string? accountEmail, DateTime nowUtc)
    {
        var email = LicenseStore.NormalizeEmail(accountEmail);
        bool removed;
        lock (_gate)
        {
            var all = Read(tenant).ToList();
            removed = all.RemoveAll(a => a.AccountEmail == email) > 0;
            if (removed)
                Write(tenant, all, nowUtc);
        }
        FileLog.Write($"[LicenseApproverStore] Remove: tenant={tenant.ToLogString()}, account={email}, removed={removed}");
        return removed;
    }

    /// <summary>An account email as every Licenses route takes it: one "@" with text on both sides, at most 320
    /// characters, and no white space, control character, line or paragraph separator, or format character. It is
    /// compared and stored lower-cased.</summary>
    public static bool IsAccountEmail(string email)
    {
        var at = email.IndexOf('@');
        return email.Length <= 320 && at > 0 && at < email.Length - 1 && at == email.LastIndexOf('@')
               && !email.Any(c => char.IsWhiteSpace(c) || NotReadable(c));
    }

    /// <summary>True for a character that is not text to be read: a control character, a line or paragraph separator,
    /// or a format character such as the one that reverses the direction of what follows. Text holding one is refused
    /// before it is stored, logged or said back in a sentence.</summary>
    internal static bool NotReadable(char c)
        => char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private LicenseApproverSet Refuse(string word, string sentence)
    {
        FileLog.Write($"[LicenseApproverStore] Set: REFUSED {word}");
        return new LicenseApproverSet(null, word, sentence);
    }

    // A stored value that cannot be read is an error, never "no approvers": answering an empty list would make every
    // sign-in that needs another machine fail as if the owner had set nothing.
    private List<LicenseApprover> Read(TenantId tenant)
    {
        var raw = _settings.Get(tenant, TenantSettingKeys.LicenseApprovers);
        if (string.IsNullOrWhiteSpace(raw))
            return new List<LicenseApprover>();
        var read = JsonSerializer.Deserialize<List<LicenseApprover>>(raw, Json)
                   ?? throw new InvalidOperationException("The license approvers setting holds no list.");
        return read.OrderBy(a => a.AccountEmail, StringComparer.Ordinal).ToList();
    }

    private void Write(TenantId tenant, List<LicenseApprover> all, DateTime nowUtc)
    {
        if (all.Count == 0)
        {
            _settings.Remove(tenant, TenantSettingKeys.LicenseApprovers);
            return;
        }
        var ordered = all.OrderBy(a => a.AccountEmail, StringComparer.Ordinal).ToList();
        _settings.Set(tenant, TenantSettingKeys.LicenseApprovers, JsonSerializer.Serialize(ordered, Json), nowUtc);
    }
}
