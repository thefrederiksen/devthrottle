import { useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { setVoiceModeAllSessions, type SessionDto } from "@devthrottle/client-core/api/client";
import {
  classify,
  contextLine,
  deletionReason,
  dotHex,
  groupByDirector,
  pendingDeletion,
  snoozeCountdown,
  snoozeExpired,
} from "@devthrottle/client-core/sessions/ordering";
import {
  attentionSections,
  buildSessionTree,
  CALM_BAND_TITLE,
  childrenOf,
  descendantsOf,
  isCrewExpanded,
  isOnAnotherMachine,
  setCrewExpanded,
  type SessionTree,
} from "@devthrottle/client-core/sessions/tree";
import { changesBadge, changesTitle } from "@devthrottle/client-core/sessions/changes";
import { splitPinned, type PinnedSplit } from "@devthrottle/client-core/sessions/pinning";
import {
  DELIVERY_BADGE_TEXT,
  hasUndeliveredPrompt,
  promptDeliveryTitle,
} from "@devthrottle/client-core/sessions/delivery";
import { supervisionStats } from "@devthrottle/client-core/sessions/supervision";
import { modelChipOf } from "@devthrottle/client-core/sessions/model";
import { machinePortLabel } from "@devthrottle/client-core/fleet/directorEndpoint";
import { isDataStale } from "@devthrottle/client-core/fleet/directorPresentation";
import { useNow, waitingLabel } from "@devthrottle/client-core/sessions/waiting";
import { useNow as useSharedNow } from "@devthrottle/client-core/polling/useNow";
import { ColourLegendButton, useSessionColourLegend } from "@devthrottle/client-core/sessions/ColourLegend";
import { dotTitle } from "@devthrottle/client-core/sessions/sessionColours";
import {
  reachabilityFor,
  reachabilityLastSeen,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
} from "@devthrottle/client-core/fleet/fleetClient";
import { SessionMenu } from "./SessionMenu";
import { CrewLine } from "./CrewLine";
import { RestartRequestsPanel } from "@devthrottle/client-core/restart/RestartRequestsPanel";

// The fleet-wide session roster (issue #972) - the React port of the Blazor SessionRail. It lists
// EVERY session the Gateway roster aggregation (GET /sessions) returns, across every Director, with
// the one effective status color and the same ordering policy the mobile roster and the desktop rail
// share (client-core/sessions). Selecting a row drives the terminal pane and the reply bar (the row
// is a Link to /session/{sid}).
//
// Ordering rule (the session-rail ordering decision): manual "My order" is the DEFAULT - the stable
// desktop order (the owning Director's drag-to-reorder SortOrder), so rows hold their slot and never
// reshuffle on a color change. In "My order" the sessions are grouped by their owning cc-director under
// a "computer:port" header (the port tells apart several Directors on one machine), and each session's
// facts get their own line so nothing truncates. "Attention first" is an OPT-IN view that puts the
// session that has needed you longest at the top, then the working ones, then the snoozed ones at the
// bottom, across every machine; it is never applied automatically.
//
// THE LIST IS ALWAYS THE OWNERSHIP TREE (owner ruling, 2026-09-14). A session that started another
// session is a parent, and the sessions it started sit under it, collapsed by default, in BOTH views.
// The order applies to the top level only: children keep their own order under their parent, because a
// child with a live supervisor never goes red (#2826) and so has nothing for attention to reorder. The
// tree is built over the WHOLE roster in both views (a parent may supervise a session on another
// machine): "My order" groups the tree's ROOTS by Director, a child nests under its parent wherever the
// parent lives, and a child on another machine carries its machine line. The tree and the crew summary
// live in client-core/sessions/tree, shared with the phone.
//
// THE FLEET MANAGER IS PINNED FIRST (the Fleet Manager mission, step 8), in both views, with its team collapsed under
// it like any crew. Which row is pinned, and every word it and the heading below it wear, is the Gateway's
// (SessionDto.pin); client-core/sessions/pinning lifts it out, and the rows that are not pinned are laid out exactly as
// before, under the Gateway's heading.

export type RosterView = "my-order" | "attention";

export interface SessionRosterProps {
  sessions: SessionDto[] | null;
  /** Per-Director reachability for the Online / Wobbly / Offline rendering (issue #1215). */
  directors: DirectorReachability[];
  /** directorId -> Control API port, for the "computer:port" group headers. Empty until GET /directors
   *  lands, or for a Director whose endpoint carries no port - the header then shows the bare machine. */
  portByDirector: Map<string, string>;
  selectedId: string | undefined;
  view: RosterView;
  onView: (view: RosterView) => void;
  error: string | null;
  /** Open the "New session" dialog (issue #1023). */
  onNewSession: () => void;
}

export function SessionRoster({ sessions, directors, portByDirector, selectedId, view, onView, error, onNewSession }: SessionRosterProps) {
  const total = sessions?.length ?? 0;

  return (
    <div className="roster-rail">
      <div className="roster-head">
        <span className="roster-title">Sessions</span>
        <span className="roster-count">{total}</span>
        {/* The only way to start a session from the desktop Cockpit (issue #1023). Opens the
            dedicated machine/repo picker dialog. */}
        <button type="button" className="roster-newbtn" onClick={onNewSession} title="Start a new session">
          + New session
        </button>
      </div>

      {/* The ordering toggle. "My order" is pressed by default; "Attention first" is the opt-in view. */}
      <div className="roster-view" role="group" aria-label="Roster ordering">
        <button
          type="button"
          className={`roster-view-btn ${view === "my-order" ? "on" : ""}`}
          aria-pressed={view === "my-order"}
          onClick={() => onView("my-order")}
        >
          My order
        </button>
        <button
          type="button"
          className={`roster-view-btn ${view === "attention" ? "on" : ""}`}
          aria-pressed={view === "attention"}
          onClick={() => onView("attention")}
        >
          Attention first
        </button>
      </div>

      {/* What the dots mean. The same shared legend the phone roster mounts. */}
      <ColourLegendButton className="roster-legend-btn" />

      {/* The fleet-wide voice switch (issue #1765): one button turns voice mode on for every session,
          or off again, so a person leaving their desk can put the whole fleet on voice and take it back
          off later without touching each session. It reads the roster to pick its own direction. */}
      {sessions !== null && total > 0 && <VoiceAllButton sessions={sessions} />}

      {/* A Director restart a session has asked for (issue #2725): the owner's one accept, above the
          roster where "Needs you" lives. The same shared component the phone mounts; this shell only
          tunes its layout. Renders nothing while no request exists. */}
      <RestartRequestsPanel />

      {error !== null && (
        <div className="roster-error" role="alert">
          {sessions !== null ? "Roster stale - showing last-known sessions" : error}
        </div>
      )}

      {sessions === null && error === null && <div className="roster-empty">Loading sessions...</div>}

      {sessions !== null && total === 0 && (
        <div className="roster-empty">No sessions. The Gateway returned an empty roster.</div>
      )}

      {sessions !== null && total > 0 && (
        <PinnedAndRest sessions={sessions} directors={directors} portByDirector={portByDirector} selectedId={selectedId}>
          {(tree, rest) =>
            view === "my-order" ? (
              <MyOrderGroups tree={tree} roots={rest} directors={directors} portByDirector={portByDirector} selectedId={selectedId} />
            ) : (
              <AttentionGroups tree={tree} roots={rest} directors={directors} portByDirector={portByDirector} selectedId={selectedId} />
            )
          }
        </PinnedAndRest>
      )}
    </div>
  );
}

// ONE tree over the whole roster. The pinned rows (the Fleet Manager, step 8) come first, each with its team under it;
// the rest of the roots go to the view the owner chose, under the Gateway's heading, laid out as they always were.
function PinnedAndRest({
  sessions,
  directors,
  portByDirector,
  selectedId,
  children,
}: {
  sessions: SessionDto[];
  directors: DirectorReachability[];
  portByDirector: Map<string, string>;
  selectedId: string | undefined;
  children: (tree: SessionTree, rest: SessionDto[]) => ReactNode;
}) {
  const tree = buildSessionTree(sessions);
  const split: PinnedSplit = splitPinned(tree.roots);
  return (
    <>
      {split.pinned.length > 0 && (
        <div className="roster-pinned" data-testid="roster-pinned">
          <TreeList
            tree={{ roots: split.pinned, childrenOf: tree.childrenOf }}
            directors={directors}
            portByDirector={portByDirector}
            showMachine
            selectedId={selectedId}
          />
        </div>
      )}
      {split.pin !== null && split.rest.length > 0 && (
        <div className="roster-others-head" data-testid="roster-others-head">
          {split.pin.othersHeading}
        </div>
      )}
      {children(tree, split.rest)}
      {split.pin !== null && split.rest.some((r) => r.ownerChange?.to === "fleet-manager") && (
        <Link className="roster-handover-link" to="/fleet-manager?handover=1" data-testid="roster-handover-link">
          {split.pin.handOverLinkLabel}
        </Link>
      )}
    </>
  );
}

// "My order": the tree's ROOTS grouped by their owning Director under a "computer:port" header. A child sits under its
// parent whichever Director it is on.
function MyOrderGroups({
  tree,
  roots,
  directors,
  portByDirector,
  selectedId,
}: {
  tree: SessionTree;
  roots: SessionDto[];
  directors: DirectorReachability[];
  portByDirector: Map<string, string>;
  selectedId: string | undefined;
}) {
  return (
    <>
          {groupByDirector(roots, portByDirector).map((group) => (
            <div className="roster-group" key={group.directorId || "(no-director)"}>
              <div className="roster-group-head">
                <span className="roster-group-name">
                  {machinePortLabel(group.machineName, group.port) || "(unknown director)"}
                </span>
              </div>
              <TreeList
                tree={{ roots: group.sessions, childrenOf: tree.childrenOf }}
                directors={directors}
                portByDirector={portByDirector}
                showMachine={false}
                selectedId={selectedId}
              />
            </div>
          ))}
    </>
  );
}

// The fleet-wide voice switch shown under the roster ordering toggle (issue #1765). One control for both
// directions: while no session is a voice session it offers "Turn on voice for all N sessions"; the moment
// any session is on, it offers "Turn voice off for all sessions". It calls the Gateway's single fan-out
// endpoint, which walks the roster itself, then shows a plain summary of what changed and what was skipped
// (a session whose owning computer is offline is passed over, never failing the batch). The next roster
// poll repaints the "voice" tags and the button flips to its opposite direction.
function VoiceAllButton({ sessions }: { sessions: SessionDto[] }) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const anyOn = sessions.some((s) => Boolean(s.voiceMode));
  const enable = !anyOn;
  const count = sessions.length;

  const onClick = async () => {
    setBusy(true);
    setError(null);
    setNote(null);
    try {
      const result = await setVoiceModeAllSessions(enable);
      const changedLabel = `${result.changed} ${result.changed === 1 ? "session" : "sessions"} ${enable ? "on" : "off"}`;
      setNote(result.skipped > 0 ? `${changedLabel}, ${result.skipped} skipped (computer offline)` : changedLabel);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not change voice mode for all sessions");
    } finally {
      setBusy(false);
    }
  };

  const label = enable ? `Turn on voice for all ${count}` : "Turn voice off for all";
  const busyLabel = enable ? "Turning voice on..." : "Turning voice off...";

  return (
    <div className="roster-voice-all">
      <button
        type="button"
        className={`roster-voice-all-btn${enable ? "" : " off"}`}
        onClick={() => void onClick()}
        disabled={busy}
        title="Turn voice mode on or off for every session at once"
      >
        {busy ? busyLabel : label}
      </button>
      {note && <span className="roster-voice-all-note" role="status">{note}</span>}
      {error && <span className="roster-voice-all-error" role="alert">{error}</span>}
    </div>
  );
}

