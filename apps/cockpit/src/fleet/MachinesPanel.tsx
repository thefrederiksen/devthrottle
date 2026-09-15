import { Fragment, useState } from "react";
import { useNavigate } from "react-router-dom";
import type { FleetMachines, FleetVersionState } from "@devthrottle/client-core/fleet/machinesClient";
import { useNow } from "@devthrottle/client-core/polling/useNow";
import { relativeTime } from "./format";
import { MachineActions, type ActionOutcome } from "./MachineActions";
import { toneClass } from "./machinesFormat";

// The Machines tab (fleet maintenance, devthrottle_internal#2026): one row per machine with its launcher, and
// the Directors on it nested underneath - including a machine whose Director is stopped while its launcher still
// runs, which the Directors list alone cannot show. Everything drawn here is the Gateway's GET /machines fold.

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
                <th>Version</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {view.machines.map((m) => {
                const outcome = outcomes[m.machine];
                return (
                  <Fragment key={m.machine}>
                    <tr className="fm-machine">
                      <td>
                        <div className="dcell-name">{m.machine}</div>
                        <div className="ddim fm-sub">
                          {(m.launcherVersion ?? "").length > 0
                            ? `Launcher seen ${relativeTime(m.launcherLastSeenUtc, { withAgo: true, now })}`
                            : "No launcher registered"}
                        </div>
                      </td>
                      <td>
                        <span className={toneClass(m.reachTone)}>{m.reachLabel}</span>
                        {(m.reachDetail ?? "").length > 0 && <div className="fm-why">{m.reachDetail}</div>}
                      </td>
                      <td />
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
