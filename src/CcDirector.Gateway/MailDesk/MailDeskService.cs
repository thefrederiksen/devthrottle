using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE MAIL DESK HAND-OFF (Mail Desk phase 3 contract, sections 1 to 4 and 8). A Mail Desk start's report never reaches
/// the trigger's count rule: <see cref="Factory.Triggers.TriggerService"/> hands every one to this service, which reads
/// the check's context (version 2), screens every message through the four layers, records one decision row per email,
/// records each mailbox's check, and makes up the memory to keep. It starts no session.
///
/// FILING IN LIVE MODE (phase 4 contract, section 2). Only a decision RECORDED in live mode is ever filed, and only
/// while its channel is still live: every <c>archive</c> or <c>junk</c> decision of a live channel with no outcome yet
/// is appended to that channel's <c>filingQueue</c> in the memory kept, and the next run of the check carries it out.
/// What the check reports it came to - <c>moved</c>, <c>already-gone</c>, <c>refused</c> - is written on the decision
/// row as <c>filed</c>, <c>already-gone</c> or <c>failed</c>; <c>already-gone</c> is never read as moved by us. A
/// mailbox written back this run queues nothing, because its key table is the one from before; its emails are queued
/// on the run that rereads them.
///
/// RETRIED NEXT RUN, NEVER GUESSED. A model question that fails leaves that email <c>failed</c>, and that email's MAILBOX
/// has its check fields written back exactly as the memory held them before this run, so the check reads that mailbox's
/// same mail again; every other mailbox moves on. A reread costs nothing for an email already decided: a decision that
/// is not <c>failed</c> is never decided again. The third failed screening keeps the email for the owner and lets its
/// mailbox move on, so one email the model always refuses can never block a mailbox.
///
/// Nothing of an email is logged: counts, channel ids, kinds and times only. The preview is shown to the model and
/// dropped; no table has a place for it.
/// </summary>
public sealed class MailDeskService
{
    /// <summary>How long one whole screening may ask the model for; a question not asked by then fails that email,
    /// which is read again next run (phase 3, section 5).</summary>
    public static readonly TimeSpan ScreeningDeadline = TimeSpan.FromMinutes(4);

    /// <summary>The context version this service reads (phase 2 section 4.2, changed in phase 3).</summary>
    public const int ContextVersion = 2;

    private readonly MailDeskChannelStore _channels;
    private readonly MailDeskRulesStore _rules;
    private readonly MailDeskDecisionStore _decisions;
    private readonly MailDeskSettingsStore _settings;
    private readonly MailDeskChannelCheckStore _checks;
    private readonly Func<TenantId, IMailDeskModel> _modelFor;
    private readonly Func<DateTime> _nowUtc;
    private readonly TimeProvider _time;
    // When this Gateway last handed the Mail Desk memory to each (account, machine), on the Gateway's own clock: what
    // decides whether a channel missing from a report was handed at all (review of 4243, finding 5). Lost on a restart,
    // when the report's arrival time stands in for it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Tenant, string Machine), DateTime> _handedAt = new();

