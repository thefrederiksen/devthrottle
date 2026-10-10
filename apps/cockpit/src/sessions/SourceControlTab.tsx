import { useCallback, useEffect, useRef, useState } from "react";
import {
  getGitStatus,
  type GitSnapshot,
  type GitChangeEntry,
} from "@devthrottle/client-core/api/client";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";

// The surface label on every client-error report from this view, so the stored error
// report names where the user was standing (issue #2189).
const SURFACE = "cockpit-source-control";

// The Cockpit Source Control tab (issue #1266): a READ-ONLY view of the selected session's repository -
// branch, ahead/behind, last commit, and the staged / unstaged file lists - so a browser driver can see
// what is or is not committed on that machine. Clicking a file inserts its repository-relative path into
// the composer (append + focus), so acting happens through the agent via the prompt, never here. There
// are deliberately NO stage / unstage / discard / commit controls; the Gateway exposes no write route.
//
// Data source: the shared client-core getGitStatus (GET /sessions/{sid}/git), which the Gateway proxies
// to the owning Director. The Director enriches its ten-second-cached snapshot with the per-file lists.
//
// Refresh: fetch on tab open, a manual Refresh control, and light polling (~10s). The tab is only
// mounted while it is the active tab (SessionDetail unmounts it on a tab switch), and the polling also
// pauses while the browser page is hidden (issue #1239), so it never polls when nobody is looking.

const POLL_MS = 10_000;

// The one-letter git change kinds (GitChangeEntry.changeKind) spelled out for the reader, matching the
// desktop Source Control tab's wording.
const CHANGE_KIND_LABEL: Record<string, string> = {
  M: "modified",
  A: "added",
  D: "deleted",
  R: "renamed",
  C: "copied",
  "?": "untracked",
  U: "unmerged",
  T: "type changed",
};

function changeKindLabel(kind: string): string {
  return CHANGE_KIND_LABEL[kind] ?? "changed";
}

export interface SourceControlTabProps {
  sessionId: string | undefined;
  /** Insert a clicked file's repository-relative path into the composer (append + focus). */
  onInsertPath: (path: string) => void;
}

export function SourceControlTab({ sessionId, onInsertPath }: SourceControlTabProps) {
  const [snapshot, setSnapshot] = useState<GitSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Guards against overlapping fetches (a manual Refresh landing on top of a poll tick).
  const busyRef = useRef(false);

  const refresh = useCallback(
    async (signal?: AbortSignal) => {
      if (!sessionId || busyRef.current) return;
      busyRef.current = true;
      try {
        const snap = await getGitStatus(sessionId, signal);
        setSnapshot(snap);
        // A non-ok status ("not a git repository" / "git failed") is a normal rendered result, not a
        // transport error; clear the transport error so a recovered fetch stops showing the old banner.
        setError(null);
      } catch (err) {
        if (signal?.aborted === true) return;
        // No silent failure: surface the transport error so the reader knows the state is not shown.
        setError(describeAndReport(SURFACE, "read the repository status", err));
      } finally {
        busyRef.current = false;
        setLoading(false);
      }
    },
    [sessionId],
  );

  // Fetch on tab open, then poll ~10s while the browser page is visible; pause polling when hidden and
  // refresh once on becoming visible again (issue #1239). The interval is torn down on unmount (tab
  // switch) so the terminal-first session view never keeps a background git poll running.
  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    // Release the overlap guard for this fresh lifecycle: a previous run's in-flight fetch was aborted by
    // its cleanup, but its `finally` (which clears the guard) has not run yet, so without this reset the
    // very first fetch of the new session/mount would be swallowed and the snapshot would stay empty.
    busyRef.current = false;
    setLoading(true);
    setSnapshot(null);
    setError(null);
    void refresh(controller.signal);

    let timer: number | undefined;
    const start = () => {
      if (timer === undefined) {
        timer = window.setInterval(() => void refresh(controller.signal), POLL_MS);
      }
    };
    const stop = () => {
      if (timer !== undefined) {
        window.clearInterval(timer);
        timer = undefined;
      }
    };
    const onVisibility = () => {
      if (document.visibilityState === "visible") {
        void refresh(controller.signal);
        start();
      } else {
        stop();
      }
    };
    if (document.visibilityState === "visible") start();
    document.addEventListener("visibilitychange", onVisibility);

    return () => {
      controller.abort();
      stop();
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [sessionId, refresh]);

  const manualRefresh = useCallback(() => void refresh(), [refresh]);

  return (
    <div className="scm-tab">
      <div className="scm-header">
        <ScmHeaderBody snapshot={snapshot} error={error} loading={loading} />
        <button type="button" className="scm-refresh" onClick={manualRefresh} disabled={loading}>
          Refresh
        </button>
      </div>

      {error === null && snapshot !== null && snapshot.status === "ok" && (
        <div className="scm-lists">
          <ScmSection title="Staged" entries={snapshot.stagedChanges} onInsertPath={onInsertPath} />
          <ScmSection title="Changes" entries={snapshot.unstagedChanges} onInsertPath={onInsertPath} />
        </div>
      )}
    </div>
  );
}

