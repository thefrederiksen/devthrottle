using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Prompts;

namespace CcDirector.Gateway.History;

/// <summary>
/// The Gateway-side summariser for work history (issue #2194). Two jobs, both run from the background
/// history sweep, NEVER from a page load:
///
///  1. PER-SESSION: an ended session that never sealed its own record is summarised from the prompt
///     log the Gateway already holds. A dirty end (interrupted) is summarised the same way and MARKED
///     partial - "this is how far it got". A session whose prompt log holds nothing worth a model call
///     is honestly marked "none" without spending anything.
///  2. PER-REPOSITORY PER-DAY ROLL-UP: the paragraph the History page shows per repository group,
///     computed from the (short) per-session summaries and cached; recomputed only when the inputs
///     change (see <see cref="SessionHistoryRollupEntity"/>).
///
/// COST DECISIONS, written down (the mission's requirement):
///  - The model is the tenant's FAST wingman model over the same hosted inference path Wingman speaks
///    through - summarisation is a background digest, not a judgment call, and the fast leg is the
///    cheap one. The endpoint, key and model resolve at call time via the injected factory, so a
///    settings change is honored without restart (the dictionary-screening precedent).
///  - The per-pass caps live in the SWEEP; this class does one bounded unit of work per call.
///  - Transcript input is capped (<see cref="MaxTranscriptChars"/>): head and tail are kept, the
///    middle is elided with a marker. Sessions below <see cref="MinCharsForModelCall"/> of prompt
///    text are marked "none" - a model call over nothing produces confident filler, and costs money.
///  - Attempts are bounded by the store (<see cref="SessionHistoryStore.MaxSummaryAttempts"/>); a
///    persistently failing path marks the summary unavailable rather than billing forever.
///
/// Prompt-derived output is customer content and every read is inside the ambient tenant scope the
/// sweep entered; the prompt log itself is read with the explicit tenant, matching its directory
/// partition.
/// </summary>
public sealed class SessionHistorySummarizer
{
    /// <summary>Prompt-log text below this length is not worth a model call.</summary>
    public const int MinCharsForModelCall = 400;

    /// <summary>Cap on transcript characters sent to the model per session.</summary>
    public const int MaxTranscriptChars = 24_000;

    /// <summary>How many prompt-log DAYS around a session are scanned at most. A session lives in its
    /// own day files; this bounds the read for a weeks-long-lived session id.</summary>
    public const int MaxPromptLogDays = 14;

    private readonly SessionHistoryStore _store;
    private readonly GatewayPromptLog _promptLog;
    private readonly Func<TenantId, CancellationToken, Task<IAgentBrain>> _brainFactory;

    public SessionHistorySummarizer(SessionHistoryStore store, GatewayPromptLog promptLog,
        Func<TenantId, CancellationToken, Task<IAgentBrain>> brainFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _promptLog = promptLog ?? throw new ArgumentNullException(nameof(promptLog));
        _brainFactory = brainFactory ?? throw new ArgumentNullException(nameof(brainFactory));
    }

