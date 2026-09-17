import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { gatewayErrorMessage, getRepos, type RepoInfo, type SessionDto } from "@devthrottle/client-core/api/client";
import { dotHex, inDesktopOrder, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import { dotTitle } from "@devthrottle/client-core/sessions/sessionColours";
import { useSessionColourLegend } from "@devthrottle/client-core/sessions/ColourLegend";
import {
  getDirectorSettings,
  getFleetDirectors,
  putDirectorSettings,
  REACHABILITY_STOPPED,
  type FleetDirector,
  type MachineError,
} from "@devthrottle/client-core/fleet/fleetClient";
import { directorStateLabel } from "@devthrottle/client-core/fleet/directorPresentation";
import { useSharedRoster } from "@devthrottle/client-core/fleet/rosterStore";
import { useVisiblePolling } from "@devthrottle/client-core/polling/useVisiblePolling";
import { useNow } from "@devthrottle/client-core/polling/useNow";
import { isSettingsDirty, prettyPrintSettings } from "@devthrottle/client-core/fleet/settingsEditor";
import { ConfirmDialog } from "../components";
import { clockLabel, portLabel, relativeTime, repoBasename, uptime } from "./format";
import { directorPrimaryLabel } from "./directorsFormat";
import { DirectorUpdateCard } from "./DirectorUpdateCard";

// The standalone Director page (issue #975) - the React port of the Blazor DirectorDetail.razor:
// registration facts, health, the Director's live sessions, and the repositories it offers for new
// sessions. It also carries the Director-scoped settings editor (raw JSON GET/PUT
// /directors/{id}/settings, routed by Director id) that the Blazor Cockpit exposed as a modal - so
// the whole per-machine read/write surface lands here. Every read/write is same-origin through the
// Gateway (client-core), never a Director address.
const POLL_MS = 5000;
const REPO_EVERY_TICKS = 6; // the repo list proxies to the Director itself - slower, every 30s.

// Everything the Sessions table's row shows about a session's state, derived in ONE place from ONE
// source: the Gateway's stamped fold. Pure and exported so the row's rule is testable without a DOM -
// the row below renders exactly this and adds nothing of its own.
//
// This row used to carry THREE authorities: a Gateway-stamped dot, a State cell re-derived from raw
// activity fields (humanizeState), and a SNOOZED tag read off the raw onHold boolean. A snoozed
// session that woke up and started working therefore drew a BLUE dot beside the word SNOOZED, and a
// State cell that agreed with neither. All three now come from the same fold, so they cannot
// disagree - and it fails loudly (stateLabel / effectiveColor throw) when the Gateway did not stamp,
// rather than guessing a colour or a word.
export function directorSessionRow(s: SessionDto): {
  dot: string;
  state: string;
  snoozed: boolean;
  briefing: boolean;
} {
  const label = stateLabel(s);
  return {
    dot: dotHex(s),
    state: label,
    // "Snoozed" and "Wingman reading" are the fold's OWN words (SessionOrdering.StateLabel), so asking
    // the label is asking the fold. Never re-read s.onHold here: that is a raw sensor fact the fold
    // has already considered, and consulting it again is how the blue-dot-labelled-SNOOZED row
    // happened.
    snoozed: label === "Snoozed",
    briefing: label === "Wingman reading",
  };
}

export function DirectorDetailView() {
  const { directorId = "" } = useParams();
  const navigate = useNavigate();

  // This Director's sessions and its reachability come from the ONE shared roster store (issue #1239),
  // so the page never runs its own roster fan-out. The registry facts and the repo list are this page's
  // own concern and stay a local, visibility-aware poll below.
  const roster = useSharedRoster();
  const sessions: SessionDto[] = roster.sessions ?? [];
  const machineError: MachineError | null =
    roster.machineErrors.find((e) => (e.directorId ?? "").toLowerCase() === directorId.toLowerCase()) ?? null;
  // A Director that SAID GOODBYE is not in machineErrors - nothing failed - so it is read from the
  // reachability list instead. Not running and cannot be reached are different facts, and only the
  // second is a problem; this page has to say which one it is looking at.
  const reach = roster.directors.find((r) => (r.directorId ?? "").toLowerCase() === directorId.toLowerCase());
  const wasShutDown = reach?.state === REACHABILITY_STOPPED;

  const [director, setDirector] = useState<FleetDirector | null>(null);
  const { legend } = useSessionColourLegend();
  const [notFound, setNotFound] = useState(false);
  const [repos, setRepos] = useState<RepoInfo[] | null>(null);
  const [reposError, setReposError] = useState<string | null>(null);
  const [registryError, setRegistryError] = useState<string | null>(null);
  const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
  // Live relative times re-render off the ONE shared 1-second ticker, not a per-page timer.
  const now = useNow();
  const tickRef = useRef(0);
  // The current reachability, mirrored into a ref so the stable poll callback can gate the repo fetch on
  // it without taking the roster as a dependency (which would rebuild the poll loop every 2 seconds).
  const reachableRef = useRef(true);
  // THE GATE IS "CAN THIS DIRECTOR ANSWER", NOT "IS SOMETHING WRONG". It read machineError alone, and a
  // shut-down Director is deliberately absent from that list - so its page went on proxying getRepos to a
  // tunnel that is gone, every thirty seconds, forever, while the page itself said nothing could be read
  // from it.
  reachableRef.current = machineError === null && !wasShutDown;

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const ds = await getFleetDirectors(signal);
      const d = ds.find((x) => x.directorId.toLowerCase() === directorId.toLowerCase()) ?? null;
      setDirector(d);
      setNotFound(d === null);
      setRegistryError(null);
      setLastRefresh(new Date());

      const tick = tickRef.current;
      if (d !== null && reachableRef.current && tick % REPO_EVERY_TICKS === 0) {
        try {
          setRepos(await getRepos(directorId, signal));
          setReposError(null);
        } catch (err) {
          if (signal?.aborted !== true) setReposError(gatewayErrorMessage(err));
        }
      }
      tickRef.current = tick + 1;
    } catch (err) {
      if (signal?.aborted === true) return;
      setRegistryError(gatewayErrorMessage(err));
    }
  }, [directorId]);

  // A new director id starts a clean load (Blazor OnParametersSetAsync reset). The visible poll itself
  // restarts on the new directorId because `refresh` changes with it.
  useEffect(() => {
    setDirector(null);
    setNotFound(false);
    setRepos(null);
    setReposError(null);
    tickRef.current = 0;
  }, [directorId]);

  useVisiblePolling(refresh, POLL_MS);

  // One banner for the page: the registry fetch failure if any, otherwise the shared roster's.
  const lastError = registryError ?? roster.error;

  const mine = inDesktopOrder(sessions.filter((s) => (s.directorId ?? "").toLowerCase() === directorId.toLowerCase()));

  if (notFound) {
    return (
      <div className="dpage">
        {lastError !== null && <div className="dpage-error">{lastError}</div>}
        <header className="dpage-head"><h1 className="dpage-h1">Director</h1></header>
        <div className="dtbl-empty">
          No Director with id <span className="dmono">{directorId}</span> is registered with this Gateway - it has
          shut down and deregistered, or its registration aged out.{" "}
          <Link className="ddet-link" to="/directors">Back to all directors</Link>
        </div>
      </div>
    );
  }

  if (director === null) {
    // The initial load has not produced a Director yet. Distinguish "still loading" from "the load
    // failed" (issue #1028): a failed initial fetch used to fall through to a permanent "loading..."
    // sub-label under an error banner. When lastError is set and we have no Director, show an explicit
    // error state (the page keeps polling, so it recovers on its own once the Gateway answers).
    if (lastError !== null) {
      return (
        <div className="dpage">
          <header className="dpage-head"><h1 className="dpage-h1">Director</h1><span className="dpage-sub">unavailable</span></header>
          <div className="dpage-error">{lastError}</div>
          <div className="dtbl-empty">
            Could not load this Director from the Gateway. It will keep retrying automatically.{" "}
            <Link className="ddet-link" to="/directors">Back to all directors</Link>
          </div>
        </div>
      );
    }
    return (
      <div className="dpage">
        <header className="dpage-head"><h1 className="dpage-h1">Director</h1><span className="dpage-sub">loading...</span></header>
      </div>
    );
  }

  const d = director;
  const unreachable = machineError !== null;
  // Both states mean the same thing for what this page can DO - the tunnel is gone, so nothing here can
  // be read from or written to the Director - while meaning opposite things about whether anything is
  // wrong. The actions are gated on the first; the wording is gated on the second.
  const linkDown = unreachable || wasShutDown;

  return (
    <div className="dpage">
      {lastError !== null && <div className="dpage-error">{lastError}</div>}

      <header className="dpage-head ddet-head">
        {/* devthrottle_internal#1176: the display name is the headline when the Director reports one;
            the machine name then moves beside it as secondary detail. Unnamed Directors are unchanged,
            and a name that IS the machine name (the seeded default) is not repeated beside itself. */}
        <h1 className="dpage-h1">{directorPrimaryLabel(d)}</h1>
        {(d.machineName ?? "").trim().length > 0 &&
          (d.machineName ?? "").trim().toLowerCase() !== directorPrimaryLabel(d).toLowerCase() && (
          <span className="dpage-sub">{d.machineName}</span>
        )}
        <span className="dmono dpage-sub" title={d.directorId}>{portLabel(d.controlEndpoint, d.tailnetEndpoint, d.directorId)}</span>
        <span className="dpage-sub">v{d.version}</span>
        {unreachable ? (
          <span className="ddet-chip dstat-warn" title={machineError?.error}>UNREACHABLE</span>
        ) : wasShutDown ? (
          <span className="ddet-chip dstat-idle">{directorStateLabel(reach).toUpperCase()}</span>
        ) : (
          <span className="ddet-chip dstat-ok">OK</span>
        )}
        <span className="dpage-refreshed">{lastRefresh === null ? "" : `updated ${clockLabel(lastRefresh)}`}</span>
      </header>

      {unreachable && (
        <div className="dpage-error">
          The Gateway cannot reach this Director right now: {machineError?.error}. Registration facts below may be
          stale; its sessions are missing.
        </div>
      )}

      {/* Not an error, and not styled as one. This Director announced its own shutdown, so there is
          nothing to investigate and nothing to fix - the page simply cannot read from it, and says so
          plainly instead of raising an alarm about a machine that may be perfectly healthy. */}
      {!unreachable && wasShutDown && (
        <div className="dpage-note">
          This Director was shut down and is not running. The facts below are from when it last ran; nothing here can
          be read from or written to it until it starts again.
        </div>
      )}

      <div className="ddet-cols">
        <div className="ddet-main">
          <section className="ddet-sec">
            <div className="ddet-sec-head">
              <h2>Sessions</h2>
            </div>
            {mine.length === 0 ? (
              <div className="ddet-quiet">No sessions running on this Director.</div>
            ) : (
              <div className="dtbl-scroll">
                <table className="dtbl">
                  <thead>
                    <tr><th>Session</th><th>Repo</th><th>State</th><th>Last activity</th><th /></tr>
                  </thead>
                  <tbody>
                    {mine.map((s) => {
                      const sid = s.sessionId ?? "";
                      // Issue #1177 (Phase 2.3): render the Gateway's fold, not a local re-derive from the
                      // raw statusColor. "Wingman reading" is the Gateway's label for a briefing/auto-explain
                      // read in flight (SessionOrdering.StateLabel), which is exactly when this sub-line shows.
                      const row = directorSessionRow(s);
                      return (
                        <tr key={sid} className="dtbl-rowlink" title="Session details"
                            onClick={() => navigate(`/session/${encodeURIComponent(sid)}`)}>
                          <td>
                            <span className="dcell-dot" style={{ background: row.dot }} title={dotTitle(s, legend)} />
                            <span className="dcell-name">{(s.name ?? "").trim().length === 0 ? repoBasename(s.repoPath) : s.name}</span>
                            {row.snoozed && <span className="dtag dtag-hold">SNOOZED</span>}
                            {row.briefing ? (
                              <div className="dcell-sub dcell-briefing">wingman reading...</div>
                            ) : (s.railLine ?? "").trim().length > 0 ? (
                              <div className="dcell-sub">{s.railLine}</div>
                            ) : null}
                          </td>
                          <td className="dcell-ellipsis" title={s.repoPath ?? undefined}>{repoBasename(s.repoPath)}</td>
                          {/* The Gateway's stamped label - the SAME fold that produced the dot two cells
                              left, so the two cannot disagree. This used to be
                              humanizeState(s.assessedState ?? s.activityState): a local re-derive from raw
                              sensor fields, which made this one row carry two authorities. */}
                          <td className="ddim">{row.state}</td>
                          <td className="ddim" title={s.lastActivityAt ?? undefined}>{relativeTime(s.lastActivityAt, { withAgo: true, now })}</td>
                          <td>
                            <Link className="ddet-link" to={`/session/${encodeURIComponent(sid)}`} onClick={(e) => e.stopPropagation()}>drive &rarr;</Link>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <section className="ddet-sec">
            <div className="ddet-sec-head">
              <h2>Repositories</h2>
              <span className="ddet-sec-meta">{repos === null ? "" : `${repos.length} registered`}</span>
            </div>
            {reposError !== null ? (
              <div className="ddet-quiet">Could not list repositories: {reposError}</div>
            ) : repos === null ? (
              <div className="ddet-quiet">Loading...</div>
            ) : repos.length === 0 ? (
              <div className="ddet-quiet">No repositories registered on this Director.</div>
            ) : (
              <div className="dtbl-scroll">
                <table className="dtbl">
                  <thead><tr><th>Name</th><th>Path</th><th>Last used</th></tr></thead>
                  <tbody>
                    {[...repos].sort((a, b) => String(b.lastUsed ?? "").localeCompare(String(a.lastUsed ?? ""))).map((r) => (
                      <tr key={r.path}>
                        <td className="dcell-name">{r.name}</td>
                        <td className="dmono dcell-ellipsis" title={r.path}>{r.path}</td>
                        <td className="ddim" title={r.lastUsed ?? undefined}>{relativeTime(r.lastUsed, { withAgo: true, now })}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <DirectorSettings directorId={d.directorId} reachable={!linkDown} />
        </div>

        <div className="ddet-side">
          <DirectorUpdateCard directorId={d.directorId} machineName={d.machineName ?? ""} />
          <section className="ddet-sec">
            <div className="ddet-sec-head"><h2>Registration</h2></div>
            <dl className="ddet-kv">
              <dt>Director id</dt><dd className="dmono">{d.directorId}</dd>
              {(d.displayName ?? "").trim().length > 0 && (
                <>
                  <dt>Name</dt><dd>{d.displayName}</dd>
                </>
              )}
              <dt>Machine</dt><dd>{d.machineName}</dd>
              <dt>User</dt><dd>{d.user}</dd>
              <dt>Process id</dt><dd className="dmono">{d.pid}</dd>
              <dt>Version</dt><dd className="dmono">{d.version}</dd>
              <dt>Discovery</dt><dd>{d.source === "http" ? "push (HTTP registration)" : "local (file watch)"}</dd>
              <dt>Control endpoint</dt><dd className="dmono">{d.controlEndpoint}</dd>
              {(d.tailnetEndpoint ?? "").trim().length > 0 && (
                <>
                  <dt>Tailnet endpoint</dt><dd className="dmono">{d.tailnetEndpoint}</dd>
                </>
              )}
              <dt>Started</dt><dd title={d.startedAt}>{relativeTime(d.startedAt, { withAgo: true, now })} (up {uptime(d.startedAt, now)})</dd>
              <dt>Last seen</dt><dd title={d.lastSeen ?? undefined}>{relativeTime(d.lastSeen, { withAgo: true, now })}</dd>
              <dt>Terminal stream</dt>
              {(d.streamVerifyError ?? null) !== null ? (
                <dd className="dstat-err" title={d.streamVerifyError ?? undefined}>DOWN - WebSocket stream unreachable</dd>
              ) : (d.streamVerifiedAt ?? null) !== null ? (
                <dd className="dstat-ok" title={d.streamVerifiedAt ?? undefined}>OK (verified {relativeTime(d.streamVerifiedAt, { withAgo: true, now })})</dd>
              ) : (
                <dd className="ddim">not verified</dd>
              )}
            </dl>
          </section>
        </div>
      </div>
    </div>
  );
}

// The Director-scoped settings editor: a raw-JSON GET/PUT round-trip (routed by Director id through
// the Gateway). It matches the Blazor Cockpit's settings modal - a free-form JSON editor, not a
// structured form, because the Director's settings body is an opaque object the Director owns. Load
// pretty-prints the JSON; Save validates (JSON.parse) before writing so a malformed edit never
// reaches the wire, and the Director re-applies live.
function DirectorSettings({ directorId, reachable }: { directorId: string; reachable: boolean }) {
  const [text, setText] = useState<string>("");
  // The last-loaded (or last-saved) text: the baseline the current text is compared against for dirty
  // tracking (issue #1255). Save is disabled while the text matches it, and Reload asks before
  // discarding when the text differs.
  const [baseline, setBaseline] = useState<string>("");
  const [loaded, setLoaded] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirmReload, setConfirmReload] = useState(false);

  const dirty = loaded && isSettingsDirty(text, baseline);

  const load = useCallback(async () => {
    setBusy(true);
    setError(null);
    setStatus(null);
    try {
      const raw = await getDirectorSettings(directorId);
      // Pretty-print for editing; keep the raw text if it is not valid JSON so nothing is lost. The
      // same value becomes the dirty-tracking baseline, so a freshly-loaded editor reads as clean.
      const pretty = prettyPrintSettings(raw);
      setText(pretty);
      setBaseline(pretty);
      setLoaded(true);
      setStatus("Loaded");
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  }, [directorId]);

  const save = async () => {
    setError(null);
    setStatus(null);
    // Validate the edit before sending it (fail loud on a malformed body, never write it).
    let normalized: string;
    try {
      normalized = JSON.stringify(JSON.parse(text));
    } catch {
      setError("Settings are not valid JSON - fix the text before saving.");
      return;
    }
    setBusy(true);
    try {
      await putDirectorSettings(directorId, normalized);
      // The saved text is now the clean baseline, so the editor goes clean and Save disables again.
      setBaseline(text);
      setStatus("Saved - the Director re-applied it live.");
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  // Reload replaces the current text with the Director's stored settings. When there are unsaved edits,
  // route it through the shared confirmation so a stray Reload can never silently drop them (#1255).
  const requestReload = () => {
    if (dirty) {
      setConfirmReload(true);
    } else {
      void load();
    }
  };

  return (
    <section className="ddet-sec">
      <div className="ddet-sec-head">
        <h2>Settings</h2>
        <span className="ddet-sec-meta">GET/PUT /directors/{"{id}"}/settings</span>
      </div>
      {!loaded ? (
        <div className="ddet-settings-load">
          <p className="ddet-quiet">The Director's raw settings JSON, read and written by Director id through the Gateway.</p>
          <button type="button" className="ddet-btn" onClick={() => void load()} disabled={busy || !reachable}>
            {busy ? "Loading..." : "Load settings"}
          </button>
          {/* Covers both reasons the tunnel can be gone - shut down, or unreachable - because the
              consequence here is identical and the page has already said which one it is. */}
          {!reachable && <span className="ddet-settings-status">This Director is not answering - settings cannot be read right now.</span>}
        </div>
      ) : (
        <div className="ddet-settings">
          <textarea
            className="ddet-settings-box"
            value={text}
            spellCheck={false}
            onChange={(e) => setText(e.target.value)}
          />
          <div className="ddet-settings-actions">
            <button
              type="button"
              className="ddet-btn ddet-btn-primary"
              onClick={() => void save()}
              disabled={busy || !dirty}
            >
              {busy ? "Saving..." : "Save"}
            </button>
            <button type="button" className="ddet-btn" onClick={requestReload} disabled={busy}>Reload</button>
            {dirty && <span className="ddet-settings-dirty">Unsaved changes</span>}
            {status !== null && !dirty && <span className="ddet-settings-status">{status}</span>}
          </div>
        </div>
      )}
      {error !== null && <div className="ddet-settings-error">{error}</div>}
      <ConfirmDialog
        open={confirmReload}
        title="Discard unsaved changes?"
        message="Reloading replaces your edits with the Director's stored settings. This cannot be undone."
        confirmLabel="Discard and reload"
        cancelLabel="Keep editing"
        onConfirm={() => void load()}
        onClose={() => setConfirmReload(false)}
      />
    </section>
  );
}
