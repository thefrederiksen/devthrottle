import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  createSession,
  getDirectors,
  getRepos,
  type DirectorInfo,
  type RepoInfo,
} from "../api/client";
import { durationLabel, useNow } from "../sessions/waiting";

// Add-session flow (issue #812): a faithful 1:1 translation of the Android (MAUI)
// phone/CcDirectorClient NewSessionPanel (TalkPage.xaml ~287-345, TalkPage.xaml.cs
// OpenNewSessionPanelAsync/OnDirectorSelected/LoadReposAsync/CreateSessionAsync ~819-947). NOT a
// redesign:
//
//   * Step 1 pick a MACHINE from the live GET /directors (default-select the most-recently-seen,
//     so the repos load with one fewer tap).
//   * Step 2 pick a REPOSITORY from GET /directors/{id}/repos (newest-used first) OR type a path.
//   * Create via POST /directors/{id}/sessions { repoPath, agent: "ClaudeCode", wingmanEnabled:
//     false } - agent hardcoded, type omitted, exactly like Android (NO agent picker).
//   * On success, open the new session straight on the Terminal view (a fresh session is
//     wingman-off, so the Terminal is the natural landing - matching the Android EnterTalk).
//
// Every call carries the Bearer token, so the flow works with global Gateway auth on or off.

