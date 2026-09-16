using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.DevReports;

/// <summary>Whether a session can take the owner's items right now.</summary>
internal enum DevReportSessionReach
{
    /// <summary>Live on the roster and waiting for input: deliver now.</summary>
    Idle,

    /// <summary>Working, or its machine is not connected: hold until the turn ends.</summary>
    Busy,

    /// <summary>The session has ended: refuse new items.</summary>
    Ended,
}

/// <summary>The Gateway's verdict on one session's reach, the id of the Director running it when it is live,
/// and a sentence for the log.</summary>
internal sealed record DevReportSessionLiveness(DevReportSessionReach Reach, string? DirectorId, string Why);

/// <summary>One item's state as the send answers it - exactly the contract's <c>status</c> update.</summary>
internal sealed record DevReportItemUpdate(string Id, string Status, string StatusLabel);

/// <summary>
/// WHEN THE OWNER'S NOTES AND ANSWERS GO INTO THE SESSION (issue #2958, PLAN-phase-2.md "Delivery"). The Gateway
/// owns every ruling here (mission ruling 4):
///
/// <list type="number">
/// <item>An item id the report already holds is the same item. Its CURRENT state is returned and it is never
/// stored or delivered again, whatever its text says now.</item>
/// <item>A later answer to the same question replaces an earlier one still waiting (the store applies it).</item>
/// <item>An ended session refuses every new item, and nothing is stored as deliverable.</item>
/// <item>A working session - or one whose machine is not connected - holds the items. It is never interrupted.
/// An idle one gets them now, in the same request, as ONE prompt.</item>
/// <item>A turn end drains everything held for the session, across all its reports, as ONE prompt. The held
/// items are rows in the database, so a restart does not lose them: the turn-end watcher's catch-up fires for
/// a session first seen already waiting, and that drains them.</item>
/// <item>Deciding held-or-deliver and draining run under ONE lock per (tenant, session), so a send racing a
/// turn end can neither deliver an item twice nor strand it.</item>
/// </list>
///
/// A send that never left this Gateway leaves the items held (nothing was sent, the next turn end tries again).
/// A send that went out and was not confirmed is marked delivered-not-confirmed and is NEVER retried: typing the
/// owner's words into a session twice is worse than telling him we cannot confirm the once.
/// </summary>
internal sealed class DevReportDelivery
{
    private readonly DevReportStore _store;
    private readonly Func<TenantId, string, DevReportSessionLiveness> _liveness;
    private readonly Func<TenantId, string, SessionVerbClient?> _route;
    private readonly Func<DateTime> _nowUtc;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly ConcurrentDictionary<(string Tenant, string SessionId), SemaphoreSlim> _locks = new();

