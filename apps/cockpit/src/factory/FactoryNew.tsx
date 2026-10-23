import { useRef, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  FactoryDefinitionRefusal,
  importFactoryDefinitionZip,
  type FactoryDefinitionFinding,
} from "@devthrottle/client-core/factory/factoryDefinitionClient";
import type { FactoriesListView } from "@devthrottle/client-core/factory/factoriesScreenClient";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import { Button, ErrorBanner } from "../components";
import { Findings } from "./FactoryDefinitionTab";

const SURFACE = "cockpit-factory-new";

// New factory on the Factories screen (Factory Definition mission, phase 6b). The owner picks a zip of a definition
// folder; the Gateway imports it as the first draft of the factory its factory.yaml names, and the Cockpit opens that
// factory's Definition tab, where it is read and published.
//
// Rule 7: the label, the note, every refusal and every finding are the Gateway's. This file sends the file and shows
// what came back; it never decides whether an id is taken or what a refusal means.

/** The New factory button with its note. A refusal stays under it with every finding, by file and line. */
export function NewFactoryButton({ view }: { view: FactoriesListView }) {
  const navigate = useNavigate();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [refused, setRefused] = useState<{ lead: string; findings: FactoryDefinitionFinding[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const action = view.newFactory;

  const send = async (file: File) => {
    setRefused(null);
    setError(null);
    setBusy(true);
    try {
      const imported = await importFactoryDefinitionZip(action.href, file);
      if (imported.openHref !== null && imported.openHref !== undefined) navigate(imported.openHref);
    } catch (err) {
      const what = "import the folder as a new factory";
      if (err instanceof FactoryDefinitionRefusal) setRefused({ lead: describeAndReport(SURFACE, what, err), findings: err.findings });
      else setError(describeAndReport(SURFACE, what, err));
    } finally {
      setBusy(false);
      if (input.current !== null) input.current.value = "";
    }
  };

  return (
    <div className="fa-new-factory" data-testid="fa-new-factory">
      <div className="fa-inline-action">
        <Button variant="primary" data-testid="fa-new-factory-button" disabled={busy} onClick={() => input.current?.click()}>
          {busy ? action.busyLabel : action.label}
        </Button>
        <input
          ref={input}
          type="file"
          accept=".zip,application/zip"
          hidden
          data-testid="fa-new-factory-file"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file !== undefined) void send(file);
          }}
        />
        <span className="fa-dim">{action.noteText}</span>
      </div>
      {refused !== null && (
        <div className="fa-memory-conflict" data-testid="fa-new-factory-refused">
          <p className="fa-memory-conflict-lead" role="alert">
            {refused.lead}
          </p>
          <Findings findings={refused.findings} />
        </div>
      )}
      {error !== null && <ErrorBanner message={error} />}
    </div>
  );
}

/** The factories that are not published yet, each opening its page on the Definition tab. Nothing when there are none. */
export function DraftFactories({ view }: { view: FactoriesListView }) {
  if (view.draftRows.length === 0) return null;
  return (
    <section className="fa-drafts" aria-label={view.draftTitle} data-testid="fa-drafts">
      <h3 className="fa-outside-title">{view.draftTitle}</h3>
      {view.draftRows.map((row) => (
        <div key={row.id} className="fa-draft-row" data-testid={`fa-draft-${row.id}`}>
          <Link className="fa-link" to={row.href}>
            {row.text}
          </Link>
          <span className="fa-dim">{row.lineText}</span>
        </div>
      ))}
    </section>
  );
}