// The header line: branch, ahead/behind, last commit for an ok snapshot; a plain explanation for a
// non-repository or a git failure; the transport error when the fetch itself failed; "Loading..." until
// the first result lands. Never blank.
function ScmHeaderBody({
  snapshot,
  error,
  loading,
}: {
  snapshot: GitSnapshot | null;
  error: string | null;
  loading: boolean;
}) {
  if (error !== null) {
    return (
      <div className="scm-state scm-state-error" role="alert">
        <span className="scm-state-title">Could not load source control</span>
        <span className="scm-state-detail">{error}</span>
      </div>
    );
  }
  if (snapshot === null) {
    return <div className="scm-state">{loading ? "Loading..." : "No source-control data yet."}</div>;
  }
  if (snapshot.status === "not_a_repo") {
    return (
      <div className="scm-state">
        <span className="scm-state-title">Not a git repository</span>
        <span className="scm-state-detail">This session's working directory is not tracked by git.</span>
      </div>
    );
  }
  if (snapshot.status !== "ok") {
    // "git_failed" (or any future non-ok status): show the error detail, never a silent blank.
    return (
      <div className="scm-state scm-state-error" role="alert" /* error-report-exempt: git's own failure on the session's machine, carried in the Director's answer; nothing failed in the Cockpit */>
        <span className="scm-state-title">git failed</span>
        <span className="scm-state-detail">{snapshot.error ?? "git could not read this repository."}</span>
      </div>
    );
  }

  return (
    <div className="scm-repo">
      <span className="scm-branch" title="Current branch">
        {snapshot.branch.length > 0 ? snapshot.branch : "(detached)"}
      </span>
      <span className="scm-sync">
        {snapshot.ahead > 0 && <span className="scm-ahead">{snapshot.ahead} ahead</span>}
        {snapshot.behind > 0 && <span className="scm-behind">{snapshot.behind} behind</span>}
        {snapshot.ahead === 0 && snapshot.behind === 0 && (
          <span className="scm-insync">up to date</span>
        )}
      </span>
      {snapshot.lastCommit.length > 0 && (
        <span className="scm-commit" title="Last commit">
          {snapshot.lastCommit}
        </span>
      )}
    </div>
  );
}

// One of the two file sections (Staged / Changes). Each row is the change kind plus the repository-
// relative path; the WHOLE row inserts the path into the composer, with a small insert affordance too.
function ScmSection({
  title,
  entries,
  onInsertPath,
}: {
  title: string;
  entries: GitChangeEntry[];
  onInsertPath: (path: string) => void;
}) {
  return (
    <section className="scm-section">
      <div className="scm-section-title">
        {title}
        <span className="scm-count">{entries.length}</span>
      </div>
      {entries.length === 0 ? (
        <div className="scm-section-empty">No files</div>
      ) : (
        <ul className="scm-files">
          {entries.map((entry, i) => (
            <li key={`${entry.path}-${i}`}>
              <button
                type="button"
                className="scm-file"
                onClick={() => onInsertPath(entry.path)}
                title={`Insert ${entry.path} into the composer`}
              >
                <span
                  className={`scm-kind scm-kind-${entry.changeKind === "?" ? "untracked" : entry.changeKind.toLowerCase()}`}
                  title={changeKindLabel(entry.changeKind)}
                >
                  {entry.changeKind}
                </span>
                <span className="scm-path">{entry.path}</span>
                <span className="scm-insert" aria-hidden="true">
                  insert
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
