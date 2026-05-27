using System.Text;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Builds the <see cref="WingmanAskContext"/> blob fed to the wingman (recent
/// wingman events, turn summaries, cleaned terminal-buffer tail, git-dirty flag).
///
/// Extracted so the on-demand "ask/explain" endpoint and the proactive
/// <see cref="ProactiveExplainService"/> build identical context from one place.
/// </summary>
internal static class WingmanContextBuilder
{
    public static async Task<WingmanAskContext> BuildAsync(
        Session session, TurnSummaryCache? turnSummaryCache, CancellationToken ct = default)
    {
        var events = session.RecentWingmanEvents
            .Select(e => new WingmanAskEvent(e.At, e.OldColor, e.NewColor, e.Reason))
            .ToList();

        var summaries = turnSummaryCache?.GetForSession(session.Id).ToList() ?? new List<TurnSummary>();

        var bufferTail = "";
        try
        {
            var bytes = session.Buffer?.DumpAll();
            if (bytes is not null && bytes.Length > 0)
            {
                const int TailBytes = 8192;
                var start = Math.Max(0, bytes.Length - TailBytes);
                var tail = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
                bufferTail = AnsiCleaner.Clean(tail);
                if (bufferTail.Length > 4000) bufferTail = bufferTail[^4000..];
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanContextBuilder] buffer tail FAILED: {ex.Message}");
        }

        var gitDirty = false;
        try
        {
            var snap = await WingmanService.GitSnapshotAsync(session.RepoPath, ct);
            gitDirty = snap.Dirty;
        }
        catch { /* best-effort; not having git is fine */ }

        var (screenRows, cursorRow, cursorCol) = session.SnapshotScreenRowsWithCursor();

        return new WingmanAskContext
        {
            SessionId = session.Id.ToString(),
            RepoPath = session.RepoPath,
            AgentKind = session.AgentKind.ToString(),
            ActivityState = session.ActivityState.ToString(),
            CurrentColor = session.StatusColor,
            CurrentReason = session.LastStatusReason,
            GitDirty = gitDirty,
            RecentWingmanEvents = events,
            RecentTurnSummaries = summaries,
            BufferTailText = bufferTail,
            ScreenRows = screenRows,
            CursorRow = cursorRow,
            CursorCol = cursorCol,
        };
    }
}
