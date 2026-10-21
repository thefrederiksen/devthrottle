namespace CcDirector.Gateway.Contracts;

// What is using the license (License Switching, the usage-by-session mandate of 2026-10-20): which sessions use a
// Claude account, how fast, and how much so far - in ONE unit, percent of that account's weekly limit.
//
// THE CLIENT IS DUMB (CLAUDE.md rule 7). Every number and sentence here is folded on the Gateway
// (Licenses.SessionUsageFold); the Cockpit and the agents' command only show it. A percent is null when the account's
// calibration is not measured yet, and then the sentence beside it says why.
//
// NEVER A SIGN-IN KEY (contract section 6 rule 1): these are License* types, so LicenseMessagesCarryNoKeyTests holds
// their member names to that. The counts are called "weighted" for that reason too.

/// <summary>The usage routes of the Gateway.</summary>
public static class LicenseUsageRoutes
{
    /// <summary>GET: what is using one account (<see cref="LicenseUsageResponse"/>). Query: <c>account</c> (default: the
    /// account in use), <c>closed=true</c> to include sessions that have gone.</summary>
    public const string Usage = "/gateway/licenses/usage";

    /// <summary>PUT: set or clear what an account's subscription costs a month (<see cref="LicensePriceRequest"/>,
    /// <see cref="LicensePriceResponse"/>). A write: the owner's own, refused to a session key.</summary>
    public const string AccountPrice = "/gateway/licenses/accounts/price";
}

/// <summary>What is using one Claude account.</summary>
public sealed class LicenseUsageResponse
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>The account the numbers are about, lower-cased; null when no account is known yet.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>A machine is working on it now.</summary>
    public bool AccountInUse { get; set; }

    /// <summary>Every account that can be asked about, in email order.</summary>
    public List<LicenseUsageAccountChoiceDto> Accounts { get; set; } = new();

    /// <summary>The sentence shown in place of the tables when there is nothing to show; null otherwise.</summary>
    public string? EmptyText { get; set; }

    /// <summary>How the account's weighted use is turned into percent of its weekly limit.</summary>
    public LicenseUsageCalibrationDto Calibration { get; set; } = new();

    /// <summary>The explanation of the one unit, as sentences, in order.</summary>
    public List<string> UnitText { get; set; } = new();

    /// <summary>How much of the weekly limit is used, from the account's freshest reading; null when not known.</summary>
    public double? WeekUsedPercent { get; set; }

    /// <summary>When the weekly limit resets; null when not known.</summary>
    public DateTime? WeekResetsAtUtc { get; set; }

    /// <summary>What every session together is using now, in percent of the weekly limit an hour (the sum of their
    /// rates over the last hour); null when not calibrated.</summary>
    public double? BurnPercentPerHour { get; set; }

    /// <summary>The sentence for <see cref="BurnPercentPerHour"/>.</summary>
    public string BurnText { get; set; } = "";

    /// <summary>What the subscription costs a month, in US dollars, as a person set it; null when not set.</summary>
    public double? MonthlyPriceUsd { get; set; }

    /// <summary>What one percent of the weekly limit is worth, in US dollars; null when no price is set.</summary>
    public double? DollarsPerPercent { get; set; }

    /// <summary>Every session, by rate now (highest first), then by use so far.</summary>
    public List<LicenseUsageSessionDto> Sessions { get; set; } = new();

    /// <summary>The top-level sessions - each one answering to the owner, with every session under it - by rate now.</summary>
    public List<LicenseUsageGroupDto> TopLevel { get; set; } = new();

    /// <summary>The Missions, with every session attached to each, by rate now. Sessions on no Mission are one group
    /// with a null id.</summary>
    public List<LicenseUsageGroupDto> Missions { get; set; } = new();
}

/// <summary>One account that can be asked about.</summary>
public sealed class LicenseUsageAccountChoiceDto
{
    public string AccountEmail { get; set; } = "";
    public bool InUse { get; set; }

    /// <summary>"soren@example.com (in use)" and so on.</summary>
    public string Label { get; set; } = "";
}

/// <summary>
/// How weighted use becomes percent of the weekly limit, MEASURED: the rise of the account's weekly use between two of
/// its readings, divided by the weighted use every session made on it between the same two times.
/// </summary>
public sealed class LicenseUsageCalibrationDto
{
    /// <summary>A rate was measured; when false every percent in the answer is null.</summary>
    public bool IsMeasured { get; set; }

    /// <summary>Percent of the weekly limit per million weighted units; null when not measured.</summary>
    public double? PercentPerMillionWeighted { get; set; }

    /// <summary>The span it was measured over; null when not measured.</summary>
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    /// <summary>How far the weekly use rose over that span, in percent.</summary>
    public double? RisePercent { get; set; }

    /// <summary>The weighted use of every session on the account over that span.</summary>
    public double? Weighted { get; set; }

    /// <summary>The sentence: how it was measured, or why it is not yet.</summary>
    public string Text { get; set; } = "";
}

