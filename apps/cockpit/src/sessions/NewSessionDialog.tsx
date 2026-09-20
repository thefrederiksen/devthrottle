import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  addRepo,
  createSession,
  getAgents,
  getDirectors,
  getKnownRepositories,
  gatewayErrorMessage,
  type AgentChoice,
  type DirectorInfo,
  type KnownRepoInfo,
  type RepoAddResult,
  type RepoInfo,
} from "@devthrottle/client-core/api/client";
import { directorPort } from "@devthrottle/client-core/fleet/directorEndpoint";
import { durationLabel, useNow } from "@devthrottle/client-core/sessions/waiting";
import { useDismissOnBackdrop } from "../components";

// The desktop Cockpit "New session" dialog (issue #1023, QA sweep epic #967). The React Cockpit had
// no way to start a session; this is the dedicated picker dialog the roster rail's "+ New session"
// control opens (the "selection via a dialog, not inline" convention). It reuses the same client-core
// contract the mobile NewSession flow uses - getDirectors / getKnownRepositories / createSession - so
// both shells create sessions through the one Gateway front door (POST /directors/{id}/sessions).
//
// The flow mirrors the desktop New Session tab:
//   1. REPOSITORY, on a machine. The machine row at the top of the repository area says WHICH machine
//      these repositories are on and lets you change it - the Director's own tab has no such row
//      because it IS the machine. Beneath it is the Director's tab, structure for structure: a search
//      box that filters by name OR path, a Name / Path / Last Used table with sortable headings, and a
//      path box for a repository the list does not hold.
//      The list comes from GET /directors/{id}/known-repositories: the ONE repository list, already in
//      the ONE order the Gateway decided (the one-repository-list mission, phase 4) - most recently
//      used first, with the repositories a Director found under a registered root folder and nobody
//      has ever opened beneath them. This screen used to read GET /directors/{id}/repos, which is the
//      Director's own registry - HALF of that list, and the half that empties out on a machine where
//      nobody uses the desktop dialog's Start button. The one list is also durable and machine-keyed,
//      so it survives that machine's Director being disconnected, which is exactly when this screen
//      still needs something to show.
//      THE ORDER IS NOT THIS SCREEN'S TO DECIDE - see orderRepositories below.
//   2. LAUNCH OPTIONS: pick the AGENT from GET /directors/{id}/agents (that machine's configured,
//      enabled agents), and choose the permission mode. There is deliberately NO model picker - the
//      model comes from the chosen agent's own configured default, exactly like the desktop dialog
//      (issue #1497). The client sends only the agent kind and the Bypass-permissions choice; the
//      Director applies that agent's configured default model and permission preset.
// Clicking a repository SELECTS it; the footer's "Create session" button is what starts a session.
// On success the parent is told the new session id so it can refresh the roster and open it.
//
// TWO THINGS THE DESKTOP TAB HAS THAT ARE DELIBERATELY NOT DRAWN HERE:
//   * BROWSE. The desktop tab's Browse button opens a folder picker on the machine it runs on. The
//     Cockpit runs in a browser and is NEVER the machine the path describes, so a Browse button here
//     would offer the VIEWER's file system for a path that has to exist on the Director's - this
//     mission's recurring path-comparison defect wearing a button. The path box and Add stand in for it.
//   * PER-ROW REMOVE. The desktop tab has a per-row X. DELETE /directors/{id}/repos removes the row
//     from the DIRECTOR'S REGISTRY, but this screen reads the GATEWAY CATALOGUE, which has no delete
//     at all - so the row would stay exactly where it was. A button that visibly does nothing is the
//     Voice-screen failure Critical Rule 7 exists to stop, so it is not drawn until the provenance
//     model that can honestly answer for it exists.

