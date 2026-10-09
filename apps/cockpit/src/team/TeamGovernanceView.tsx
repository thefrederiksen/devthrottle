import { useCallback, useEffect, useState } from "react";
import { GatewayError, gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  changeTeamGovernance,
  getTeamGovernance,
  type GovernanceChange,
  type GovernanceLimit,
  type GovernanceSwitch,
  type TeamGovernance,
} from "@devthrottle/client-core/teams/governanceClient";
import { Button, ErrorBanner, LoadingState, PageHeader } from "../components";
import "./team.css";

// The team's Governance tab (Teams v1; owner, 8 Oct 2026: "under the team, we really need a new tab called Governance").
// Six sections, as the approved mockup lays them out: Review, Required skills and workflows, Agents members may run, Who
// may read what, Limits, and Changes to the rules.
//
// CRITICAL RULE 7 - every word and verdict here is the Gateway's: the rows and their labels, whether this person may
// change them and the sentence when not, the "who may read what" answers, how each limit shows, and each line of the
// record. The tab lays them out. It sends back only what the person changed, and draws the Gateway's answer to that.
//
// Responsive (CodingStyle.md): the tab shows a loading line at once and loads in the background; a save disables the
// controls until the Gateway answers, and a refusal is shown in the Gateway's own words.

type Level = "Required" | "Suggested" | "None";

