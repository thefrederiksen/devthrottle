namespace CcDirector.Gateway.Contracts;

// The License Switching mission, contract section 8: what the Gateway answers the Licenses page of the Cockpit with
// about SIGN-INS - who approves the sign-ins of an account, and the two actions that start one ("Add a spare" and
// "Renew now"). The single source for a reader is missions/license-switching/cockpit-api.md, section 7.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Whether an approver can approve now, whether a sign-in can be started on a
// machine, and the sentence saying why not, are folded on the Gateway in Licenses.LicenseSignInFold and travel here
// finished. A refusal of one of the writes is `{ "code": "...", "error": "..." }`, as on every Licenses route.
//
// NEVER A SIGN-IN KEY, AND NEVER WHAT COMPLETES A SIGN-IN (contract section 8.3). No type here carries the address a
// sign-in is approved at or the approval itself: the Gateway holds both only in memory while it passes them on.
// Every type here is a License* type in this namespace, so LicenseMessagesCarryNoKeyTests holds its member names.

/// <summary>The sign-in routes of the Gateway.</summary>
public static class LicenseSignInRoutes
{
    /// <summary>GET: who approves each account (<see cref="LicenseApproversResponse"/>). PUT: set the approver of one
    /// account (<see cref="LicenseSetApproverRequest"/>). DELETE with <c>?accountEmail=</c>: remove it.</summary>
    public const string Approvers = "/gateway/licenses/approvers";

    /// <summary>POST: sign a machine in to an account it does not hold yet (<see cref="LicenseStartSignInRequest"/>).</summary>
    public const string AddSpare = "/gateway/licenses/sign-ins/add-spare";

    /// <summary>POST: renew a sign-in a machine holds (<see cref="LicenseStartSignInRequest"/>).</summary>
    public const string Renew = "/gateway/licenses/sign-ins/renew";
}

/// <summary>Whether the machine set to approve an account can approve a sign-in now.</summary>
public static class LicenseApproverStatuses
{
    /// <summary>Its Director is connected, and its last check, within the not-checked window, found it can approve.</summary>
    public const string Ready = "ready";

    /// <summary>Its Director is connected, but its health says it cannot approve now: its last check found a problem,
    /// or it has not been checked in the not-checked window. <see cref="LicenseApproverDto.HealthText"/> says which.</summary>
    public const string CannotApprove = "cannot-approve";

    /// <summary>The machine has reported to this account, and its Director is not connected now.</summary>
    public const string NotConnected = "not-connected";

    /// <summary>No machine of that name and user has ever reported to this account.</summary>
    public const string NeverReported = "never-reported";
}

// ---- GET /gateway/licenses/approvers ----

/// <summary>Who approves the sign-ins of each account, for the caller's own DevThrottle account.</summary>
public sealed class LicenseApproversResponse
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>True when no approver is set for any account.</summary>
    public bool IsEmpty { get; set; }

    /// <summary>The sentence to show in place of the list when <see cref="IsEmpty"/>; null otherwise.</summary>
    public string? EmptyText { get; set; }

    /// <summary>In account email order.</summary>
    public List<LicenseApproverDto> Approvers { get; set; } = new();
}

/// <summary>The machine and browser profile that approve the sign-ins of one account (contract section 8.3).</summary>
public sealed class LicenseApproverDto
{
    public string AccountEmail { get; set; } = "";

    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";

    /// <summary>The IDENTIFIER of the browser profile on that machine that is signed in to claude.ai as the account
    /// (the Director's identifier, for example "work-chrome", never its display name).</summary>
    public string BrowserProfile { get; set; } = "";

    /// <summary>The identifier of the machine's row in the overview's machines; null when no such machine has reported.</summary>
    public string? MachineId { get; set; }

    /// <summary>What to call the machine: see <see cref="LicenseMachineRefDto.Label"/>.</summary>
    public string Label { get; set; } = "";

    /// <summary>The machine's Director is connected and the approver's health says it can approve, so a sign-in can be
    /// approved there now.</summary>
    public bool CanApproveNow { get; set; }

