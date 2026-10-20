import { type AgentChoice } from "../api/client";
import { connectionToSend, offersConnectionChoice } from "./connectionChoice";
import "./connectionChoiceRow.css";

// THE NEW SESSION "CONNECTION" ROW - shared by the Cockpit and the phone (API mode mission #4070, phase 5).
//
// Connection: [ Terminal ] [ API mode ]. It is drawn only for an agent the Gateway offers more than one connection
// (Pi today, and only while the Gateway's one switch offers API mode), so for every other agent - and on every
// agent while API mode is hidden - the screen is exactly what it was. The choices and their words are the Gateway's,
// rendered verbatim; the row never names an agent or a mode itself. Until the person picks, the selection is the
// choice the Gateway marks as the agent's default (API mode for Pi, mission #4070 section 9); Terminal stays one click
// away. The selection shown is what the create will send (connectionToSend), so a choice made for another agent never
// shows as chosen here.
// Under the buttons, the selected choice's sentence - the Gateway's words, verbatim - says what that choice means;
// Terminal has none, so nothing is drawn for it.

export interface ConnectionChoiceRowProps {
  /** The agent the session will start with, or null before one is chosen (nothing is drawn). */
  agent: AgentChoice | null;
  /** The connection the person chose (a mode word), or null before they pick: the agent's default is shown. */
  chosen: string | null;
  /** The person picked a connection. */
  onChoose: (mode: string) => void;
  /** Prefix for the row's element ids, unique on the screen. */
  idPrefix: string;
  /** True while the session is being created, so the choice cannot change under the request. */
  disabled?: boolean;
}

export default function ConnectionChoiceRow({ agent, chosen, onChoose, idPrefix, disabled = false }: ConnectionChoiceRowProps) {
  if (agent === null || !offersConnectionChoice(agent)) return null;
  const selected = connectionToSend(agent, chosen);
  const labelId = `${idPrefix}-connection-label`;
  const description = agent.connections.find((c) => c.mode === selected)?.description ?? null;
  return (
    <div className="conn-choice" data-testid="connection-choice">
      <div className="conn-choice-label" id={labelId}>
        Connection
      </div>
      <div className="conn-choice-seg" role="group" aria-labelledby={labelId}>
        {agent.connections.map((c) => (
          <button
            key={c.mode}
            type="button"
            className={`conn-choice-btn${c.mode === selected ? " sel" : ""}`}
            aria-pressed={c.mode === selected}
            disabled={disabled}
            onClick={() => onChoose(c.mode)}
          >
            {c.label}
          </button>
        ))}
      </div>
      {description !== null && (
        <p className="conn-choice-desc" data-testid="connection-choice-description">
          {description}
        </p>
      )}
    </div>
  );
}
