import { Fragment, useState } from "react";
import { useNavigate } from "react-router-dom";
import type { FleetMachineCapacity, FleetMachines, FleetVersionState } from "@devthrottle/client-core/fleet/machinesClient";
import { useNow } from "@devthrottle/client-core/polling/useNow";
import { relativeTime } from "./format";
import { MachineActions, type ActionOutcome } from "./MachineActions";
import { nextSpareOrder, outcomeStillShown, sortBySpare, toneClass, type SpareOrder } from "./machinesFormat";
import { MachineResourcesPanel } from "./MachineResourcesPanel";
import { Chevron } from "../components/Chevron";

/** The number of columns, so the expanded machine's row spans them all. */
const COLUMNS = 8;

// The Machines tab (fleet maintenance, devthrottle_internal#2026): one row per machine with its launcher, and
// the Directors on it nested underneath - including a machine whose Director is stopped while its launcher still
// runs, which the Directors list alone cannot show. Everything drawn here is the Gateway's GET /machines fold.
//
// The processor, memory and Spare columns (Mission: Resource Monitor, section 5j) draw the Gateway's capacity fold
// verbatim: an offline machine arrives with the Gateway's dashes and is drawn with them, never as zeros - offline is
// not idle. No figure is turned into a bar here, so no unknown can be drawn as an empty, idle-looking one.

/** One capacity figure: the Gateway's label in its tone, and the line under it when there is one. */
function CapacityFigure({ label, detail, tone, testId }: { label: string; detail?: string | null; tone: string; testId: string }) {
  return (
    <>
      <div className={`fm-figure ${toneClass(tone)}`} data-testid={testId}>{label}</div>
      {detail != null && detail.length > 0 && <div className="ddim fm-sub">{detail}</div>}
    </>
  );
}

function CapacityStatus({ capacity }: { capacity: FleetMachineCapacity }) {
  return (
    <>
      {capacity.statusLabel != null && capacity.statusLabel.length > 0 && (
        <div>
          <span className={`fm-pill fm-capacity-status ${toneClass(capacity.statusTone)}`} data-testid="fm-capacity-status">
            {capacity.statusLabel}
          </span>
        </div>
      )}
      {capacity.why != null && capacity.why.length > 0 && (
        <div className="fm-why" data-testid="fm-capacity-why">{capacity.why}</div>
      )}
    </>
  );
}

export function VersionPill({ state }: { state: FleetVersionState }) {
  if (state.label.length === 0) return null;
  return <span className={`fm-pill ${toneClass(state.tone)}`}>{state.label}</span>;
}

