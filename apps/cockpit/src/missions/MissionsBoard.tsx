import { useCallback, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { type SessionDto } from "@devthrottle/client-core/api/client";
import { dotColor, dotHex, effectiveColor, stateLabel } from "@devthrottle/client-core/sessions/ordering";
import { setMissionWhy, type MissionDto } from "@devthrottle/client-core/missions/missions";
import { repoBasename, relativeTime } from "../fleet/format";
import { groupByMission, splitEmptyMissions, type MissionGroup } from "./missionGrouping";

// The Missions board (issue #1405): the same live fleet the Fleet Map draws, seen the way the owner
// actually thinks about the work - grouped into MISSIONS rather than machines or repos. A mission is the
// one a session is ATTACHED to (SessionDto.missionId), joined against the Gateway's mission records so a
// mission nobody is on yet still appears; the pure module missionGrouping.ts does that join and this board
// only lays the result out. Sessions attached to no mission fall into a Standalone group shown last.
//
// This is the "Missions" pivot of the Fleet Map (the page that owns the roster, the header, and the
// error/empty handling): the board is handed the already-loaded fleet roster as a prop rather than
// polling its own, so it shares the ONE fleet roster store (issue #1239) the Fleet Map already reads.
// It reuses the ONE shared effective-color + state-label rule so a status dot here matches every other
// Cockpit surface. Clicking a session row opens that session (/session/:id) - the "linking".
//
// The WHY: every card carries its mission's WHY, front and center, read straight off the Mission record
// the board already has and written back with setMissionWhy (PATCH /missions/{id}). A mission with no WHY
// shows a loud flag (the mission's founding rule), never a silent blank; the flag is the button that adds
// one, and the WHY is editable inline.
//
// It used to come from a SEPARATE Gateway store keyed by the mission's lower-cased NAME, which meant there
// was no second fetch to keep in step and, worse, no way to rename a mission without silently orphaning its
// WHY. Both problems went with the key: the WHY is now a field on the mission, so it arrives with it and
// travels with it.

// The loud flag shown when a mission has no WHY set - a mission with no stated WHY is a red flag the
// screen makes obvious (the mission's founding rule), never a silent blank. It is also the button that
// opens the inline editor to add one.
const NO_WHY_TEXT = "No why set - add one";

interface MissionsBoardBaseProps {
  sessions: SessionDto[];
  /** The Gateway's mission records, so a mission with nobody on it still gets a card. Defaults to none,
   *  which renders only the missions at least one session is attached to. */
  missions?: MissionDto[];
  /** Set when the mission records could not be loaded. Shown as a banner: the board can still draw every
   *  mission that has a session on it, but not the empty ones, and it must say so rather than present a
   *  short list as the whole truth. */
  error?: string | null;
}

/**
 * Hiding is only ever offered TOGETHER with the way back, and the type enforces it rather than trusting
 * every caller to remember. `hideEmpty` without `onShowEmpty` does not compile.
 *
 * This is not type ceremony. The first cut of this component took both as independent optional props, and
 * a caller that hid the empties without supplying the callback rendered a notice saying "1 mission is
 * hidden - show it" above a button that did nothing. The requirement is that nothing is ever hidden
 * without a one-click way back; a requirement a caller can silently fail to meet is not enforced, it is
 * merely written down. One of this file's own tests had already made exactly that mistake.
 */
export type MissionsBoardProps = MissionsBoardBaseProps &
  (
    | {
        /** Hide the missions nobody is on right now. A VIEW preference only - it says nothing about
         *  whether that work is finished. The board always states how many it hid and offers them back. */
        hideEmpty: true;
        /** Required when hiding: how the owner asks for the hidden missions back. */
        onShowEmpty: () => void;
      }
    | { hideEmpty?: false; onShowEmpty?: never }
  );

export function MissionsBoard({
  sessions,
  missions = [],
  error = null,
  hideEmpty = false,
  onShowEmpty,
}: MissionsBoardProps) {
  const navigate = useNavigate();

  // WHYs the owner has edited since this board loaded, by mission id. The WHY arrives ON the mission, so
  // this is only an overlay for edits made HERE - it stops a card flicking back to its old text while the
  // parent's mission list catches up. Anything not in here renders the mission's own why.
  const [editedWhy, setEditedWhy] = useState<Map<string, string>>(new Map());

  // Save (or clear) one mission's WHY by ID. A blank why clears it and the card returns to its flag. The
  // Gateway's answer is what gets shown - not the text that was typed - so the screen reflects what was
  // actually stored.
  const saveWhy = useCallback(async (missionId: string, why: string) => {
    const updated = await setMissionWhy(missionId, why);
    setEditedWhy((prev) => new Map(prev).set(missionId, updated.why ?? ""));
  }, []);

  const grouped = useMemo(() => groupByMission(sessions, missions), [sessions, missions]);

  // Split before deciding what to draw, so the count of what is hidden is always available even when it
  // is being hidden. The board never drops a card without saying how many it dropped.
  const { staffed, empty } = useMemo(() => splitEmptyMissions(grouped.missions), [grouped.missions]);
  const shown = hideEmpty ? staffed : grouped.missions;
  const hiddenCount = hideEmpty ? empty.length : 0;

  const openSession = (sid: string | null | undefined) => {
    const id = (sid ?? "").trim();
    if (id.length > 0) navigate(`/session/${encodeURIComponent(id)}`);
  };

  // "There is nothing to show" is only true if we actually managed to READ what there is. When the mission
  // list failed to load, this same screen would otherwise state, calmly and with no error anywhere on it,
  // that the fleet is running no missions at all - the most confident possible answer produced from the
  // least information. So the failure banner wins over the empty state, and the empty state speaks only
  // when the read succeeded.
  if (error === null && grouped.missions.length === 0 && grouped.standalone.length === 0) {
    return (
      <div className="msn-empty">
        <p>No missions are running anywhere on the fleet.</p>
        <p className="msn-empty-sub">A mission appears here the moment its sessions start.</p>
      </div>
    );
  }

  return (
    <div className="msn-list">
      {error !== null && (
        <div className="msn-loaderr" role="status">
          The mission list could not be loaded, so any mission with no sessions on it is missing from this
          board. {error}
        </div>
      )}

      {shown.map((m) => (
        <MissionCard
          key={m.key}
          mission={m}
          // An edit made on this board wins until the parent's mission list catches up; otherwise the
          // mission's own why is the truth.
          why={editedWhy.get(m.key) ?? m.why}
          onSaveWhy={saveWhy}
          onOpen={openSession}
        />
      ))}

      {/* Whatever is hidden is COUNTED and offered back, right where the cards would have been. Hiding
          without saying so would leave the owner unable to tell an empty fleet from a filtered view -
          and this board is the one that already told him he had two missions when he had eleven. */}
      {hiddenCount > 0 && (
        <button type="button" className="msn-hidden-note" onClick={onShowEmpty}>
          {hiddenCount} mission{hiddenCount === 1 ? "" : "s"} with no sessions{" "}
          {hiddenCount === 1 ? "is" : "are"} hidden - show {hiddenCount === 1 ? "it" : "them"}
        </button>
      )}

      {grouped.standalone.length > 0 && (
        <>
          <div className="msn-group-label">Standalone</div>
          <div className="msn-card msn-card-standalone">
            <div className="msn-sessions">
              {grouped.standalone.map((s) => (
                <SessionRow
                  key={s.sessionId ?? s.number}
                  session={s}
                  label={(s.name ?? "").trim().length === 0 ? "(unnamed)" : (s.name as string)}
                  onOpenSession={openSession}
                />
              ))}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

// Fleet-wide mission counts, derived from the same grouping the board renders. The Fleet Map header
// shows these when the Missions pivot is active so the owner sees the mission / standalone tally the
// standalone Missions page used to carry.
export function missionCounts(
  sessions: SessionDto[],
  missions: MissionDto[] = [],
): { missions: number; standalone: number; empty: number } {
  const grouped = groupByMission(sessions, missions);
  // `missions` is the TOTAL and stays the total whether or not the empties are being drawn - the header
  // reports what exists, and reports separately how many of them are currently hidden.
  const { empty } = splitEmptyMissions(grouped.missions);
  return {
    missions: grouped.missions.length,
    standalone: grouped.standalone.length,
    empty: empty.length,
  };
}

interface MissionCardProps {
  mission: MissionGroup;
  why: string;
  onSaveWhy: (missionId: string, why: string) => Promise<void>;
  onOpen: (sessionId: string | null | undefined) => void;
}

function MissionCard({ mission, why, onSaveWhy, onOpen }: MissionCardProps) {
  const sessions = mission.members.map((m) => m.session);

  // The card's accent (left border) and status pill follow the same color rule as everything else: red
  // if any session needs you, else blue if any is working, else a neutral idle.
  const needs = sessions.filter((s) => effectiveColor(s) === "red").length;
  const working = sessions.filter((s) => effectiveColor(s) === "blue").length;
  const accent = needs > 0 ? "red" : working > 0 ? "blue" : "grey";

  // The repositories this mission spans - one label when they agree, a count when they do not. A mission
  // nobody is on yet spans none, and says so rather than rendering an empty chip.
  const repos = useMemo(() => {
    const set = new Set<string>();
    for (const s of sessions) set.add(repoBasename(s.repoPath));
    return [...set];
  }, [sessions]);
  const repoLabel =
    repos.length === 0 ? "no sessions yet" : repos.length === 1 ? repos[0] : `${repos.length} repos`;

  const count = mission.members.length;

  return (
    <section className="msn-card">
      <div className="msn-card-head" style={{ borderLeftColor: dotColor(accent) }}>
        <div className="msn-card-title">
          <div className="msn-name">{mission.name}</div>
          <div className="msn-meta">
            <span className="msn-repo">{repoLabel}</span>
            <span>
              {count} session{count === 1 ? "" : "s"}
            </span>
            {/* Sessions say they are on this mission, but the Gateway's mission list does not contain it -
                so this card's name is the copy cached on a session, not the record. Say so rather than
                presenting it as though the two agreed: the mission records and the workflow runs are
                separate stores and are already known to disagree in exactly this direction. */}
            {mission.fromSessionOnly && (
              <span className="msn-unlisted" title="Sessions are attached to this mission, but it is not in the Gateway's mission list. The name shown is the one cached on those sessions.">
                not in the mission list
              </span>
            )}
          </div>
        </div>
        <MissionPill needs={needs} working={working} />
      </div>

      {/* The WHY slot (issue #1405): first-class on every card, front and center. The WHY comes from the
          durable, shared Gateway store and is editable inline; a missing WHY shows the loud flag. */}
      <WhySlot missionId={mission.key} why={why} onSave={onSaveWhy} />

      <div className="msn-sessions">
        {/* The row's primary label is the session's NAME, with its role as a badge beside it. The role
            alone is not an address: a mission with five workers on it would read "Worker" five times and
            tell the owner nothing about which is which. This is the same reason the mission model says a
            Worker is addressed by its TASK rather than by its role. */}
        {mission.members.map((m) => (
          <SessionRow
            key={m.session.sessionId ?? m.session.number}
            session={m.session}
            label={
              (m.session.name ?? "").trim().length === 0
                ? "(unnamed)"
                : (m.session.name as string)
            }
            role={m.role}
            onOpenSession={onOpen}
          />
        ))}
      </div>
    </section>
  );
}

interface WhySlotProps {
  /** The mission the WHY is written against. The id, never the name - a name-keyed WHY was orphaned by
   *  any rename, which is exactly why this moved onto the record. */
  missionId: string;
  why: string;
  onSave: (missionId: string, why: string) => Promise<void>;
}

// The WHY slot on a mission card: shows the mission's WHY front and center, or a loud flag when none is
// set. Either the flag or an "Edit" affordance opens an inline editor; saving writes through the shared
// store (an empty value clears the WHY back to the flag). Save errors are shown loudly and keep the
// editor open rather than silently dropping the edit.
function WhySlot({ missionId, why, onSave }: WhySlotProps) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(why);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const beginEdit = () => {
    setDraft(why);
    setError(null);
    setEditing(true);
  };

  const cancel = () => {
    setEditing(false);
    setError(null);
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await onSave(missionId, draft);
      setEditing(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not save the why");
    } finally {
      setSaving(false);
    }
  };

  if (editing) {
    return (
      <div className="msn-why msn-why-editing">
        <span className="msn-why-k">Why</span>
        <div className="msn-why-edit">
          <textarea
            className="msn-why-input"
            value={draft}
            autoFocus
            rows={2}
            placeholder="Why are we on this mission?"
            disabled={saving}
            onChange={(e) => setDraft(e.target.value)}
          />
          <div className="msn-why-actions">
            <button type="button" className="msn-why-save" disabled={saving} onClick={save}>
              {saving ? "Saving..." : "Save"}
            </button>
            <button type="button" className="msn-why-cancel" disabled={saving} onClick={cancel}>
              Cancel
            </button>
          </div>
          {error !== null && <div className="msn-why-error">{error}</div>}
        </div>
      </div>
    );
  }

  const hasWhy = why.trim().length > 0;
  return (
    <div className={hasWhy ? "msn-why" : "msn-why msn-why-empty"}>
      <span className="msn-why-k">Why</span>
      {hasWhy ? (
        <>
          <span className="msn-why-text">{why}</span>
          <button type="button" className="msn-why-editbtn" onClick={beginEdit}>
            Edit
          </button>
        </>
      ) : (
        <button type="button" className="msn-why-flag" onClick={beginEdit}>
          {NO_WHY_TEXT}
        </button>
      )}
    </div>
  );
}

function MissionPill({ needs, working }: { needs: number; working: number }) {
  if (needs > 0) {
    return (
      <span className="msn-pill msn-pill-needs">
        <span className="msn-pdot" style={{ backgroundColor: dotColor("red") }} />
        {needs} needs you
      </span>
    );
  }
  if (working > 0) {
    return (
      <span className="msn-pill msn-pill-work">
        <span className="msn-pdot" style={{ backgroundColor: dotColor("blue") }} />
        working
      </span>
    );
  }
  return (
    <span className="msn-pill msn-pill-idle">
      <span className="msn-pdot" style={{ backgroundColor: dotColor("grey") }} />
      idle
    </span>
  );
}

interface SessionRowProps {
  session: SessionDto;
  // The primary label for the row: the session's name, on both a mission card and in Standalone.
  label: string;
  // The Gateway-resolved role, shown as a badge beside the name. Null on a Standalone row and on any
  // session the Gateway gave no role - never inferred here.
  role?: string | null;
  onOpenSession: (sessionId: string | null | undefined) => void;
}

// One clickable session row: number badge, primary label, machine chip, and the live state label - all
// colored by the ONE shared effective-color rule so the row agrees with the rail and the Fleet Map.
// Clicking (or Enter/Space) opens the session.
//
// There is deliberately no line under the name. It used to print lastStatusReason, the DIRECTOR's reason
// for its local colour, written before the Wingman judges the turn. The Director only knows running or
// stopped, so every stopped row read "needs you" - under a purple "Carrying on" state and under a snoozed
// one. The state label on the right is the Gateway's word and already says what the session is doing.
function SessionRow({ session: s, label, role = null, onOpenSession }: SessionRowProps) {
  // A real session row paints the Gateway-stamped dot hex, not the local COLORS table (which is only for
  // the mission-card accent and the priority-legend swatches below, none of which have a session).
  const hex = dotHex(s);
  const sid = s.sessionId ?? "";
  const num = s.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  const machine = (s.machineName ?? "").trim();

  return (
    <div
      className="msn-srow"
      tabIndex={0}
      role="button"
      title={sid.length > 0 ? "Open session" : undefined}
      onClick={() => onOpenSession(sid)}
      onKeyDown={(e) => {
        if (sid.length > 0 && (e.key === "Enter" || e.key === " ")) {
          e.preventDefault();
          onOpenSession(sid);
        }
      }}
    >
      <span className="msn-lbar" style={{ backgroundColor: hex }} />
      {hasNum && <span className="num-badge">{num}</span>}
      <div className="msn-srow-grow">
        <div className="msn-role">
          <span className="msn-sname">{label}</span>
          {role !== null && <span className={`msn-rolebadge ${role.toLowerCase()}`}>{role}</span>}
        </div>
      </div>
      {machine.length > 0 && <span className="msn-machine">{machine}</span>}
      <span className="msn-state" style={{ color: hex }}>
        {stateLabel(s)}
        <span className="msn-idle">{relativeTime(s.lastActivityAt)}</span>
      </span>
    </div>
  );
}