    /// <param name="store">The record.</param>
    /// <param name="liveness">The session's reach, from the pushed roster and the session history.</param>
    /// <param name="route">A tunnel caller for (tenant, director id), or null when that Director is not connected.</param>
    /// <param name="nowUtc">The clock, as a seam.</param>
    /// <param name="enterTenantScope">Enters the account's scope for the send. REQUIRED on a hosted Gateway in
    /// practice: the tunnel refuses a command with no account in scope, and a turn end raised by the watcher's
    /// catch-up sweep - the restart path - arrives with none. Optional only because self-host is one partition.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public DevReportDelivery(
        DevReportStore store,
        Func<TenantId, string, DevReportSessionLiveness> liveness,
        Func<TenantId, string, SessionVerbClient?> route,
        Func<DateTime>? nowUtc = null,
        Func<TenantId, IDisposable>? enterTenantScope = null)
    {
        _enterTenantScope = enterTenantScope;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>The session's reach right now, for the report's <c>sessionEnded</c> verdict.</summary>
    public DevReportSessionLiveness Liveness(TenantId tenant, string sessionId) => _liveness(tenant, sessionId);

    /// <summary>
    /// The owner sends items on a report. Answers one update per item, in the order sent.
    /// </summary>
    /// <param name="senderKind">The credential kind behind the send, recorded on the delivered prompt.</param>
    public async Task<IReadOnlyList<DevReportItemUpdate>> SendAsync(
        TenantId tenant, DevReportEntity report, IReadOnlyList<DevReportItem> items, string senderKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(items);
        FileLog.Write($"[DevReportDelivery] SendAsync: tenant={tenant.ToLogString()} report={report.Id} sid={report.SessionId} items={items.Count}");

        var gate = LockFor(tenant, report.SessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = items.Select(i => i.Id).Distinct(StringComparer.Ordinal).ToList();
            var known = _store.FindItems(tenant, report.Id, ids);

            // Rule 1: an id the report already holds is that item. Within one batch the first occurrence counts.
            var seen = new HashSet<string>(known.Keys, StringComparer.Ordinal);
            var fresh = new List<DevReportItem>();
            foreach (var item in items)
                if (seen.Add(item.Id)) fresh.Add(item);

            var live = _liveness(tenant, report.SessionId);
            var refused = new HashSet<string>(StringComparer.Ordinal);
            if (live.Reach == DevReportSessionReach.Ended)
            {
                // Rule 3: refused, and not stored - a refused item stays in the owner's queue on the page.
                foreach (var item in fresh) refused.Add(item.Id);
                FileLog.Write($"[DevReportDelivery] SendAsync: sid={report.SessionId} has ended ({live.Why}); refused {refused.Count} new item(s)");
            }
            else if (fresh.Count > 0)
            {
                _store.AddItems(tenant, report, fresh, DevReportItemStates.HeldState, senderKind, _nowUtc());
            }

            // Rule 4: an idle session takes them now, as one prompt with anything else held for it.
            if (live.Reach == DevReportSessionReach.Idle)
                await DrainLockedAsync(tenant, report.SessionId, live, ct).ConfigureAwait(false);
            else
                FileLog.Write($"[DevReportDelivery] SendAsync: sid={report.SessionId} reach={live.Reach} ({live.Why}); nothing typed");

            var after = _store.FindItems(tenant, report.Id, ids);
            var updates = new List<DevReportItemUpdate>(items.Count);
            foreach (var item in items)
            {
                if (after.TryGetValue(item.Id, out var row))
                    updates.Add(new DevReportItemUpdate(item.Id, row.Status, row.StatusLabel));
                else if (refused.Contains(item.Id))
                    updates.Add(new DevReportItemUpdate(item.Id, DevReportItemStates.SessionEndedState.Status,
                        DevReportItemStates.SessionEndedState.Label));
                else
                    throw new InvalidOperationException(
                        $"item {item.Id} on report {report.Id} was neither stored nor refused; the send has no state for it");
            }
            return updates;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// A session's turn has ended: deliver everything held for it as one prompt. Returns how many items went.
    /// </summary>
    public async Task<int> DrainAsync(TenantId tenant, string sessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        sessionId = NormalizeSessionId(sessionId);
        var gate = LockFor(tenant, sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var live = _liveness(tenant, sessionId);
            return await DrainLockedAsync(tenant, sessionId, live, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> DrainLockedAsync(TenantId tenant, string sessionId, DevReportSessionLiveness live, CancellationToken ct)
    {
        var open = _store.OpenItemsForSession(tenant, sessionId);
        if (open.Count == 0) return 0;

        // Held while working stays held: the turn end asked, but the roster now says otherwise.
        if (live.Reach != DevReportSessionReach.Idle || string.IsNullOrEmpty(live.DirectorId))
        {
            FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} {open.Count} item(s) stay held, reach={live.Reach} ({live.Why})");
            return 0;
        }

        var route = _route(tenant, live.DirectorId);
        if (route is null)
        {
            FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} {open.Count} item(s) stay held, director {live.DirectorId} is not connected");
            return 0;
        }

        var reports = new List<DevReportPromptFold.FoldReport>();
        foreach (var group in open.GroupBy(i => i.ReportId))
        {
            var report = _store.Get(tenant, group.Key)
                ?? throw new InvalidOperationException($"held items name report {group.Key}, which the account does not hold");
            var foldItems = group
                .Select(row => new DevReportPromptFold.FoldItem(ToItem(row),
                    row.Kind == DevReportItem.Answer && _store.HasDeliveredAnswer(tenant, report.Id, row.QuestionId)))
                .ToList();
            reports.Add(new DevReportPromptFold.FoldReport(report.Id, report.Key, report.Title, report.Version, foldItems));
        }

        var text = DevReportPromptFold.Compose(reports);
        var senders = open.Select(i => i.SenderKind).Distinct(StringComparer.Ordinal).ToList();
        var request = new PromptRequest
        {
            Text = text,
            AppendEnter = true,
            WaitForIdle = false,
            // The owner's turn: not an agent prompting another, so the Director counts it as the owner's.
            AgentDriven = false,
            Provenance = new SubmissionProvenanceDto
            {
                Route = SubmissionRoutes.GatewayDevReport,
                IdentityKind = senders.Count == 1 && senders[0].Length > 0 ? senders[0] : SubmissionIdentityKinds.Unknown,
            },
        };

        // THE ACCOUNT'S SCOPE IS ENTERED FOR THE SEND, HERE, whatever called us. A turn end from a live push arrives
        // inside the tunnel connection's scope and an owner's send inside the request's, but the watcher's catch-up
        // sweep - the only thing that drains held items after a restart - carries none, and the tunnel then drops
        // the command as "never left the Gateway". The items would sit held until some later turn end.
        SessionVerbClient.PromptSendOutcome sent;
        using (_enterTenantScope?.Invoke(tenant))
            sent = await route.SendPromptAsync(sessionId, request, ct).ConfigureAwait(false);
        var outcome = sent.Kind switch
        {
            SessionVerbClient.PromptSendKind.Accepted => DevReportItemStates.SendOutcome.Accepted,
            SessionVerbClient.PromptSendKind.NeverLeftTheGateway => DevReportItemStates.SendOutcome.NeverLeft,
            _ => DevReportItemStates.SendOutcome.Unconfirmed,
        };
        var state = DevReportItemStates.AfterSend(outcome);
        _store.SetState(tenant, open.Select(i => i.Id).ToList(), state, _nowUtc());
        FileLog.Write($"[DevReportDelivery] Drain: sid={sessionId} reports={reports.Count} items={open.Count} outcome={outcome} " +
                      $"chars={text.Length}{(sent.Detail.Length > 0 ? " detail=" + sent.Detail : "")}");
        return outcome == DevReportItemStates.SendOutcome.NeverLeft ? 0 : open.Count;
    }

    /// <summary>A session id in the one form reports are stored under: a GUID's canonical lower-case form, the
    /// form a session key's identity carries. The roster and the path can spell the same session differently.</summary>
    public static string NormalizeSessionId(string sessionId)
        => Guid.TryParse(sessionId, out var id) ? id.ToString("D") : sessionId;

    private SemaphoreSlim LockFor(TenantId tenant, string sessionId)
        => _locks.GetOrAdd((tenant.Value, sessionId), _ => new SemaphoreSlim(1, 1));

    private static DevReportItem ToItem(DevReportItemEntity row) => new(
        row.ClientItemId,
        row.Kind,
        row.Text,
        row.AnchorJson is null ? null : DevReportAnchor.FromJson(row.AnchorJson),
        row.QuestionId,
        row.Question,
        row.OptionValue,
        row.OptionLabel,
        row.Comment);
}