export function MachinesPanel({
  view,
  error,
  onChanged,
}: {
  view: FleetMachines | null;
  error: string | null;
  onChanged: () => void;
}) {
  const navigate = useNavigate();
  const now = useNow();
  const [outcomes, setOutcomes] = useState<Record<string, ActionOutcome>>({});
  const [spareOrder, setSpareOrder] = useState<SpareOrder>("none");
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});

  if (view === null) {
    return error !== null ? <div className="dpage-error">{error}</div> : <div className="ddet-quiet">Loading machines...</div>;
  }

  const newest = view.newestRelease;

  return (
    <div className="fm-panel">
      {error !== null && <div className="dpage-error">{error}</div>}

      <div className="fm-release">
        <span className="fm-release-label" title={newest.detail ?? undefined}>
          Newest release <span className="dmono fm-release-version">{newest.label}</span>
        </span>
        {view.highlights.map((h) => (
          <span key={h.text} className={`fm-highlight ${toneClass(h.tone)}`}>{h.text}</span>
        ))}
      </div>
      {(newest.detail ?? "").length > 0 && <div className="fm-why">{newest.detail}</div>}

      {view.machines.length === 0 ? (
        <div className="dtbl-empty">
          No machines yet. A machine appears here when its launcher or a Director on it connects to this Gateway.
        </div>
      ) : (
        <div className="dtbl-scroll">
          <table className="dtbl fm-table">
            <thead>
              <tr>
                <th>Machine / Director</th>
                <th>Status</th>
                <th className="fm-num">Sessions</th>
                <th>CPU now / peak 1h</th>
                <th>Memory now / peak 1h</th>
                <th aria-sort={spareOrder === "none" ? "none" : spareOrder === "most" ? "descending" : "ascending"}>
                  <button
                    type="button"
                    className="fm-sort"
                    data-testid="fm-sort-spare"
                    title="Free memory at the last hour's peak. Press to sort: most room first, least room first, then as listed."
                    onClick={() => setSpareOrder(nextSpareOrder(spareOrder))}
                  >
                    Spare{spareOrder === "most" ? " \u2193" : spareOrder === "least" ? " \u2191" : ""}
                  </button>
                </th>
                <th>Version</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {sortBySpare(view.machines, spareOrder).map((m) => {
                const open = expanded[m.machine] === true;
                const outcome = outcomeStillShown(outcomes[m.machine], m) ? outcomes[m.machine] : undefined;
                return (
                  <Fragment key={m.machine}>
                    <tr className="fm-machine-row" data-testid="fm-machine-row" data-machine={m.machine}>
                      <td>
                        <button
                          type="button"
                          className="fm-expand"
                          aria-expanded={open}
                          aria-label={open ? `Hide ${m.machine}'s last 24 hours` : `Show ${m.machine}'s last 24 hours`}
                          title={open ? "Hide the last 24 hours" : "Show the last 24 hours and the cost per factory"}
                          onClick={() => setExpanded((prev) => ({ ...prev, [m.machine]: !open }))}
                        >
                          <Chevron pointing={open ? "down" : "right"} />
                          <span className="dcell-name">{m.machine}</span>
                        </button>
                        <div className="ddim fm-sub">
                          {(m.launcherVersion ?? "").length > 0
                            ? `Launcher seen ${relativeTime(m.launcherLastSeenUtc, { withAgo: true, now })}`
                            : "No launcher registered"}
                        </div>
                      </td>
                      <td>
                        <span className={toneClass(m.reachTone)}>{m.reachLabel}</span>
                        {(m.reachDetail ?? "").length > 0 && <div className="fm-why">{m.reachDetail}</div>}
                        <CapacityStatus capacity={m.capacity} />
                      </td>
                      <td />
                      <td>
                        <CapacityFigure label={m.capacity.processorLabel} detail={m.capacity.processorDetail} tone={m.capacity.processorTone} testId="fm-cpu" />
                      </td>
                      <td>
                        <CapacityFigure label={m.capacity.memoryLabel} detail={m.capacity.memoryDetail} tone={m.capacity.memoryTone} testId="fm-memory" />
                      </td>
                      <td>
                        <div className="fm-figure dmono" data-testid="fm-spare">{m.capacity.spareLabel}</div>
                      </td>
                      <td>
                        {(m.launcherVersion ?? "").length > 0 && (
                          <>
                            <span className="dmono">{m.launcherVersion}</span> <VersionPill state={m.launcherVersionState} />
                            <div className="ddim fm-sub">launcher</div>
                          </>
                        )}
                      </td>
                      <td>
                        <MachineActions
                          machine={m}
                          onOutcome={(o) => {
                            setOutcomes((prev) => ({ ...prev, [m.machine]: o }));
                            onChanged();
                          }}
                        />
                        {outcome !== undefined && <div className={`fm-result ${toneClass(outcome.tone)}`}>{outcome.text}</div>}
                      </td>
                    </tr>
                    {open && (
                      <tr className="fm-resources-row">
                        <td colSpan={COLUMNS}>
                          <MachineResourcesPanel machine={m.machine} />
                        </td>
                      </tr>
                    )}
                    {m.directors.map((d) => (
                      <tr
                        key={d.directorId}
                        className="fm-director dtbl-rowlink"
                        title="Director details"
                        onClick={() => navigate(`/directors/${encodeURIComponent(d.directorId)}`)}
                      >
                        <td className="fm-indent">
                          <span className="dcell-name">{d.name}</span>
                          <span className="ddim dmono fm-id"> {d.directorId.slice(0, 8)}</span>
                        </td>
                        <td><span className={toneClass(d.stateTone)}>{d.stateLabel}</span></td>
                        <td className="fm-num">{d.sessions}</td>
                        <td />
                        <td />
                        <td />
                        <td>
                          <span className="dmono">{d.version ?? "-"}</span> <VersionPill state={d.versionState} />
                        </td>
                        <td />
                      </tr>
                    ))}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