    /// <param name="modelFor">Builds the model for one account's screening (<see cref="MailDeskModelFactory.Create"/>
    /// in production); asked once per screening, and only when some email needs a model question.</param>
    /// <param name="nowUtc">The clock decisions and checks are stamped with.</param>
    /// <param name="time">The clock the screening's deadline runs on; the system clock when null.</param>
    public MailDeskService(MailDeskChannelStore channels, MailDeskRulesStore rules, MailDeskDecisionStore decisions,
        MailDeskSettingsStore settings, MailDeskChannelCheckStore checks, Func<TenantId, IMailDeskModel> modelFor,
        Func<DateTime> nowUtc, TimeProvider? time = null)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _modelFor = modelFor ?? throw new ArgumentNullException(nameof(modelFor));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _time = time ?? TimeProvider.System;
    }

    // ---------- the memory handed to the check (section 4) ----------

    /// <summary>The memory a Director on <paramref name="machine"/> is handed with the Mail Desk start.</summary>
    public string HandMemory(TenantId tenant, string? kept, string machine)
    {
        var handed = MailDeskMemory.Hand(kept, _channels.List(tenant), machine);
        _handedAt[HandedKey(tenant, machine)] = DateTime.SpecifyKind(_nowUtc(), DateTimeKind.Utc);
        FileLog.Write($"[MailDeskService] HandMemory: account={tenant.ToLogString()}, machine={machine}, channels={MailDeskMemory.HandedTo(_channels.List(tenant), machine).Count}");
        return handed;
    }

    /// <summary>
    /// The sentence a failed Mail Desk check is recorded with when no active channel names the reporting machine - the
    /// check then fails with phase 2's "no channels" sentence, and this says the fix instead. Null when one does.
    /// </summary>
    public string? NoChannelSentence(TenantId tenant, string machine)
        => MailDeskMemory.HandedTo(_channels.List(tenant), machine).Count > 0
            ? null
            : $"the Mail Desk start runs on {machine}, but no channel names it: add it with maildesk channel add <address> --provider outlook --machine {machine} --why \"<text>\"";

    // ---------- the screening (sections 1, 3 and 8) ----------

    /// <summary>
    /// Screen one successful report of the Mail Desk start. Answers what the run came to - <c>screened</c>, or
    /// <c>failed</c> when the report broke its contract or the memory could not be kept - with the memory to keep and
    /// each mailbox's check. Never throws for a report: a report it cannot read is a <c>failed</c> run with the reason.
    /// </summary>
    /// <param name="tenant">The account.</param>
    /// <param name="machine">The machine of the Director that ran the check.</param>
    /// <param name="context">The context the check printed (phase 2, section 4.1).</param>
    /// <param name="returnedMemory">The memory the check wrote back.</param>
    /// <param name="keptMemory">The memory kept before this run.</param>
    /// <param name="lifetime">The Gateway's lifetime, cancelled only when it stops.</param>
    public async Task<MailDeskScreeningRun> ScreenAsync(TenantId tenant, string machine, string? context,
        string? returnedMemory, string? keptMemory, CancellationToken lifetime)
    {
        FileLog.Write($"[MailDeskService] ScreenAsync: account={tenant.ToLogString()}, machine={machine}");
        var now = _nowUtc();
        var all = _channels.List(tenant, includeRemoved: true);
        var byId = all.ToDictionary(c => c.Id);

        var (sections, unreadable) = MailDeskReport.Read(context);
        if (unreadable is not null)
            return MailDeskScreeningRun.Failed(unreadable);
        foreach (var section in sections)
            if (!byId.ContainsKey(section.ChannelId))
                return MailDeskScreeningRun.Failed($"the screening check reported channel {section.ChannelId}, which is not a channel of this account");
        var handedIds = sections.Select(s => s.ChannelId).ToHashSet();
        // A HANDED CHANNEL LEFT OUT IS A DEFECT, REFUSED (section 4; review of 4243, finding 2) - even when the context and
        // the memory agree on leaving it out, or the mailbox would silently stop being read. Every active channel that
        // names this machine and was neither added nor changed after the memory was HANDED OUT was in that memory, so it
        // must be in the report; one added or changed since is excused, because that report could not have held it. The
        // time is the Gateway's own - when it handed the memory out, or, after a restart lost that, when the report
        // arrived - never the Director's stamp, so a Director whose clock runs behind excuses nothing (finding 5).
        var handedAt = _handedAt.TryGetValue(HandedKey(tenant, machine), out var recorded) ? recorded : (DateTime?)null;
        var at = handedAt ?? DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var missing = MailDeskMemory.HandedTo(all, machine)
            .Where(c => !handedIds.Contains(c.Id)
                        && DateTime.SpecifyKind(c.CreatedUtc, DateTimeKind.Utc) <= at
                        && DateTime.SpecifyKind(c.ChangedUtc, DateTimeKind.Utc) <= at)
            .ToList();
        if (missing.Count > 0)
        {
            FileLog.Write($"[MailDeskService] ScreenAsync: {missing.Count} handed channels missing from the report, judged against the {(handedAt is null ? "report's arrival" : "hand-out")} at {at:O}");
            return MailDeskScreeningRun.Failed(
                "the screening check left out " + string.Join(", ", missing.Select(c => $"channel {c.Id:D} ({c.Address})")) +
                $", which it was handed on {machine}, so the report was not screened and no memory was kept");
        }

        // The memory is checked BEFORE any email is screened: a check that dropped or invented a channel is a defect,
        // and its report is not screened at all.
        var precheck = MailDeskMemory.Merge(keptMemory, returnedMemory, handedIds, Array.Empty<Guid>(), all, now);
        if (precheck.Refusal is { } broken)
            return MailDeskScreeningRun.Failed(broken);

        // THE FILING RESULTS (phase 4, sections 2.2 and 2.3), written before any email is screened: every result must
        // name a decision of its channel, or the report is not trusted at all and NONE of its results is written - a
        // move it did make is then retried from the kept queue next run and comes back already-gone (review of 4274,
        // the observation). A trusted report's results are written whether or not the memory is kept, because the moves
        // were made; a result for a decision already holding an outcome is ignored.
        if (FilingRefusal(tenant, sections) is { } unknown)
        {
            FileLog.Write($"[MailDeskService] ScreenAsync: account={tenant.ToLogString()}, machine={machine}: {unknown}");
            return MailDeskScreeningRun.Failed(unknown);
        }
        var filed = RecordFiling(tenant, sections, now);

        var rules = _rules.Newest(tenant);
        var settings = _settings.Read(tenant);
        var spend = MailDeskSpendLedger.Start(settings, _decisions.SpentMicros(tenant));
        IMailDeskModel? model = null;
        using var deadline = new CancellationTokenSource(ScreeningDeadline, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lifetime);

        var tally = sections.ToDictionary(s => s.ChannelId, _ => new MailboxTally());
        var spendStopped = false;
        var ordered = sections.Where(s => s.Failure is null)
            .SelectMany(s => s.Messages.Select(m => (Section: s, Message: m)))
            .OrderBy(x => x.Message.ReceivedUtc)
            .ToList();
        foreach (var (section, message) in ordered)
        {
            var channel = byId[section.ChannelId];
            var t = tally[channel.Id];
            t.Reported++;
            var kept = _decisions.Find(tenant, channel.Id, message.MessageKey);
            if (kept is not null && kept.Decision != MailDeskWords.Decisions.Failed)
            {
                t.AlreadyDecided++;
                continue;
            }

            model ??= _modelFor(tenant);
            var decision = await MailDeskScreening.DecideAsync(message, rules, settings, model, spend, linked.Token).ConfigureAwait(false);
            var attempt = (kept?.AttemptCount ?? 0) + 1;
            var cost = AddCost(kept?.CostMicros, decision.CostMicros);
            var layer = decision.Layer;
            var word = decision.Decision;
            var reason = decision.Reason;
            if (decision.ModelQuestionFailed)
            {
                var kind = decision.ErrorKind ?? MailDeskModelErrorKinds.Unexpected;
                if (attempt >= MailDeskReasons.FailuresBeforeKept)
                {
                    word = MailDeskWords.Decisions.KeepForOwner;
                    reason = MailDeskReasons.KeptAfterFailures(kind);
                    t.KeptAfterFailures++;
                }
                else
                {
                    t.ModelFailed++;
                    t.ErrorKinds.Add(kind);
                }
            }
            else if (decision.SpendLimitReached)
            {
                spendStopped = true;
                t.SpendStopped++;
            }
            else
            {
                t.Decided++;
            }

            _decisions.Record(tenant, new MailDeskDecisionInput(
                ChannelId: channel.Id,
                MessageKey: message.MessageKey,
                ReceivedUtc: message.ReceivedUtc,
                SenderAddress: message.Sender,
                Subject: message.Subject,
                Layer: layer,
                Decision: word,
                RouteFactory: word == MailDeskWords.Decisions.Route ? decision.RouteFactory : null,
                RouteSeat: word == MailDeskWords.Decisions.Route ? decision.RouteSeat : null,
                Reason: reason,
                Confidence: word == decision.Decision ? decision.Confidence : null,
                CostMicros: cost,
                Mode: channel.Mode,
                Outcome: null,
                OutcomeDetail: null,
                DecidedUtc: _nowUtc(),
                RulesVersion: rules.Version,
                AttemptCount: attempt));
        }

        if (spendStopped)
            _settings.MarkSpendStopped(tenant, _nowUtc());

        var writtenBack = tally.Where(kv => kv.Value.ModelFailed > 0).Select(kv => kv.Key).ToHashSet();
        var checks = new List<MailDeskMailboxCheck>();
        foreach (var section in sections)
        {
            var t = tally[section.ChannelId];
            if (section.Failure is { } failure)
                checks.Add(new MailDeskMailboxCheck(section.ChannelId, false, failure.Sentence, failure.Detail));
            else if (t.ModelFailed > 0)
                checks.Add(new MailDeskMailboxCheck(section.ChannelId, false,
                    ModelFailureSentence(t.ModelFailed, t.Reported, byId[section.ChannelId].Address, t.ErrorKinds), ""));
            else
                checks.Add(new MailDeskMailboxCheck(section.ChannelId, true, null, null));
        }

        // THE FILING QUEUE (phase 4, section 2.1): a handed mailbox whose check fields move on, still live, queues every
        // live archive and junk decision it holds that has no outcome yet and whose key is in the key table the check
        // returned. One whose key is not there waits for the run that rereads it - unless the check's cursor has
        // already read past its receive time, when it will never come back (moved or deleted before the reread, or its
        // key pruned while the channel was in observe mode): it ends failed, saying so (review of 4274, finding 1).
        var toQueue = new Dictionary<Guid, IReadOnlyList<MailDeskFilingItem>>();
        var ended = 0;
        var waiting = 0;
        foreach (var section in sections)
        {
            if (section.Failure is not null || writtenBack.Contains(section.ChannelId)
                || byId[section.ChannelId].Mode != MailDeskWords.Modes.Live)
                continue;
            var seen = MailDeskMemory.Seen(returnedMemory, section.ChannelId);
            var items = new List<MailDeskFilingItem>();
            foreach (var d in _decisions.AwaitingFiling(tenant, section.ChannelId))
            {
                if (seen.Keys.Contains(d.MessageKey))
                    items.Add(new MailDeskFilingItem(d.MessageKey, d.Decision));
                else if (seen.CursorNewestUtc is { } newest && newest > DateTime.SpecifyKind(d.ReceivedUtc, DateTimeKind.Utc)
                         && _decisions.RecordFiling(tenant, section.ChannelId, d.MessageKey, MailDeskWords.Outcomes.Failed, NotSeenAgain, now) == MailDeskFilingWrite.Written)
                    ended++;
                else
                    waiting++;
            }
            toQueue[section.ChannelId] = items;
        }

        var merged = MailDeskMemory.Merge(keptMemory, returnedMemory, handedIds, writtenBack, all, now, toQueue);
        var sum = tally.Values.Aggregate(new MailboxTally(), (a, b) => a.Add(b));
        var summary = $"{sum.Decided} decided, {sum.AlreadyDecided} already decided, {sum.ModelFailed} to read again, " +
                      $"{sum.KeptAfterFailures} kept after 3 failures, {sum.SpendStopped} stopped by the spend limit, " +
                      $"{checks.Count(c => !c.Ok)} of {checks.Count} mailboxes failing";
        if (filed.Total > 0 || toQueue.Values.Any(q => q.Count > 0) || ended > 0 || waiting > 0)
            summary += $"; filing: {filed.Filed} filed, {filed.AlreadyGone} already gone, {filed.Failed} refused, {filed.Ignored} already settled, " +
                       $"{toQueue.Values.Sum(q => q.Count)} in the queue, {waiting} waiting to be read again, {ended} not seen again";
        FileLog.Write($"[MailDeskService] ScreenAsync: account={tenant.ToLogString()}, machine={machine}, rules v{rules.Version}: {summary}; memory {(merged.Refusal is null ? "kept" : "NOT kept")}");
        return merged.Refusal is { } refused
            ? new MailDeskScreeningRun(TriggerRunOutcome.Failed, refused, null, checks)
            : new MailDeskScreeningRun(TriggerRunOutcome.Screened, summary, merged.Memory, checks);
    }

    /// <summary>
    /// Record each mailbox's check of one run (section 6.2): every failed check, and a good check after a failure or as
    /// a mailbox's first. Called once the run row exists, so each row names its run.
    /// </summary>
    public void RecordChecks(TenantId tenant, Guid runId, DateTime checkedUtc, string machine, IReadOnlyList<MailDeskMailboxCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count == 0) return;
        var inputs = checks.Select(c => c.Ok
                ? MailDeskChannelCheckInput.Read(c.ChannelId, runId, checkedUtc, machine)
                : MailDeskChannelCheckInput.Failed(c.ChannelId, runId, checkedUtc, machine, c.Sentence!, c.Detail))
            .ToList();
        var written = _checks.RecordAll(tenant, inputs);
        FileLog.Write($"[MailDeskService] RecordChecks: account={tenant.ToLogString()}, run={runId}, checks={inputs.Count}, written={written.Count(w => w.Written)}");
    }

    /// <summary>Why the report's filing results cannot be trusted - a result naming a message that has no decision on
    /// its channel - or null.</summary>
    private string? FilingRefusal(TenantId tenant, IReadOnlyList<MailDeskReport.Section> sections)
    {
        foreach (var section in sections)
            foreach (var result in section.Filing)
                if (_decisions.Find(tenant, section.ChannelId, result.MessageKey) is null)
                    return $"the screening check broke its contract: it reports filing {result.MessageKey} on channel {section.ChannelId}, which has no decision for it, so the report was not screened and no memory was kept";
        return null;
    }

    /// <summary>Write each filing result on its decision (phase 4 contract, section 2.3).</summary>
    private FilingTally RecordFiling(TenantId tenant, IReadOnlyList<MailDeskReport.Section> sections, DateTime nowUtc)
    {
        var tally = new FilingTally();
        foreach (var section in sections)
        {
            foreach (var result in section.Filing)
            {
                var decision = _decisions.Find(tenant, section.ChannelId, result.MessageKey)!;
                if (!string.Equals(decision.Decision, result.Decision, StringComparison.Ordinal))
                    FileLog.Write($"[MailDeskService] RecordFiling: decision {decision.Id} is {decision.Decision} but was filed as '{result.Decision}'; the decision row's word is used");
                var (outcome, detail) = FilingOutcome(decision.Decision, result);
                var write = _decisions.RecordFiling(tenant, section.ChannelId, result.MessageKey, outcome, detail, nowUtc);
                if (write != MailDeskFilingWrite.Written) { tally.Ignored++; continue; }
                switch (outcome)
                {
                    case MailDeskWords.Outcomes.Filed: tally.Filed++; break;
                    case MailDeskWords.Outcomes.AlreadyGone: tally.AlreadyGone++; break;
                    default: tally.Failed++; break;
                }
            }
        }
        return tally;
    }

    /// <summary>The outcome and detail a filing result writes on its decision (phase 4 contract, section 2.3).</summary>
    internal static (string Outcome, string Detail) FilingOutcome(string decision, MailDeskFilingResult result) => result.Result switch
    {
        MailDeskReport.FilingResults.Moved => (MailDeskWords.Outcomes.Filed,
            decision == MailDeskWords.Decisions.Junk ? MovedToJunk : MovedToArchive),
        MailDeskReport.FilingResults.AlreadyGone => (MailDeskWords.Outcomes.AlreadyGone, AlreadyGoneDetail),
        MailDeskReport.FilingResults.Refused => (MailDeskWords.Outcomes.Failed, $"the screening check refused the move: {result.Reason}"),
        _ => throw new InvalidOperationException($"The filing result '{result.Result}' of {result.MessageKey} was let through by MailDeskReport.Read."),
    };

    /// <summary>The detail of an email moved to Archive.</summary>
    public const string MovedToArchive = "moved to Archive";

    /// <summary>The detail of an email moved to Junk Email.</summary>
    public const string MovedToJunk = "moved to Junk Email";

    /// <summary>The detail of a live archive or junk the check read past without reporting it again, so it can never be
    /// queued (review of 4274, finding 1).</summary>
    public const string NotSeenAgain = "the email was not seen again to file it: the screening check read past it and no longer knows it (moved or deleted before it was read again, or read too long ago)";

    /// <summary>The detail of an email that was gone when its move was tried.</summary>
    public const string AlreadyGoneDetail = "not in the Inbox any more when the move was tried (moved or deleted before)";

    private sealed class FilingTally
    {
        public int Filed;
        public int AlreadyGone;
        public int Failed;
        public int Ignored;
        public int Total => Filed + AlreadyGone + Failed + Ignored;
    }

    private static (string, string) HandedKey(TenantId tenant, string machine) => (tenant.Value, machine.ToUpperInvariant());

    /// <summary>"the model could not be asked about &lt;n&gt; of &lt;m&gt; emails on &lt;address&gt; (&lt;error kind&gt;); they are read again next run".</summary>
    internal static string ModelFailureSentence(int failed, int reported, string address, IEnumerable<string> errorKinds)
        => $"the model could not be asked about {failed.ToString(CultureInfo.InvariantCulture)} of {reported.ToString(CultureInfo.InvariantCulture)} " +
           $"emails on {address} ({string.Join(", ", errorKinds.Distinct(StringComparer.Ordinal))}); they are read again next run";

    private static long? AddCost(long? before, long? now)
        => before is null && now is null ? null : (before ?? 0) + (now ?? 0);

    private sealed class MailboxTally
    {
        public int Reported;
        public int AlreadyDecided;
        public int Decided;
        public int ModelFailed;
        public int KeptAfterFailures;
        public int SpendStopped;
        public readonly List<string> ErrorKinds = new();

        public MailboxTally Add(MailboxTally other)
        {
            Reported += other.Reported;
            AlreadyDecided += other.AlreadyDecided;
            Decided += other.Decided;
            ModelFailed += other.ModelFailed;
            KeptAfterFailures += other.KeptAfterFailures;
            SpendStopped += other.SpendStopped;
            return this;
        }
    }
}

