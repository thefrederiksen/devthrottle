import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { durationFromMs } from "@devthrottle/client-core/sessions/waiting";
import {
  getWorkHistoryReport,
  type WorkHistoryReport,
  type WorkHistorySession,
} from "@devthrottle/client-core/history/historyClient";
import {
  accountedFor,
  buildLineage,
  indexReport,
  nodeCount,
  originLabel,
  tallyOrigins,
  type LineageNode,
} from "@devthrottle/client-core/history/lineage";
import { ErrorBanner, LoadingState, PageHeader } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

const SURFACE = "cockpit-work-history";

// The History page (issue #2194): "what have I been working on?" answered from the Gateway's durable
// per-session record, over a range you pick, grouped by repository and day with the individual sessions
// beneath each day. Sessions running RIGHT NOW appear as entries that have not
// ended yet - the current session is just the row without an ending.
//
// THE CLIENT IS DUMB (rule 7): every ending label, tone and description line on this page was folded
// once on the Gateway and is rendered verbatim. The one thing this page computes is layout. Honesty
// rule (#2157): no number or sentence is ever invented client-side.
//
// There are no written summaries any more. The AI session summary and the AI day paragraph were removed
// in October 2026: this page was their only reader and nobody read them.

const RANGES: ReadonlyArray<{ label: string; days: number }> = [
  { label: "Last day", days: 1 },
  { label: "3 days", days: 3 },
  { label: "Week", days: 7 },
  { label: "30 days", days: 30 },
];

/** The inclusive UTC day range for a preset: today back (days - 1). */
function rangeFor(days: number): { from: string; to: string } {
  const dayString = (d: Date) => d.toISOString().slice(0, 10);
  const now = new Date();
  const from = new Date(now.getTime() - (days - 1) * 24 * 60 * 60 * 1000);
  return { from: dayString(from), to: dayString(now) };
}

