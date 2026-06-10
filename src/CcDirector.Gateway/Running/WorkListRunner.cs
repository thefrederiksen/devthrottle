using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The thin Gateway queue runner (issue #274, child 3 of #270) - the heart of the autonomous
/// work-item queue. Given a named work list (child 2, #273) it:
///
///   1. CLAIMS the list's single-consumer claim (a second drainer is refused - rides #273's 409).
///   2. DRAINS the list in <c>GET /lists/{name}</c> order, ONE github item in flight at a time:
///      for each <c>source = github</c> item it starts one implementation session seeded with
///      <c>/implementation-loop &lt;id&gt;</c> (child 1's loop), then WATCHES that session's
///      transcript until it emits the <c>IMPL-LOOP-TERMINAL</c> sentinel (child 1, #272), reads the
///      terminal signal, RECORDS it, and only THEN advances - never overlapping two items.
///   3. Applies the documented SOURCE-GATING rule for non-github items (skip; see below).
///   4. RELEASES the consumer claim when the list is drained.
///
/// The Director stays dumb: all orchestration lives here at the Gateway. The runner never re-drives
/// a <c>failed</c> / <c>needs-human</c> item (retry is OUT for v1) - it records the signal and
/// advances.
///
/// SOURCE-GATING RULE (v1, criterion 3): the only RUNNABLE source is <c>github</c>. When the next
/// item's source is not github (devops/jira), the runner does NOT start a session for it - it
/// records the item as <see cref="WorkListItemOutcome.SkippedNonGithub"/> and SKIPS PAST it to the
/// next runnable item (it does not HOLD the whole list on the first non-github ref). The non-github
/// item is left untouched in the list (the runner never writes status back to the list - #273).
/// </summary>
public sealed class WorkListRunner
{
    /// <summary>The one runnable source in v1.</summary>
    public const string RunnableSource = "github";

    private readonly WorkListStore _store;
    private readonly IImplSessionDriver _driver;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _perItemTimeout;

    /// <param name="store">The fleet's work-list store (child 2, #273).</param>
    /// <param name="driver">The seam that starts/reads an implementation session on a Director.</param>
    /// <param name="pollInterval">How often to re-read a running session's transcript for the sentinel.</param>
    /// <param name="perItemTimeout">
    /// Max time to wait for one item's session to emit its terminal sentinel before the runner gives
    /// up on that item (records <c>failed</c>, reason=timeout) and advances. Guards against a wedged
    /// session stalling the whole list forever.
    /// </param>
    public WorkListRunner(
        WorkListStore store,
        IImplSessionDriver driver,
        TimeSpan? pollInterval = null,
        TimeSpan? perItemTimeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _perItemTimeout = perItemTimeout ?? TimeSpan.FromHours(2);
    }

    /// <summary>
    /// Claim the named list and drain it to completion, returning the per-item record. Throws
    /// <see cref="WorkListClaimRefusedException"/> if the list is already being drained (criterion 5)
    /// or <see cref="InvalidOperationException"/> if the list does not exist. The caller supplies the
    /// consumer token so a specific machine/runner identity owns the claim.
    /// </summary>
    public async Task<WorkListRunResult> DrainAsync(string listName, string consumerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(listName))
            throw new ArgumentException("list name is required", nameof(listName));
        if (string.IsNullOrWhiteSpace(consumerToken))
            throw new ArgumentException("consumer token is required", nameof(consumerToken));

        FileLog.Write($"[WorkListRunner] DrainAsync: list={listName}, consumer={consumerToken}");

        var claim = _store.Claim(listName, consumerToken);
        switch (claim)
        {
            case WorkListStore.ClaimResult.NoSuchList:
                throw new InvalidOperationException($"no such list: {listName}");
            case WorkListStore.ClaimResult.AlreadyClaimed:
                FileLog.Write($"[WorkListRunner] DrainAsync REFUSED: list={listName} already claimed");
                throw new WorkListClaimRefusedException(listName);
        }

        var results = new List<WorkListItemResult>();
        var released = false;
        try
        {
            // Read the ordered items once. v1 lists are static during a drain (the producer appends
            // before/after, not mid-run); re-reading every step would let an edit reshuffle a live
            // run. The snapshot is the order the runner commits to draining.
            var list = _store.Get(listName)
                ?? throw new InvalidOperationException($"list vanished mid-drain: {listName}");

            foreach (var item in list.Items)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await ProcessItemAsync(item, ct));
            }

            FileLog.Write($"[WorkListRunner] DrainAsync done: list={listName}, items={results.Count}");
        }
        finally
        {
            // Release the claim even on cancellation/exception so the list is reclaimable - the
            // single-consumer invariant is for live drainers, not a permanent lock.
            released = _store.Release(listName);
            FileLog.Write($"[WorkListRunner] DrainAsync released: list={listName}, released={released}");
        }

        return new WorkListRunResult
        {
            ListName = listName,
            ConsumerToken = consumerToken,
            Items = results,
            ConsumerReleased = released,
        };
    }

    private async Task<WorkListItemResult> ProcessItemAsync(WorkListItemRef item, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;

        // SOURCE GATING (criterion 3): only github items are started in v1. Anything else is
        // skipped - never started, left in the list - and the runner advances to the next item.
        if (!string.Equals(item.Source, RunnableSource, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[WorkListRunner] skip non-github item: source={item.Source}, id={item.Id}");
            return new WorkListItemResult
            {
                Item = item,
                Outcome = WorkListItemOutcome.SkippedNonGithub,
                Note = $"source '{item.Source}' is not runnable in v1 (only '{RunnableSource}'); skipped, left in list",
                StartedAtUtc = startedAt,
                FinishedAtUtc = DateTime.UtcNow,
            };
        }

        var (sessionId, startError) = await _driver.StartImplementationSessionAsync(item.Id, ct);
        if (sessionId is null)
        {
            FileLog.Write($"[WorkListRunner] start failed: id={item.Id}, error={startError}");
            return new WorkListItemResult
            {
                Item = item,
                Outcome = WorkListItemOutcome.StartFailed,
                Note = startError ?? "session start failed",
                StartedAtUtc = startedAt,
                FinishedAtUtc = DateTime.UtcNow,
            };
        }

        if (!int.TryParse(item.Id, out var issueNumber))
        {
            // A github ref whose id is not a number cannot correlate with the sentinel's issue field.
            // We started a session, but we cannot watch it deterministically - record and advance.
            FileLog.Write($"[WorkListRunner] github id not numeric: id={item.Id}, sid={sessionId}");
            return new WorkListItemResult
            {
                Item = item,
                Outcome = WorkListItemOutcome.StartFailed,
                SessionId = sessionId,
                Note = $"github id '{item.Id}' is not a numeric issue number; cannot correlate the terminal sentinel",
                StartedAtUtc = startedAt,
                FinishedAtUtc = DateTime.UtcNow,
            };
        }

        var signal = await WatchForTerminalAsync(sessionId, issueNumber, ct);
        return new WorkListItemResult
        {
            Item = item,
            Outcome = WorkListItemOutcome.Ran,
            SessionId = sessionId,
            Signal = signal?.Signal ?? ImplLoopSignal.Failed,
            Note = signal?.Reason ?? "no terminal sentinel observed before the per-item timeout",
            StartedAtUtc = startedAt,
            FinishedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Poll the session transcript until the <c>IMPL-LOOP-TERMINAL</c> sentinel for this issue
    /// appears (child 1, #272), or the per-item timeout elapses. Returns the parsed signal, or null
    /// on timeout (the caller records that as <c>failed</c> with a timeout reason).
    /// </summary>
    private async Task<ImplLoopTerminalSignal?> WatchForTerminalAsync(string sessionId, int issueNumber, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _perItemTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var transcript = await _driver.ReadTranscriptAsync(sessionId, ct);
            var signal = ImplLoopTerminalSignal.ParseLatest(transcript, issueNumber);
            if (signal is not null)
            {
                FileLog.Write($"[WorkListRunner] terminal signal: issue={issueNumber}, sid={sessionId}, signal={signal.Signal}");
                return signal;
            }

            await Task.Delay(_pollInterval, ct);
        }

        FileLog.Write($"[WorkListRunner] per-item timeout: issue={issueNumber}, sid={sessionId}");
        return null;
    }
}

/// <summary>
/// Thrown by <see cref="WorkListRunner.DrainAsync"/> when the named list is already being drained by
/// another consumer (criterion 5 - the runner rides #273's single-consumer claim and refuses a
/// second drainer rather than racing the same items).
/// </summary>
public sealed class WorkListClaimRefusedException : Exception
{
    public WorkListClaimRefusedException(string listName)
        : base($"list '{listName}' already has an active draining consumer") => ListName = listName;

    public string ListName { get; }
}
