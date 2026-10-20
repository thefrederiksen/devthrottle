using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// ROUTING IN LIVE MODE (Mail Desk mission, phase 4 contract, sections 3.2 to 3.4): after a Mail Desk run is recorded
/// <c>screened</c>, <see cref="TriggerService"/> runs this, and it gives every email routed to a seat to a session of
/// that seat, through the SAME starter the trigger uses (production: <c>MachineSessionSpawner</c>), on the machine of
/// the Director that ran the check, naming that Director.
///
/// In order: create a queued row for every live route and mail-desk-agent decision that has none; settle the starts
/// whose outcome was not known (adopt the session by its exact name, or count the start failed once the grace is
/// over); re-route the orphans (an open row whose session ended without done or return); then start ONE session per
/// seat that is not busy, holding at most <see cref="MaxEmailsPerSession"/> of its queued emails, oldest first, and
/// record what the start came to.
///
/// NO BODY TEXT: a seat is told the routed id, the mailbox, the sender, the subject, the receive time, why it was
/// routed and any return note - never the preview. It reads the email itself.
///
/// One run per account at a time on this Gateway: a second is skipped and logged, and the next screening runs it again.
/// </summary>
public sealed class MailDeskRouter
{
    /// <summary>The most emails one session is given; the rest wait for the next run.</summary>
    public const int MaxEmailsPerSession = 10;

    /// <summary>The largest check.json context a routed session is given, in bytes. Under the 8 KB a check's context
    /// may be, so the same reader takes it.</summary>
    public const int MaxContextBytes = 7168;

    /// <summary>
    /// How long a routed session the Gateway cannot see at all still counts as running (contract 3.2 step 3; review of
    /// 4275, finding 1). A session is ENDED only on evidence - a row saying it exited or crashed, or its history record
    /// holding an ending that is final (<see cref="EndedForGood"/>). Its absence alone
    /// proves nothing: after a Gateway restart, or while its Director is away and a backup holds the claim, a live
    /// session is invisible, and re-routing it would put two sessions on one email. So absence counts as ended only
    /// this long after the start - long enough to outlast a restart or a backup takeover, short enough that an email
    /// whose Director died with it is still never lost.
    /// </summary>
    public static readonly TimeSpan UnseenSessionEndsAfter = TimeSpan.FromHours(24);