/** "2026-07-26" -> "Saturday 26 Jul 2026" in the viewer's locale, UTC calendar day. */
function dayHeading(day: string): string {
  const parsed = new Date(`${day}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime())) return day;
  return parsed.toLocaleDateString(undefined, {
    weekday: "long",
    day: "numeric",
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  });
}

function timeOf(iso: string | null | undefined): string {
  if (!iso) return "";
  const parsed = new Date(iso.endsWith("Z") ? iso : `${iso}Z`);
  if (Number.isNaN(parsed.getTime())) return iso;
  return parsed.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

function SessionEntry({ node, depth = 0 }: { node: LineageNode; depth?: number }) {
  const session = node.session;
  // Children start EXPANDED. The whole point of nesting is that you can see what a session set off;
  // collapsed-by-default would hide the answer behind a click on every row that has one.
  const [childrenOpen, setChildrenOpen] = useState(true);
  const live = session.endingKind == null;
  const descendants = nodeCount(node) - 1;
  const meta: string[] = [];
  if (session.sessionName) meta.push(session.sessionNumber != null ? `${session.sessionName} (#${session.sessionNumber})` : session.sessionName);
  else if (session.sessionNumber != null) meta.push(`#${session.sessionNumber}`);
  if (session.machineName) meta.push(session.machineName);
  if (session.agentKind) meta.push(session.agentKind);
  if (session.model) meta.push(session.model);
  meta.push(`started ${timeOf(session.startedAtUtc)}`);
  // Agent turns (completed turns, internal#625) are the sharper fact when the Director reports
  // them; the input-turn count stays as the fallback for records from older Directors.
  if (session.agentTurnCount != null && session.agentTurnCount > 0) meta.push(`${session.agentTurnCount} turns`);
  else if (session.turnCount != null && session.turnCount > 0) meta.push(`${session.turnCount} turns`);
  if (session.idleSeconds != null && session.idleSeconds >= 60)
    meta.push(`idle ${durationFromMs(session.idleSeconds * 1000)}`);
  // The interruption COUNT beside the clock (internal#982). Twelve five-minute waits and one
  // hour-long wait read identically on the clock and are nothing alike to live with.
  if (session.waitingStretchCount != null && session.waitingStretchCount > 0)
    meta.push(`needed you ${session.waitingStretchCount}x`);
  // Who started it (internal#982). Null for "unknown" and for rows that predate the field - a row
  // that cannot say shows nothing rather than a hedge.
  const origin = originLabel(session);
  if (origin !== null) meta.push(origin);

  return (
    <li className={`wh-session wh-tone-${session.endingTone}${depth > 0 ? " wh-session-child" : ""}`}>
      <div className="wh-session-row">
        <span className="wh-dot" aria-hidden="true" />
        <span className="wh-session-main">
          <span className="wh-session-desc">{session.descriptionLine}</span>
          <span className="wh-session-meta">{meta.join(" · ")}</span>
          {/* The parent is real but not in this group - almost always because it spawned work in
              another repository, which is the fleet's most ordinary move. Nesting the row here
              would file that work under a repository it never touched, so it stays put and says
              who started it instead. A null label means the parent is outside the report
              altogether (pruned, or older than the window) - which is worth saying, not hiding. */}
          {node.parentElsewhere && (
            <span className="wh-parent-note">
              {node.parentElsewhere.label !== null
                ? `started by ${node.parentElsewhere.label}, elsewhere in this range`
                : "started by a session outside this range"}
            </span>
          )}
        </span>
        <span className="wh-ending">
          {live ? "Running now" : session.endingLabel ?? session.endingKind}
        </span>
      </div>
      {node.children.length > 0 && (
        <>
          <button
            type="button"
            className="wh-children-toggle"
            onClick={() => setChildrenOpen((v) => !v)}
            aria-expanded={childrenOpen}
          >
            {childrenOpen ? "Hide" : "Show"} {descendants} session
            {descendants === 1 ? "" : "s"} this one started
          </button>
          {childrenOpen && (
            <ul className="wh-sessions wh-sessions-nested">
              {node.children.map((child) => (
                <SessionEntry key={child.session.sessionId} node={child} depth={depth + 1} />
              ))}
            </ul>
          )}
        </>
      )}
    </li>
  );
}

export function HistoryView() {
  const [days, setDays] = useState(3);
  const [report, setReport] = useState<WorkHistoryReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (windowDays: number, signal?: AbortSignal) => {
    try {
      const { from, to } = rangeFor(windowDays);
      const result = await getWorkHistoryReport(from, to, signal);
      setReport(result);
      setError(null);
    } catch (err) {
      if (signal?.aborted !== true) setError(describeAndReport(SURFACE, "load the work history", err));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setReport(null);
    void load(days, controller.signal);
    return () => controller.abort();
  }, [load, days]);

  // Every session in the report, keyed by id - what a cross-group parent is resolved against, so a
  // child can name its parent even when that parent is filed under another repository or day.
  const reportIndex = useMemo(() => (report === null ? null : indexReport(report)), [report]);

  const totals = useMemo(() => {
    if (report === null || reportIndex === null) return null;
    const unique = new Map<string, WorkHistorySession>();
    let running = 0;
    for (const repo of report.repos)
      for (const day of repo.days)
        for (const s of day.sessions) {
          if (!unique.has(s.sessionId) && s.endingKind == null) running++;
          if (!unique.has(s.sessionId)) unique.set(s.sessionId, s);
        }
    const origins = tallyOrigins(unique.values());
    return {
      repos: report.repos.length,
      sessions: unique.size,
      running,
      origins,
      known: accountedFor(origins),
    };
  }, [report, reportIndex]);

  return (
    <div className="page wh">
      <PageHeader
        title="History"
        subtitle="What was worked on, across every repository and machine - including the sessions running right now."
      />

      <div className="wh-ranges" role="group" aria-label="Time range">
        {RANGES.map((r) => (
          <button
            key={r.label}
            type="button"
            className={days === r.days ? "wh-range wh-range-on" : "wh-range"}
            onClick={() => setDays(r.days)}
          >
            {r.label}
          </button>
        ))}
        {totals && (
          <span className="wh-totals">
            {totals.sessions} session{totals.sessions === 1 ? "" : "s"} across {totals.repos}{" "}
            repositor{totals.repos === 1 ? "y" : "ies"}
            {totals.running > 0 ? ` · ${totals.running} running now` : ""}
            {/* The agent share, stated over the sessions we can actually ACCOUNT FOR rather than
                over all of them. These fields only start being written on 2026-07-27, so a window
                reaching back further is mostly rows that predate them - and dividing by the total
                would report a share far lower than the truth. When some rows cannot say, the
                denominator says so out loud rather than quietly absorbing them. */}
            {totals.known > 0 && (
              <>
                {" · "}
                {totals.origins.agent} of {totals.known} started by agents
                {totals.known < totals.sessions && (
                  <span className="wh-totals-caveat">
                    {" "}
                    ({totals.sessions - totals.known} older session
                    {totals.sessions - totals.known === 1 ? "" : "s"} do
                    {totals.sessions - totals.known === 1 ? "es" : ""} not record who started
                    {totals.sessions - totals.known === 1 ? " it" : " them"})
                  </span>
                )}
              </>
            )}
          </span>
        )}
      </div>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load(days)} />
      ) : report === null ? (
        <LoadingState message="Loading the work history..." />
      ) : report.repos.length === 0 ? (
        <div className="wh-empty">
          <p>No work recorded in this range yet.</p>
          <p className="wh-empty-sub">
            The Gateway records every session from the moment it first sees it on the stream, so
            history builds up from now on. Open <Link to="/sessions">Sessions</Link> to start one.
          </p>
        </div>
      ) : (
        report.repos.map((repo) => (
          <section key={repo.repoKey} className="wh-repo">
            <h2 className="wh-repo-name">{repo.displayName}</h2>
            {repo.days.map((day) => (
              <div key={day.day} className="wh-day">
                <h3 className="wh-day-head">{dayHeading(day.day)}</h3>
                {/* The day's sessions as the shape they actually had (internal#989): a day where
                    three things were started and they spawned nineteen helpers is a list of
                    twenty-two rows and a tree of three roots - identical data, completely
                    different stories. Built per group, resolving parents against the whole report
                    so a child can still name a parent filed under another repository. */}
                <ul className="wh-sessions">
                  {buildLineage(day.sessions, reportIndex ?? undefined).map((node) => (
                    <SessionEntry key={node.session.sessionId} node={node} />
                  ))}
                </ul>
              </div>
            ))}
          </section>
        ))
      )}
    </div>
  );
}
