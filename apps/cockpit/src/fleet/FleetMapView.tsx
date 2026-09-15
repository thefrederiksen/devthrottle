import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { type SessionDto } from "@devthrottle/client-core/api/client";
import { listMissions, type MissionDto } from "@devthrottle/client-core/missions/missions";
import { dotColor, dotHex, effectiveColor, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import {
  reachabilityFor,
  reachabilityLastSeen,
  REACHABILITY_OFFLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
} from "@devthrottle/client-core/fleet/fleetClient";
import {
  canStartSessionOn,
  directorStateLabel,
  emptySlotTextOf,
  isDataStale,
} from "@devthrottle/client-core/fleet/directorPresentation";
import { useSharedRoster } from "@devthrottle/client-core/fleet/rosterStore";
import { repoBasename, repoIdentity, relativeTime } from "./format";
import {
  agentBadgeText,
  buildControllerTree,
  directorLabelOf,
  directorsByMachine,
  groupByDirector,
  machineKeyOf,
  modelChip,
  modelKeyOf,
  shortDir,
  type DirectorGroup as FormatDirectorGroup,
} from "./fleetMapFormat";
import { MissionsBoard, missionCounts } from "../missions/MissionsBoard";
import { NewSessionDialog } from "../sessions/NewSessionDialog";

// Per-Director reachability for the Online / Wobbly / Offline node rendering (issue #1215), provided at
// the Fleet Map root and read by each NodeCard so the cards dim in place without prop-drilling.
const ReachabilityContext = createContext<DirectorReachability[]>([]);

// The Fleet Map (issue #1109): a live, spatial view of everything running across the tailnet. Where
// the Fleet page (FleetView) is a list of cards grouped by machine, this is a node canvas - a root
// node for the Gateway branching down to grouped "lane" panels of terminal-window session cards,
// joined by SVG elbow connectors, with one status dot per node. The same fleet is re-laid-out on the
// fly several ways (the pivot switch): by machine (machine -> Director -> session, the topology), by
// repository (the GitHub "owner/repo" we are building - worktrees and clones fold together), by working
// tree (the on-disk checkout folder - each worktree stands alone), by agent (Claude/Codex/Gemini/... -
// the workforce), a flat Fleet list, or by Mission - the way the owner actually thinks about the work.
// Missions used to be its own left-rail page; it is now a pivot of this one page (issue #1405), so the
// fleet has a single home.
//
// "By repository" and "By working tree" are two DIFFERENT truths about the same fleet: a session cut in a
// worktree at "devthrottle-enroll-fix" belongs to the repository "thefrederiksen/devthrottle" (repository
// pivot folds it in) yet lives in its own checkout folder (working-tree pivot keeps it separate). The
// repository identity comes from SessionDto.repoName, which the owning Director resolves from the
// checkout's git origin remote; the working-tree identity is just the folder leaf of repoPath.
//
// It reads the ONE shared fleet roster store (issue #1239) - the same GET /sessions?envelope=true
// envelope Sessions and Directors read, from a single poll loop - never a Director address - and reuses
// the ONE shared effective-color rule so a node's dot matches every other Cockpit surface. Because every
// fleet page reads that one store, this map and the Sessions roster always agree on the fleet at the
// same moment, and a hidden tab makes no roster requests at all.

type Pivot = "machine" | "director" | "repo" | "worktree" | "agent" | "model" | "list" | "mission";

const PIVOTS: ReadonlyArray<{ key: Pivot; label: string; kindLabel: string }> = [
  { key: "machine", label: "By machine", kindLabel: "Machine" },
  // "By director" (devthrottle_internal#1177): one lane per Director across the fleet, headed by the
  // Director's display name (machine as the subtitle) - the level below machine in the machine pivot's
  // machine -> Director -> session hierarchy, promoted to lanes so several Directors on one machine sit
  // side by side. Idle reachable Directors keep a lane (a free slot), exactly like the machine pivot's
  // sub-groups.
  { key: "director", label: "By director", kindLabel: "Director" },
  // "By repository" groups by the GitHub "owner/repo" (SessionDto.repoName), so every worktree and clone
  // of one repository rolls up together. "By working tree" groups by the on-disk checkout folder, so each
  // worktree stands on its own - the two sit side by side because they answer two different questions.
  { key: "repo", label: "By repository", kindLabel: "Repository" },
  { key: "worktree", label: "By working tree", kindLabel: "Working tree" },
  { key: "agent", label: "By agent", kindLabel: "Agent" },
  // "By model" (issue devthrottle_internal#1340): the fleet laid out by the model each session is actually
  // running, the same way it can be laid out by agent. It sits beside "By agent" because it answers the
  // question one step down - not "which tool", but "which brain in that tool" - and that is the one the
  // fleet's cost and quality actually turn on. The lane title is the model id exactly as the agent's own
  // records spell it; the two absences ("no model yet", "model not reported") keep separate lanes rather
  // than pooling, because pooling them in the layout would undo the distinction the fold exists to make.
  { key: "model", label: "By model", kindLabel: "Model" },
  // The flat "Fleet list" pivot (issue #1212) absorbs the retired Fleet page: every session once, as
  // the same node card, no lane grouping (machines are already their own pivot).
  { key: "list", label: "Fleet list", kindLabel: "Session" },
  // The Missions pivot (issue #1405): the fleet grouped into missions (derived from the session name),
  // each mission card carrying its WHY. Rendered by MissionsBoard, not the node canvas - a distinct
  // board rather than a lane layout - so it sits at the end of the strip.
  { key: "mission", label: "Missions", kindLabel: "Mission" },
];

// The pivots that lay the fleet out on the node canvas (root -> lanes). "list" is a flat grid and
// "mission" is its own board, so neither uses the canvas. The title search applies to the canvas
// pivots and to "list"; only the Missions board hides it.
const CANVAS_PIVOTS: ReadonlySet<Pivot> = new Set<Pivot>(["machine", "director", "repo", "worktree", "agent", "model"]);

// The grouping is sticky per browser: the map opens on whatever the user last chose (rather than a
// "smart" default that guesses from the fleet shape), so returning to the map lands them exactly where
// they left off. Defaults to "By machine" the first time, or when storage is unavailable.
const PIVOT_STORAGE_KEY = "cockpit.fleetMapPivot";

// Whether the Missions pivot hides the missions nobody is on right now. Sticky per browser like the
// pivot itself, and ON by default: a mission that has finished keeps its card forever until somebody ends
// it, and four of those were enough to push the live work off the bottom of the screen.
//
// This is a VIEW preference and nothing more. It does not mean the hidden missions are over, and it is
// not the way to end one - that is a state on the Mission record and a separate piece of work. Whenever
// this hides anything the board says how many and offers them back, so it can never be confused with
// there being none.
const HIDE_EMPTY_MISSIONS_STORAGE_KEY = "cockpit.fleetMapHideEmptyMissions";

function initialHideEmptyMissions(): boolean {
  try {
    // Only an explicit "0" turns it off, so a first-time user (null) gets the clean board.
    return window.localStorage.getItem(HIDE_EMPTY_MISSIONS_STORAGE_KEY) !== "0";
  } catch {
    /* storage unavailable (private mode) - fall through to the default */
  }
  return true;
}

function initialPivot(): Pivot {
  try {
    const saved = window.localStorage.getItem(PIVOT_STORAGE_KEY);
    if (
      saved === "machine" ||
      saved === "director" ||
      saved === "repo" ||
      saved === "worktree" ||
      saved === "agent" ||
      saved === "model" ||
      saved === "list" ||
      saved === "mission"
    )
      return saved;
  } catch {
    /* storage unavailable (private mode) - fall through to the default */
  }
  return "machine";
}

// One column on the canvas for the active pivot: a header (the machine/repo/agent) plus its sessions,
// optionally sub-grouped by Director (machine pivot only) and by team.
interface Lane {
  key: string;
  kindLabel: string;
  title: string;
  subtitle: string;
  sessions: SessionDto[];
  // The machine's reachable Directors (machine pivot only; empty for every other pivot). Carries the
  // idle Directors that have no sessions so the lane can render them as free slots.
  directors: DirectorReachability[];
}

export function FleetMapView() {
  const navigate = useNavigate();
  // The sessions, the Gateway's unreachable verdict, and the per-Director reachability all come from the
  // ONE shared roster store; lastError is its keep-last banner text. No poll of its own.
  const { sessions, machineErrors, directors, unreachableBanner, error: lastError } = useSharedRoster();
  const [pivot, setPivotState] = useState<Pivot>(initialPivot);
  // Persist the choice so the map reopens on it (see initialPivot). Wraps the raw setter so every
  // pivot change - from any button - writes through to storage.
  const setPivot = useCallback((next: Pivot) => {
    setPivotState(next);
    try {
      window.localStorage.setItem(PIVOT_STORAGE_KEY, next);
    } catch {
      /* storage unavailable (private mode) - the selection still applies this session */
    }
  }, []);
  // Title search (issue #1211): a case-insensitive substring filter over the session name. It hides
  // non-matches and NEVER reorders what remains (see the lanes memo below). Not persisted across
  // reloads.
  const [query, setQuery] = useState("");

  // The Fleet Map "new session" button (top-right of each Director sub-header on the machine pivot):
  // holds the Director id to pre-target while the shared New Session dialog is open, or null when it is
  // closed. It reuses the EXACT same dialog the Sessions tab opens - the only difference is that the
  // clicked Director is already selected.
  const [newSessionDirectorId, setNewSessionDirectorId] = useState<string | null>(null);
  const onCreated = useCallback(
    (sessionId: string) => {
      setNewSessionDirectorId(null);
      navigate(`/session/${encodeURIComponent(sessionId)}`);
    },
    [navigate],
  );

  const list = useMemo(() => sessions ?? [], [sessions]);
  // Build the lanes from the WHOLE fleet first, so lane order and each card's slot are fixed. The title
  // search then only removes non-matching cards from within those fixed lanes and drops lanes that go
  // empty - a matching card never moves and the lanes never reorder (issue #1211). The flat "list"
  // pivot does not use lanes.
  const allLanes = useMemo(
    () => (CANVAS_PIVOTS.has(pivot) ? buildLanes(list, pivot, directors) : []),
    [list, pivot, directors],
  );
  const lanes = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (q.length === 0) return allLanes;
    return allLanes
      .map((lane) => ({ ...lane, sessions: lane.sessions.filter((s) => (s.name ?? "").toLowerCase().includes(q)) }))
      .filter((lane) => lane.sessions.length > 0);
  }, [allLanes, query]);
  // The flat "Fleet list" pivot (issue #1212): every session once, filtered by the same title search,
  // in a stable session-number order (the identity the owner reads), so matches never move.
  const flatSessions = useMemo(() => {
    const q = query.trim().toLowerCase();
    const filtered = q.length === 0 ? list : list.filter((s) => (s.name ?? "").toLowerCase().includes(q));
    return [...filtered].sort(flatSort);
  }, [list, query]);
  const matchCount = useMemo(
    () => (pivot === "list" ? flatSessions.length : lanes.reduce((n, lane) => n + lane.sessions.length, 0)),
    [pivot, flatSessions, lanes],
  );

  // Distinct machines in the fleet - counted by the SAME machine key the "By machine" lanes use, and
  // including a machine that has a Director but no sessions (an empty machine). This is why the header
  // can read "3 machines" while only one is running sessions: the other two are capacity, and the machine
  // pivot shows them as such. An unreachable machine counts too - it is still a machine in this fleet,
  // and the lane says so in words - so this number matches the lanes below it rather than quietly
  // disagreeing with them.
  const machineCount = useMemo(() => {
    const set = new Set<string>();
    for (const s of list) set.add(machineKeyOf(s.machineName).key);
    for (const m of directorsByMachine(directors)) set.add(m.key);
    return set.size;
  }, [list, directors]);
  // "N repos" counts distinct GitHub repositories, not checkouts: two worktrees of one repository are one
  // repo here (matching the "By repository" pivot). The "By working tree" pivot is where checkouts split.
  const repoCount = useMemo(() => {
    const set = new Set<string>();
    for (const s of list) set.add(repoIdentity(s.repoName, s.repoPath).toLowerCase());
    return set.size;
  }, [list]);
  const redCount = list.filter((s) => effectiveColor(s) === "red").length;
  const workingCount = list.filter((s) => effectiveColor(s) === "blue").length;
  // The Gateway's mission RECORDS, loaded when the Missions pivot is opened. They are what make a mission
  // with nobody on it yet appear at all - the roster alone can only reveal missions that already have a
  // session attached. Missions change rarely, so this is a one-shot read per pivot entry rather than a
  // poll; a mission attached while the page is open still renders, because the grouping falls back to the
  // name cached on the session for a mission id it has no record for.
  const [missions, setMissions] = useState<MissionDto[]>([]);
  const [missionsError, setMissionsError] = useState<string | null>(null);

  // The hide-empties preference, written through on every change exactly like the pivot above.
  const [hideEmptyMissions, setHideEmptyMissionsState] = useState<boolean>(initialHideEmptyMissions);
  const setHideEmptyMissions = useCallback((next: boolean) => {
    setHideEmptyMissionsState(next);
    try {
      window.localStorage.setItem(HIDE_EMPTY_MISSIONS_STORAGE_KEY, next ? "1" : "0");
    } catch {
      /* storage unavailable (private mode) - the choice still applies this session */
    }
  }, []);
  useEffect(() => {
    if (pivot !== "mission") return;
    let cancelled = false;
    // Drop any previous failure before re-reading, so a banner from an earlier visit cannot survive next to
    // a list that has since loaded cleanly.
    setMissionsError(null);
    listMissions()
      .then((m) => {
        if (cancelled) return;
        setMissions(m);
        setMissionsError(null);
      })
      .catch((e: unknown) => {
        // Loudly, not silently: without the records this board can still show every mission that has a
        // session on it, but it CANNOT show the empty ones - and a short list that looks complete is
        // exactly the kind of quiet wrong answer this screen was rebuilt to stop telling.
        if (cancelled) return;
        setMissionsError(e instanceof Error ? e.message : "The mission list could not be loaded.");
      });
    return () => {
      cancelled = true;
    };
  }, [pivot]);

  // Mission / standalone tally, shown in the header only while the Missions pivot is active so it
  // carries the count the standalone Missions page used to show. Grouped only when needed.
  const missionStats = useMemo(
    () => (pivot === "mission" ? missionCounts(list, missions) : null),
    [pivot, list, missions],
  );

  return (
    <ReachabilityContext.Provider value={directors}>
    <div className="fmap">
      <header className="fmap-head">
        <h1 className="fmap-title">Fleet Map</h1>
        <span className="fmap-stats">
          {missionStats !== null ? (
            <>
              <span>
                {missionStats.missions} mission{missionStats.missions === 1 ? "" : "s"}
              </span>
              {missionStats.standalone > 0 && (
                <>
                  <span className="fmap-sep" aria-hidden="true" />
                  <span>{missionStats.standalone} standalone</span>
                </>
              )}
              {/* The hidden count is part of the tally, not a footnote: the header states what EXISTS,
                  and says in the same breath how much of it is currently not being drawn. */}
              {hideEmptyMissions && missionStats.empty > 0 && (
                <>
                  <span className="fmap-sep" aria-hidden="true" />
                  <button
                    type="button"
                    className="fmap-emptytoggle"
                    onClick={() => setHideEmptyMissions(false)}
                    title="Show the missions that have no sessions on them right now"
                  >
                    {missionStats.empty} empty hidden
                  </button>
                </>
              )}
              {!hideEmptyMissions && missionStats.empty > 0 && (
                <>
                  <span className="fmap-sep" aria-hidden="true" />
                  <button
                    type="button"
                    className="fmap-emptytoggle"
                    onClick={() => setHideEmptyMissions(true)}
                    title="Hide the missions that have no sessions on them right now"
                  >
                    hide {missionStats.empty} empty
                  </button>
                </>
              )}
            </>
          ) : (
            <>
              <span>
                {list.length} session{list.length === 1 ? "" : "s"}
              </span>
              <span className="fmap-sep" aria-hidden="true" />
              <span>
                {machineCount} machine{machineCount === 1 ? "" : "s"}
              </span>
              <span className="fmap-sep" aria-hidden="true" />
              <span>
                {repoCount} repo{repoCount === 1 ? "" : "s"}
              </span>
            </>
          )}
          {workingCount > 0 && (
            <>
              <span className="fmap-sep" aria-hidden="true" />
              <span className="fmap-stat-blue">{workingCount} working</span>
            </>
          )}
          {redCount > 0 && (
            <>
              <span className="fmap-sep" aria-hidden="true" />
              <span className="fmap-stat-red">{redCount} needs you</span>
            </>
          )}
        </span>

        <div className="fmap-controls">
          {/* The title search filters the node canvas / flat list; the Missions board is not searchable,
              so the box is hidden while that pivot is active rather than left as a dead control. */}
          {pivot !== "mission" && (
            <input
              type="search"
              className="fmap-search"
              placeholder="Search titles..."
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              aria-label="Search sessions by title"
            />
          )}
          <div className="fmap-pivot" role="group" aria-label="Group the fleet by">
            {PIVOTS.map((p) => (
              <button
                key={p.key}
                type="button"
                className={pivot === p.key ? "fmap-pivot-btn on" : "fmap-pivot-btn"}
                aria-pressed={pivot === p.key}
                onClick={() => setPivot(p.key)}
              >
                {p.label}
              </button>
            ))}
          </div>
        </div>
      </header>

      {lastError !== null && <div className="fmap-error">{lastError}</div>}

      {/* THE GATEWAY'S SENTENCE, PRINTED VERBATIM. This used to count the rows in machineErrors and call
          each one a machine. Those rows are per-DIRECTOR, so one shut-down slot on SOREN_NORTH announced
          "1 machine unreachable" while three other Directors on that machine were pushing fifteen live
          sessions every few seconds. Whether a machine is unreachable - which needs EVERY Director on it
          to be - is a verdict, and verdicts are folded once on the Gateway (FleetReachabilityFold). */}
      {unreachableBanner !== null && unreachableBanner.length > 0 && (
        <div className="fmap-warn">{unreachableBanner}</div>
      )}

      {sessions === null && lastError === null && <div className="fmap-empty">Loading the fleet...</div>}

      {sessions !== null && list.length === 0 && machineErrors.length === 0 && lanes.length === 0 && (
        <div className="fmap-empty">
          <p>No sessions are running anywhere on the fleet.</p>
          <p className="fmap-empty-sub">
            A node appears here the moment a Director starts a session.
          </p>
        </div>
      )}

      {sessions !== null &&
        list.length > 0 &&
        matchCount === 0 &&
        query.trim().length > 0 && (
          <div className="fmap-empty">
            <p>No sessions match &ldquo;{query.trim()}&rdquo;.</p>
            <p className="fmap-empty-sub">Clear the search to see the whole fleet.</p>
          </div>
        )}

      {pivot === "mission"
        ? // The board also mounts on a mission-load FAILURE with nothing else to show: otherwise the one
          // case where we know least - no sessions and no mission list - is the case that renders the most
          // confident answer, a silent "no missions at all".
          (list.length > 0 || missions.length > 0 || missionsError !== null) && (
            // Hiding and the way back are passed as ONE unit - MissionsBoardProps refuses hideEmpty
            // without onShowEmpty, so a card can never be hidden with no affordance to bring it back.
            <MissionsBoard
              sessions={list}
              missions={missions}
              error={missionsError}
              {...(hideEmptyMissions
                ? ({ hideEmpty: true, onShowEmpty: () => setHideEmptyMissions(false) } as const)
                : ({ hideEmpty: false } as const))}
            />
          )
        : pivot === "list"
        ? flatSessions.length > 0 && (
            <FleetList
              sessions={flatSessions}
              onOpen={(sid) => navigate(`/session/${encodeURIComponent(sid)}`)}
            />
          )
        : lanes.length > 0 && (
            <Canvas
              lanes={lanes}
              pivot={pivot}
              sessionCount={query.trim().length > 0 ? matchCount : list.length}
              machineCount={machineCount}
              onOpen={(sid) => navigate(`/session/${encodeURIComponent(sid)}`)}
              onNewSession={(directorId) => setNewSessionDirectorId(directorId)}
            />
          )}

      <div className="fmap-legend" aria-hidden="true">
        <LegendDot color="blue" label="Working" />
        <LegendDot color="red" label="Needs you" />
        <LegendDot color="green" label="Ready" />
        <LegendDot color="cyan" label="Done" />
        <LegendDot color="yellow" label="Wingman reading" />
        <LegendDot color="orange" label="Transcribing" />
        <LegendDot color="supporting" label="Sub-agent" />
        <LegendDot color="grey" label="Snoozed" />
      </div>

      {newSessionDirectorId !== null && (
        <NewSessionDialog
          initialDirectorId={newSessionDirectorId}
          onClose={() => setNewSessionDirectorId(null)}
          onCreated={onCreated}
        />
      )}
    </div>
    </ReachabilityContext.Provider>
  );
}

