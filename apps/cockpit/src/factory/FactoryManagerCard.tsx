import { useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  getFactoryManagerCard,
  startFactoryManagerTalk,
  type FactoryManagerCard as FactoryManagerCardView,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ErrorBanner } from "../components";
import { useView } from "./FactoryActivityTabs";
import { ToneChip } from "./FactoryParts";

const SURFACE = "cockpit-factory";

// The Factory Manager card (Factory Control, step 5), at the top of the Factories tab: the one seat over every
// factory, its state, the button that opens it, and the name check. Every word, tone and finding is the Gateway's
// (FactoryManagerEndpoints.CardOf), rendered verbatim (rule 7). The one thing laid out here is the placement form:
// before the first start the owner chooses a computer the Gateway lists and types a folder there; both are sent with
// the first press and the Gateway saves them only once the start succeeded.

export function FactoryManagerCard() {
  const view = useView<FactoryManagerCardView>((s) => getFactoryManagerCard(s), "factory-manager", "load the Factory Manager");
  // error-reported-by: useView
  if (view.error !== null) return <ErrorBanner message={view.error} onRetry={view.reload} />;
  if (view.data === null) return null;
  return <CardBody card={view.data} />;
}

function CardBody({ card }: { card: FactoryManagerCardView }) {
  const navigate = useNavigate();
  const [computer, setComputer] = useState(card.computers[0] ?? "");
  const [folder, setFolder] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const press = async () => {
    setBusy(true);
    setError(null);
    try {
      const started = await startFactoryManagerTalk(card.needsPlacement ? { computer, folder } : null);
      navigate(started.href);
    } catch (err) {
      setError(describeAndReport(SURFACE, "start the Factory Manager", err));
      setBusy(false);
    }
  };

  const check = card.nameCheck;
  return (
    <section className="fa-fm-card" data-testid="fa-factory-manager" aria-label={card.title}>
      <div className="fa-fm-top">
        <h2 className="fa-fm-title">{card.title}</h2>
        <ToneChip word={card.stateText} tone={card.stateTone} />
      </div>
      <p className="fa-card-purpose">{card.purpose}</p>

      {card.needsPlacement && (
        <div className="fa-fm-placement" data-testid="fa-fm-placement">
          {card.placementText !== null && <p className="fa-dim">{card.placementText}</p>}
          <div className="fa-filters">
            <label>
              Computer
              <select value={computer} onChange={(e) => setComputer(e.target.value)} data-testid="fa-fm-computer">
                {card.computers.map((c) => (
                  <option key={c} value={c}>
                    {c}
                  </option>
                ))}
              </select>
            </label>
            <label className="fa-fm-folder">
              Folder
              <input
                type="text"
                value={folder}
                placeholder="D:\ReposFred\factories"
                onChange={(e) => setFolder(e.target.value)}
                data-testid="fa-fm-folder"
              />
            </label>
          </div>
        </div>
      )}
      {card.placementSavedText !== null && <p className="fa-dim" data-testid="fa-fm-saved">{card.placementSavedText}</p>}

      <span className="fa-inline-action">
        <Button variant="primary" disabled={busy} aria-busy={busy} data-testid="fa-fm-talk" onClick={press}>
          {busy ? card.busyLabel : card.actionLabel}
        </Button>
        {error !== null && (
          <span className="fa-error" role="alert">
            {error}
          </span>
        )}
      </span>

      <div className="fa-fm-names" data-testid="fa-fm-names">
        <h3 className="fa-fm-subtitle">{check.headingText}</h3>
        <p className="fa-dim">{check.ruleText}</p>
        {check.passText !== null && <p className="fa-fm-pass">{check.passText}</p>}
        {check.findings.length > 0 && (
          <ul className="fa-fm-findings">
            {check.findings.map((f) => (
              <li key={`${f.factory}/${f.seatId}`} data-testid={`fa-fm-finding-${f.factory}-${f.seatId}`}>
                {f.text}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