// The local time-of-day for an ISO 8601 stamp (e.g. "6:20 AM"), or "" if missing/unparseable.
function timeOfDay(iso: string): string {
  if (!iso) return "";
  const t = new Date(iso);
  if (Number.isNaN(t.getTime())) return "";
  return t.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

// Absolute start label: the time-of-day when the Director started today (e.g. "6:20 AM"), otherwise
// a short date (e.g. "Jun 28"). Returns "" if the stamp is missing/unparseable so the caller omits
// the "started" segment.
function startedAbsolute(iso: string): string {
  if (!iso) return "";
  const t = new Date(iso);
  if (Number.isNaN(t.getTime())) return "";
  const now = new Date();
  const startedToday =
    t.getFullYear() === now.getFullYear() &&
    t.getMonth() === now.getMonth() &&
    t.getDate() === now.getDate();
  return startedToday
    ? timeOfDay(iso)
    : t.toLocaleDateString([], { month: "short", day: "numeric" });
}

// Machine-row subtitle (issue #848). With a valid startedAt it reads
// "up <uptime> . started <startTime> . seen <lastSeen>" - uptime climbs the shared duration ladder
// and ticks live off `now`, started is absolute (time today / short date older), seen is the kept
// last-seen. With no usable startedAt it degrades to the prior last-seen-only subtitle (never an
// "up NaN" / "Invalid Date"). The " . " separator is ASCII per the approved layout.
function directorSubtitle(d: DirectorInfo, now: number): string {
  const seenTime = timeOfDay(d.lastSeen);
  const up = durationLabel(d.startedAt, now);
  if (up === "") {
    // No usable startedAt: keep the existing last-seen rendering only.
    return seenTime ? `last seen ${seenTime}` : "not seen recently";
  }

  const parts: string[] = [up === "just now" ? "just now" : `up ${up}`];
  const started = startedAbsolute(d.startedAt);
  if (started) parts.push(`started ${started}`);
  parts.push(seenTime ? `seen ${seenTime}` : "not seen recently");
  return parts.join(" . ");
}

function directorLabel(d: DirectorInfo): string {
  if (d.machineName.trim()) return d.machineName.trim();
  return d.directorId || "director";
}

function repoLabel(r: RepoInfo): string {
  if (r.name.trim()) return r.name.trim();
  const parts = r.path.replace(/[\\/]+$/, "").split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : r.path;
}

export function NewSession() {
  const navigate = useNavigate();

  // Ticks once a second so each machine row's "up <uptime>" recomputes from its startedAt without
  // refetching /directors (issue #848), matching the roster's needs-you cards (issue #844).
  const now = useNow(1000);

  const [directors, setDirectors] = useState<DirectorInfo[] | null>(null);
  const [directorsError, setDirectorsError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const [repos, setRepos] = useState<RepoInfo[] | null>(null);
  const [reposStatus, setReposStatus] = useState("Pick a machine first.");

  const [manualPath, setManualPath] = useState("");
  const [creating, setCreating] = useState(false);
  const [createStatus, setCreateStatus] = useState<string | null>(null);

  // Guards against a stale repo response landing after the user switched machines.
  const reposReqRef = useRef(0);

  // Step 1: load the machines once. Default-select the most-recently-seen (directors[0]) so the
  // repos load immediately, exactly like the Android OpenNewSessionPanelAsync.
  useEffect(() => {
    const controller = new AbortController();
    getDirectors(controller.signal)
      .then((list) => {
        setDirectors(list);
        setDirectorsError(null);
        if (list.length > 0) setSelectedId(list[0].directorId);
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        setDirectorsError(err instanceof Error ? err.message : "Could not load machines");
      });
    return () => controller.abort();
  }, []);

  // Step 2: whenever the selected machine changes, load THAT machine's repos.
  useEffect(() => {
    if (!selectedId) return;
    const controller = new AbortController();
    const reqId = ++reposReqRef.current;
    setRepos(null);
    setReposStatus("Loading repos...");
    getRepos(selectedId, controller.signal)
      .then((list) => {
        if (reqId !== reposReqRef.current) return; // a newer selection superseded this one
        setRepos(list);
        setReposStatus(
          list.length === 0 ? "No recent repos here. Enter a path below." : `${list.length} recent repo(s). Tap one to start.`,
        );
      })
      .catch((err) => {
        if (controller.signal.aborted || reqId !== reposReqRef.current) return;
        setRepos([]);
        setReposStatus(err instanceof Error ? `Could not load repos: ${err.message}` : "Could not load repos");
      });
    return () => controller.abort();
  }, [selectedId]);

  const create = useCallback(
    async (repoPath: string) => {
      if (creating) return; // a create is already in flight (guards a double-tap)
      if (!selectedId) {
        setCreateStatus("Pick a machine first.");
        return;
      }
      const path = repoPath.trim();
      if (!path) {
        setCreateStatus("Enter a repo path, or tap a recent repo above.");
        return;
      }
      setCreating(true);
      setCreateStatus(`Creating session in ${path}...`);
      try {
        const session = await createSession(selectedId, path);
        const sid = session.sessionId;
        if (!sid) throw new Error("the created session had no id");
        // Open the new session straight on the Terminal view (Android EnterTalk parity).
        navigate(`/session/${encodeURIComponent(sid)}`);
      } catch (err) {
        setCreateStatus(err instanceof Error ? err.message : "Could not create session");
        setCreating(false);
      }
    },
    [creating, selectedId, navigate],
  );

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">&larr; Roster</Link>
        <h1>Start a session</h1>
      </header>

      {createStatus !== null && <div className="status-line">{createStatus}</div>}

      {/* Step 1: machine */}
      <section className="group">
        <h2 className="group-title">1. Machine</h2>
        {directorsError !== null && <div className="banner banner-error" role="alert">{directorsError}</div>}
        {directors === null && directorsError === null && <p className="status-line">Loading machines...</p>}
        {directors !== null && directors.length === 0 && (
          <p className="status-line">No machines found.</p>
        )}
        {directors !== null && directors.length > 0 && (
          <ul className="roster">
            {directors.map((d) => (
              <li key={d.directorId} className={`row${d.directorId === selectedId ? " row-selected" : ""}`}>
                <button type="button" className="picker-link" onClick={() => setSelectedId(d.directorId)}>
                  <span className="row-body">
                    <span className="row-name">{directorLabel(d)}</span>
                    <span className="row-context">{directorSubtitle(d, now)}</span>
                  </span>
                  {d.directorId === selectedId && <span className="picker-check" aria-hidden="true">selected</span>}
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      {/* Step 2: repository */}
      <section className="group">
        <h2 className="group-title">2. Repository</h2>
        <p className="status-line">{reposStatus}</p>
        {repos !== null && repos.length > 0 && (
          <ul className="roster">
            {repos.map((r) => (
              <li key={r.path} className="row">
                <button
                  type="button"
                  className="picker-link"
                  disabled={creating}
                  onClick={() => create(r.path)}
                >
                  <span className="row-body">
                    <span className="row-name">{repoLabel(r)}</span>
                    <span className="row-context">{r.path}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}

        <label className="newsession-manual-label" htmlFor="newsession-path">Or enter a path</label>
        <div className="newsession-manual">
          <input
            id="newsession-path"
            className="term-input"
            type="text"
            inputMode="text"
            autoComplete="off"
            autoCapitalize="off"
            autoCorrect="off"
            spellCheck={false}
            placeholder="D:\Repos\my-project"
            value={manualPath}
            onChange={(e) => setManualPath(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                void create(manualPath);
              }
            }}
          />
          <button
            type="button"
            className="term-btn newsession-create"
            disabled={creating || !selectedId}
            onClick={() => create(manualPath)}
          >
            {creating ? "Creating..." : "Create session"}
          </button>
        </div>
      </section>
    </div>
  );
}