// Opt-in attention view: needs-you first, then working, then snoozed - the attention order from
// client-core/sessions/tree, applied to the TOP LEVEL of the tree across every machine.
//
// The needs-you section is a WAITING LINE, the same order the phone roster uses: the session that has
// been asking for you the LONGEST sits at the top, and a session that only just started needing you
// joins at the BOTTOM. Work it from the top down and it is first-in, first-handled, and it never
// reshuffles under you as new work arrives. The working and snoozed sections keep their members in
// desktop order, so a session holds its slot within its section. A crew is one row in whichever
// section its parent falls, with its children collapsed under it.
//
// These sections mix machines, so - unlike the grouped "My order" view - each card still shows its own
// "computer:port" line so you can see which cc-director a needs-you session lives on.
function AttentionGroups({
  tree,
  roots,
  directors,
  portByDirector,
  selectedId,
}: {
  tree: SessionTree;
  roots: SessionDto[];
  directors: DirectorReachability[];
  portByDirector: Map<string, string>;
  selectedId: string | undefined;
}) {
  return (
    <>
      {attentionSections(roots).map((section) => (
        <div className="roster-bucket" key={section.key}>
          <div className={`roster-bucket-head ${section.key === "needsYou" ? "needs" : section.key === "onHold" ? "hold" : ""}`}>
            {section.title} <span className="roster-bucket-count">{section.bandStart}</span>
          </div>
          <TreeList
            tree={{ roots: section.roots.slice(0, section.bandStart), childrenOf: tree.childrenOf }}
            directors={directors}
            portByDirector={portByDirector}
            showMachine
            selectedId={selectedId}
          />
          {/* The calm band: the rows the Wingman judged a report, below the reds and not counted in them. Which
              rows they are is decided in client-core from the Gateway's stamps; this only lays them out. */}
          {section.bandStart < section.roots.length && (
            <>
              <div className="roster-bucket-head">
                {CALM_BAND_TITLE} <span className="roster-bucket-count">{section.roots.length - section.bandStart}</span>
              </div>
              <TreeList
                tree={{ roots: section.roots.slice(section.bandStart), childrenOf: tree.childrenOf }}
                directors={directors}
                portByDirector={portByDirector}
                showMachine
                selectedId={selectedId}
              />
            </>
          )}
        </div>
      ))}
    </>
  );
}