function LegendDot({ color, label }: { color: string; label: string }) {
  return (
    <span className="fmap-legend-item">
      <span className="fmap-legend-dot" style={{ backgroundColor: dotColor(color) }} />
      {label}
    </span>
  );
}

interface CanvasProps {
  lanes: Lane[];
  pivot: Pivot;
  sessionCount: number;
  machineCount: number;
  onOpen: (sessionId: string) => void;
  // Start a new session on a specific Director (the machine-pivot sub-header "+ New session" button).
  onNewSession: (directorId: string) => void;
}

// The node canvas: a root node above a horizontal row of lane panels, joined by SVG elbow connectors
// measured from the live DOM (so the wiring tracks whatever width the panels lay out at). The lanes
// scroll horizontally inside their own container; the SVG is sized to the scrollable content so the
// wires stay attached when the row is wider than the pane.
function Canvas({ lanes, pivot, sessionCount, machineCount, onOpen, onNewSession }: CanvasProps) {
  const innerRef = useRef<HTMLDivElement | null>(null);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const headRefs = useRef<Map<string, HTMLDivElement>>(new Map());
  const [wires, setWires] = useState<string[]>([]);

  const laneKeys = lanes.map((l) => l.key).join("|");

  const drawWires = useCallback(() => {
    const inner = innerRef.current;
    const root = rootRef.current;
    if (inner === null || root === null) return;
    const base = inner.getBoundingClientRect();
    const r = root.getBoundingClientRect();
    const rx = r.left - base.left + r.width / 2;
    const ry = r.bottom - base.top;
    const next: string[] = [];
    for (const lane of lanes) {
      const head = headRefs.current.get(lane.key);
      if (head === undefined) continue;
      const hb = head.getBoundingClientRect();
      const hx = hb.left - base.left + hb.width / 2;
      const hy = hb.top - base.top;
      const midY = ry + (hy - ry) * 0.5;
      next.push(`M ${rx} ${ry} V ${midY} H ${hx} V ${hy}`);
    }
    setWires((prev) => (prev.length === next.length && prev.every((p, i) => p === next[i]) ? prev : next));
  }, [lanes]);

  // Redraw after layout whenever the grouping or pivot changes, and on any resize of the canvas.
  // useLayoutEffect so the wires are computed against the just-painted DOM.
  useLayoutEffect(() => {
    drawWires();
    const inner = innerRef.current;
    if (inner === null) return;
    const ro = new ResizeObserver(() => drawWires());
    ro.observe(inner);
    window.addEventListener("resize", drawWires);
    return () => {
      ro.disconnect();
      window.removeEventListener("resize", drawWires);
    };
  }, [drawWires, laneKeys, pivot]);

  return (
    <div className="fmap-canvas-scroll">
      <div className="fmap-canvas" ref={innerRef}>
        <svg className="fmap-wires" aria-hidden="true">
          {wires.map((d, i) => (
            <path key={i} d={d} />
          ))}
        </svg>

        <div className="fmap-canvas-inner">
          <div className="fmap-root" ref={rootRef}>
            <div className="fmap-root-k">Gateway</div>
            <div className="fmap-root-t">This fleet</div>
            <div className="fmap-root-s">
              {machineCount} machine{machineCount === 1 ? "" : "s"} &nbsp;/&nbsp; {sessionCount} session
              {sessionCount === 1 ? "" : "s"}
            </div>
          </div>

          <div className="fmap-lanes">
            {lanes.map((lane) => (
              <LanePanel
                key={lane.key}
                lane={lane}
                pivot={pivot}
                onOpen={onOpen}
                onNewSession={onNewSession}
                headRef={(el) => {
                  if (el === null) headRefs.current.delete(lane.key);
                  else headRefs.current.set(lane.key, el);
                }}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

interface LanePanelProps {
  lane: Lane;
  pivot: Pivot;
  onOpen: (sessionId: string) => void;
  onNewSession: (directorId: string) => void;
  headRef: (el: HTMLDivElement | null) => void;
}

function LanePanel({ lane, pivot, onOpen, onNewSession, headRef }: LanePanelProps) {
  const agg = aggregateColors(lane.sessions);

  // Machine pivot: sub-group the lane's sessions by Director so the panel reads machine -> Director ->
  // session, folding in the machine's session-less Directors so an empty Director shows as a free slot
  // (or, when its tunnel is down, as an unreachable one). Other pivots render a flat list (each card
  // still tags its machine/Director).
  const directorGroups = pivot === "machine" ? groupByDirector(lane.sessions, sessionSort, lane.directors) : null;

  // Director pivot (devthrottle_internal#1177): the lane IS one Director, carrying its own reachability
  // entry, so the head takes over what the machine pivot's Director sub-header does - state the tunnel
  // condition, offer "+ New session" only while it could be honoured (hidden too for the unaddressable
  // "(unknown)" lane), and say why an idle lane is idle. Every one of those is the Gateway's ruling, read.
  const directorLane = pivot === "director";
  const laneReach = directorLane ? lane.directors[0] : undefined;
  const laneStateLabel = directorLane ? directorStateLabel(laneReach) : "";
  const laneLastSeen = laneStateLabel.length > 0 ? reachabilityLastSeen(laneReach?.lastSeenAgeSeconds) : "";

  return (
    <section className="fmap-lane">
      <div className="fmap-lane-head" ref={headRef}>
        <span className="fmap-lane-k">{lane.kindLabel}</span>
        <span className="fmap-lane-t">{lane.title}</span>
        {lane.subtitle.length > 0 && <span className="fmap-lane-sub">{lane.subtitle}</span>}
        {laneStateLabel.length > 0 && (
          <span className="fmap-subhead-state">
            {laneLastSeen.length > 0 ? `${laneStateLabel} - ${laneLastSeen}` : laneStateLabel}
          </span>
        )}
        {directorLane && lane.key !== "(unknown)" && canStartSessionOn(laneReach) && (
          <button
            type="button"
            className="fmap-subhead-new"
            title="Start a new session on this Director"
            aria-label={`Start a new session on ${lane.title}`}
            onClick={() => onNewSession(lane.key)}
          >
            + New session
          </button>
        )}
        <span className="fmap-lane-agg">
          {agg.map((c) => (
            <span key={c} className="fmap-lane-aggdot" style={{ backgroundColor: dotColor(c) }} />
          ))}
        </span>
      </div>

      <div className="fmap-lane-body">
        {directorGroups !== null
          ? directorGroups.map((dg) => <DirectorSubGroup key={dg.key} group={dg} pivot={pivot} onOpen={onOpen} onNewSession={onNewSession} />)
          : directorLane && lane.sessions.length === 0
          ? <div className="fmap-freeslot">{emptySlotTextOf(laneReach)}</div>
          : <LaneCards sessions={lane.sessions} pivot={pivot} onOpen={onOpen} />}
      </div>
    </section>
  );
}

// One Director sub-group inside a machine lane (machine pivot): its sessions, or the placeholder line
// that says why there are none.
//
// AN UNREACHABLE DIRECTOR IS DIMMED AND DATED, NEVER DROPPED. Directors used to be filtered out of the
// lane fold entirely when their tunnel went down, so the machine disappeared from the map - the same
// delete-instead-of-dim defect the Gateway has stopped committing (it now serves that machine's sessions
// with their age). The sub-header therefore states the Director's condition and when it was last heard,
// the cards below it dim themselves (NodeCard), and the placeholder says why there are no sessions
// rather than claiming free capacity.
//
// NOTE THE NOUN. This sub-header is a DIRECTOR, and it used to say "machine unreachable" - on a machine
// that could be running three other Directors and fifteen live sessions. What each state means, what it
// is called, and whether "+ New session" could be honoured are all the Gateway's ruling now; this
// component reads them and lays them out.
function DirectorSubGroup({
  group,
  pivot,
  onOpen,
  onNewSession,
}: {
  group: FormatDirectorGroup;
  pivot: Pivot;
  onOpen: (sid: string) => void;
  onNewSession: (directorId: string) => void;
}) {
  const reach = group.reachability;
  const state = reach?.state;
  const label = directorStateLabel(reach);
  const lastSeen = label.length > 0 ? reachabilityLastSeen(reach?.lastSeenAgeSeconds) : "";
  return (
    <div className="fmap-dgroup">
      <div
        className={
          "fmap-subhead" +
          (state === REACHABILITY_OFFLINE ? " fmap-subhead-offline" : "") +
          (state === REACHABILITY_WOBBLY ? " fmap-subhead-wobbly" : "")
        }
      >
        <span className="fmap-subhead-label">{group.label}</span>
        {label.length > 0 && (
          <span className="fmap-subhead-state">
            {lastSeen.length > 0 ? `${label} - ${lastSeen}` : label}
          </span>
        )}
        {/* Start a new session on THIS Director, using the same dialog the Sessions tab opens (only
            pre-targeted here). Hidden for an unidentified Director - it is not an addressable slot, so
            there is nothing to start a session on - and whenever the Gateway says a start could not be
            delivered, because a button that cannot do what it says reads as the app being broken. */}
        {group.key !== "(unknown)" && canStartSessionOn(reach) && (
          <button
            type="button"
            className="fmap-subhead-new"
            title="Start a new session on this Director"
            aria-label={`Start a new session on ${group.label}`}
            onClick={() => onNewSession(group.key)}
          >
            + New session
          </button>
        )}
      </div>
      {group.sessions.length > 0 ? (
        <LaneCards sessions={group.sessions} pivot={pivot} onOpen={onOpen} />
      ) : (
        <div className="fmap-freeslot">{emptySlotTextOf(reach)}</div>
      )}
    </div>
  );
}

// The flat "Fleet list" pivot body (issue #1212): every session as the same NodeCard, in a responsive
// grid, no lane grouping. Reuses the exact card the lane pivots render, so the number badge and the
// title search apply identically.
function FleetList({ sessions, onOpen }: { sessions: SessionDto[]; onOpen: (sid: string) => void }) {
  return (
    <div className="fmap-list">
      {sessions.map((s) => (
        <NodeCard key={s.sessionId ?? s.number} session={s} pivot="list" onOpen={onOpen} />
      ))}
    </div>
  );
}

// Issue #1626: a lane's cards are ordered and indented as the spawn tree - a Manager's Workers sit
// under it, not scattered through the lane. The tree itself is built in fleetMapFormat (pure, unit
// tested); this only renders it.
function LaneCards({ sessions, pivot, onOpen }: { sessions: SessionDto[]; pivot: Pivot; onOpen: (sid: string) => void }) {
  const nodes = useMemo(() => buildControllerTree(sessions, sessionSort), [sessions]);
  return (
    <>
      {nodes.map((n) => (
        <NodeCard
          key={n.session.sessionId ?? n.session.number}
          session={n.session}
          depth={n.depth}
          pivot={pivot}
          onOpen={onOpen}
        />
      ))}
    </>
  );
}

function NodeCard({
  session: s,
  pivot,
  onOpen,
  depth = 0,
}: {
  session: SessionDto;
  pivot: Pivot;
  onOpen: (sid: string) => void;
  depth?: number;
}) {
  const directors = useContext(ReachabilityContext);
  const color = effectiveColor(s);
  const sid = s.sessionId ?? "";
  const unnamed = (s.name ?? "").trim().length === 0;
  const num = s.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  // The Gateway-resolved role (issue #1626). READ, never re-derived: only the Gateway can answer it
  // (FleetRoleResolver), because a session's controller may live on another Director entirely.
  const sessionRole = (s.sessionRole ?? "").trim();
  const showRole = sessionRole.length > 0 && sessionRole.toLowerCase() !== "standalone";
  // Reachability of the owning Director (issue #1215): a Director that did not answer this refresh dims
  // its cards in place and dates them - its rows are the last thing it said, not a confirmed state. WHICH
  // states those are is the Gateway's ruling (isDataStale), read here; the two class names below stay
  // keyed to the specific state because they are styling, not meaning.
  const reach = reachabilityFor(directors, s.directorId);
  const wobbly = reach?.state === REACHABILITY_WOBBLY;
  const stale = isDataStale(reach);
  const lastSeen = stale ? reachabilityLastSeen(reach?.lastSeenAgeSeconds) : "";
  const cls =
    "fmap-card" +
    (color === "red" ? " needs" : "") +
    (depth > 0 ? " fmap-card-child" : "") +
    (wobbly ? " fmap-card-wobbly" : "") +
    // Anything the Gateway calls stale that is not specifically wobbly dims like an offline card: a
    // stopped Director's leftover rows are last-known too, and must not read as live.
    (stale && !wobbly ? " fmap-card-offline" : "");

  // The card tags carry the two hierarchy coordinates NOT already implied by the lane the card sits in.
  const tags = cardTags(s, pivot);
  // Issue #1625: the agent rides the meta row WITH the tags, never the title row. On the title row it was
  // rigid (flex: 0 0 auto) against a title that was the only flexible thing there, so the title was the
  // only thing that could shrink and it was ellipsized to a few characters on every card. Null on the
  // agent pivot, where the lane header already states the agent and a per-card badge only repeats it.
  const agentBadge = agentBadgeText(s, pivot);
  // Which model this session is running (issue devthrottle_internal#1340), beside the agent because the
  // two are one identity. Every string is the Gateway's; this card chooses nothing about the wording, and
  // in particular does not decide what a missing model means.
  const model = modelChip(s, pivot);

  return (
    <article
      className={cls}
      // Indent by tree depth (issue #1626). The depth itself is uncapped - nesting is real - but the
      // INDENT is capped, so a deep chain leans right a little and then stops rather than squeezing the
      // cards into a sliver. The connector rail (::before) is what keeps a capped level readable.
      style={depth > 0 ? { marginLeft: `${Math.min(depth, 4) * 14}px` } : undefined}
      tabIndex={0}
      role="button"
      title={sid.length > 0 ? "Open session" : undefined}
      onClick={() => sid.length > 0 && onOpen(sid)}
      onKeyDown={(e) => {
        if (sid.length > 0 && (e.key === "Enter" || e.key === " ")) {
          e.preventDefault();
          onOpen(sid);
        }
      }}
    >
      <div className="fmap-card-top">
        <span
          className={color === "blue" ? "fmap-dot working" : "fmap-dot"}
          style={{ backgroundColor: dotHex(s) }}
          title={s.lastStatusReason ?? undefined}
        />
        {hasNum && <span className="num-badge">{num}</span>}
        <span className={unnamed ? "fmap-card-name unnamed" : "fmap-card-name"}>
          {unnamed ? "(unnamed)" : s.name}
        </span>
      </div>

      {(showRole || agentBadge !== null || model !== null || tags.length > 0) && (
        <div className="fmap-card-tags">
          {showRole && <span className={`fmap-role ${sessionRole.toLowerCase()}`}>{sessionRole}</span>}
          {agentBadge !== null && <span className="fmap-agent">{agentBadge}</span>}
          {model !== null && (
            <span className={model.absent ? "fmap-model absent" : "fmap-model"} title={model.title}>
              {model.text}
            </span>
          )}
          {tags.map((t) => (
            <span key={t.k} className="fmap-card-tag">
              <span className="fmap-card-tag-k">{t.k}</span>
              {t.v}
            </span>
          ))}
        </div>
      )}

      <div className="fmap-card-state" style={{ color: dotHex(s) }}>
        {stateLabel(s)}
        <span className="fmap-card-idle">{relativeTime(s.lastActivityAt)}</span>
      </div>

      {/* The Gateway's word for this Director's condition, not a local guess. This branched on
          `offline ? "Offline" : "Wobbly"`, so a leftover row owned by a shut-down Director - which the
          Gateway deliberately keeps serving - was captioned "Wobbly": the one state it certainly was not. */}
      {lastSeen.length > 0 && (
        <div className="fmap-card-lastseen">{directorStateLabel(reach)} - {lastSeen}</div>
      )}
    </article>
  );
}

// ---- grouping (pure) ----

function buildLanes(sessions: SessionDto[], pivot: Pivot, directors: DirectorReachability[] = []): Lane[] {
  const byKey = new Map<string, { title: string; list: SessionDto[]; directors: DirectorReachability[] }>();
  const keyOf = (s: SessionDto): { key: string; title: string } => {
    if (pivot === "machine") {
      // Key through the shared machineKeyOf so a session's lane and an idle Director's lane collapse
      // onto the same key (see fleetMapFormat).
      return machineKeyOf(s.machineName);
    }
    if (pivot === "director") {
      // Key by the RAW director id - it is also the "+ New session" target, so it must stay addressable
      // (the machine pivot's groupByDirector keys the same way). The title here is the id fallback; the
      // fold below re-titles every lane through directorLabelOf once reachability (and with it the
      // display name) is in hand.
      const dir = (s.directorId ?? "").trim();
      return { key: dir.length === 0 ? "(unknown)" : dir, title: directorLabelOf(dir, undefined) };
    }
    if (pivot === "repo") {
      // GitHub "owner/repo" identity - worktrees and clones of one repository fold into one lane.
      const title = repoIdentity(s.repoName, s.repoPath);
      return { key: title.toLowerCase(), title };
    }
    if (pivot === "worktree") {
      // On-disk checkout folder - each worktree stands alone, even when it belongs to the same repository.
      const title = repoBasename(s.repoPath);
      return { key: title.toLowerCase(), title };
    }
    if (pivot === "model") {
      // The FULL recorded model id, through the shared key so the lane is named the way the records name
      // the model and both absences keep their own lanes (see fleetMapFormat.modelKeyOf).
      return modelKeyOf(s);
    }
    const agent = (s.agent ?? "").trim();
    const title = agent.length === 0 ? "(unknown agent)" : agent;
    return { key: title.toLowerCase(), title };
  };

  for (const s of sessions) {
    const { key, title } = keyOf(s);
    let g = byKey.get(key);
    if (g === undefined) {
      g = { title, list: [], directors: [] };
      byKey.set(key, g);
    }
    g.list.push(s);
  }

  // Machine pivot only: fold in every Director so a machine that has a Director but no sessions still
  // gets a lane, and a machine's Directors ride on the lane so the panel can render each one as a free
  // slot or as an unreachable one. Other pivots group only real sessions - a repository or an agent with
  // no session is not a thing to show. Offline Directors are INCLUDED (they used to be filtered out
  // upstream, which deleted an unreachable machine from the map instead of dimming it).
  if (pivot === "machine") {
    for (const m of directorsByMachine(directors)) {
      let g = byKey.get(m.key);
      if (g === undefined) {
        g = { title: m.title, list: [], directors: [] };
        byKey.set(m.key, g);
      }
      g.directors = m.directors;
    }
  }

  // Director pivot (devthrottle_internal#1177): fold in EVERY Director the envelope reports - idle ones
  // included, offline ones included (same delete-instead-of-dim rule as the machine pivot) - so a
  // Director with no sessions still gets a lane (a free slot). Each lane carries its own reachability
  // entry, and every lane is then re-titled through directorLabelOf so a lane whose Director reports a
  // display name reads as that name rather than as 8 hex chars.
  if (pivot === "director") {
    for (const d of directors) {
      const id = (d.directorId ?? "").trim();
      if (id.length === 0) continue; // an unidentified Director is not an addressable slot
      let g = byKey.get(id);
      if (g === undefined) {
        g = { title: "", list: [], directors: [] };
        byKey.set(id, g);
      }
      g.directors = [d];
    }
    for (const [key, g] of byKey.entries()) {
      g.title = key === "(unknown)" ? "Director (unknown)" : directorLabelOf(key, g.directors[0]);
    }
  }

  const kindLabel = PIVOTS.find((p) => p.key === pivot)?.kindLabel ?? "";

  return [...byKey.entries()]
    .map(([key, g]) => ({
      key,
      kindLabel,
      title: g.title,
      subtitle: laneSubtitle(g.list, pivot, g.directors),
      sessions: [...g.list].sort(sessionSort),
      directors: g.directors,
    }))
    // Busiest lanes first, then by title; a free (zero-session) machine sorts to the end, after every
    // machine that is actually running work.
    .sort((a, b) => b.sessions.length - a.sessions.length || a.title.toLowerCase().localeCompare(b.title.toLowerCase()));
}

function laneSubtitle(sessions: SessionDto[], pivot: Pivot, machineDirectors: DirectorReachability[] = []): string {
  const n = sessions.length;
  const sessionWord = `${n} session${n === 1 ? "" : "s"}`;
  if (pivot === "director") {
    // Lane = one Director; the missing coordinate is WHICH MACHINE it runs on. From the reachability
    // entry when the envelope has one, else from any of the lane's sessions.
    const machine =
      (machineDirectors[0]?.machineName ?? "").trim() ||
      sessions.map((s) => (s.machineName ?? "").trim()).find((m) => m.length > 0) ||
      "";
    return machine.length > 0 ? `${machine} / ${sessionWord}` : sessionWord;
  }
  if (pivot === "machine") {
    // Count the machine's Directors from BOTH the sessions and the folded-in idle Directors, so an
    // otherwise-empty machine still reads "1 director / 0 sessions" (an available slot) rather than
    // "0 directors".
    const ids = new Set<string>();
    for (const s of sessions) {
      const id = (s.directorId ?? "").trim();
      if (id.length > 0) ids.add(id);
    }
    for (const d of machineDirectors) {
      const id = (d.directorId ?? "").trim();
      if (id.length > 0) ids.add(id);
    }
    const d = ids.size;
    return `${d} director${d === 1 ? "" : "s"} / ${sessionWord}`;
  }
  return sessionWord;
}

// Stable order within a lane: creation time, then session id, so a card never jumps when its color
// changes (matches the Fleet page ordering intent).
function sessionSort(a: SessionDto, b: SessionDto): number {
  const byCreated = String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
  if (byCreated !== 0) return byCreated;
  return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
}

// Stable order for the flat "Fleet list" pivot: by session number ascending (the identity the owner
// reads), numbers before the unnumbered, then session id - so a card never jumps on a color change.
function flatSort(a: SessionDto, b: SessionDto): number {
  const na = Number(a.number);
  const nb = Number(b.number);
  const aHas = Number.isFinite(na);
  const bHas = Number.isFinite(nb);
  if (aHas && bHas && na !== nb) return na - nb;
  if (aHas !== bHas) return aHas ? -1 : 1;
  return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
}

// groupByDirector and shortDir moved to fleetMapFormat.ts (pure, unit tested) - groupByDirector now folds
// a machine's idle Directors in as empty groups so the machine pivot shows free slots. See the import.

// The groupId "team" clustering that used to live here is GONE (issue #1626). It was a live consumer of
// a producer that does not exist: SessionDto.groupId is only ever assigned from SessionManager's
// `groupId` parameter, that parameter defaults to null, no call site passes it, and NewSessionRequest
// has no group field at all - so no session created through the Gateway, the CLI, or the desktop can
// ever carry one. The clustering therefore never rendered, for anyone. The controller spawn tree
// (buildControllerTree) is the real relationship and replaces it, which also keeps ONE grouping
// mechanism in this view instead of two competing ones.
//
// The DTO field and its C# plumbing are deliberately left alone here - removing them is a separate
// decision with its own blast radius, and this issue is a Cockpit change.

// The agent badge text and the controller tree live in fleetMapFormat.ts (pure, unit tested) - see the
// import at the top.

// The two hierarchy coordinates to show on a card that the lane header does NOT already state.
function cardTags(s: SessionDto, pivot: Pivot): Array<{ k: string; v: string }> {
  const dir = (s.directorId ?? "").trim();
  const machine = (s.machineName ?? "").trim();
  if (pivot === "machine") {
    // Lane = machine, sub-grouped by Director; the missing coordinate is the repository.
    return [{ k: "repo", v: repoIdentity(s.repoName, s.repoPath) }];
  }
  if (pivot === "director") {
    // Lane = one Director (machine already in the lane subtitle); the missing coordinate is the
    // repository.
    return [{ k: "repo", v: repoIdentity(s.repoName, s.repoPath) }];
  }
  if (pivot === "repo") {
    // Lane = repository; show where it runs (machine + Director) and which working tree, so two worktrees
    // of the same repository sharing a lane are still told apart.
    const out: Array<{ k: string; v: string }> = [];
    if (machine.length > 0) out.push({ k: machine, v: dir.length > 0 ? shortDir(dir) : "-" });
    out.push({ k: "tree", v: repoBasename(s.repoPath) });
    return out;
  }
  if (pivot === "worktree") {
    // Lane = working tree; show where it runs (machine + Director) and which repository it belongs to.
    const out: Array<{ k: string; v: string }> = [];
    if (machine.length > 0) out.push({ k: machine, v: dir.length > 0 ? shortDir(dir) : "-" });
    out.push({ k: "repo", v: repoIdentity(s.repoName, s.repoPath) });
    return out;
  }
  // Lane = agent or model, or the flat Fleet list (no lane at all); show machine + repository. A model
  // lane wants the same two coordinates an agent lane does - a lane full of "claude-opus-5" cards says
  // nothing about WHERE each one is running.
  const out: Array<{ k: string; v: string }> = [];
  if (machine.length > 0) out.push({ k: machine, v: repoIdentity(s.repoName, s.repoPath) });
  return out;
}

function aggregateColors(sessions: SessionDto[]): string[] {
  const priority = ["red", "orange", "yellow", "blue", "cyan", "green", "supporting", "grey"];
  const present = new Set(sessions.map((s) => effectiveColor(s)));
  return priority.filter((c) => present.has(c));
}