    /// <summary>One of <see cref="LicenseApproverStatuses"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>The label: "Ready", "Not connected", "Not reporting yet", "Cannot approve" or "Not checked".</summary>
    public string StatusText { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string StatusTone { get; set; } = "";

    /// <summary>One or two sentences saying who approves the account and whether that can happen now.</summary>
    public string Text { get; set; } = "";

    /// <summary>The profile's display name as its last check saw it; null until a check has named it. The sentences say
    /// it in place of <see cref="BrowserProfile"/>, which is the profile's identifier.</summary>
    public string? BrowserProfileName { get; set; }

    /// <summary>Who made the record: one of <see cref="LicenseApproverSources"/>.</summary>
    public string Source { get; set; } = "";

    /// <summary>Who made the record, in words: "Set by you." or "Registered by machine-a's Director.".</summary>
    public string SourceText { get; set; } = "";

    /// <summary>Why no sign-in can reach the approving machine now - it has never reported, or its Director is not
    /// connected; null when its Director is connected. The profile's own health is <see cref="HealthText"/>.</summary>
    public string? ReachText { get; set; }

    /// <summary>The record's health now: one of <see cref="LicenseApproverStates"/>, aged by the not-checked rule.</summary>
    public string State { get; set; } = "";

    /// <summary>When the record was last checked; null when it never was.</summary>
    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>The finished sentence of the record's health, naming the account, the machine and the profile.</summary>
    public string HealthText { get; set; } = "";

    /// <summary>The one thing the owner must do so it can approve; null when it can.</summary>
    public string? WhatToDo { get; set; }
}

// ---- PUT /gateway/licenses/approvers ----

/// <summary>Set the machine and browser profile that approve the sign-ins of one account. Setting it again replaces it.</summary>
public sealed class LicenseSetApproverRequest
{
    public string AccountEmail { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OsUser { get; set; } = "";
    public string BrowserProfile { get; set; } = "";
}

/// <summary>The answer to removing an approver.</summary>
public sealed class LicenseApproverRemovedResponse
{
    public string AccountEmail { get; set; } = "";

    /// <summary>True when an approver was set and is now removed; false when none was set.</summary>
    public bool Removed { get; set; }

    /// <summary>The sentence saying which.</summary>
    public string Text { get; set; } = "";
}

// ---- POST /gateway/licenses/sign-ins/add-spare and /renew ----

/// <summary>Start a sign-in on one machine: a new spare, or the renewal of a sign-in the machine holds.</summary>
public sealed class LicenseStartSignInRequest
{
    public string AccountEmail { get; set; } = "";

    /// <summary>The <see cref="LicenseMachineDto.MachineId"/> of the machine to sign in on.</summary>
    public string MachineId { get; set; } = "";
}

/// <summary>The answer when a sign-in was started. It is answered at once: the sign-in itself takes minutes, and
/// its outcome arrives as the same row of the log, by <see cref="SignInId"/>.</summary>
public sealed class LicenseSignInStartedResponse
{
    /// <summary>The sign-in's identifier.</summary>
    public string SignInId { get; set; } = "";

    public string AccountEmail { get; set; } = "";

    public LicenseMachineRefDto Machine { get; set; } = new();

    /// <summary>One sentence saying what was started and where its outcome appears.</summary>
    public string Text { get; set; } = "";

    /// <summary>The sign-in's row in the log as it stands now. Its <see cref="LicenseLogEntryDto.EntryId"/> is the
    /// row the log route later shows the outcome on.</summary>
    public LicenseLogEntryDto Entry { get; set; } = new();
}

/// <summary>A sign-in a machine is in the middle of.</summary>
public sealed class LicenseSignInInProgressDto
{
    public string SignInId { get; set; } = "";
    public string AccountEmail { get; set; } = "";

    /// <summary>One sentence saying where the sign-in stands.</summary>
    public string Text { get; set; } = "";
}