// One level of the tree as a list: each root is a card, and a root with sessions under it carries the
// chevron, the crew line while collapsed, and its children as a nested list while expanded.
function TreeList({
  tree,
  directors,
  portByDirector,
  showMachine,
  selectedId,
}: {
  tree: SessionTree;
  directors: DirectorReachability[];
  portByDirector: Map<string, string>;
  showMachine: boolean;
  selectedId: string | undefined;
}) {
  return (
    <ul className="roster-list">
      {tree.roots.map((s) => (
        <RosterRow
          key={s.sessionId}
          session={s}
          tree={tree}
          directors={directors}
          portByDirector={portByDirector}
          showMachine={showMachine}
          selectedId={selectedId}
        />
      ))}
    </ul>
  );
}

function RosterRow({
  session,
  tree,
  directors,
  portByDirector,
  showMachine,
  selectedId,
}: {
  session: SessionDto;
  /** The whole tree (client-core/sessions/tree), so a row can render the rows under it at any depth. */
  tree: SessionTree;
  directors: DirectorReachability[];
  portByDirector: Map<string, string>;
  /** Show the "computer:port" line on the card. False in "My order" (the group header carries it); true
   *  in the cross-machine Attention view, where each card needs to say which cc-director it lives on. */
  showMachine: boolean;
  selectedId: string | undefined;
}) {
  const sid = session.sessionId ?? "";
  const selected = sid === selectedId;
  const { legend } = useSessionColourLegend();
  // A parent row: collapsed by default, remembered per crew on this device. The chevron sits OUTSIDE
  // the Link so opening the crew never navigates into the parent's session. The count on the chevron
  // is everything under it, at every level - the same number the crew line carries.
  const kids = childrenOf(tree, session);
  const isParent = kids.length > 0;
  const underCount = isParent ? descendantsOf(tree, session).length : 0;
  const [expanded, setExpanded] = useState<boolean>(() => isCrewExpanded(sid));
  const toggle = () => {
    const next = !expanded;
    setExpanded(next);
    setCrewExpanded(sid, next);
  };
  const attention = classify(session) === "needsYou";
  const name = session.name && session.name.trim().length > 0 ? session.name : session.repoPath || "(unnamed session)";
  const num = session.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  const directorId = (session.directorId ?? "").trim();
  const machineLine = showMachine
    ? machinePortLabel((session.machineName ?? "").trim(), portByDirector.get(directorId) ?? "")
    : "";
  // The owning Director's reachability (issue #1215): a Wobbly/Offline Director dims its sessions in
  // place and shows a "last seen" age; an Online (or unknown) Director renders normally.
  const reach = reachabilityFor(directors, session.directorId);
  const wobbly = reach?.state === REACHABILITY_WOBBLY;
  // WHETHER these rows are last-known is the Gateway's ruling, read - not a list of states enumerated
  // here. This checked only wobbly and offline, so a row owned by a Director that had been shut down (the
  // Gateway deliberately keeps serving those) rendered as current, undimmed and undated.
  const stale = isDataStale(reach);
  const offline = stale && !wobbly;
  const lastSeen = stale ? reachabilityLastSeen(reach?.lastSeenAgeSeconds) : "";
  // The hold time ("wakes in 3h 48m") and the winding-down flag are the Gateway's fold, read the same way
  // the desktop rail reads them - never the raw onHold sensor, which can drift from the fold (a Held
  // session that starts working is blue, and must not still read "snoozed"). The snooze LABEL already
  // rides on contextLine (the stamped stateLabel); this tag adds the countdown beside it.
  const holdCountdown = snoozeCountdown(session);
  const windingDown = pendingDeletion(session);
  // How much uncommitted work this session is sitting on, in the same words the desktop rail uses. The
  // Director measures it and the Gateway passes it through; the shared formatter decides the wording so
  // this roster and the mobile one cannot say it two different ways. Null on a clean tree AND on an
  // unknown one - see client-core/sessions/changes for why those stay distinct upstream.
  const changes = changesBadge(session);
  // A prompt to this session did not go and nothing has landed since (issue internal#811). The Gateway
  // decides that and writes the words; the card only has to refuse to be quiet about it.
  const undelivered = hasUndeliveredPrompt(session);
  // Which model this session is actually running (issue devthrottle_internal#1340). The Gateway folds the
  // words - including which of the two absences applies - and this card renders them; it is read through
  // the same shared reader the Fleet Map card uses, so the two cannot word one session two ways.
  const model = modelChipOf(session);
  // The tag row (model / not-delivered / changes / voice / hold-time / snooze-ended / winding-down /
  // last-seen / waiting) renders only when it has something to say. It used to say that a plain working
  // session stays a compact two lines; the model changes that on purpose - it is the fact the fleet's cost
  // and quality turn on, and it was previously visible nowhere while a session was alive.
  const hasTags =
    model !== null ||
    undelivered ||
    changes !== null ||
    holdCountdown !== null ||
    snoozeExpired(session) ||
    windingDown ||
    !!session.voiceMode ||
    lastSeen.length > 0 ||
    (attention && !!session.needsYouSince);
  return (
    <li className={`roster-li${isParent ? " roster-li-parent" : ""}`}>
      {isParent && (
        <button
          type="button"
          className={`roster-chevron${expanded ? " open" : ""}`}
          aria-expanded={expanded}
          aria-label={expanded ? `Collapse the ${underCount} sessions under ${name}` : `Expand the ${underCount} sessions under ${name}`}
          title={expanded ? "Collapse" : "Expand"}
          onClick={toggle}
        />
      )}
      <Link
        className={`roster-row${selected ? " roster-row-selected" : ""}${attention ? " roster-row-attention" : ""}${wobbly ? " roster-row-wobbly" : ""}${offline ? " roster-row-offline" : ""}${session.pin ? " roster-row-pinned" : ""}`}
        style={{ borderLeftColor: dotHex(session) }}
        to={`/session/${encodeURIComponent(sid)}`}
        title={dotTitle(session, legend)}
      >
        <span className="roster-dot" style={{ backgroundColor: dotHex(session) }} aria-hidden="true" />
        <span className="roster-body">
          {/* Line 1: the session number badge + the full name (wraps freely, no clamp). */}
          <span className="roster-name">
            {hasNum && <span className="num-badge">{num}</span>}
            <span className="roster-name-text">{name}</span>
            {session.pin && (
              <span className="roster-pin-mark" title={session.pin.title}>
                {session.pin.mark}
              </span>
            )}
          </span>
          {/* Line 2: the Gateway-stamped status, on its own line so it is never squeezed to "Wor...". */}
          <span className="roster-state">{contextLine(session)}</span>
          {/* Line 2a (only when something waits): the row line - what waits in this session's fleet inbox,
              in the Gateway's words, rendered verbatim (Message Load mission, slice 4). */}
          {session.inboxLine && <span className="roster-inbox">{session.inboxLine}</span>}
          {/* Line 2b: the supervision facts (internal#625) - started / open / idle / turns, from the
              ONE shared formatter, ticking on the shared one-second clock. Stats a Director does not
              report are omitted, never shown as zero. */}
          <SupervisionLine session={session} />
          {/* Line 3 (Attention view only): which cc-director this session lives on. */}
          {machineLine && (
            <span className="roster-machine" title={session.directorId ?? undefined}>
              {machineLine}
            </span>
          )}
          {/* Line 4 (only when there is something to show): the tags and the live waiting timer. */}
          {hasTags && (
            <span className="roster-tags">
              {/* First chip in the row, in alarm red: the user's words did not reach the agent. Everything
                  else on this card describes what the session is doing; only this one says something was
                  lost. It leads. */}
              {undelivered && (
                <span className="roster-tag not-delivered" title={promptDeliveryTitle(session) ?? undefined}>
                  {DELIVERY_BADGE_TEXT}
                </span>
              )}
              {changes !== null && (
                <span className="roster-tag changes" title={changesTitle(session) ?? undefined}>
                  {changes}
                </span>
              )}
              {/* Which model this session is running. Muted when the Gateway's verdict is one of the two
                  absences, so an absence never carries the weight of a fact - the words say which. */}
              {model !== null && (
                <span className={model.absent ? "roster-tag model absent" : "roster-tag model"} title={model.title}>
                  {model.text}
                </span>
              )}
              {session.voiceMode && <span className="roster-tag voice">voice</span>}
              {windingDown && (
                <span className="roster-tag winding-down" title={deletionReason(session) ?? "Marked for deletion"}>
                  winding down
                </span>
              )}
              {holdCountdown !== null && <HoldCountdown session={session} />}
              {snoozeExpired(session) && <span className="roster-tag snooze-ended">Snooze ended</span>}
              {lastSeen && <span className="roster-lastseen">{lastSeen}</span>}
              {attention && session.needsYouSince && <WaitingTime since={String(session.needsYouSince)} />}
            </span>
          )}
          {/* The attention narration line, when present. */}
          {attention && session.railLine && session.railLine.trim().length > 0 && (
            <span className="roster-railline">{session.railLine}</span>
          )}
          {/* A collapsed crew still says what is under it: every child's colour, the counts, the age. */}
          {isParent && !expanded && <CrewLine root={session} tree={tree} />}
        </span>
      </Link>
      {/* The same session menu as the session page (issue #1214), pinned to the card's top-right. It
          sits OUTSIDE the Link so opening the menu never navigates into the session. */}
      <SessionMenu session={session} variant="rail" />
      {/* The sessions under this one, each card exactly as it would be at the top level, and each
          with its own chevron if it supervises sessions in turn. They keep their own desktop order
          whatever order the top level is in. A child on another Director says which. */}
      {isParent && expanded && (
        <ul className="roster-list roster-kids" aria-label={`Sessions under ${name}`}>
          {kids.map((k) => (
            <RosterRow
              key={k.sessionId}
              session={k}
              tree={tree}
              directors={directors}
              portByDirector={portByDirector}
              showMachine={isOnAnotherMachine(session, k)}
              selectedId={selectedId}
            />
          ))}
        </ul>
      )}
    </li>
  );
}

