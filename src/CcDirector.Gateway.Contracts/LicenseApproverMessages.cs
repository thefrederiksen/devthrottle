namespace CcDirector.Gateway.Contracts;

// License Switching, phase 6 Track C: standing approvers. A Director whose machine offers its browser profiles as
// approvers checks each profile daily by reading a real approval page, and reports what it found over its own hub
// connection. The Gateway keeps one approver record per account; the owner's record always wins.
//
// Rule 6.1 of the contract holds here as in LicenseMessages.cs: nothing in this file carries a sign-in key, the
// address a sign-in is approved at, or any text read from a page. Only emails, profile names, state words, times
// and a short sentence from the Director's own fixed words.

/// <summary>The health of one approver record. Written ONCE, here, so the Gateway and the Director cannot drift.</summary>
public static class LicenseApproverStates
{
    /// <summary>The approval page showed the account signed in and ready: an approval would go through.</summary>
    public const string CanApprove = "can-approve";

    /// <summary>The profile is not signed in to claude.ai at all.</summary>
    public const string SignedOut = "signed-out";

    /// <summary>The approval page asked for a fresh sign-in before it would approve (the "reauth" page), even though
    /// the profile may still look signed in elsewhere on claude.ai.</summary>
    public const string NeedsFreshSignIn = "needs-fresh-sign-in";

    /// <summary>The profile is signed in, but as a different account.</summary>
    public const string WrongAccount = "wrong-account";

    /// <summary>The check itself could not run; the row's detail says why.</summary>
    public const string CheckFailed = "check-failed";

    /// <summary>The Gateway's own word for a record with no check in <see cref="LicenseApproverContract.NotCheckedAfter"/>,
    /// or never checked. A Director never reports it: an absence is never read as healthy.</summary>
    public const string NotChecked = "not-checked";

    /// <summary>The words a Director may report: every state except <see cref="NotChecked"/>.</summary>
    public static readonly IReadOnlySet<string> Reportable = new HashSet<string>(StringComparer.Ordinal)
    {
        CanApprove, SignedOut, NeedsFreshSignIn, WrongAccount, CheckFailed,
    };

    /// <summary>All six words.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        CanApprove, SignedOut, NeedsFreshSignIn, WrongAccount, CheckFailed, NotChecked,
    };
}

/// <summary>Who made an approver record.</summary>
public static class LicenseApproverSources
{
    /// <summary>The owner set it on the Licenses page. A registration never overwrites it.</summary>
    public const string Owner = "owner";

    /// <summary>The Director of the machine it names registered it, from a check that proved the profile can approve.</summary>
    public const string Registered = "registered";
}

/// <summary>The names and numbers of the approver exchange in one place.</summary>
public static class LicenseApproverContract
{
    /// <summary>The hub method a Director calls for the approver records that name its machine and user.</summary>
    public const string GetAssignmentsHubMethod = "GetLicenseApproverAssignments";

    /// <summary>The hub method a Director calls with the results of its daily checks.</summary>
    public const string ReportChecksHubMethod = "ReportLicenseApproverChecks";

    /// <summary>The most rows one check report may carry. A report with more is refused whole.</summary>
    public const int MaxCheckRows = 32;

    /// <summary>The longest <see cref="LicenseApproverCheckRow.Detail"/> taken.</summary>
    public const int MaxDetailLength = 200;

    /// <summary>A record whose last check is older than this, or that was never checked, reads
    /// <see cref="LicenseApproverStates.NotChecked"/>. A day plus two hours, so one late daily check is not an alarm.</summary>
    public static readonly TimeSpan NotCheckedAfter = TimeSpan.FromHours(26);
}

/// <summary>The answer to <see cref="LicenseApproverContract.GetAssignmentsHubMethod"/>: the approver records that name
/// the calling Director's machine and operating system user, which that Director checks daily.</summary>
public sealed class LicenseApproverAssignments
{
    public List<LicenseApproverAssignment> Rows { get; set; } = new();
}

/// <summary>One approver record the calling Director is to check.</summary>
public sealed class LicenseApproverAssignment
{
    public string AccountEmail { get; set; } = "";