/// <summary>One session's use of the account. Every percent is of THIS account's weekly limit.</summary>
public sealed class LicenseUsageSessionDto
{
    public string SessionId { get; set; } = "";
    public int? Number { get; set; }
    public string Name { get; set; } = "";
    public Guid? MissionId { get; set; }
    public string? MissionName { get; set; }

    /// <summary>The session at the top of its chain of controllers - the one that answers to the owner. Itself when it
    /// is one.</summary>
    public string TopLevelSessionId { get; set; } = "";

    /// <summary>It is in a connected Director's roster now.</summary>
    public bool IsLive { get; set; }

    /// <summary>"working", "waiting" or "gone".</summary>
    public string State { get; set; } = "";

    /// <summary>"Working", "Waiting", "Gone".</summary>
    public string StateText { get; set; } = "";

    public string MachineName { get; set; } = "";
    public string? Model { get; set; }

    /// <summary>"claude", "codex", ...: only "claude" is counted against a Claude account.</summary>
    public string AgentKind { get; set; } = "";

    /// <summary>Percent of the weekly limit an hour, measured over the last hour.</summary>
    public double? RateNowPercentPerHour { get; set; }

    /// <summary>Percent of the weekly limit used in the last four hours.</summary>
    public double? Last4HoursPercent { get; set; }

    /// <summary>Percent of the weekly limit used over the session's whole life, through every compaction.</summary>
    public double? SoFarPercent { get; set; }

    /// <summary>Hours since the session was created (to when it was last seen, for one that has gone).</summary>
    public double RunningHours { get; set; }

    /// <summary><see cref="SoFarPercent"/> over <see cref="RunningHours"/>.</summary>
    public double? AveragePercentPerHour { get; set; }

    /// <summary><see cref="SoFarPercent"/> as a share of the subscription price; null when no price is set.</summary>
    public double? DollarsSoFar { get; set; }

    /// <summary>The weighted use behind the percents: last hour, last four hours, whole life on this account.</summary>
    public double WeightedLastHour { get; set; }
    public double WeightedLast4Hours { get; set; }
    public double WeightedSoFar { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}

/// <summary>A group of sessions: a top-level session with every session under it, or a Mission.</summary>
public sealed class LicenseUsageGroupDto
{
    /// <summary>The top-level session's id, or the Mission's id; null for "no Mission".</summary>
    public string? Id { get; set; }

    /// <summary>The top-level session's number; null for a Mission.</summary>
    public int? Number { get; set; }

    /// <summary>"#124 Cube Setup - Delivery Lead", "Cube Setup", "No Mission".</summary>
    public string Label { get; set; } = "";

    /// <summary>Every session in the group, the top-level one first.</summary>
    public List<string> SessionIds { get; set; } = new();

    /// <summary>The ones in a connected Director's roster now.</summary>
    public List<string> LiveSessionIds { get; set; } = new();

    public double? RateNowPercentPerHour { get; set; }
    public double? Last4HoursPercent { get; set; }
    public double? SoFarPercent { get; set; }
    public double? DollarsSoFar { get; set; }
}

/// <summary>Set (a number) or clear (null) what an account's subscription costs a month.</summary>
public sealed class LicensePriceRequest
{
    public string AccountEmail { get; set; } = "";

    /// <summary>US dollars a month, from 0 to 100000; null clears it.</summary>
    public double? MonthlyPriceUsd { get; set; }
}

/// <summary>The price as stored.</summary>
public sealed class LicensePriceResponse
{
    public string AccountEmail { get; set; } = "";
    public double? MonthlyPriceUsd { get; set; }
}

// ---- GET /gateway/licenses/plan: make it last ----

/// <summary>The "make it last" planner's route.</summary>
public static class LicensePlanRoutes
{
    /// <summary>GET: will the account's weekly limit last until a time, and what to stop if not
    /// (<see cref="LicensePlanResponse"/>). Query: <c>account</c> (default: the one in use), <c>until</c> (a target id
    /// from <see cref="LicensePlanResponse.Targets"/>, or a time in ISO 8601; default: the first target), and the
    /// what-if: <c>stop</c> (session ids) and <c>stopGroup</c> (top-level session ids, each standing for every live
    /// session under it), each comma separated. A read: nothing here stops a session.</summary>
    public const string Plan = "/gateway/licenses/plan";
}

/// <summary>
/// Will one account's weekly limit last until a time, and if not, what to stop. SUGGESTIONS ONLY: nothing here stops a
/// session; stopping stays with the session commands and the owner.
/// </summary>
public sealed class LicensePlanResponse
{
    public DateTime AsOfUtc { get; set; }
    public string? AccountEmail { get; set; }
    public bool AccountInUse { get; set; }

    /// <summary>The sentence shown in place of the plan when there is none; null otherwise.</summary>
    public string? EmptyText { get; set; }

    /// <summary>The times it can be asked to last until: each other account's next "has room" time, then this account's
    /// own weekly reset.</summary>
    public List<LicensePlanTargetDto> Targets { get; set; } = new();