/// <summary>What one screening came to, for <see cref="Factory.Triggers.TriggerService"/> to record.</summary>
/// <param name="Outcome"><see cref="TriggerRunOutcome.Screened"/> or <see cref="TriggerRunOutcome.Failed"/>.</param>
/// <param name="Reason">What the screening did, in counts; or why it failed.</param>
/// <param name="Memory">The memory to keep, or null when none may be kept.</param>
/// <param name="Checks">Each mailbox's check of this run.</param>
public sealed record MailDeskScreeningRun(string Outcome, string? Reason, string? Memory, IReadOnlyList<MailDeskMailboxCheck> Checks)
{
    public static MailDeskScreeningRun Failed(string reason)
        => new(TriggerRunOutcome.Failed, reason, null, Array.Empty<MailDeskMailboxCheck>());
}

/// <summary>One mailbox's check in one run: read, or failed with the sentence that names the fix.</summary>
public sealed record MailDeskMailboxCheck(Guid ChannelId, bool Ok, string? Sentence, string? Detail);

/// <summary>
/// The screening check's context, version 2 (phase 2 contract, section 4.1): one section per channel it was handed,
/// each with its messages or the failure that stopped that channel. Every section names its channel id, because the
/// Gateway hands every channel with one.
/// </summary>
public static class MailDeskReport
{
    /// <summary>One channel's section: its messages, the filing it carried out (phase 4 contract, section 2.2), or the
    /// failure that stopped it.</summary>
    public sealed record Section(Guid ChannelId, IReadOnlyList<MailDeskMessage> Messages, MailDeskReportFailure? Failure,
        IReadOnlyList<MailDeskFilingResult> Filing)
    {
        public Section(Guid channelId, IReadOnlyList<MailDeskMessage> messages, MailDeskReportFailure? failure)
            : this(channelId, messages, failure, Array.Empty<MailDeskFilingResult>()) { }
    }