    public string BrowserProfile { get; set; } = "";

    /// <summary>One of <see cref="LicenseApproverSources"/>.</summary>
    public string Source { get; set; } = "";
}

/// <summary>The payload of <see cref="LicenseApproverContract.ReportChecksHubMethod"/>: what the Director's checks found.</summary>
public sealed class LicenseApproverCheckReport
{
    /// <summary>True when this machine is opted in to offer its browser profiles as approvers. False: the report still
    /// updates the health of records that already name this machine, and registers nothing.</summary>
    public bool OffersApprovers { get; set; }

    /// <summary>At most <see cref="LicenseApproverContract.MaxCheckRows"/>.</summary>
    public List<LicenseApproverCheckRow> Rows { get; set; } = new();
}

/// <summary>One profile's check.</summary>
public sealed class LicenseApproverCheckRow
{
    public string BrowserProfile { get; set; } = "";

    /// <summary>The account checked, or null when the profile showed none.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>Who the page said is signed in, or null.</summary>
    public string? ShownAccount { get; set; }

    /// <summary>One of <see cref="LicenseApproverStates.Reportable"/>.</summary>
    public string State { get; set; } = "";

    public DateTime CheckedAtUtc { get; set; }

    /// <summary>A short sentence from the Director's own fixed words - never text read from a page. At most
    /// <see cref="LicenseApproverContract.MaxDetailLength"/> characters.</summary>
    public string? Detail { get; set; }
}

/// <summary>What the Gateway did with each row of a check report, in the report's order.</summary>
public sealed class LicenseApproverCheckAnswer
{
    public List<LicenseApproverCheckRowAnswer> Rows { get; set; } = new();
}

/// <summary>What the Gateway did with one row.</summary>
public sealed class LicenseApproverCheckRowAnswer
{
    public string BrowserProfile { get; set; } = "";

    public string? AccountEmail { get; set; }

    /// <summary>One of <see cref="LicenseApproverCheckOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>For a refused row, one of <see cref="LicenseApproverRefusals"/>; otherwise null.</summary>
    public string? Reason { get; set; }
}

/// <summary>What the Gateway did with one check row.</summary>
public static class LicenseApproverCheckOutcomes
{
    /// <summary>The row became the approver record for its account.</summary>
    public const string Registered = "registered";

    /// <summary>The row updated the health of the record that names this machine, user and profile.</summary>
    public const string HealthUpdated = "health-updated";

    /// <summary>The row was taken and there was nothing to store: no record for the account and nothing proven.</summary>
    public const string NothingToStore = "nothing-to-store";

    /// <summary>The row was refused whole; nothing of it was stored.</summary>
    public const string Refused = "refused";
}

/// <summary>Why a check row was refused.</summary>
public static class LicenseApproverRefusals
{
    /// <summary>The caller is not the Director that last reported licenses for its machine.</summary>
    public const string NotTheReportingDirector = "not-the-reporting-director";

    /// <summary>A field is missing, too long, or of the wrong shape.</summary>
    public const string InvalidRow = "invalid-row";

    /// <summary>The account is not one this DevThrottle account's machines hold.</summary>
    public const string AccountNotHeld = "account-not-held";

    /// <summary>The machine does not offer its profiles as approvers, so it registers nothing.</summary>
    public const string NotOffered = "not-offered";

    /// <summary>The owner set this account's approver; a registration never overwrites it.</summary>
    public const string OwnerSet = "owner-set";

    /// <summary>The owner removed this registration; it stays removed until the owner sets an approver for the account.</summary>
    public const string OwnerRemoved = "owner-removed";

    /// <summary>Another registered approver for the account can approve and was checked recently.</summary>
    public const string HealthyIncumbent = "healthy-incumbent";

    /// <summary>The account's record names another machine, user or profile; a row that does not prove the profile can
    /// approve only updates the health of a record that names exactly the caller's machine, user and that profile.</summary>
    public const string RecordNamesAnotherApprover = "record-names-another-approver";

    /// <summary>The DevThrottle account already keeps the most approvers it may.</summary>
    public const string TooManyApprovers = "too-many-approvers";
}