    /// <summary>The target the answer is for: one of <see cref="Targets"/>' ids, or "time" for a time given.</summary>
    public string TargetId { get; set; } = "";
    public DateTime? TargetAtUtc { get; set; }

    /// <summary>"until account-b@example.com has room again", "until the time you picked", ...</summary>
    public string TargetText { get; set; } = "";

    /// <summary>How much of the weekly limit is used now.</summary>
    public double? WeekUsedPercent { get; set; }

    /// <summary>What is left before 97%.</summary>
    public double? LeftPercent { get; set; }

    /// <summary>Hours from now to the target (to the weekly reset when that comes first).</summary>
    public double? HoursToGo { get; set; }

    /// <summary><see cref="LeftPercent"/> over <see cref="HoursToGo"/>: what can be used an hour and still last.</summary>
    public double? AllowedPercentPerHour { get; set; }

    /// <summary>What the account is burning now, in percent an hour - the forecast's own pace (the faster of this
    /// week's average and the last 6 hours).</summary>
    public double? BurnPercentPerHour { get; set; }

    /// <summary>Which pace <see cref="BurnPercentPerHour"/> is, in words.</summary>
    public string BurnText { get; set; } = "";

    /// <summary>How much faster than <see cref="AllowedPercentPerHour"/> it is burning; 0 when it lasts.</summary>
    public double? ShortfallPercentPerHour { get; set; }

    /// <summary>When it reaches 97% at <see cref="BurnPercentPerHour"/>; null when it does not before the target.</summary>
    public DateTime? RunsOutAtUtc { get; set; }

    /// <summary>It lasts to the target at the burn; null when that is not known.</summary>
    public bool? Lasts { get; set; }

    /// <summary>The verdict, as one or two sentences.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>One of <see cref="LicenseTones"/>.</summary>
    public string VerdictTone { get; set; } = "";

    /// <summary>What to stop to make it last: waiting sessions before working ones, biggest first.</summary>
    public LicensePlanSuggestionDto Suggestion { get; set; } = new();

    /// <summary>Each top-level session with the live sessions under it: one choice that stops them all.</summary>
    public List<LicensePlanChoiceDto> Choices { get; set; } = new();

    /// <summary>Every live session using the account, by rate now.</summary>
    public List<LicensePlanSessionDto> Sessions { get; set; } = new();

    /// <summary>The what-if, when sessions to stop were given; null otherwise.</summary>
    public LicensePlanWhatIfDto? WhatIf { get; set; }
}

/// <summary>One time the plan can be asked to last until.</summary>
public sealed class LicensePlanTargetDto
{
    /// <summary>"room:account-b@example.com" or "reset": what to pass as <c>until</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>"account-b@example.com has room again", "this account's own weekly reset".</summary>
    public string Label { get; set; } = "";

    public DateTime AtUtc { get; set; }
}

/// <summary>What the planner suggests stopping.</summary>
public sealed class LicensePlanSuggestionDto
{
    public List<string> SessionIds { get; set; } = new();

    /// <summary>What stopping them saves, in percent an hour; null when the sessions' rates are not measured.</summary>
    public double? SavesPercentPerHour { get; set; }

    public string Text { get; set; } = "";
}

/// <summary>A top-level session with every live session under it.</summary>
public sealed class LicensePlanChoiceDto
{
    public string TopLevelSessionId { get; set; } = "";
    public int? Number { get; set; }
    public string Label { get; set; } = "";

    /// <summary>The live sessions in the group, the top-level one first when it is live.</summary>
    public List<string> SessionIds { get; set; } = new();

    public double? RateNowPercentPerHour { get; set; }
    public int Working { get; set; }
    public int Waiting { get; set; }
}

/// <summary>One live session as the planner weighs it.</summary>
public sealed class LicensePlanSessionDto
{
    public string SessionId { get; set; } = "";
    public int? Number { get; set; }
    public string Name { get; set; } = "";
    public string TopLevelSessionId { get; set; } = "";

    /// <summary>"working" or "waiting".</summary>
    public string State { get; set; } = "";
    public string StateText { get; set; } = "";
    public double? RateNowPercentPerHour { get; set; }

    /// <summary>The suggestion stops it.</summary>
    public bool Suggested { get; set; }
}

/// <summary>What stopping the given sessions would do.</summary>
public sealed class LicensePlanWhatIfDto
{
    /// <summary>Every live session it would stop: the ones given, and every live one under each group given.</summary>
    public List<string> StoppedSessionIds { get; set; } = new();

    /// <summary>Ids given that are not a live session or a top-level session using the account.</summary>
    public List<string> NotFound { get; set; } = new();

    public double? SavesPercentPerHour { get; set; }
    public double? BurnPercentPerHour { get; set; }
    public DateTime? RunsOutAtUtc { get; set; }
    public bool? Lasts { get; set; }
    public string Verdict { get; set; } = "";
    public string VerdictTone { get; set; } = "";
}