    /// <summary>What the check says one filing item came to (phase 2 contract, section 3.4).</summary>
    public static class FilingResults
    {
        /// <summary>Graph moved it.</summary>
        public const string Moved = "moved";
        /// <summary>Graph answered 404: not in the Inbox any more - never read as moved by us.</summary>
        public const string AlreadyGone = "already-gone";
        /// <summary>Nothing was sent; the reason says why.</summary>
        public const string Refused = "refused";
        public static readonly IReadOnlyList<string> All = new[] { Moved, AlreadyGone, Refused };
    }

    /// <summary>Read the context. Answers the sections, or why the context breaks the contract.</summary>
    public static (IReadOnlyList<Section> Sections, string? Refusal) Read(string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return (Array.Empty<Section>(), "the screening check printed no context, so nothing could be screened");
        try
        {
            using var doc = JsonDocument.Parse(context);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refused("its context is not a JSON object");
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != MailDeskService.ContextVersion)
                return Refused($"its context is not version {MailDeskService.ContextVersion}");
            if (!root.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
                return Refused("its context has no channels list");

            var sections = new List<Section>();
            foreach (var c in channels.EnumerateArray())
            {
                if (!Guid.TryParse(Text(c, "id"), out var id))
                    return Refused("a channel section of its context names no channel id");
                if (sections.Any(s => s.ChannelId == id))
                    return Refused($"channel {id} has two sections in its context");
                MailDeskReportFailure? failure = null;
                if (c.TryGetProperty("failure", out var f) && f.ValueKind == JsonValueKind.Object)
                {
                    var sentence = Text(f, "sentence");
                    if (string.IsNullOrWhiteSpace(sentence))
                        return Refused($"the failure of channel {id} has no sentence");
                    failure = new MailDeskReportFailure(sentence, Text(f, "detail") ?? "");
                }
                var messages = new List<MailDeskMessage>();
                if (c.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in ms.EnumerateArray())
                    {
                        var key = Text(m, "messageKey");
                        if (string.IsNullOrWhiteSpace(key))
                            return Refused($"a message of channel {id} has no message key");
                        if (!DateTime.TryParse(Text(m, "receivedUtc"), CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var received))
                            return Refused($"message {key} of channel {id} has no receive time");
                        messages.Add(new MailDeskMessage(
                            MessageKey: key,
                            ReceivedUtc: DateTime.SpecifyKind(received, DateTimeKind.Utc),
                            Sender: (Text(m, "sender") ?? "").Trim().ToLowerInvariant(),
                            SenderName: Text(m, "senderName") ?? "",
                            Subject: Text(m, "subject") ?? "",
                            KnownSender: m.TryGetProperty("knownSender", out var known) && known.ValueKind == JsonValueKind.True,
                            Preview: Text(m, "preview") ?? ""));
                    }
                }
                else if (failure is null)
                    return Refused($"channel {id} has no messages list");
                if (failure is not null && messages.Count > 0)
                    return Refused($"channel {id} reports messages and a failure");
                var filing = new List<MailDeskFilingResult>();
                if (c.TryGetProperty("filing", out var fs))
                {
                    if (fs.ValueKind != JsonValueKind.Array)
                        return Refused($"the filing of channel {id} is not a list");
                    foreach (var item in fs.EnumerateArray())
                    {
                        var key = Text(item, "messageKey");
                        if (string.IsNullOrWhiteSpace(key))
                            return Refused($"a filing result of channel {id} has no message key");
                        var result = Text(item, "result");
                        if (!MailDeskWords.IsOneOf(result, FilingResults.All))
                            return Refused($"the filing result of {key} on channel {id} is '{result}', which is not {MailDeskWords.InWords(FilingResults.All)}");
                        var reason = Text(item, "reason");
                        if (result == FilingResults.Refused && string.IsNullOrWhiteSpace(reason))
                            return Refused($"the refused filing of {key} on channel {id} gives no reason");
                        if (filing.Any(x => x.MessageKey == key))
                            return Refused($"{key} has two filing results on channel {id}");
                        filing.Add(new MailDeskFilingResult(key, Text(item, "decision") ?? "", result!, reason));
                    }
                }
                if (failure is not null && filing.Count > 0)
                    return Refused($"channel {id} reports filing and a failure");
                sections.Add(new Section(id, messages, failure, filing));
            }
            return (sections, null);
        }
        catch (JsonException)
        {
            return Refused("its context is not JSON");
        }
    }

    private static (IReadOnlyList<Section>, string?) Refused(string what)
        => (Array.Empty<Section>(), $"the screening check broke its contract: {what}");

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

/// <summary>One filing item the check attempted: the key, the decision it was queued with, what came of it, and the
/// reason of a refusal.</summary>
public sealed record MailDeskFilingResult(string MessageKey, string Decision, string Result, string? Reason);

/// <summary>A channel's failure in the context: the check's sentence (which names the fix) and its detail.</summary>
public sealed record MailDeskReportFailure(string Sentence, string Detail);
