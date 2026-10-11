import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  getFactoryEmail,
  type FactoryEmail,
  type FactoryPageEmails,
} from "@devthrottle/client-core/factory/factoriesScreenClient";
import { ErrorBanner, LoadingState, PageHeader } from "../components";
import { describeAndReport } from "@devthrottle/client-core/errors/reportClientError";
import "./factory.css";

const SURFACE = "cockpit-factory-emails";

// The factory emails the account was sent that covered this factory (Factory Control, step 1b on brief 5's store),
// newest first. Every word and every link is the Gateway's (FactoryEmailsFold); this only lays them out.
export function EmailsCard({ emails }: { emails: FactoryPageEmails }) {
  return (
    <section className="fa-panel" id="emails" data-testid="fa-page-emails">
      <h2 className="fa-section-title">{emails.heading}</h2>
      <p className="fa-dim">{emails.note}</p>
      {emails.emptyText !== null && <p className="fa-dim" data-testid="fa-emails-empty">{emails.emptyText}</p>}
      <div className="fa-waiting">
        {emails.items.map((item) => (
          <div className="fa-waiting-item" key={item.id} data-testid={`fa-email-${item.id}`}>
            <div className="fa-waiting-head">
              <strong>{item.kindText}</strong>
              <span className="fa-dim">{item.whenText}</span>
            </div>
            <div className="dt-private">{item.title}</div>
            <div className="fa-waiting-foot">
              <span className="fa-dim">{item.problemText}</span>
              <Link className="fa-link" to={item.href} data-testid="fa-email-read">
                {item.readLabel}
              </Link>
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}

// One kept factory email, exactly as it was sent: the Gateway's stored HTML in a sandboxed frame, so nothing in it
// runs and nothing in it can reach the Cockpit.
export function FactoryEmailView() {
  const { id = "" } = useParams();
  const [email, setEmail] = useState<FactoryEmail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);
  const reload = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    const ctrl = new AbortController();
    setError(null);
    setEmail(null);
    getFactoryEmail(id, ctrl.signal).then(setEmail, (err: unknown) => {
      if (!ctrl.signal.aborted) setError(describeAndReport(SURFACE, "load the report", err));
    });
    return () => ctrl.abort();
  }, [id, nonce]);

  if (error !== null) return <ErrorBanner message={error} onRetry={reload} />;
  if (email === null) return <LoadingState />;

  return (
    <div className="fa-page" data-testid="factory-email-page">
      <nav className="fa-crumbs">
        <Link to="/factories">Factories</Link> /
      </nav>
      <PageHeader title={email.title} />
      <iframe className="fa-email-frame dt-private" title={email.title} sandbox="" srcDoc={email.html} data-testid="fa-email-body" />
    </div>
  );
}
