import { useCallback, useEffect, useState } from "react";
import { MicrophoneQualityPanel } from "@devthrottle/client-core/transcription/MicrophoneQualityPanel";
import {
  clearTranscriptionHistory,
  countFailedTurns,
  getTranscriptionStats,
  getTranscriptionTerms,
  SUCCESS_OUTCOME,
  type TranscriptionStats,
  type TermFrequency,
} from "@devthrottle/client-core/transcription/transcriptionAnalysisClient";

// The Transcription Health page: shows how fast and how well voice dictation is working on THIS
// machine, read from the minimized local transcription history the Gateway records for every turn (nothing
// leaves the machine). It reads GET /transcription/stats + /transcription/terms through the Gateway
// front door (client-core). Responsive (CodingStyle.md): renders immediately with a loading state and
// loads asynchronously; a load failure shows an explicit message, never a fabricated healthy state.

const WINDOWS: ReadonlyArray<{ label: string; days: number | undefined }> = [
  { label: "Today", days: 1 },
  { label: "7 days", days: 7 },
  { label: "30 days", days: 30 },
];

function seconds(ms: number): string {
  if (ms <= 0) return "-";
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`;
}

function outcomeLabel(code: string): string {
  switch (code) {
    case "ok":
      return "succeeded";
    case "out_of_credits":
      return "ran out of credits";
    case "provider_error":
      return "the provider rejected it";
    default:
      return code;
  }
}

export function TranscriptionHealthView() {
  const [days, setDays] = useState<number | undefined>(7);
  const [stats, setStats] = useState<TranscriptionStats | null>(null);
  const [terms, setTerms] = useState<TermFrequency[]>([]);
  const [loadError, setLoadError] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [clearing, setClearing] = useState(false);

  const load = useCallback(async (window: number | undefined, signal?: AbortSignal) => {
    try {
      setLoadError(false);
      const [s, t] = await Promise.all([
        getTranscriptionStats(window, signal),
        getTranscriptionTerms(10, window, signal),
      ]);
      setStats(s);
      setTerms(t);
    } catch {
      if (signal?.aborted) return;
      setLoadError(true);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(days, controller.signal);
    return () => controller.abort();
  }, [load, days]);

  // Refetch the current window on demand, so the page can be brought up to date without a full
  // browser reload. Immediate visual feedback: the button reports "Refreshing..." while it runs.
  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await load(days);
    } finally {
      setRefreshing(false);
    }
  }, [load, days]);

  const clearHistory = useCallback(async () => {
    if (!window.confirm("Delete all local Transcription Health history and troubleshooting audio?")) return;
    setClearing(true);
    try {
      await clearTranscriptionHistory();
      await load(days);
    } catch {
      setLoadError(true);
    } finally {
      setClearing(false);
    }
  }, [days, load]);

  // The failure count comes from the same byOutcome map the outcome breakdown renders, so the
  // number, the banner, and the breakdown are one source and can never contradict each other.
  const failures = stats ? countFailedTurns(stats) : 0;
  const healthy = stats !== null && stats.totalTurns > 0 && failures === 0;

  return (
    <div className="page txh">
      <div className="page-head">
        <h1>Transcription Health</h1>
        <button
          type="button"
          className="txh-refresh"
          onClick={() => void refresh()}
          disabled={refreshing}
        >
          {refreshing ? "Refreshing..." : "Refresh"}
        </button>
        <button
          type="button"
          className="txh-refresh"
          onClick={() => void clearHistory()}
          disabled={clearing}
        >
          {clearing ? "Clearing..." : "Clear local data"}
        </button>
      </div>
      <p className="txh-lede">
        How fast and how well your voice dictation is working, from your self-hosted Gateway only. Nothing
        here is sent to DevThrottle or an analytics service. History excludes transcript text and provider
        error bodies and is kept for 30 days.
        Associated troubleshooting audio is kept for at most 24 hours. Clear removes both.
      </p>

      <div className="txh-windows" role="group" aria-label="Time window">
        {WINDOWS.map((w) => (
          <button
            key={w.label}
            type="button"
            className={days === w.days ? "txh-window txh-window-on" : "txh-window"}
            onClick={() => setDays(w.days)}
          >
            {w.label}
          </button>
        ))}
      </div>

      {loadError ? (
        <div className="txh-error">
          Couldn&apos;t load transcription data from the Gateway.
          <button type="button" className="txh-retry" onClick={() => void load(days)}>
            Retry
          </button>
        </div>
      ) : stats === null ? (
        <div className="txh-loading">Loading...</div>
      ) : stats.totalTurns === 0 ? (
        <div className="txh-empty">No dictations recorded in this window yet. Try dictating something.</div>
      ) : (
        <>
          <div className={healthy ? "txh-banner txh-ok" : "txh-banner txh-warn"}>
            {healthy
              ? `Transcription is healthy - all ${stats.totalTurns} dictations succeeded.`
              : `${failures} of ${stats.totalTurns} dictations did not succeed - see the breakdown below.`}
          </div>

          <div className="txh-cards">
            <div className="txh-card">
              <div className="txh-card-title">Speech to text</div>
              <div className="txh-card-big">{seconds(stats.transcriptionMs.p50)}</div>
              <div className="txh-card-sub">
                typical &middot; slowest {seconds(stats.transcriptionMs.max)}
              </div>
            </div>
            <div className="txh-card">
              <div className="txh-card-title">Fixing your words</div>
              <div className="txh-card-big">{seconds(stats.cleanupMs.p50)}</div>
              <div className="txh-card-sub">
                typical &middot; slowest {seconds(stats.cleanupMs.max)}
              </div>
            </div>
            <div className="txh-card">
              <div className="txh-card-title">Dictations</div>
              <div className="txh-card-big">{stats.totalTurns}</div>
              <div className="txh-card-sub">{stats.successfulTurns} succeeded</div>
            </div>
            <div className="txh-card">
              <div className="txh-card-title">Words dictated</div>
              <div className="txh-card-big">{stats.totalWords.toLocaleString()}</div>
              <div className="txh-card-sub">
                {stats.cleanupAppliedTurns} had a word corrected
              </div>
            </div>
          </div>

          {failures > 0 && (
            <div className="txh-section">
              <h2>Why dictations failed</h2>
              <ul className="txh-outcomes">
                {Object.entries(stats.byOutcome)
                  .filter(([code]) => code !== SUCCESS_OUTCOME)
                  .map(([code, n]) => (
                    <li key={code}>
                      <span className="txh-count">{n}</span> {outcomeLabel(code)}
                    </li>
                  ))}
              </ul>
            </div>
          )}

          <div className="txh-section">
            <h2>Most-corrected words</h2>
            {terms.length === 0 ? (
              <p className="txh-muted">No custom words needed correcting in this window.</p>
            ) : (
              <table className="txh-terms">
                <thead>
                  <tr>
                    <th>Heard as</th>
                    <th>Corrected to</th>
                    <th>Times</th>
                  </tr>
                </thead>
                <tbody>
                  {terms.map((t) => (
                    <tr key={`${t.find}->${t.replace}`}>
                      <td>{t.find}</td>
                      <td>{t.replace}</td>
                      <td>{t.count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          <p className="txh-agent-note">
            Want more? Ask any agent to dig into this - it can query the Gateway directly at
            <code> /transcription/stats</code>, <code>/transcription/turns</code>,
            and <code> /transcription/terms</code> - for example
            &quot;which word do I mis-say most&quot; or &quot;how has my dictation speed changed&quot;.
          </p>
        </>
      )}

      {/* Outside the stats conditional on purpose: the microphone measurements are most useful exactly
          when there are no stats to show - a Gateway that cannot be reached, or a first run with no
          history - because that is when the user is asking "is my microphone even working?". A load
          failure above must not take this down with it. */}
      <div className="txh-section">
        <MicrophoneQualityPanel />
      </div>

      {/* The two on-demand checks used to be mounted here as well. They are settings-page controls -
          you run them to fix something, not to read a report - and they now live in one place, the
          Transcription tab of Settings, which is the SAME tab the phone shows. Duplicating them here
          would put the same widget on two pages and invite them to drift. */}
      {/* This report sits beneath those checks on the same tab now (owner, 8 Oct 2026), so it points up at them rather
          than linking to the page it is already on. */}
      <p className="txh-agent-note">
        Microphone or dictation coming out wrong? Run the checks at the top of this tab - Test microphone records a
        clip and plays it back to you, Test transcription shows what the transcriber heard.
      </p>
    </div>
  );
}
