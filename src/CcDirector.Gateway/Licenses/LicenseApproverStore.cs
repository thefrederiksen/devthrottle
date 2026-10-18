using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Licenses;

/// <summary>Which machine and browser profile approve the sign-ins of one Claude account (contract section 8.3).</summary>
/// <param name="AccountEmail">The account, lower-cased.</param>
/// <param name="MachineName">The approving machine, as its Director reports it.</param>
/// <param name="OsUser">The operating system user on that machine whose Director approves.</param>
/// <param name="BrowserProfile">The IDENTIFIER of the browser profile there that is signed in to claude.ai as the account
/// (the Director's identifier, for example "work-chrome", never the display name). Compared exactly.</param>
/// <param name="Source">Who made the record: <see cref="LicenseApproverSources.Owner"/> or
/// <see cref="LicenseApproverSources.Registered"/>. A record stored before sources existed reads as owner-set.</param>
/// <param name="State">The state the last check reported (<see cref="LicenseApproverStates.Reportable"/>); null when the
/// record was never checked. Read it through <see cref="LicenseApproverHealth.StateOf"/>, which ages it.</param>
/// <param name="CheckedAtUtc">When the last check ran, as the checking Director said; null when never checked.</param>
/// <param name="ShownAccount">Who the approval page said is signed in at the last check, or null.</param>
/// <param name="Detail">The checking Director's short sentence from its own fixed words, or null.</param>
/// <param name="BrowserProfileName">The profile's display name as the last check saw it, or null when no check has named
/// it. Said in sentences in place of the identifier.</param>
public sealed record LicenseApprover(string AccountEmail, string MachineName, string OsUser, string BrowserProfile,
    string Source = LicenseApproverSources.Owner, string? State = null, DateTime? CheckedAtUtc = null, string? ShownAccount = null,
    string? Detail = null, string? BrowserProfileName = null)
{
    /// <summary>True when this record names that machine, operating system user and browser profile.</summary>
    public bool Names(string machineName, string osUser, string browserProfile)
        => NamesMachine(machineName, osUser) && string.Equals(BrowserProfile, browserProfile, StringComparison.Ordinal);

    /// <summary>True when this record names that machine and operating system user, whatever the profile.</summary>
    public bool NamesMachine(string machineName, string osUser)
        => string.Equals(MachineName, machineName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(OsUser, osUser, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The owner removed a registered approver: that account, machine, user and profile register nothing again
/// until the owner sets an approver for the account.</summary>
public sealed record LicenseApproverRemoval(string AccountEmail, string MachineName, string OsUser, string BrowserProfile, DateTime RemovedAtUtc)
{
    public bool Matches(string accountEmail, string machineName, string osUser, string browserProfile)
        => AccountEmail == accountEmail
           && string.Equals(MachineName, machineName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(OsUser, osUser, StringComparison.OrdinalIgnoreCase)
           && string.Equals(BrowserProfile, browserProfile, StringComparison.Ordinal);
}

/// <summary>Everything the approver store keeps for one DevThrottle account, read and written as one under the store's lock.</summary>
public sealed record LicenseApproverRecords(IReadOnlyList<LicenseApprover> Approvers, IReadOnlyList<LicenseApproverRemoval> Removals);

/// <summary>How healthy an approver record is NOW: the not-checked rule, in one place.</summary>
public static class LicenseApproverHealth
{
    /// <summary>The record's state at <paramref name="nowUtc"/>: <see cref="LicenseApproverStates.NotChecked"/> when it
    /// was never checked or its last check is older than <see cref="LicenseApproverContract.NotCheckedAfter"/>, else the
    /// state the last check reported. An absence is never read as healthy.</summary>
    public static string StateOf(LicenseApprover approver, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(approver);
        if (approver.CheckedAtUtc is not { } checkedAt || approver.State is null)
            return LicenseApproverStates.NotChecked;
        return nowUtc - checkedAt > LicenseApproverContract.NotCheckedAfter ? LicenseApproverStates.NotChecked : approver.State;
    }

    /// <summary>True when the record can approve now: its last check said so, within the not-checked window.</summary>
    public static bool CanApprove(LicenseApprover approver, DateTime nowUtc)
        => StateOf(approver, nowUtc) == LicenseApproverStates.CanApprove;
}

/// <summary>The answer to setting an approver: the approver as stored, or the word and the sentence of the refusal.</summary>
public sealed record LicenseApproverSet(LicenseApprover? Approver, string? RefusalWord, string? RefusalText)
{
    public bool Accepted => Approver is not null;
}

/// <summary>
/// WHO APPROVES THE SIGN-INS OF EACH ACCOUNT, per DevThrottle account (License Switching, contract section 8.3). A
/// machine that cannot approve a sign-in in a browser of its own asks the Gateway, and the Gateway asks the machine
/// named here.
///
/// ONE VALUE PER ACCOUNT, in the per-tenant settings (<see cref="TenantSettingKeys.LicenseApprovers"/>), so there is
/// no table of its own. Every call names its tenant and reads and writes through <see cref="TenantSettingsStore"/>,
/// which a tenant can never read another tenant's row through.
///
/// TWO WAYS IN (phase 6 Track C). The owner sets a record on the Licenses page (<see cref="Set"/>), or the Director of
/// the machine a record names registers one from a check that proved the profile can approve
/// (<see cref="LicenseApproverRegistration"/>, which decides under this store's lock through <see cref="Change{T}"/>).
/// The owner keeps the last word: an owner-set record is never overwritten by a registration, and a registration the
/// owner removed is kept as a removal record (its own setting, <see cref="TenantSettingKeys.LicenseApproverRemovals"/>)
/// until the owner sets an approver for that account again.
///
/// WHAT IS KEPT: an account email, a machine name, an operating system user and the name of a browser profile, who
/// made the record, and the latest check of it - a state word, when, who the page said is signed in, and a sentence
/// from the checking Director's fixed words. Nothing from a sign-in, and never the address a sign-in is approved at or
/// the approval itself - those are only ever in memory while <see cref="LicenseApprovalRelay"/> passes them on.
/// </summary>
public sealed class LicenseApproverStore
{
    /// <summary>The most approvers one DevThrottle account keeps: one per Claude account, and an account's machines
    /// hold at most <see cref="LicenseUsageIngest.MaxHoldings"/> of those each.</summary>
    public const int MaxApprovers = LicenseUsageIngest.MaxHoldings;

    /// <summary>The most removal records one DevThrottle account keeps; past it the oldest goes first.</summary>
    public const int MaxRemovals = 64;

    public const int MaxNameLength = 128;

    /// <summary>A browser profile's name: it is handed to a command on the approving machine, so it is letters,
    /// digits, spaces, dots, underscores and hyphens, starting with a letter or a digit.</summary>
    public static readonly Regex BrowserProfileShape = LicenseApproverContract.BrowserProfileShape;

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

    /// <summary>Every approver of this account, in account email order, with NO log line: for the once-a-minute approver
    /// alarm, which must leave no trace when there is nothing to say (the ruling on the review of pull request 3936,
    /// finding 9, that every minute check follows). A read that fails still throws.</summary>
    internal IReadOnlyList<LicenseApprover> ListQuietly(TenantId tenant) => Read(tenant);

    /// <summary>The approver of one account; null when none is set.</summary>
    public LicenseApprover? Find(TenantId tenant, string? accountEmail)
    {
        var email = LicenseStore.NormalizeEmail(accountEmail);
        var found = Read(tenant).FirstOrDefault(a => a.AccountEmail == email);
        FileLog.Write($"[LicenseApproverStore] Find: tenant={tenant.ToLogString()}, account={email}, found={found is not null}");
        return found;
    }

    /// <summary>
    /// The OWNER sets the approver of one account, replacing the one it had, whoever made that. Refused - and nothing
    /// stored - when a value is missing or too long, when the browser profile's name is not a profile name, or when the
    /// account would be the one past <see cref="MaxApprovers"/>. The machine need not have reported yet: the owner may
    /// set it first. Setting an account's approver clears every removal record of that account.
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
        LicenseApprover approver;
        lock (_gate)
        {
            var all = Read(tenant).ToList();
            var at = all.FindIndex(a => a.AccountEmail == email);
            if (at < 0 && all.Count >= MaxApprovers)
                return Refuse("too_many_approvers", $"An account keeps at most {MaxApprovers} approvers.");
            // The owner's record. The last check is kept only when the owner named the very profile that was checked:
            // a check of one profile says nothing about another.
            approver = new LicenseApprover(email, machine, user, profile);
            if (at >= 0 && all[at].Names(machine, user, profile))
                approver = approver with
                {
                    State = all[at].State, CheckedAtUtc = all[at].CheckedAtUtc, ShownAccount = all[at].ShownAccount, Detail = all[at].Detail,
                    BrowserProfileName = all[at].BrowserProfileName,
                };
            if (at < 0) all.Add(approver);
            else all[at] = approver;
            Write(tenant, all, nowUtc);
            // The owner has set this account's approver again, so a registration removed for it may register again.
            var removals = ReadRemovals(tenant);
            if (removals.RemoveAll(r => r.AccountEmail == email) > 0)
                WriteRemovals(tenant, removals, nowUtc);
        }
        FileLog.Write($"[LicenseApproverStore] Set: stored the approver of {email}");
        return new LicenseApproverSet(approver, null, null);
    }

    /// <summary>The OWNER removes the approver of one account. False when none was set. A REGISTERED record leaves a
    /// removal record behind, so the same machine, user and profile do not register it again on their next check; it
    /// lasts until the owner sets an approver for the account (<see cref="Set"/>).</summary>
    public bool Remove(TenantId tenant, string? accountEmail, DateTime nowUtc)
    {
        var email = LicenseStore.NormalizeEmail(accountEmail);
        LicenseApprover? gone;
        lock (_gate)
        {
            var all = Read(tenant);
            gone = all.FirstOrDefault(a => a.AccountEmail == email);
            if (gone is not null)
            {
                all.Remove(gone);
                Write(tenant, all, nowUtc);
                if (gone.Source == LicenseApproverSources.Registered)
                {
                    var removals = ReadRemovals(tenant);
                    removals.RemoveAll(r => r.Matches(email, gone.MachineName, gone.OsUser, gone.BrowserProfile));
                    removals.Add(new LicenseApproverRemoval(email, gone.MachineName, gone.OsUser, gone.BrowserProfile, nowUtc));
                    WriteRemovals(tenant, removals, nowUtc);
                }
            }
        }
        FileLog.Write($"[LicenseApproverStore] Remove: tenant={tenant.ToLogString()}, account={email}, removed={gone is not null}, source={gone?.Source ?? "-"}");
        return gone is not null;
    }

    /// <summary>The registrations the owner removed and has not set an approver for since, oldest first.</summary>
    public IReadOnlyList<LicenseApproverRemoval> ListRemovals(TenantId tenant)
    {
        var all = ReadRemovals(tenant);
        FileLog.Write($"[LicenseApproverStore] ListRemovals: tenant={tenant.ToLogString()}, count={all.Count}");
        return all;
    }

    /// <summary>
    /// Read every record of one DevThrottle account and, under the store's lock, let <paramref name="change"/> decide
    /// what to keep: it returns the records to write (null writes nothing) and its own answer. This is how a registration
    /// is decided - inside the lock, so an owner's <see cref="Set"/> can never land between the decision and the write
    /// and be overwritten. <paramref name="change"/> must not call back into this store. Removal records are not
    /// changed through here: only the owner makes or clears them.
    /// </summary>
    internal T Change<T>(TenantId tenant, Func<LicenseApproverRecords, (IReadOnlyList<LicenseApprover>? Next, T Answer)> change, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var (next, answer) = change(new LicenseApproverRecords(Read(tenant), ReadRemovals(tenant)));
            if (next is not null)
            {
                if (next.Count > MaxApprovers)
                    throw new InvalidOperationException($"A change left {next.Count} approvers; an account keeps at most {MaxApprovers}.");
                Write(tenant, next.ToList(), nowUtc);
            }
            return answer;
        }
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
        return read.Select(WithSource).Select(WithReportableState).OrderBy(a => a.AccountEmail, StringComparer.Ordinal).ToList();
    }

    // A check's state is one of the words a Director may report, or none when the record was never checked. Anything
    // else cannot be read, and is an error here rather than a sentence folded from a word nobody wrote.
    private static LicenseApprover WithReportableState(LicenseApprover approver)
        => approver.State is null || LicenseApproverStates.Reportable.Contains(approver.State)
            ? approver
            : throw new InvalidOperationException("The license approvers setting holds a record whose state is not a state word.");

    // A record stored before sources existed has none, and the owner set it: nothing else could set one then. Any
    // other word is a stored value that cannot be read, which is an error like any other.
    private static LicenseApprover WithSource(LicenseApprover approver) => approver.Source switch
    {
        null or "" => approver with { Source = LicenseApproverSources.Owner },
        LicenseApproverSources.Owner or LicenseApproverSources.Registered => approver,
        _ => throw new InvalidOperationException("The license approvers setting holds a record whose source is not a source word."),
    };

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

    private List<LicenseApproverRemoval> ReadRemovals(TenantId tenant)
    {
        var raw = _settings.Get(tenant, TenantSettingKeys.LicenseApproverRemovals);
        if (string.IsNullOrWhiteSpace(raw))
            return new List<LicenseApproverRemoval>();
        var read = JsonSerializer.Deserialize<List<LicenseApproverRemoval>>(raw, Json)
                   ?? throw new InvalidOperationException("The license approver removals setting holds no list.");
        return read.OrderBy(r => r.RemovedAtUtc).ToList();
    }

    private void WriteRemovals(TenantId tenant, List<LicenseApproverRemoval> all, DateTime nowUtc)
    {
        if (all.Count == 0)
        {
            _settings.Remove(tenant, TenantSettingKeys.LicenseApproverRemovals);
            return;
        }
        var kept = all.OrderBy(r => r.RemovedAtUtc).TakeLast(MaxRemovals).ToList();
        _settings.Set(tenant, TenantSettingKeys.LicenseApproverRemovals, JsonSerializer.Serialize(kept, Json), nowUtc);
    }
}