    /// <summary>
    /// Summarise up to <paramref name="maxSessions"/> ended-but-unsummarised sessions for the CURRENT
    /// tenant (ambient scope entered by the sweep; <paramref name="tenant"/> is the same tenant, used
    /// for the prompt-log partition and the brain). Returns how many rows were settled (summary,
    /// "none", or a counted failure).
    /// </summary>
    public async Task<int> SummarizePendingAsync(TenantId tenant, int maxSessions, CancellationToken ct)
    {
        // A seal can arrive moments after the farewell; give it a settling minute before generating.
        var pending = _store.PendingSummaries(DateTime.UtcNow - TimeSpan.FromMinutes(1), maxSessions);
        var settled = 0;
        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();
            var isPartial = string.Equals(row.EndingKind, SessionHistoryEndings.Interrupted, StringComparison.Ordinal);
            try
            {
                var transcript = BuildTranscript(tenant, row);
                if (transcript.TotalChars < MinCharsForModelCall)
                {
                    _store.StoreGeneratedSummary(row.SessionId, SessionHistorySummaryKinds.None, isPartial,
                        summaryText: null, null, null, null, null, null);
                    settled++;
                    continue;
                }

                using var brain = await _brainFactory(tenant, ct).ConfigureAwait(false);
                var reply = await brain.AskAsync(SessionPrompt(row, transcript.Text), ct).ConfigureAwait(false);
                var parsed = ParseSessionSummary(reply.Text);
                if (parsed is null)
                {
                    FileLog.Write($"[SessionHistorySummarizer] unparseable model reply for session={row.SessionId}");
                    _store.NoteSummaryFailure(row.SessionId);
                    settled++;
                    continue;
                }

                _store.StoreGeneratedSummary(row.SessionId, SessionHistorySummaryKinds.Generated, isPartial,
                    parsed.Summary, parsed.WhatWasBuilt, parsed.LeftUnverified,
                    parsed.Branches, parsed.PullRequests, parsed.Commits);
                settled++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistorySummarizer] summarise FAILED for session={row.SessionId}: {ex.Message}");
                _store.NoteSummaryFailure(row.SessionId);
                settled++;
            }
        }
        return settled;
    }

    /// <summary>
    /// Recompute up to <paramref name="maxRollups"/> missing-or-stale repository/day roll-ups over the
    /// inclusive day window. Staleness is an input-hash mismatch; a row that failed
    /// <see cref="SessionHistoryStore.MaxSummaryAttempts"/> times for the SAME inputs is left alone.
    /// </summary>
    public async Task<int> RefreshRollupsAsync(TenantId tenant, DateTime fromDayUtc, DateTime toDayUtc,
        int maxRollups, CancellationToken ct)
    {
        // Staleness is decided from the narrow read; only a group that is rewritten has its full records read
        // (devthrottle_internal#2199 - this pass runs every two minutes for every account).
        var groups = RollupGroups(_store.ReadRollupInputs(fromDayUtc.Date, toDayUtc.Date.AddDays(1).AddTicks(-1)),
            fromDayUtc.Date, toDayUtc.Date);
        if (groups.Count == 0) return 0;

        var cached = _store.ReadRollups(fromDayUtc.Date, toDayUtc.Date)
            .ToDictionary(r => (r.RepoKey, r.DayUtc.Date));

        var written = 0;
        foreach (var group in groups.OrderByDescending(g => g.Day))
        {
            if (written >= maxRollups) break;
            ct.ThrowIfCancellationRequested();

            cached.TryGetValue((group.RepoKey, group.Day), out var existing);
            var upToDate = existing is not null && string.Equals(existing.InputHash, group.InputHash, StringComparison.Ordinal)
                           && (existing.SummaryText is not null || existing.Attempts >= SessionHistoryStore.MaxSummaryAttempts);
            if (upToDate) continue;

            var attempts = existing is not null && string.Equals(existing.InputHash, group.InputHash, StringComparison.Ordinal)
                ? existing.Attempts
                : 0;

            try
            {
                var full = WithFullRecords(group);
                using var brain = await _brainFactory(tenant, ct).ConfigureAwait(false);
                var reply = await brain.AskAsync(RollupPrompt(full), ct).ConfigureAwait(false);
                var text = reply.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("the model returned an empty roll-up");
                _store.SaveRollup(group.RepoKey, group.Day, text, group.InputHash, attempts, DateTime.UtcNow);
                written++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionHistorySummarizer] roll-up FAILED for {group.RepoKey} {group.Day:yyyy-MM-dd}: {ex.Message}");
                _store.SaveRollup(group.RepoKey, group.Day, summaryText: null, group.InputHash, attempts + 1, DateTime.UtcNow);
                written++;
            }
        }
        return written;
    }

    /// <summary>
    /// The group with its sessions' full records, in the group's own order, for the prompt. The hash stays the one
    /// the staleness decision was made on, so what is saved is exactly what the next pass compares against. A
    /// session whose record went between the two reads (retention) is left out of the prompt; the next pass sees a
    /// different input set and writes the roll-up again.
    /// </summary>
    private RollupGroup WithFullRecords(RollupGroup group)
    {
        var byId = _store.ReadMany(group.Sessions.Select(s => s.SessionId).ToList())
            .ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        var sessions = group.Sessions
            .Where(s => byId.ContainsKey(s.SessionId))
            .Select(s => byId[s.SessionId])
            .ToList();
        return group with { Sessions = sessions };
    }

    // ----- roll-up grouping (shared with the report endpoint via RollupGroups/InputHash) -----

    /// <summary>One repository group's one day: the sessions active that day and the input hash.</summary>
    public sealed record RollupGroup(string RepoKey, DateTime Day, IReadOnlyList<WorkHistorySessionDto> Sessions, string InputHash);

    /// <summary>
    /// Fold a range of session records into (repository, day) groups. A session appears on every UTC
    /// day of its observed life inside the window - "you worked on it Tuesday and Wednesday" is the
    /// honest reading of a session that spanned both.
    /// </summary>
    public static List<RollupGroup> RollupGroups(IReadOnlyList<WorkHistorySessionDto> sessions, DateTime fromDay, DateTime toDay)
    {
        var groups = new Dictionary<(string, DateTime), List<WorkHistorySessionDto>>();
        foreach (var s in sessions)
        {
            var key = SessionHistoryFold.RepoKey(s.RepoName, s.RepoPath);
            var first = s.StartedAtUtc.Date < fromDay ? fromDay : s.StartedAtUtc.Date;
            var last = s.LastSeenUtc.Date > toDay ? toDay : s.LastSeenUtc.Date;
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (!groups.TryGetValue((key, day), out var list))
                    groups[(key, day)] = list = new List<WorkHistorySessionDto>();
                list.Add(s);
            }
        }
        return groups
            .Select(kv => new RollupGroup(kv.Key.Item1, kv.Key.Item2, kv.Value, InputHash(kv.Value)))
            .ToList();
    }

    /// <summary>Hash of what the roll-up paragraph depends on: the set of sessions, their ending state
    /// and their summary state. Changes when a session ends, reopens, or gains a summary.</summary>
    public static string InputHash(IReadOnlyList<WorkHistorySessionDto> sessions)
    {
        var canonical = string.Join('\n', sessions
            .OrderBy(s => s.SessionId, StringComparer.Ordinal)
            .Select(s => $"{s.SessionId}|{s.EndingKind}|{s.SummaryKind}|{s.SummaryText?.Length ?? 0}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }

    // ----- prompts and parsing -----

    private sealed record Transcript(string Text, int TotalChars);

    private Transcript BuildTranscript(TenantId tenant, SessionHistoryEntity row)
    {
        var from = row.StartedAtUtc.Date;
        var to = (row.EndedAtUtc ?? row.LastSeenUtc).Date;
        if ((to - from).TotalDays > MaxPromptLogDays)
            from = to.AddDays(-MaxPromptLogDays);

        var records = _promptLog.Read(tenant, from, to)
            .Where(r => string.Equals(r.SessionId, row.SessionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.TsUtc)
            .ToList();

        var sb = new StringBuilder();
        foreach (var r in records)
        {
            sb.Append(string.Equals(r.Role, "user", StringComparison.OrdinalIgnoreCase) ? "USER: " : "AGENT: ");
            sb.AppendLine(r.Text.Trim());
            sb.AppendLine();
        }
        var full = sb.ToString();
        if (full.Length <= MaxTranscriptChars)
            return new Transcript(full, full.Length);

        // Keep the opening (what was asked) and the tail (how it ended); elide the middle loudly.
        const int head = MaxTranscriptChars / 3;
        var tail = MaxTranscriptChars - head;
        var elided = full[..head] + "\n\n[... transcript elided for length ...]\n\n" + full[^tail..];
        return new Transcript(elided, full.Length);
    }

    private static string SessionPrompt(SessionHistoryEntity row, string transcript)
    {
        var ending = string.IsNullOrEmpty(row.EndingLabel) ? row.EndingKind : row.EndingLabel;
        return $$"""
You are writing the permanent work-history record of one coding-agent session that has ended.
Below is the conversation transcript (operator prompts and agent replies; tool output is not included).

Session facts:
- Repository: {{row.RepoName ?? row.RepoPath ?? "(unknown)"}}
- Session name: {{row.SessionName ?? "(unnamed)"}}
- How it ended: {{ending}}

Reply with ONLY a JSON object, no markdown fence, with exactly these keys:
{
  "summary": "2-4 plain sentences: what was asked, what was actually done, and where it was left",
  "what_was_built": ["each concrete thing implemented or changed, as a short phrase"],
  "left_unverified": ["each thing built but not tested or proven, or explicitly left to check"],
  "branches": ["git branch names mentioned as worked on"],
  "pull_requests": ["pull request numbers or URLs mentioned"],
  "commits": ["commit hashes or descriptions of commits made"]
}
Use empty arrays when the transcript shows none. Report only what the transcript supports - never
invent branches, numbers, or outcomes. Write in plain English with no abbreviations.

Transcript:
{{transcript}}
""";
    }

    private static string RollupPrompt(RollupGroup group)
    {
        var lines = new StringBuilder();
        foreach (var s in group.Sessions)
        {
            lines.Append("- ").Append(s.DescriptionLine);
            if (!string.IsNullOrEmpty(s.EndingLabel)) lines.Append(" [").Append(s.EndingLabel).Append(']');
            else if (s.EndingKind is null) lines.Append(" [still running]");
            if (!string.IsNullOrWhiteSpace(s.SummaryText))
                lines.Append(" -- ").Append(s.SummaryText);
            lines.AppendLine();
        }
        return $$"""
You are writing a one-paragraph plain-language summary of a day's development work in one repository,
for the owner's work-history page. Below are the sessions that ran there that day, each with what it
was for, how it ended, and its summary when one exists.

Repository: {{group.RepoKey}}
Day (UTC): {{group.Day:yyyy-MM-dd}}
Sessions:
{{lines}}
Reply with ONLY the paragraph - 2 to 5 sentences, plain English, no abbreviations, no headings, no
lists. Say what was worked on and what state it reached. Only state what the session lines support.
""";
    }

    internal sealed record ParsedSummary(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("what_was_built")] List<string>? WhatWasBuilt,
        [property: JsonPropertyName("left_unverified")] List<string>? LeftUnverified,
        [property: JsonPropertyName("branches")] List<string>? Branches,
        [property: JsonPropertyName("pull_requests")] List<string>? PullRequests,
        [property: JsonPropertyName("commits")] List<string>? Commits);

    /// <summary>Extract and parse the JSON object from a model reply (tolerating prose or a fence
    /// around it). Null when no valid object with a summary is found.</summary>
    internal static ParsedSummary? ParseSessionSummary(string? replyText)
    {
        if (string.IsNullOrWhiteSpace(replyText)) return null;
        var start = replyText.IndexOf('{');
        var end = replyText.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<ParsedSummary>(replyText[start..(end + 1)]);
            return string.IsNullOrWhiteSpace(parsed?.Summary) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
