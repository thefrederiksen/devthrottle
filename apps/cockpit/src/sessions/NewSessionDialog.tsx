import { useCallback, useEffect, useRef, useState } from "react";
import {
  createSession,
  getAgents,
  getDirectors,
  getKnownRepositories,
  gatewayErrorMessage,
  type AgentChoice,
  type DirectorInfo,
  type KnownRepoInfo,
  type RepoInfo,
} from "@devthrottle/client-core/api/client";
import { directorPort } from "@devthrottle/client-core/fleet/directorEndpoint";
import { durationLabel, useNow } from "@devthrottle/client-core/sessions/waiting";
import { useDismissOnBackdrop } from "../components";

// The desktop Cockpit "New session" dialog (issue #1023, QA sweep epic #967). The React Cockpit had
// no way to start a session; this is the dedicated picker dialog the roster rail's "+ New session"
// control opens (the "selection via a dialog, not inline" convention). It reuses the same client-core
// contract the mobile NewSession flow uses - getDirectors / getRepos / createSession - so both shells
// create sessions through the one Gateway front door (POST /directors/{id}/sessions).
//
// The flow mirrors the desktop New Session dialog:
//   1. Pick a MACHINE from GET /directors (default-select the most-recently-seen so repos+agents load
//      with one fewer click).
//   2. Pick a REPOSITORY from GET /directors/{id}/known-repositories OR type a path. That route is the
//      ONE repository list, already in the ONE order the Gateway decided (the one-repository-list
//      mission, phase 4): most recently used first, with the repositories a Director found under a
//      registered root folder and nobody has ever opened beneath them. This screen used to read
//      GET /directors/{id}/repos, which is the Director's own registry - HALF of that list, and the
//      half that empties out on a machine where nobody uses the desktop dialog's Start button. The one
//      list is also durable and machine-keyed, so it survives that machine's Director being
//      disconnected, which is exactly when this screen still needs something to show.
//      THE ORDER IS NOT THIS SCREEN'S TO DECIDE. It renders the rows in the order they arrived and
//      never compares lastUsed to work out where one belongs - Critical Rule 7 in CLAUDE.md, applied
//      to a list instead of a verdict.
//   3. LAUNCH OPTIONS: pick the AGENT from GET /directors/{id}/agents (that machine's configured,
//      enabled agents), and choose the permission mode. There is deliberately NO model picker - the
//      model comes from the chosen agent's own configured default, exactly like the desktop dialog
//      (issue #1497). The client sends only the agent kind and the Bypass-permissions choice; the
//      Director applies that agent's configured default model and permission preset.
// On success the parent is told the new session id so it can refresh the roster and open it.

export interface NewSessionDialogProps {
  /** Close the dialog without creating (Cancel / backdrop / Escape). */
  onClose: () => void;
  /** A session was created; the parent refreshes the roster and opens it. */
  onCreated: (sessionId: string) => void;
  /**
   * The Director to pre-select (issue: Fleet Map "new session" button). When set and that Director is
   * present in the loaded list, machine step 1 opens on it instead of the newest-started Director, so
   * the Fleet Map card the owner clicked is already the target. When absent, or the id is not in the
   * list, the dialog falls back to the newest-started Director exactly as before.
   */
  initialDirectorId?: string;
}

// Permission choices, mapped to the desktop dialog's "Bypass permission prompts" checkbox. "Skip
// permission prompts" is the desktop default (the checkbox defaults to ON), so it is first / pre-selected.
// Neither adds a command-line argument on the client: the choice is sent as a structured flag and the
// Director resolves the agent's configured launch line accordingly, so the model is unaffected either way.
interface PermissionChoice {
  key: string;
  label: string;
  bypass: boolean;
}
const PERMISSION_CHOICES: PermissionChoice[] = [
  { key: "skip", label: "Skip permission prompts", bypass: true },
  { key: "ask", label: "Ask for permissions", bypass: false },
];

const AGENT_STORAGE_KEY = "cockpit.newSession.agent";
const PERMISSION_STORAGE_KEY = "cockpit.newSession.permission";