// The card's supervision line (internal#625): started / open / idle / turns. The wording and the
// amber/red idle thresholds live in client-core/sessions/supervision, shared with the mobile row,
// so the two surfaces cannot say the same fact two different ways. Ticks on the app's ONE shared
// one-second clock; the re-render is scoped to this line, not the whole card.
function SupervisionLine({ session }: { session: SessionDto }) {
  const now = useSharedNow();
  const stats = supervisionStats(session, now);
  if (stats.length === 0) return null;
  return (
    <span className="roster-stats">
      {stats.map((s) => (
        <span className="roster-stat" key={s.key} title={s.title}>
          <span className="roster-stat-k">{s.key}</span>
          <span className={`roster-stat-v${s.tone === "normal" ? "" : ` ${s.tone}`}`}>{s.value}</span>
        </span>
      ))}
    </span>
  );
}

// The live "waiting <dur>" label for a needs-you row, ticking each second from the held
// needsYouSince (no roster refetch). Only mounted for needs-you rows, so the per-second re-render
// never touches active/other rows.
function WaitingTime({ since }: { since: string }) {
  const now = useNow(1000);
  const label = waitingLabel(since, now);
  if (label.length === 0) return null;
  return <span className="roster-waiting">{label}</span>;
}

// The live "wakes in <dur>" hold time for a snoozed row, ticking each second from the Gateway-owned
// snooze clock (no roster refetch). Only mounted for snoozed rows that carry a clock, so the per-second
// re-render never touches other rows - the same pattern as WaitingTime.
function HoldCountdown({ session }: { session: SessionDto }) {
  const now = useNow(1000);
  const label = snoozeCountdown(session, now);
  if (label === null) return null;
  return <span className="roster-tag hold-time">{label}</span>;
}