export interface NewSessionDialogProps {
  /** Close the dialog without creating (Cancel / backdrop / Escape). */
  onClose: () => void;
  /** A session was created; the parent refreshes the roster and opens it. */
  onCreated: (sessionId: string) => void;
  /**
   * The Director to pre-select (issue: Fleet Map "new session" button). When set and that Director is
   * present in the loaded list, the machine row opens on it instead of the newest-started Director, so
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

/** The three sortable headings, in the desktop tab's own column order and with its own labels. */
export type RepoSortColumn = "Name" | "Path" | "LastUsed";

const REPO_COLUMNS: Array<{ key: RepoSortColumn; label: string }> = [
  { key: "Name", label: "Name" },
  { key: "Path", label: "Path" },
  { key: "LastUsed", label: "Last Used" },
];

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

/**
 * The "Last Used" cell's wording, and it is the DESKTOP TAB'S LADDER, thresholds copied from
 * RepositoryConfig.FormatTimeAgo in src/CcDirector.Core/Configuration/RepositoryConfig.cs:
 * "just now", then "5m ago", "3h ago", "2d ago", "4mo ago", "1y ago".
 *
 * It is deliberately NOT client-core's durationLabel. That ladder words the same span differently
 * below a minute and prints "2d 3h" where this one prints "2d ago", so reaching for it would make one
 * screen say a repository was used at a time another screen words another way - a silent divergence,
 * in the mission whose whole point is that two screens stop disagreeing about one list.
 *
 * A span that has not reached a minute - including a negative one, from a clock that disagrees with
 * the Gateway's - reads "just now", exactly as the C# does.
 */
export function lastUsedAgo(lastUsedIso: string, nowMs: number): string {
  const trimmed = (lastUsedIso ?? "").trim();
  if (trimmed.length === 0) return "";
  const then = Date.parse(trimmed);
  if (Number.isNaN(then)) return "";

  const minutes = (nowMs - then) / 60000;
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${Math.floor(minutes)}m ago`;
  const hours = minutes / 60;
  if (hours < 24) return `${Math.floor(hours)}h ago`;
  const days = hours / 24;
  if (days < 30) return `${Math.floor(days)}d ago`;
  if (days < 365) return `${Math.floor(days / 30)}mo ago`;
  return `${Math.floor(days / 365)}y ago`;
}

/**
 * What the "Last Used" cell says for one row.
 *
 * THE VERDICT IS READ, NEVER INFERRED. `neverOpened` is the Gateway's own stamp - it decided this
 * repository was found under a registered root folder and has never been opened, and it decided where
 * the row therefore belongs. This screen must not write `lastUsed ? ... : ...` and conclude the same
 * thing for itself: a client that infers renders something plausible the first time it meets a row it
 * did not expect, which is the Voice-screen failure Critical Rule 7 exists to stop.
 *
 * The desktop tab shows an EMPTY cell for such a repository. The Cockpit says "Never opened", which is
 * the Tech Lead's decision and is deliberate: the Gateway now stamps that verdict, and an empty cell is
 * the client deciding an absence is not worth saying - the habit this mission exists to end. The
 * desktop cell is empty only because it has no such field yet; phase 6 gives it one.
 */
export function lastUsedCell(repository: KnownRepoInfo, nowMs: number): string {
  if (repository.neverOpened) return "Never opened";
  return lastUsedAgo(repository.lastUsed, nowMs);
}

// Ordinal, case-insensitive string comparison - the desktop tab sorts Name and Path with
// StringComparer.OrdinalIgnoreCase, and this is that comparison.
function compareIgnoringCase(a: string, b: string): number {
  const left = a.toLowerCase();
  const right = b.toLowerCase();
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}

/**
 * The rows to draw, for the heading the user has chosen.
 *
 * THIS SCREEN NEVER READS lastUsed TO DECIDE ORDER. Not once, anywhere. The Gateway already ruled -
 * most recently used first, never-opened beneath, then by name and by path - and Critical Rule 7 says
 * the client renders that ruling rather than re-deriving it. So:
 *
 *   * "Last Used" DESCENDING, which is how this list opens, is THE SERVED ARRAY ITSELF, returned
 *     index for index with no sort call at all. The heading reads as the active column because it
 *     LABELS the Gateway's ruling, not because anything here computed it.
 *   * Its ascending flip is THAT ARRAY REVERSED - a pure reversal, with no key and no null policy.
 *   * Name and Path are string comparisons on those fields, which is the user asking for something
 *     else and is allowed: that is the user's request, not the client deciding what the list MEANS.
 *
 * So no date parsing and no nulls-last rule ever reaches this file. That matters more than it looks:
 * phase 2 of this mission proved PostgreSQL and C# disagree about where a null sorts, and a null
 * policy here would be a third opinion on one list.
 *
 * Name ties break on path so the result is a total order; without that, reversing a list with ties
 * would not be the exact inverse of ordering it.
 */
export function orderRepositories(
  served: KnownRepoInfo[],
  column: RepoSortColumn,
  ascending: boolean,
): KnownRepoInfo[] {
  if (column === "LastUsed") {
    return ascending ? [...served].reverse() : served;
  }
  const ordered = [...served].sort((a, b) =>
    column === "Name"
      ? compareIgnoringCase(a.name, b.name) || compareIgnoringCase(a.path, b.path)
      : compareIgnoringCase(a.path, b.path),
  );
  return ascending ? ordered : ordered.reverse();
}

/**
 * What a click on a heading does, copied from the desktop tab's RepoHeader_Click: clicking the column
 * that is already active flips its direction, and clicking a different one starts it ascending -
 * except Last Used, which starts descending, because descending is the Gateway's own order.
 */
export function nextRepoSort(
  column: RepoSortColumn,
  ascending: boolean,
  clicked: RepoSortColumn,
): { column: RepoSortColumn; ascending: boolean } {
  if (column === clicked) return { column: clicked, ascending: !ascending };
  return { column: clicked, ascending: clicked !== "LastUsed" };
}

// The line above the search box: what this list IS. It names the order it is currently in, so it stays
// true when the user sorts by a heading - a line that said "most recently used first" over a list
// sorted by name would be the screen telling the user something it can see is not so.
function listSummary(count: number, column: RepoSortColumn, ascending: boolean): string {
  const noun = count === 1 ? "1 repository" : `${count} repositories`;
  if (column === "LastUsed") {
    return ascending
      ? `${noun} on this machine, least recently used first.`
      : `${noun} on this machine, most recently used first.`;
  }
  const by = column === "Name" ? "by name" : "by path";
  return ascending ? `${noun} on this machine, ${by}.` : `${noun} on this machine, ${by}, reversed.`;
}

/**
 * What to say once the Add button has done its work. TWO FACTS, AND NOT A THIRD: what the route did,
 * and what the list looked like when this screen went and read it again. No prediction of any kind.
 *
 * That shape is deliberate and it is the Tech Lead's ruling. POST /directors/{id}/repos writes a
 * DIRECTOR'S OWN REGISTRY, and this list is the GATEWAY'S CATALOGUE - two different stores, which is
 * why an added path can be genuinely absent from the list a moment later. The feed that will join them
 * is being built by another seat as this is written, so any sentence here that explained the gap by its
 * mechanism ("it joins the list once a session has run in it") would be TRUE TODAY AND WRONG NEXT WEEK.
 * Reporting only the act and the observed list is true in both worlds: when the feed lands, the re-read
 * simply finds the row and this says so, with nothing here to rewrite.
 */
export function addOutcome(result: RepoAddResult, listedNow: boolean): string {
  const name = result.name.trim() || result.path.trim();
  // The route answers 201 for a path it newly registered and 200 for one the machine already had. Two
  // different true outcomes, and the screen must not report them as one.
  const act = result.added
    ? `Added ${name} to this machine.`
    : `${name} was already on this machine.`;
  const whereItStands = listedNow
    ? "It is in the list above."
    : "The list above is the one the Gateway serves, and it does not show this path.";
  return `${act} ${whereItStands}`;
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
  const [reposError, setReposError] = useState<string | null>(null);

  // The repository table's own controls: the search box's text, and which heading is active.
  const [repoFilter, setRepoFilter] = useState("");
  const [sortColumn, setSortColumn] = useState<RepoSortColumn>("LastUsed");
  const [sortAscending, setSortAscending] = useState(false);

  // The selected machine's configured agents (issue #1497), loaded like the repos when the machine
  // changes. `agentsStatus` carries the loading / empty / error line for the agent picker.
  const [agents, setAgents] = useState<AgentChoice[] | null>(null);
  const [agentsStatus, setAgentsStatus] = useState<string | null>(null);
  const [selectedAgentType, setSelectedAgentType] = useState<string | null>(null);

  // The path box is the one place a repository path lives, exactly as the desktop tab's PathInput is:
  // selecting a row writes that row's path here, and a row reads as selected while it matches. One
  // piece of state cannot disagree with itself about which repository is about to be used.
  const [manualPath, setManualPath] = useState("");
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [addNote, setAddNote] = useState<string | null>(null);
  const pathInputRef = useRef<HTMLInputElement | null>(null);

  // Permission mode, pre-selected from the last-used choice (default: Skip, matching the desktop
  // Bypass-permissions checkbox default of ON).
  const [permissionKey, setPermissionKey] = useState(() => {
    const saved = loadStored(PERMISSION_STORAGE_KEY);
    return saved && PERMISSION_CHOICES.some((c) => c.key === saved) ? saved : PERMISSION_CHOICES[0].key;
  });
  const permission = PERMISSION_CHOICES.find((c) => c.key === permissionKey) ?? PERMISSION_CHOICES[0];

  const selectedAgent = agents?.find((a) => a.type === selectedAgentType) ?? null;

  // A slow tick (30s) so each machine's "up <uptime>" fact and each repository's "Last Used" cell stay
  // roughly current while the dialog is open, without a per-second re-render this picker does not need.
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
    setReposError(null);
    setAddNote(null);
    getKnownRepositories(selectedId, controller.signal)
      .then((list) => {
        if (reqId !== reposReqRef.current) return; // a newer selection superseded this one
        // Stored exactly as it arrived. No sort, no filter, no re-ordering: the Gateway decided this
        // order and this screen renders it.
        setRepos(list);
      })
      .catch((err) => {
        if (controller.signal.aborted || reqId !== reposReqRef.current) return;
        setRepos([]);
        setReposError(gatewayErrorMessage(err));
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

  // The rows on the page: the chosen heading's order, then the search box's filter over name OR path -
  // the desktop tab's ApplyRepoFilter, which searches both fields and is case-insensitive.
  const visibleRepos = useMemo(() => {
    const ordered = orderRepositories(repos ?? [], sortColumn, sortAscending);
    const needle = repoFilter.trim().toLowerCase();
    if (needle.length === 0) return ordered;
    return ordered.filter(
      (r) => r.name.toLowerCase().includes(needle) || r.path.toLowerCase().includes(needle),
    );
  }, [repos, sortColumn, sortAscending, repoFilter]);

  // The one line describing the list: loading, empty, failed, or what it holds and the order it is in.
  // It lives in exactly one place so the screen cannot say two things about one list.
  const reposStatus = useMemo(() => {
    if (!selectedId) return "Pick a machine first.";
    if (reposError !== null) return `Could not load repositories: ${reposError}`;
    if (repos === null) return "Loading repositories...";
    if (repos.length === 0) return "No repositories known on this machine. Enter a path below.";
    return listSummary(repos.length, sortColumn, sortAscending);
  }, [selectedId, reposError, repos, sortColumn, sortAscending]);

  const selectedPath = manualPath.trim();

  // Clicking a repository SELECTS it. It does not start a session - the footer's "Create session"
  // button does that, and this is the defect this phase was told to fix: the row used to be a button
  // whose click created a session immediately, so a mis-aimed click launched an agent.
  const selectRepository = useCallback((path: string) => {
    setManualPath(path);
    setCreateError(null);
    setAddNote(null);
  }, []);

  const create = useCallback(
    async (repoPath: string) => {
      if (creating) return; // a create is already in flight (guards a double-click)
      if (!selectedId) {
        setCreateError("Pick a machine first.");
        return;
      }
      const path = repoPath.trim();
      if (!path) {
        setCreateError("Select a repository above, or enter a path below.");
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

  // Register the path in the box on the selected machine, then RE-READ the one list and say what
  // actually happened - see addOutcome. This stands where the desktop tab puts Browse.
  const addRepository = useCallback(async () => {
    if (adding || creating) return;
    if (!selectedId) {
      setCreateError("Pick a machine first.");
      return;
    }
    const path = manualPath.trim();
    if (path.length === 0) {
      setCreateError("Enter the path of a repository on this machine, then add it.");
      pathInputRef.current?.focus();
      return;
    }
    setAdding(true);
    setCreateError(null);
    setAddNote(null);
    const directorId = selectedId;
    try {
      const result = await addRepo(directorId, path);
      const reqId = ++reposReqRef.current;
      const list = await getKnownRepositories(directorId);
      if (reqId !== reposReqRef.current) return; // the machine changed under us
      setRepos(list);
      setReposError(null);
      const wanted = (result.path.trim() || path).toLowerCase();
      setAddNote(addOutcome(result, list.some((r) => r.path.trim().toLowerCase() === wanted)));
    } catch (err) {
      setCreateError(gatewayErrorMessage(err));
    } finally {
      setAdding(false);
    }
  }, [adding, creating, selectedId, manualPath]);

  // Backdrop dismissal that a mouse drag cannot trigger: highlighting the repository path with the
  // mouse and releasing outside the panel must not throw the half-filled dialog away (see
  // useDismissOnBackdrop).
  const dismiss = useDismissOnBackdrop(onClose);

  // The first-run empty state belongs to an empty CATALOGUE, never to a filter that matched nothing -
  // the desktop tab draws the same distinction - and never to a read that failed, which would tell the
  // owner his machine holds nothing when all that is known is that the Gateway could not be asked.
  const showEmptyState = reposError === null && repos !== null && repos.length === 0;
  const showTable = reposError === null && repos !== null && repos.length > 0;

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
          {/* Step 1: the repository, and the machine it is on */}
          <div className="newsess-step">
            <div className="newsess-step-label">1. Repository</div>

            {/* The machine row. The desktop tab has no machine picker because it IS the machine; the
                Cockpit has to say which machine these repositories are on and let you change it. Each
                chip carries what the old tall buttons carried - the display name, the Control API
                port, how long that Director has been up, and its version - on one line. */}
            <div className="newsess-machinerow">
              <span className="newsess-machinerow-label" id="newsess-machine-label">
                Machine
              </span>
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
                <ul className="newsess-machines" aria-labelledby="newsess-machine-label">
                  {directors.map((d) => {
                    const port = directorPort(d.controlEndpoint);
                    const uptime = durationLabel(d.startedAt, now);
                    const version = d.version.trim();
                    // The facts that tell two same-named Directors apart: how long each has been up,
                    // and its version. Either part is omitted when absent, never faked.
                    const facts = [uptime ? `up ${uptime}` : "", version ? `v${version}` : ""]
                      .filter((part) => part.length > 0)
                      .join("   ");
                    const chosen = d.directorId === selectedId;
                    return (
                      <li key={d.directorId}>
                        <button
                          type="button"
                          className={`newsess-machine${chosen ? " sel" : ""}`}
                          aria-pressed={chosen}
                          onClick={() => setSelectedId(d.directorId)}
                        >
                          <span className="newsess-machine-name">{directorLabel(d)}</span>
                          {port && <span className="newsess-machine-port">:{port}</span>}
                          <span className="newsess-machine-meta">{facts}</span>
                          {chosen && (
                            <span className="newsess-machine-check" aria-hidden="true">
                              selected
                            </span>
                          )}
                        </button>
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>

            <div className="newsess-status">{reposStatus}</div>

            {/* The search box. The desktop tab gets away with a tooltip because it is a desktop
                application; a tooltip is not discoverable on the web, so the placeholder says it. */}
            <input
              id="newsess-repo-filter"
              className="newsess-input newsess-search"
              type="text"
              autoComplete="off"
              spellCheck={false}
              placeholder="Filter repositories by name or path"
              aria-label="Filter repositories by name or path"
              value={repoFilter}
              onChange={(e) => setRepoFilter(e.target.value)}
            />

            <div className="newsess-repotable">
              <div className="newsess-repohead">
                {REPO_COLUMNS.map((c) => {
                  const active = c.key === sortColumn;
                  return (
                    <button
                      key={c.key}
                      type="button"
                      className={`newsess-repohead-btn${active ? " active" : ""}`}
                      aria-pressed={active}
                      onClick={() => {
                        const next = nextRepoSort(sortColumn, sortAscending, c.key);
                        setSortColumn(next.column);
                        setSortAscending(next.ascending);
                      }}
                    >
                      {c.label}
                      {active ? (sortAscending ? "  ^" : "  v") : ""}
                    </button>
                  );
                })}
                {/* The desktop tab's fourth column is its per-row remove; here it is the selection
                    marker, so the heading row has nothing to put above it. */}
                <span className="newsess-repohead-end" aria-hidden="true" />
              </div>

              {showEmptyState && (
                <div className="newsess-empty">
                  <div className="newsess-empty-title">No repositories yet</div>
                  <p className="newsess-empty-body">
                    A session runs your coding agent inside one repository. Enter the path of the folder
                    that holds your code on this machine, and add it to get started.
                  </p>
                  <button
                    type="button"
                    className="newsess-btn primary"
                    disabled={adding || creating || !selectedId}
                    onClick={() => void addRepository()}
                  >
                    {adding ? "Adding..." : "Add a repository to this machine"}
                  </button>
                  <p className="newsess-empty-hint">
                    Repositories you have used appear in this list so next time it is one click.
                  </p>
                </div>
              )}

              {showTable && (
                <ul className="newsess-list">
                  {visibleRepos.map((r) => {
                    const chosen = r.path === selectedPath;
                    return (
                      <li key={r.path}>
                        <button
                          type="button"
                          className={`newsess-pick${chosen ? " sel" : ""}`}
                          aria-pressed={chosen}
                          disabled={creating}
                          onClick={() => selectRepository(r.path)}
                        >
                          <span className="newsess-pick-name">{repoLabel(r)}</span>
                          <span className="newsess-pick-path">{r.path}</span>
                          <span className="newsess-pick-when">{lastUsedCell(r, now)}</span>
                          <span className="newsess-pick-check">{chosen ? "selected" : ""}</span>
                        </button>
                      </li>
                    );
                  })}
                </ul>
              )}

              {showTable && visibleRepos.length === 0 && (
                <div className="newsess-nomatch">No repository here matches that.</div>
              )}
            </div>

            <label className="newsess-manual-label" htmlFor="newsess-path">
              Or enter a path
            </label>
            <div className="newsess-manual">
              <input
                id="newsess-path"
                ref={pathInputRef}
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
              {/* Where the desktop tab puts Browse. It does not browse: it registers the typed path on
                  the selected machine, which is a thing a browser CAN honestly do for a disk it is not
                  sitting on. */}
              <button
                type="button"
                className="newsess-btn"
                disabled={adding || creating || !selectedId}
                onClick={() => void addRepository()}
              >
                {adding ? "Adding..." : "Add to this machine"}
              </button>
            </div>
            {addNote !== null && (
              <div className="newsess-status" aria-live="polite">
                {addNote}
              </div>
            )}
          </div>

          {/* Step 2: launch options (agent and permission mode), remembered across uses. No model
              picker - the model comes from the chosen agent's own configured default (issue #1497). */}
          <div className="newsess-step">
            <div className="newsess-step-label">2. Launch options</div>

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