// Read a remembered string from localStorage, or null when unavailable/absent (private mode is fine).
function loadStored(storageKey: string): string | null {
  try {
    return window.localStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

// Save a chosen value so the next open pre-selects it. Best-effort (private mode may reject it).
function saveStored(storageKey: string, value: string): void {
  try {
    window.localStorage.setItem(storageKey, value);
  } catch {
    /* localStorage can be unavailable (private mode); remembering the choice is best-effort */
  }
}

function directorLabel(d: DirectorInfo): string {
  // devthrottle_internal#1176: the user-editable display name wins when the Director reports one - it
  // is the label that tells several Directors on one machine apart by something a human chose.
  if (d.displayName.trim()) return d.displayName.trim();
  if (d.machineName.trim()) return d.machineName.trim();
  return d.directorId || "director";
}

// The Director to default-select: the NEWEST-started one (the freshest cc-director launch), which is
// also how the newest slot wins when a machine runs several. startedAt is ISO 8601 UTC, so a lexical
// max is chronological; a Director with no startedAt sorts oldest. Falls back to the first entry when
// nothing carries a startedAt.
function newestDirector(list: DirectorInfo[]): DirectorInfo | null {
  if (list.length === 0) return null;
  let best = list[0];
  for (const d of list) {
    if ((d.startedAt || "").localeCompare(best.startedAt || "") > 0) best = d;
  }
  return best;
}

function repoLabel(r: RepoInfo): string {
  if (r.name.trim()) return r.name.trim();
  const parts = r.path.replace(/[\\/]+$/, "").split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : r.path;
}

// The one-line summary of what will launch, e.g. "Claude Code . Opus 4.8 . skips permission prompts".
// Mirrors the desktop dialog telling you the agent and the model it will use (issue #1497).
function launchSummary(agent: AgentChoice | null, bypass: boolean): string {
  if (agent === null) return "Pick an agent above.";
  const model = agent.modelLabel.trim() || "its default model";
  const permission = bypass ? "skips permission prompts" : "asks for permissions";
  return `${agent.displayName} . ${model} . ${permission}`;
}

export function NewSessionDialog({ onClose, onCreated, initialDirectorId }: NewSessionDialogProps) {
  const [directors, setDirectors] = useState<DirectorInfo[] | null>(null);
  const [directorsError, setDirectorsError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const [repos, setRepos] = useState<KnownRepoInfo[] | null>(null);
  const [reposStatus, setReposStatus] = useState("Pick a machine first.");

  // The selected machine's configured agents (issue #1497), loaded like the repos when the machine
  // changes. `agentsStatus` carries the loading / empty / error line for the agent picker.
  const [agents, setAgents] = useState<AgentChoice[] | null>(null);
  const [agentsStatus, setAgentsStatus] = useState<string | null>(null);
  const [selectedAgentType, setSelectedAgentType] = useState<string | null>(null);

  const [manualPath, setManualPath] = useState("");
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);

  // Permission mode, pre-selected from the last-used choice (default: Skip, matching the desktop
  // Bypass-permissions checkbox default of ON).
  const [permissionKey, setPermissionKey] = useState(() => {
    const saved = loadStored(PERMISSION_STORAGE_KEY);
    return saved && PERMISSION_CHOICES.some((c) => c.key === saved) ? saved : PERMISSION_CHOICES[0].key;
  });
  const permission = PERMISSION_CHOICES.find((c) => c.key === permissionKey) ?? PERMISSION_CHOICES[0];

  const selectedAgent = agents?.find((a) => a.type === selectedAgentType) ?? null;

  // A slow tick (30s) so each machine's "up <uptime>" subtitle stays roughly current while the dialog
  // is open, without a per-second re-render this picker does not need.
  const now = useNow(30000);

  // Guards against a stale repo/agent response landing after the user switched machines.
  const reposReqRef = useRef(0);
  const agentsReqRef = useRef(0);

  // Step 1: load the machines once, default-selecting the NEWEST-started Director so the repos and
  // agents load immediately (desktop New Session parity) and, when a machine runs several cc-director
  // slots, the freshest one is picked.
  useEffect(() => {
    const controller = new AbortController();
    getDirectors(controller.signal)
      .then((list) => {
        setDirectors(list);
        setDirectorsError(null);
        // Pre-select the Director the caller asked for (the Fleet Map card that was clicked), but only
        // when it is actually in the list; otherwise fall back to the newest-started Director.
        const requested = (initialDirectorId ?? "").trim();
        const preselected =
          requested.length > 0 ? list.find((d) => d.directorId === requested) ?? null : null;
        const pick = preselected ?? newestDirector(list);
        if (pick) setSelectedId(pick.directorId);
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        setDirectorsError(gatewayErrorMessage(err));
      });
    return () => controller.abort();
  }, [initialDirectorId]);

  // Step 2: whenever the selected machine changes, load THAT machine's one repository list.
  useEffect(() => {
    if (!selectedId) return;
    const controller = new AbortController();
    const reqId = ++reposReqRef.current;
    setRepos(null);
    setReposStatus("Loading repositories...");
    getKnownRepositories(selectedId, controller.signal)
      .then((list) => {
        if (reqId !== reposReqRef.current) return; // a newer selection superseded this one
        // Stored exactly as it arrived. No sort, no filter, no re-ordering: the Gateway decided this
        // order and this screen renders it.
        setRepos(list);
        // The status line describes THE LIST, which is what this read changed. It deliberately says
        // nothing about what clicking a row does - that is not this change's to promise.
        setReposStatus(
          list.length === 0
            ? "No repositories known on this machine. Enter a path below."
            : `${list.length === 1 ? "1 repository" : `${list.length} repositories`} on this machine, ` +
              "most recently used first.",
        );
      })
      .catch((err) => {
        if (controller.signal.aborted || reqId !== reposReqRef.current) return;
        setRepos([]);
        setReposStatus(`Could not load repositories: ${gatewayErrorMessage(err)}`);
      });
    return () => controller.abort();
  }, [selectedId]);

  // Step 3: whenever the selected machine changes, load THAT machine's configured agents (issue #1497).
  // Default-select the last-used agent when it is still offered, else the first - mirroring the desktop
  // dialog, which pre-selects the first enabled agent.
  useEffect(() => {
    if (!selectedId) return;
    const controller = new AbortController();
    const reqId = ++agentsReqRef.current;
    setAgents(null);
    setSelectedAgentType(null);
    setAgentsStatus("Loading agents...");
    getAgents(selectedId, controller.signal)
      .then((list) => {
        if (reqId !== agentsReqRef.current) return; // a newer selection superseded this one
        setAgents(list);
        if (list.length === 0) {
          setAgentsStatus("No agents configured on this machine.");
          return;
        }
        setAgentsStatus(null);
        const remembered = loadStored(AGENT_STORAGE_KEY);
        const pick = list.find((a) => a.type === remembered) ?? list[0];
        setSelectedAgentType(pick.type);
      })
      .catch((err) => {
        if (controller.signal.aborted || reqId !== agentsReqRef.current) return;
        setAgents([]);
        setAgentsStatus(`Could not load agents: ${gatewayErrorMessage(err)}`);
      });
    return () => controller.abort();
  }, [selectedId]);

  // Close on Escape, matching the desktop dialog convention.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const create = useCallback(
    async (repoPath: string) => {
      if (creating) return; // a create is already in flight (guards a double-click)
      if (!selectedId) {
        setCreateError("Pick a machine first.");
        return;
      }
      const path = repoPath.trim();
      if (!path) {
        setCreateError("Enter a repo path, or click a recent repo above.");
        return;
      }
      // The agent is required when the machine offers a list; fall back to Claude Code only when the
      // list could not be loaded, so the dialog still works if the agents read failed.
      if (agents !== null && agents.length > 0 && !selectedAgentType) {
        setCreateError("Pick an agent below.");
        return;
      }
      const agentType = selectedAgentType ?? "ClaudeCode";
      setCreating(true);
      setCreateError(null);
      // Remember the choices so the next open pre-selects them.
      saveStored(AGENT_STORAGE_KEY, agentType);
      saveStored(PERMISSION_STORAGE_KEY, permissionKey);
      try {
        const session = await createSession(selectedId, path, {
          agent: agentType,
          bypassPermissions: permission.bypass,
          signal: undefined,
        });
        const sid = session.sessionId;
        if (!sid) throw new Error("The created session had no id.");
        onCreated(sid);
      } catch (err) {
        // Surface the Gateway's message (a bad repo path or an unreachable Director) inline, never a
        // raw thrown error.
        setCreateError(gatewayErrorMessage(err));
        setCreating(false);
      }
    },
    [creating, selectedId, agents, selectedAgentType, permission.bypass, permissionKey, onCreated],
  );

  // Backdrop dismissal that a mouse drag cannot trigger: highlighting the repository path with the
  // mouse and releasing outside the panel must not throw the half-filled dialog away (see
  // useDismissOnBackdrop).
  const dismiss = useDismissOnBackdrop(onClose);

  return (
    <div className="newsess-backdrop" {...dismiss}>
      <div
        className="newsess-modal"
        role="dialog"
        aria-modal="true"
        aria-label="Start a new session"
      >
        <div className="newsess-head">Start a new session</div>

        <div className="newsess-body">
          {/* Step 1: machine */}
          <div className="newsess-step">
            <div className="newsess-step-label">1. Machine</div>
            {directorsError !== null && (
              <div className="newsess-error" role="alert">
                {directorsError}
              </div>
            )}
            {directors === null && directorsError === null && (
              <div className="newsess-status">Loading machines...</div>
            )}
            {directors !== null && directors.length === 0 && (
              <div className="newsess-status">No machines found on this Gateway.</div>
            )}
            {directors !== null && directors.length > 0 && (
              <ul className="newsess-machines">
                {directors.map((d) => {
                  const port = directorPort(d.controlEndpoint);
                  const uptime = durationLabel(d.startedAt, now);
                  const version = d.version.trim();
                  // The subtitle tells two same-named Directors apart: how long each has been up, and its
                  // version. Either part is omitted when absent, never faked.
                  const subtitle = [uptime ? `up ${uptime}` : "", version ? `v${version}` : ""]
                    .filter((part) => part.length > 0)
                    .join("   ");
                  return (
                    <li key={d.directorId}>
                      <button
                        type="button"
                        className={`newsess-machine${d.directorId === selectedId ? " sel" : ""}`}
                        onClick={() => setSelectedId(d.directorId)}
                      >
                        <span className="newsess-machine-top">
                          <span className="newsess-machine-name">{directorLabel(d)}</span>
                          {port && <span className="newsess-machine-port">:{port}</span>}
                        </span>
                        <span className="newsess-machine-sub">
                          <span className="newsess-machine-meta">{subtitle}</span>
                          {d.directorId === selectedId && (
                            <span className="newsess-machine-check" aria-hidden="true">
                              selected
                            </span>
                          )}
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          {/* Step 2: repository */}
          <div className="newsess-step">
            <div className="newsess-step-label">2. Repository</div>
            <div className="newsess-status">{reposStatus}</div>
            {repos !== null && repos.length > 0 && (
              <ul className="newsess-list">
                {repos.map((r) => (
                  <li key={r.path}>
                    <button
                      type="button"
                      className="newsess-pick"
                      disabled={creating}
                      onClick={() => void create(r.path)}
                    >
                      <span className="newsess-pick-name">{repoLabel(r)}</span>
                      <span className="newsess-pick-path">{r.path}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}

            <label className="newsess-manual-label" htmlFor="newsess-path">
              Or enter a path
            </label>
            <div className="newsess-manual">
              <input
                id="newsess-path"
                className="newsess-input mono"
                type="text"
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
            </div>
          </div>

          {/* Step 3: launch options (agent and permission mode), remembered across uses. No model
              picker - the model comes from the chosen agent's own configured default (issue #1497). */}
          <div className="newsess-step">
            <div className="newsess-step-label">3. Launch options</div>

            <div className="newsess-opt-label" id="newsess-agent-label">
              Agent
            </div>
            {agentsStatus !== null && <div className="newsess-status">{agentsStatus}</div>}
            {agents !== null && agents.length > 0 && (
              <div className="newsess-seg" role="group" aria-labelledby="newsess-agent-label">
                {agents.map((a) => (
                  <button
                    key={a.type}
                    type="button"
                    className={`newsess-seg-btn${a.type === selectedAgentType ? " sel" : ""}`}
                    aria-pressed={a.type === selectedAgentType}
                    disabled={creating}
                    onClick={() => setSelectedAgentType(a.type)}
                  >
                    {a.displayName}
                  </button>
                ))}
              </div>
            )}

            <div className="newsess-opt-label" id="newsess-permission-label">
              Permission mode
            </div>
            <div className="newsess-seg" role="group" aria-labelledby="newsess-permission-label">
              {PERMISSION_CHOICES.map((c) => (
                <button
                  key={c.key}
                  type="button"
                  className={`newsess-seg-btn${c.key === permissionKey ? " sel" : ""}`}
                  aria-pressed={c.key === permissionKey}
                  disabled={creating}
                  onClick={() => setPermissionKey(c.key)}
                >
                  {c.label}
                </button>
              ))}
            </div>

            <div className="newsess-opt-label">This session will start as</div>
            <div className="newsess-args mono" aria-live="polite">
              {launchSummary(selectedAgent, permission.bypass)}
            </div>
          </div>

          {createError !== null && (
            <div className="newsess-error" role="alert">
              {createError}
            </div>
          )}
        </div>

        <div className="newsess-foot">
          <button type="button" className="newsess-btn" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="newsess-btn primary"
            disabled={creating || !selectedId}
            onClick={() => void create(manualPath)}
          >
            {creating ? "Creating..." : "Create session"}
          </button>
        </div>
      </div>
    </div>
  );
}
