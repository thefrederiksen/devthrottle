using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// The thin Gateway queue runner (issue #274, child 3 of #270) - the heart of the autonomous
/// work-item queue. Given a named work list (child 2, #273) it:
///
///   1. CLAIMS the list's single-consumer claim (a second drainer is refused - rides #273's 409).
///   2. DRAINS the list in <c>GET /lists/{name}</c> order, ONE runnable item in flight at a time:
///      for each item whose source has a registered <see cref="ISourceAdapter"/> (github and devops
///      since issue #300) it starts one implementation session seeded with that adapter's seed
///      prompt (child 1's loop in the matching source mode), then WATCHES that session's transcript
///      until it emits the <c>IMPL-LOOP-TERMINAL</c> sentinel (child 1, #272), reads the terminal
///      signal, RECORDS it, and only THEN advances - never overlapping two items.
///   3. Applies the documented SOURCE-GATING rule for sources WITHOUT an adapter (skip; see below).
///   4. RELEASES the consumer claim when the list is drained.
///
/// The Director stays dumb: all orchestration lives here at the Gateway. The runner never re-drives
/// a <c>failed</c> / <c>needs-human</c> item (retry is OUT for v1) - it records the signal and
/// advances.
///
/// SOURCE-GATING RULE (issue #300, formerly the v1 github-only gate): runnability is per-source
/// adapter dispatch - <see cref="SourceAdapters.TryGet"/>. When the next item's source has no
/// adapter (jira in v1), the runner does NOT start a session for it - it records the item as
/// <see cref="WorkListItemOutcome.SkippedNonGithub"/> and SKIPS PAST it to the next runnable item
/// (it does not HOLD the whole list on the first non-runnable ref). The skipped item is left
/// untouched in the list (the runner never writes status back to the list - #273).
/// </summary>
public sealed class WorkListRunner
{
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

        // SOURCE GATING via adapter dispatch (issue #300): a source with no registered adapter
        // (jira in v1) is skipped - never started, left in the list - and the runner advances.
        var adapter = SourceAdapters.TryGet(item.Source);
        if (adapter is null)
        {
            FileLog.Write($"[WorkListRunner] skip item without source adapter: source={item.Source}, id={item.Id}");
            return new WorkListItemResult
            {
                Item = item,
                Outcome = WorkListItemOutcome.SkippedNonGithub,
                Note = $"source '{item.Source}' is not runnable (runnable: {SourceAdapters.RunnableSourceNames}); skipped, left in list",
                StartedAtUtc = startedAt,
                FinishedAtUtc = DateTime.UtcNow,
            };
        }

        var seedPrompt = adapter.BuildSeedPrompt(item);
        var (sessionId, startError) = await _driver.StartImplementationSessionAsync(item.Id, seedPrompt, ct);
        if (sessionId is null)
        {
            FileLog.Write($"[WorkListRunner] start failed: source={item.Source}, id={item.Id}, error={startError}");
            return new WorkListItemResult
            {
                Item = item,
                Outcome = WorkListItemOutcome.StartFailed,
                Note = startError ?? "session start failed",
                StartedAtUtc = startedAt,
                FinishedAtUtc = DateTime.UtcNow,
            };
        }

        if (!adapter.TryGetCorrelationKey(item, out var correlationKey))
        {
            // An item whose id yields no correlation key cannot correlate with the sentinel's issue
            // field. We started a session, but we cannot watch it deterministically - record and advance.
            FileLog.Write($"[WorkListRunner] no correlation key: source={item.Source}, id={item.Id}, sid={sessionId}");
            return new WorkListItemResult
            {
                Item = item,
                Outcome = WorkListItemOutcome.StartFailed,
                SessionId = sessionId,
                Note = $"{item.Source} id '{item.Id}' is not a numeric item id; cannot correlate the terminal sentinel",
                StartedAtUtc = startedAt,
                FinishedAtUtc = DateTime.UtcNow,
            };
        }

        var signal = await WatchForTerminalAsync(sessionId, correlationKey, ct);
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
    /// Poll the session transcript until the <c>IMPL-LOOP-TERMINAL</c> sentinel for this item's
    /// correlation key appears (child 1, #272; the block's <c>issue:</c> field is source-agnostic -
    /// for github it is the issue number, for devops the work item id), or the per-item timeout
    /// elapses. Returns the parsed signal, or null on timeout (the caller records that as
    /// <c>failed</c> with a timeout reason).
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
