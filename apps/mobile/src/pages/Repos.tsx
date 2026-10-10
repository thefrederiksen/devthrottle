import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import {
  getThrottle,
  formatPercent,
  type ThrottleData,
  type ThrottleFigure,
  type RepoStat,
  type RepoSummary,
} from "@devthrottle/client-core/stats/statsClient";
import { backgroundRecovered, describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

// The private "Repos" page for the phone: where the owner's development actually happens - how driving
// splits across the codebases worked in, ranked by submitted turns. Its own page, deliberately separate
// from Your Throttle, because which repos take the owner's time is private (unlike the shareable
// voice/surface splits). Reads the same GET /stats/data feed as the Cockpit Repos tab: the per-repository
// split is part of the one figure the Gateway computes from the submission ledger (mission "Clean up Your
// Throttle", ruling R9), so both shells show one identical view over one stated window. A self-hosted
// Gateway answers with a sentence, and this page shows it (rulings R1 and R6). Renders immediately with a
// loading state, loads asynchronously, shows an explicit error banner on failure (no-fallback rule), and
// auto-refreshes. Counts only - never any message text, and no character volume (ruling R16).

const REFRESH_MS = 10_000;

type Metric = "turns" | "sessions";
const METRIC_LABEL: Record<Metric, string> = { turns: "Turns", sessions: "Sessions" };
const METRIC_WORD: Record<Metric, string> = { turns: "turns", sessions: "sessions" };

function metricValue(r: RepoStat, metric: Metric): number {
  return metric === "turns" ? r.turns : r.sessions;
}

export function Repos() {
  const [data, setData] = useState<ThrottleData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [metric, setMetric] = useState<Metric>("turns");

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;

    const tick = async () => {
      try {
        const fresh = await getThrottle(controller.signal);
        if (controller.signal.aborted) return;
        setData(fresh);
        backgroundRecovered("mobile-repos", "read the repositories");
        setError(null);
      } catch (err) {
        if (controller.signal.aborted) return;
        setError(describeAndReport("mobile-repos", "read the repositories", err, { background: true }));
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(() => void tick(), REFRESH_MS);
      }
    };
    void tick();

    return () => {
      controller.abort();
      if (timer !== undefined) clearTimeout(timer);
    };
  }, []);

  const figure: ThrottleFigure | null = data !== null && data.available ? data.throttle : null;
  const repos = figure?.repos ?? [];
  // The headline cards are the Gateway's (fix-round finding F-01): nothing here totals a row.
  const summary: RepoSummary | null = figure === null ? null : figure.reposSummary;

  const ranked = useMemo(
    () => [...repos].sort((a, b) => metricValue(b, metric) - metricValue(a, metric)),
    [repos, metric],
  );
  const max = ranked.length > 0 ? metricValue(ranked[0], metric) : 0;

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Repos</h1>
      </header>

      <div className="repos-private">
        <span className="repos-lock" aria-hidden="true" />
        Private - only you
      </div>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {error}
        </div>
      )}

      {data === null && error === null && <div className="thr-note">Loading...</div>}

      {/* The Gateway has no figure to show here and said why, in one sentence. Rendered verbatim. */}
      {data !== null && !data.available && (
        <div className="thr-note" role="status">
          {data.reason}
        </div>
      )}

      {figure !== null && summary !== null && !summary.hasData && (
        <div className="thr-note">
          No turn counted in this window ({figure.window.label.toLowerCase()}). Drive a session in any repo
          and the codebases you work in will appear here, ranked.
        </div>
      )}

      {figure !== null && summary !== null && summary.hasData && (
        <>
          <section className="thr-cards" aria-label="Repo headlines">
            <div className="thr-card">
              <div className="thr-card-value">{summary.repoCount}</div>
              <div className="thr-card-label">Repos</div>
            </div>
            <div className="thr-card">
              <div className="thr-card-value">{summary.totalTurns.toLocaleString()}</div>
              <div className="thr-card-label">Turns</div>
            </div>
            <div className="thr-card">
              <div className="thr-card-value">{formatPercent(summary.topPercent)}</div>
              <div className="thr-card-label">Busiest</div>
            </div>
          </section>

          <div className="thr-caption">
            {figure.window.label}.{" "}
            {summary.topRepoName !== null && (
              <>
                {summary.topRepoName} leads;{" "}
              </>
            )}
            {summary.totalSessions} session{summary.totalSessions === 1 ? "" : "s"} in total.
            {figure.reposUnattributedTurns > 0 && (
              <>
                {" "}
                {figure.reposUnattributedTurns.toLocaleString()} of your turns went into sessions with no
                repository on record and are in no row here.
              </>
            )}
          </div>

          <div className="repos-seg" role="tablist" aria-label="Rank repositories by">
            {(["turns", "sessions"] as Metric[]).map((m) => (
              <button
                key={m}
                type="button"
                role="tab"
                aria-selected={metric === m}
                className={metric === m ? "repos-seg-btn on" : "repos-seg-btn"}
                onClick={() => setMetric(m)}
              >
                {METRIC_LABEL[m]}
              </button>
            ))}
          </div>

          <section className="thr-list" aria-label="By repository">
            <div className="thr-list-title">By {METRIC_WORD[metric]}</div>
            {ranked.map((r, i) => {
              const value = metricValue(r, metric);
              // The bar's length against the longest bar is layout; every RATIO printed or drawn is the
              // Gateway's own row share (fix-round finding F-01).
              const width = max > 0 ? (value / max) * 100 : 0;
              const share = formatPercent(metric === "turns" ? r.turnPercent : r.sessionPercent);
              const voiceWidth = r.voiceShare === null ? 0 : r.voiceShare;
              const showSplit = metric === "turns" && r.turns > 0;
              return (
                <div className="repo-item" key={r.repo}>
                  <div className="repo-item-top">
                    <span className="repo-item-rank">{i + 1}</span>
                    <span className="repo-item-name">{r.repoName}</span>
                    <span className="repo-item-val">
                      {value.toLocaleString()}
                      <span className="repo-item-share"> - {share}</span>
                    </span>
                  </div>
                  <div className="repo-item-bar">
                    {showSplit ? (
                      <>
                        <span className="repo-item-voice" style={{ width: `${width * voiceWidth}%` }} />
                        <span className="repo-item-typed" style={{ width: `${width * (1 - voiceWidth)}%` }} />
                      </>
                    ) : (
                      <span className="repo-item-voice" style={{ width: `${width}%` }} />
                    )}
                  </div>
                </div>
              );
            })}
          </section>
        </>
      )}

      {figure !== null && (
        <section className="thr-caveats" aria-label="What this does and doesn't count">
          <div className="thr-list-title">What this does and doesn't count</div>
          <ul>
            <li>
              A turn is one submitted message; the amber part of each bar is the share driven by voice.
              Repos are grouped by their GitHub repository, so every worktree and machine rolls up into one row.
            </li>
            <li>
              Time is not shown (an idle hour looks like a busy one), and tokens and characters are not shown -
              the figure counts submitted turns. Nothing here is fabricated.
            </li>
          </ul>
          {data !== null && data.available && data.notCaptured.length > 0 && (
            <ul>
              {data.notCaptured.map((c, i) => (
                <li key={i}>{c}</li>
              ))}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}