    /// <summary>The <c>kind</c> of the check.json context of a routed session.</summary>
    public const string ContextKind = "mail-desk-routed";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly MailDeskRoutedStore _routed;
    private readonly MailDeskChannelStore _channels;
    private readonly MailDeskDecisionStore _decisions;
    private readonly TriggerSessionStarter _startSession;
    private readonly Func<TenantId, string, SessionDto?> _findSession;
    private readonly Func<TenantId, string, SessionDto?> _findSessionByName;
    private readonly Func<TenantId, IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> _endingsOf;
    private readonly Func<TenantId, string, PublishedFactoryDefinition?> _definitionOf;
    private readonly Func<TenantId, TimeZoneInfo> _timeZone;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _startLifetime;
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    /// <param name="startSession">The trigger's own starter.</param>
    /// <param name="findSession">The last row any Director of the account reported for a session, or null.</param>
    /// <param name="findSessionByName">The last row any Director of the account reported for a session started from
    /// the Gateway with this exact name, or null - how a start whose outcome was not known finds its session. The name
    /// ("Mail Desk - &lt;factory&gt;/&lt;seat&gt; - ...") is never a trigger's.</param>
    /// <param name="endingsOf">How each of these sessions ended, from their history records
    /// (<see cref="SessionHistoryStore.EndingsOf(TenantId, IReadOnlyCollection{string})"/> in production), asked once
    /// per run for every session the run reads. A session with no record is absent from the answer.</param>
    /// <param name="definitionOf">The published definition of a factory, or null; throws
    /// <see cref="InvalidOperationException"/> when the published version cannot be read.</param>
    /// <param name="timeZone">The account's time zone, for the time in a session's name.</param>
    /// <param name="startLifetime">The Gateway's lifetime, which every start runs on.</param>
    public MailDeskRouter(MailDeskRoutedStore routed, MailDeskChannelStore channels, MailDeskDecisionStore decisions,
        TriggerSessionStarter startSession, Func<TenantId, string, SessionDto?> findSession,
        Func<TenantId, string, SessionDto?> findSessionByName,
        Func<TenantId, IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> endingsOf,
        Func<TenantId, string, PublishedFactoryDefinition?> definitionOf,
        Func<TenantId, TimeZoneInfo> timeZone, Func<DateTime> nowUtc, CancellationToken startLifetime)
    {
        _routed = routed ?? throw new ArgumentNullException(nameof(routed));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _startSession = startSession ?? throw new ArgumentNullException(nameof(startSession));
        _findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));
        _findSessionByName = findSessionByName ?? throw new ArgumentNullException(nameof(findSessionByName));
        _endingsOf = endingsOf ?? throw new ArgumentNullException(nameof(endingsOf));
        _definitionOf = definitionOf ?? throw new ArgumentNullException(nameof(definitionOf));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        _startLifetime = startLifetime;
    }

    /// <summary>
    /// Route the account's emails (section 3.2). <paramref name="trigger"/> is the Mail Desk start whose run was just
    /// recorded; <paramref name="directorId"/> and <paramref name="machine"/> are the Director that ran its check and
    /// that Director's machine, where every session starts; <paramref name="checkedUtc"/> is the run's check time.
    /// </summary>
    public async Task<MailDeskRouterRun> RouteAsync(TenantId tenant, TriggerEntity trigger, string directorId, string machine,
        DateTime checkedUtc)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(directorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        if (!_running.TryAdd(tenant.Value, 0))
        {
            FileLog.Write($"[MailDeskRouter] RouteAsync: account={tenant.ToLogString()} is already being routed; this run is skipped");
            return MailDeskRouterRun.Skipped;
        }
        try
        {
            FileLog.Write($"[MailDeskRouter] RouteAsync: account={tenant.ToLogString()}, director={directorId}, machine={machine}");
            var tally = new MailDeskRouterRun();
            tally.Created = _routed.CreateMissing(tenant, _nowUtc()).Count;
            SettleUnknownStarts(tenant, tally);
            var endings = EndingsOfThisRun(tenant);
            RerouteOrphans(tenant, endings, tally);
            await StartSeatsAsync(tenant, trigger, directorId, machine, checkedUtc, endings, tally).ConfigureAwait(false);
            FileLog.Write($"[MailDeskRouter] RouteAsync: account={tenant.ToLogString()}: {tally}");
            return tally;
        }
        finally
        {
            _running.TryRemove(tenant.Value, out _);
        }
    }

    // ---------- step 2: settle the starts whose outcome was not known ----------

    private void SettleUnknownStarts(TenantId tenant, MailDeskRouterRun tally)
    {
        var unknown = _routed.Open(tenant).Where(r => r.SessionId is null && r.StartName is not null)
            .GroupBy(r => r.StartName!, StringComparer.Ordinal);
        foreach (var start in unknown)
        {
            var now = _nowUtc();
            var found = _findSessionByName(tenant, start.Key);
            if (found is not null && !string.IsNullOrEmpty(found.SessionId))
            {
                var adopted = _routed.Adopt(tenant, start.Key, found.SessionId, found.MachineName, now);
                tally.Adopted += adopted.Count;
                FileLog.Write($"[MailDeskRouter] SettleUnknownStarts: adopted session {found.SessionId} for {adopted.Count} rows");
                continue;
            }
            var startedUtc = start.Max(r => Utc(r.StartedUtc!.Value));
            if (now - startedUtc < TriggerService.StartGrace) continue;
            var error = $"no session named '{start.Key}' showed up within {(int)TriggerService.StartGrace.TotalMinutes} minutes of a start whose outcome was not known";
            foreach (var row in start)
            {
                var after = _routed.RecordStartFailure(tenant, row.Id, error, now);
                tally.StartsFailed++;
                if (after?.State == MailDeskWords.RoutedStates.Returned) tally.ToOwner++;
            }
            FileLog.Write($"[MailDeskRouter] SettleUnknownStarts: a start of {start.Count()} rows found no session within the grace; counted failed");
        }
    }

    // ---------- step 3: the orphans ----------

    /// <summary>
    /// The history endings of every session this run reads, asked ONCE (contract 3.2 step 3): the session of every
    /// open row, and the newest Mail Desk session of every seat with a queued email (the busy-seat rule). Read after
    /// the unknown starts are settled, so an adopted session is in it. The steps after this start new sessions but
    /// never read one: each seat is started at most once a run.
    /// </summary>
    private IReadOnlyDictionary<string, SessionEndingFact> EndingsOfThisRun(TenantId tenant)
    {
        var open = _routed.Open(tenant);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in open.Where(r => r.SessionId is not null))
            ids.Add(row.SessionId!);
        foreach (var (factory, seat) in open.Where(r => r.SessionId is null && r.StartName is null).Select(r => (r.Factory, r.Seat)).Distinct())
        {
            if (_routed.StartsOf(tenant, factory, seat).FirstOrDefault(r => r.SessionId is not null) is { } newest)
                ids.Add(newest.SessionId!);
        }
        if (ids.Count == 0) return new Dictionary<string, SessionEndingFact>(StringComparer.OrdinalIgnoreCase);
        var endings = new Dictionary<string, SessionEndingFact>(_endingsOf(tenant, ids), StringComparer.OrdinalIgnoreCase);
        FileLog.Write($"[MailDeskRouter] EndingsOfThisRun: {ids.Count} sessions read, {endings.Count} with a history record");
        return endings;
    }

    private void RerouteOrphans(TenantId tenant, IReadOnlyDictionary<string, SessionEndingFact> endings, MailDeskRouterRun tally)
    {
        var withSession = _routed.Open(tenant).Where(r => r.SessionId is not null)
            .GroupBy(r => r.SessionId!, StringComparer.OrdinalIgnoreCase);
        foreach (var session in withSession)
        {
            var startedUtc = session.Max(r => r.StartedUtc is { } s ? Utc(s) : DateTime.MinValue);
            if (IsAlive(tenant, session.Key, startedUtc, endings)) continue;
            foreach (var row in session)
            {
                var after = _routed.Reroute(tenant, row.Id, _nowUtc());
                if (after?.State == MailDeskWords.RoutedStates.Returned) tally.ToOwner++;
                else tally.Rerouted++;
            }
            FileLog.Write($"[MailDeskRouter] RerouteOrphans: session {session.Key} ended with {session.Count()} emails open");
        }
    }

    // ---------- step 4 and 5: start, and record ----------

    private async Task StartSeatsAsync(TenantId tenant, TriggerEntity trigger, string directorId, string machine,
        DateTime checkedUtc, IReadOnlyDictionary<string, SessionEndingFact> endings, MailDeskRouterRun tally)
    {
        var channels = _channels.List(tenant, includeRemoved: true).ToDictionary(c => c.Id);
        var queued = new List<(MailDeskRoutedEntity Row, MailDeskDecisionEntity Decision, MailDeskChannelEntity Channel)>();
        foreach (var row in _routed.Open(tenant).Where(r => r.SessionId is null && r.StartName is null))
        {
            // A channel switched back to observe starts no new seat for its emails (section 1): they wait.
            if (!channels.TryGetValue(row.ChannelId, out var channel) || channel.Mode != MailDeskWords.Modes.Live) continue;
            var decision = _decisions.Get(tenant, row.DecisionId)
                           ?? throw new InvalidOperationException($"Routed email {row.Id} names decision {row.DecisionId}, which this account does not have.");
            queued.Add((row, decision, channel));
        }

        var groups = queued.GroupBy(q => (q.Row.Factory, q.Row.Seat))
            .Select(g => g.OrderBy(q => q.Decision.ReceivedUtc).ThenBy(q => q.Row.Id).ToList())
            .OrderBy(g => g[0].Decision.ReceivedUtc)
            .ToList();
        foreach (var group in groups)
        {
            var (factory, seat) = (group[0].Row.Factory, group[0].Row.Seat);
            if (IsSeatBusy(tenant, factory, seat, endings))
            {
                tally.Waiting += group.Count;
                FileLog.Write($"[MailDeskRouter] StartSeatsAsync: {factory}/{seat} is busy; {group.Count} emails wait");
                continue;
            }
            await StartOneAsync(tenant, trigger, directorId, machine, checkedUtc, factory, seat, group, tally).ConfigureAwait(false);
        }
    }

    private async Task StartOneAsync(TenantId tenant, TriggerEntity trigger, string directorId, string machine, DateTime checkedUtc,
        string factory, string seat, List<(MailDeskRoutedEntity Row, MailDeskDecisionEntity Decision, MailDeskChannelEntity Channel)> group,
        MailDeskRouterRun tally)
    {
        var (published, seatDefinition, refusal) = Target(tenant, factory, seat);
        if (refusal is not null)
        {
            foreach (var (row, _, _) in group)
            {
                var after = _routed.Redirect(tenant, row.Id, refusal, _nowUtc());
                if (after?.State == MailDeskWords.RoutedStates.Returned) tally.ToOwner++;
                else tally.Redirected++;
            }
            FileLog.Write($"[MailDeskRouter] StartOneAsync: {factory}/{seat} cannot take {group.Count} emails: {refusal}");
            return;
        }

        var emails = group.Take(MaxEmailsPerSession).Select(q => Email.Of(q.Row, q.Decision, q.Channel)).ToList();
        var context = Context(factory, seat, emails, out var given);
        emails = emails.Take(given).ToList();
        tally.Waiting += group.Count - given;

        var now = _nowUtc();
        var name = SessionName(factory, seat, given, _timeZone(tenant), now);
        var request = FactorySeatStart.Build(published!, seatDefinition!, name, PrePrompt(published!, seatDefinition!, factory, seat, emails)).Request;
        request.FactoryCheck = new FactoryCheckDto
        {
            CheckPath = MailDeskTemplate.CheckPath,
            Count = given,
            Context = context,
            CheckedUtc = Utc(checkedUtc),
            DefinitionVersion = trigger.DefinitionVersion ?? published!.Version,
            Seat = seatDefinition!.Seat,
            // Not one of this seat's starts: routed by the Mail Desk.
            Start = 0,
        };
        request.Director = directorId;
        request.AutoDismiss = true;
        request.Origin = SessionOriginKinds.Schedule;
        // The trigger's own surface (contract 3.3): a Director refuses a surface outside its fixed list, so a new word
        // would make every Director released before it refuse every Mail Desk start. Its routed rows say what it is.
        request.OriginSurface = SessionOriginSurfaces.Trigger;

        var ids = emails.Select(e => e.RoutedId).ToList();
        var begun = _routed.BeginStart(tenant, ids, name, now, now);
        if (begun.Count != ids.Count)
            throw new InvalidOperationException($"Only {begun.Count} of {ids.Count} routed emails for {factory}/{seat} could be named for a start; another writer changed them.");

        TriggerStartAttempt attempt;
        try
        {
            attempt = await _startSession(machine, request, _startLifetime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MailDeskRouter] StartOneAsync: the start for {factory}/{seat} threw {ex.GetType().Name}: {ex.Message}");
            // Cut off by the Gateway stopping, the create may already be on its way: that outcome is unknown.
            attempt = ex is OperationCanceledException && _startLifetime.IsCancellationRequested
                ? TriggerStartAttempt.Unknown("the Gateway stopped while the session was being started.")
                : TriggerStartAttempt.Failed(ex.Message);
        }

        if (!string.IsNullOrEmpty(attempt.SessionId))
        {
            var taken = _routed.Take(tenant, name, attempt.SessionId, machine, _nowUtc());
            tally.Started.Add(new MailDeskRoutedStart(attempt.SessionId, factory, seat, taken.Count));
            FileLog.Write($"[MailDeskRouter] StartOneAsync: started session {attempt.SessionId} on {machine} for {factory}/{seat} with {taken.Count} emails");
        }
        else if (attempt.OutcomeUnknown)
        {
            tally.Unknown += ids.Count;
            FileLog.Write($"[MailDeskRouter] StartOneAsync: the start for {factory}/{seat} has an unknown outcome; {ids.Count} emails wait for a session named by the start: {attempt.Error}");
        }
        else
        {
            var error = attempt.Error ?? "the machine did not return a session";
            foreach (var id in ids)
            {
                var after = _routed.RecordStartFailure(tenant, id, error, _nowUtc());
                tally.StartsFailed++;
                if (after?.State == MailDeskWords.RoutedStates.Returned) tally.ToOwner++;
            }
            FileLog.Write($"[MailDeskRouter] StartOneAsync: the start for {factory}/{seat} failed for {ids.Count} emails: {error}");
        }
    }

    /// <summary>The target factory's published definition and seat, or why it cannot take the email.</summary>
    private (PublishedFactoryDefinition? Published, SeatDefinition? Seat, string? Refusal) Target(TenantId tenant, string factory, string seat)
    {
        PublishedFactoryDefinition? published;
        try
        {
            published = _definitionOf(tenant, factory);
        }
        catch (InvalidOperationException ex)
        {
            return (null, null, ex.Message);
        }
        if (published is null)
            return (null, null, $"'{factory}' has no published definition, so its seat '{seat}' cannot be started for a routed email. Publish the definition, or change the route.");
        if (published.Seat(seat) is not { } held)
            return (null, null, FactorySeatStart.SeatNotInVersion(published, seat));
        return (published, held, null);
    }

    /// <summary>
    /// A BUSY SEAT (section 1): a start of the seat whose outcome is not known yet, inside the start grace (3.4); or the
    /// newest session the Mail Desk started for it has not ended (<see cref="IsAlive"/>).
    /// </summary>
    private bool IsSeatBusy(TenantId tenant, string factory, string seat, IReadOnlyDictionary<string, SessionEndingFact> endings)
    {
        var now = _nowUtc();
        var starts = _routed.StartsOf(tenant, factory, seat);
        if (starts.Any(r => r.State == MailDeskWords.RoutedStates.Open && r.SessionId is null && r.StartName is not null
                            && now - Utc(r.StartedUtc!.Value) < TriggerService.StartGrace))
            return true;
        var newest = starts.FirstOrDefault(r => r.SessionId is not null);
        return newest is not null && IsAlive(tenant, newest.SessionId!, Utc(newest.StartedUtc!.Value), endings);
    }

    /// <summary>Not ended (contract 3.2 step 3): neither the session list says it exited or crashed nor its history
    /// record holds an ending that is final (<see cref="EndedForGood"/>); and either the list has it, or its start is
    /// less than <see cref="UnseenSessionEndsAfter"/> ago. Unlike <see cref="TriggerService.IsLastSessionAlive"/>, which
    /// reads absence after the start grace as ended, because here a wrong "ended" puts two seats on one email.</summary>
    private bool IsAlive(TenantId tenant, string sessionId, DateTime startedUtc, IReadOnlyDictionary<string, SessionEndingFact> endings)
    {
        var row = _findSession(tenant, sessionId);
        if (row is not null && FleetManagerSessions.IsGone(row)) return false;
        // A Director REAPS a session whose agent exits cleanly, and a routed session is started with AutoDismiss, so
        // its row leaves the list: the history record is the evidence of the common end.
        if (endings.TryGetValue(sessionId, out var ending) && EndedForGood(ending.EndingKind))
        {
            FileLog.Write($"[MailDeskRouter] IsAlive: session {sessionId} has the history ending '{ending.EndingKind}'; it counts as ended");
            return false;
        }
        if (row is not null) return true;
        var alive = _nowUtc() - startedUtc < UnseenSessionEndsAfter;
        if (!alive)
            FileLog.Write($"[MailDeskRouter] IsAlive: session {sessionId} has not been seen in the {UnseenSessionEndsAfter.TotalHours:0} hours since its start; it counts as ended");
        return alive;
    }

    /// <summary>
    /// A history ending that proves the session is over: <c>closed</c>, <c>finished</c> or <c>director-stopped</c>.
    /// Never <c>interrupted</c>: the Gateway CONCLUDES that from a silent Director and reopens the record when the
    /// session reappears - the backup takeover the 24 hours protect.
    /// </summary>
    internal static bool EndedForGood(string? endingKind)
        => endingKind is SessionHistoryEndings.Closed or SessionHistoryEndings.Finished or SessionHistoryEndings.DirectorStopped;

    // ---------- what the session is given ----------

    /// <summary>One email as a routed session is told it: never its body.</summary>
    internal sealed record Email(Guid RoutedId, string Mailbox, string Sender, string Subject, DateTime ReceivedUtc,
        string Layer, string Reason, string? ReturnNote)
    {
        public static Email Of(MailDeskRoutedEntity row, MailDeskDecisionEntity decision, MailDeskChannelEntity channel)
            => new(row.Id, channel.Address, Clean(decision.SenderAddress), Clean(decision.Subject), Utc(decision.ReceivedUtc),
                decision.Layer, Clean(decision.Reason), row.ReturnNote is null ? null : Clean(row.ReturnNote));
    }

    /// <summary>
    /// The check.json context (section 3.3): <c>{"version":1,"kind":"mail-desk-routed","factory","seat","emails":[..]}</c>,
    /// at most <see cref="MaxContextBytes"/> bytes. Emails are added oldest first while they fit; <paramref name="given"/>
    /// is how many did. The first always goes in: when it alone would be too large, its note, subject and reason are cut
    /// until it fits.
    /// </summary>
    internal static string Context(string factory, string seat, IReadOnlyList<Email> emails, out int given)
    {
        var list = new JsonArray();
        var doc = new JsonObject
        {
            ["version"] = 1,
            ["kind"] = ContextKind,
            ["factory"] = factory,
            ["seat"] = seat,
            ["emails"] = list,
        };
        given = 0;
        foreach (var email in emails)
        {
            var candidate = given == 0 ? Fitting(doc, list, email) : email;
            list.Add(Node(candidate));
            if (Bytes(doc) > MaxContextBytes)
            {
                list.RemoveAt(list.Count - 1);
                break;
            }
            given++;
        }
        return doc.ToJsonString(Compact);
    }

    private static Email Fitting(JsonObject doc, JsonArray list, Email email)
    {
        var e = email;
        while (true)
        {
            list.Add(Node(e));
            var fits = Bytes(doc) <= MaxContextBytes;
            list.RemoveAt(list.Count - 1);
            if (fits) return e;
            // Cut the longest of the three free texts by half, and try again.
            var note = e.ReturnNote ?? "";
            if (note.Length >= e.Subject.Length && note.Length >= e.Reason.Length && note.Length > 0) e = e with { ReturnNote = Half(note) };
            else if (e.Subject.Length >= e.Reason.Length && e.Subject.Length > 0) e = e with { Subject = Half(e.Subject) };
            else if (e.Reason.Length > 0) e = e with { Reason = Half(e.Reason) };
            else return e;
        }
    }

    private static string Half(string text) => text.Length <= 3 ? "" : text[..(text.Length / 2)] + "...";

    private static JsonObject Node(Email e) => new()
    {
        ["routedId"] = e.RoutedId.ToString("D"),
        ["mailbox"] = e.Mailbox,
        ["sender"] = e.Sender,
        ["subject"] = e.Subject,
        ["receivedUtc"] = e.ReceivedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["layer"] = e.Layer,
        ["reason"] = e.Reason,
        ["returnNote"] = e.ReturnNote,
    };

    private static int Bytes(JsonObject doc) => Encoding.UTF8.GetByteCount(doc.ToJsonString(Compact));

    /// <summary>"Mail Desk - &lt;factory&gt;/&lt;seat&gt; - &lt;n&gt; emails - &lt;yyyy-MM-dd HH:mm&gt;", in the
    /// account's time zone, "1 email" for one.</summary>
    public static string SessionName(string factory, string seat, int count, TimeZoneInfo zone, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(Utc(nowUtc), zone);
        return $"Mail Desk - {factory}/{seat} - {Emails(count)} - {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The routed session's first prompt (section 3.3): it opens as the seat's scheduled run opens - who the seat is, its
    /// brief at the published version, where its kit is (<see cref="ScheduledRunOpening"/>, the one place that wording
    /// lives) - so the seat knows its own role; then a blank line and the Mail Desk's text.
    /// </summary>
    internal static string PrePrompt(PublishedFactoryDefinition published, SeatDefinition seatDefinition, string factory,
        string seat, IReadOnlyList<Email> emails)
        => ScheduledRunOpening.MailDeskOpening(ScheduledRunOpening.SeatOf(published, seatDefinition)) + "\n\n"
           + Prompt(factory, seat, emails);

    /// <summary>The Mail Desk's prompt (section 3.3), in plain words, then one block per email.</summary>
    internal static string Prompt(string factory, string seat, IReadOnlyList<Email> emails)
    {
        var text = new StringBuilder();
        text.Append($"The Mail Desk routed {Emails(emails.Count)} to you ({factory}/{seat}). They are listed in check.json in your kit and below. ")
            .Append("Read each one in its mailbox with cc-outlook. ")
            .Append("When you have dealt with one, run `cc-devthrottle maildesk done <routed id> --note \"<what you did>\"`. ")
            .Append("If it is not for you, run `cc-devthrottle maildesk return <routed id> --note \"<what you know about it>\"` and it goes back to the Mail Desk.")
            .Append('\n');
        for (var i = 0; i < emails.Count; i++)
        {
            var e = emails[i];
            text.Append('\n')
                .Append($"Email {i + 1} of {emails.Count}\n")
                .Append($"Routed id: {e.RoutedId:D}\n")
                .Append($"Mailbox: {e.Mailbox}\n")
                .Append($"Sender: {e.Sender}\n")
                .Append($"Subject: {e.Subject}\n")
                .Append($"Received: {e.ReceivedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC\n")
                .Append($"Why it was routed to you: {e.Layer}, {e.Reason}\n");
            if (e.ReturnNote is not null)
                text.Append($"Returned before: {e.ReturnNote}\n");
        }
        return text.ToString();
    }

    private static string Emails(int count) => count == 1 ? "1 email" : $"{count.ToString(CultureInfo.InvariantCulture)} emails";

    /// <summary>The sender's text with every control character made a space, so a subject cannot break a prompt's
    /// lines or carry a terminal escape.</summary>
    internal static string Clean(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i])) chars[i] = ' ';
        return new string(chars);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>One session the router started: its id, its seat, and how many emails it was given.</summary>
public sealed record MailDeskRoutedStart(string SessionId, string Factory, string Seat, int Emails);

/// <summary>What one router run did - counts only, for the log and for tests.</summary>
public sealed class MailDeskRouterRun
{
    /// <summary>A run skipped because another run of the same account was under way.</summary>
    public static MailDeskRouterRun Skipped => new() { WasSkipped = true };

    public bool WasSkipped { get; private init; }
    public int Created { get; set; }
    public int Adopted { get; set; }
    public int Rerouted { get; set; }
    public int Redirected { get; set; }
    public int ToOwner { get; set; }
    public int StartsFailed { get; set; }
    public int Unknown { get; set; }
    public int Waiting { get; set; }
    public List<MailDeskRoutedStart> Started { get; } = new();

    public override string ToString()
        => WasSkipped ? "skipped"
            : $"{Created} created, {Adopted} adopted, {Rerouted} re-routed, {Redirected} sent to the Mail Desk seat, " +
              $"{ToOwner} handed to the owner, {StartsFailed} failed starts, {Unknown} starts with an unknown outcome, " +
              $"{Started.Count} sessions started with {Started.Sum(s => s.Emails)} emails, {Waiting} waiting";
}