export function TeamGovernanceView({ teamId }: { teamId: string }) {
  const [view, setView] = useState<TeamGovernance | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [refused, setRefused] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoadError(null);
    try {
      setView(await getTeamGovernance(teamId, signal));
    } catch (err) {
      if (signal?.aborted) return;
      // A 403 is the Gateway saying this role has no Governance tab; it is an answer, not a failure to retry.
      if (err instanceof GatewayError && err.status === 403) setRefused(gatewayErrorMessage(err, "load the team's rules"));
      else setLoadError(gatewayErrorMessage(err, "load the team's rules"));
    }
  }, [teamId]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const save = async (change: GovernanceChange) => {
    setBusy(true);
    setError(null);
    try {
      setView(await changeTeamGovernance(teamId, change));
    } catch (err) {
      setError(gatewayErrorMessage(err, "change the team's rules"));
    } finally {
      setBusy(false);
    }
  };

  if (refused !== null) {
    return (
      <div className="team-page">
        <PageHeader title="Governance" />
        <p className="team-card team-blocked" role="note">{refused}</p>
      </div>
    );
  }
  if (loadError !== null) {
    return (
      <div className="team-page">
        <PageHeader title="Governance" />
        <ErrorBanner message={loadError} onRetry={() => void load()} />
      </div>
    );
  }
  if (view === null) {
    return (
      <div className="team-page">
        <PageHeader title="Governance" />
        <LoadingState message="Loading the team's rules..." />
      </div>
    );
  }

  const locked = !view.canChange || busy;

  return (
    <div className="team-page team-page-wide">
      <PageHeader title="Governance" subtitle={view.summary} />
      {view.note !== null && <p className="team-hint gov-note" role="note">{view.note}</p>}
      {error !== null && <p className="team-warn team-note" role="alert">{error}</p>}

      <div className="gov-columns">
        <div>
          <section className="team-card" aria-label="Review">
            <h2 className="team-h2">Review</h2>
            <SwitchRows rows={view.review} locked={locked} onFlip={(id, on) => void save({ review: { [id]: on } })} />
          </section>

          <section className="team-card" aria-label="Required skills and workflows">
            <h2 className="team-h2">Required skills and workflows</h2>
            <p className="team-hint gov-lead">{view.library.note}</p>
            {view.library.items.length === 0 && <p className="team-hint">None yet.</p>}
            {view.library.items.map((item) => (
              <div className="gov-row" key={`${item.kind}:${item.id}`}>
                <div className="gov-grow">
                  {item.name}
                  <div className="team-hint">{item.kind}{item.gone !== null ? ` - ${item.gone}` : ""}</div>
                </div>
                {view.canChange ? (
                  <select
                    className="team-input gov-level"
                    aria-label={`Level for ${item.name}`}
                    value={item.level}
                    disabled={busy}
                    onChange={(e) => void save({ items: [{ kind: item.kind, id: item.id, level: e.target.value as Level }] })}
                  >
                    <option value="Required">Required</option>
                    <option value="Suggested">Suggested</option>
                    <option value="None">Take off the list</option>
                  </select>
                ) : (
                  <span className={item.level === "Required" ? "gov-pill gov-pill-on" : "gov-pill"}>{item.level}</span>
                )}
              </div>
            ))}
            {view.library.emptyLibraryNote !== null && view.canChange && <p className="team-hint">{view.library.emptyLibraryNote}</p>}
            {view.canChange && view.library.choices.length > 0 && (
              <AddItem choices={view.library.choices} busy={busy} onAdd={(kind, id, level) => void save({ items: [{ kind, id, level }] })} />
            )}
          </section>

          <section className="team-card" aria-label="Agents members may run">
            <h2 className="team-h2">Agents members may run</h2>
            <SwitchRows rows={view.agents} locked={locked} onFlip={(id, on) => void save({ agents: { [id]: on } })} />
          </section>
        </div>

        <div>
          <section className="team-card" aria-label="Who may read what">
            <h2 className="team-h2">Who may read what</h2>
            {view.readAccess.map((row) => (
              <div className="gov-row" key={row.label}>
                <div className="gov-grow">{row.label}</div>
                <span className="gov-pill">{row.who}</span>
              </div>
            ))}
          </section>

          <section className="team-card" aria-label="Limits">
            <h2 className="team-h2">Limits</h2>
            {view.limits.map((limit) => (
              <LimitRow key={limit.id} limit={limit} canChange={view.canChange} busy={busy}
                onSave={(value) => void save({ limits: { [limit.id]: value } })} />
            ))}
          </section>

          <section className="team-card" aria-label="Changes to the rules">
            <h2 className="team-h2">Changes to the rules</h2>
            {view.changes.length === 0 ? (
              <p className="team-hint">No changes yet.</p>
            ) : (
              <ul className="gov-changes">
                {view.changes.map((c) => (
                  <li key={c.id}>
                    {c.sentence}
                    <div className="team-hint">{c.when}</div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>
      </div>
    </div>
  );
}

function SwitchRows({ rows, locked, onFlip }: { rows: GovernanceSwitch[]; locked: boolean; onFlip: (id: string, on: boolean) => void }) {
  return (
    <>
      {rows.map((row) => (
        <div className="gov-row" key={row.id}>
          <div className="gov-grow">
            {row.label}
            {row.detail !== null && <div className="team-hint">{row.detail}</div>}
          </div>
          <button
            type="button"
            className={row.on ? "team-switch team-switch-on" : "team-switch"}
            role="switch"
            aria-checked={row.on}
            aria-label={row.label}
            disabled={locked}
            onClick={() => onFlip(row.id, !row.on)}
          ></button>
        </div>
      ))}
    </>
  );
}

function AddItem({ choices, busy, onAdd }: {
  choices: TeamGovernance["library"]["choices"];
  busy: boolean;
  onAdd: (kind: string, id: string, level: "Required" | "Suggested") => void;
}) {
  const [picked, setPicked] = useState("");
  const [level, setLevel] = useState<"Required" | "Suggested">("Required");
  const choice = choices.find((c) => `${c.kind}:${c.id}` === picked) ?? null;
  return (
    <div className="team-row gov-add">
      <select className="team-input gov-pick" aria-label="Skill or workflow to add" value={picked} disabled={busy}
        onChange={(e) => setPicked(e.target.value)}>
        <option value="">Choose a skill or workflow...</option>
        {choices.map((c) => <option key={`${c.kind}:${c.id}`} value={`${c.kind}:${c.id}`}>{c.name} ({c.kind})</option>)}
      </select>
      <select className="team-input gov-level" aria-label="Level to add it at" value={level} disabled={busy}
        onChange={(e) => setLevel(e.target.value as "Required" | "Suggested")}>
        <option value="Required">Required</option>
        <option value="Suggested">Suggested</option>
      </select>
      <Button variant="primary" disabled={busy || choice === null} onClick={() => {
        if (choice === null) return;
        onAdd(choice.kind, choice.id, level);
        setPicked("");
      }}>Add</Button>
    </div>
  );
}

function LimitRow({ limit, canChange, busy, onSave }: {
  limit: GovernanceLimit; canChange: boolean; busy: boolean; onSave: (value: number | null) => void;
}) {
  const [text, setText] = useState(limit.value === null ? "" : String(limit.value));
  useEffect(() => setText(limit.value === null ? "" : String(limit.value)), [limit.value]);
  // Empty is "no limit"; anything else is sent as typed and the Gateway rules on it.
  const asked = text.trim() === "" ? null : Number(text.trim());
  const changed = asked !== limit.value;
  return (
    <div className="gov-row">
      <div className="gov-grow">
        {limit.label}
        {limit.detail !== null && <div className="team-hint">{limit.detail}</div>}
      </div>
      {canChange ? (
        <div className="gov-limit">
          <input
            className="team-input gov-number"
            type="number"
            min={limit.min}
            max={limit.max}
            placeholder="No limit"
            aria-label={limit.label}
            value={text}
            disabled={busy}
            onChange={(e) => setText(e.target.value)}
          />
          <Button variant="ghost" disabled={busy || !changed || (asked !== null && !Number.isInteger(asked))} onClick={() => onSave(asked)}>Save</Button>
        </div>
      ) : (
        <span className="gov-value">{limit.display}</span>
      )}
    </div>
  );
}
